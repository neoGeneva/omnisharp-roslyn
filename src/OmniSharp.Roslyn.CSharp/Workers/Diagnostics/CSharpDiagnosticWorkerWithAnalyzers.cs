﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.Extensions.Logging;
using OmniSharp.Helpers;
using OmniSharp.Models.Diagnostics;
using OmniSharp.Models.Events;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Workers.Diagnostics;
using OmniSharp.Services;

namespace OmniSharp.Roslyn.CSharp.Services.Diagnostics
{
    public class CSharpDiagnosticWorkerWithAnalyzers : ICsDiagnosticWorker, IDisposable
    {
        private readonly AnalyzerWorkQueue _workQueue;
        private readonly ILogger<CSharpDiagnosticWorkerWithAnalyzers> _logger;

        private readonly ConcurrentDictionary<DocumentId, DocumentDiagnostics> _currentDiagnosticResultLookup =
            new ConcurrentDictionary<DocumentId, DocumentDiagnostics>();
        private readonly ImmutableArray<ICodeActionProvider> _providers;
        private readonly DiagnosticEventForwarder _forwarder;
        private readonly OmniSharpOptions _options;
        private readonly OmniSharpWorkspace _workspace;
        private readonly ConcurrentDictionary<ProjectId, bool> _hasHadInitialParse =
            new ConcurrentDictionary<ProjectId, bool>();
        private readonly ConcurrentDictionary<DocumentId, bool> _hasFullDiagnostics =
            new ConcurrentDictionary<DocumentId, bool>();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        // This is workaround.
        // Currently roslyn doesn't expose official way to use IDE analyzers during analysis.
        // This options gives certain IDE analysis access for services that are not yet publicly available.
        private readonly ConstructorInfo _workspaceAnalyzerOptionsConstructor;

        public CSharpDiagnosticWorkerWithAnalyzers(
            OmniSharpWorkspace workspace,
            [ImportMany] IEnumerable<ICodeActionProvider> providers,
            ILoggerFactory loggerFactory,
            DiagnosticEventForwarder forwarder,
            OmniSharpOptions options)
        {
            _logger = loggerFactory.CreateLogger<CSharpDiagnosticWorkerWithAnalyzers>();
            _providers = providers.ToImmutableArray();
            _workQueue = new AnalyzerWorkQueue(loggerFactory, timeoutForPendingWorkMs: options.RoslynExtensionsOptions.DocumentAnalysisTimeoutMs * 3);

            _forwarder = forwarder;
            _options = options;
            _workspace = workspace;

            _workspaceAnalyzerOptionsConstructor = Assembly
                .Load("Microsoft.CodeAnalysis.Features")
                .GetType("Microsoft.CodeAnalysis.Diagnostics.WorkspaceAnalyzerOptions")
                .GetConstructor(new Type[] { typeof(AnalyzerOptions), typeof(Solution) })
                ?? throw new InvalidOperationException("Could not resolve 'Microsoft.CodeAnalysis.Diagnostics.WorkspaceAnalyzerOptions' for IDE analyzers.");

            _workspace.WorkspaceChanged += OnWorkspaceChanged;
            _workspace.OnInitialized += OnWorkspaceInitialized;

            Task.Factory.StartNew(() => Worker(AnalyzerWorkType.Foreground), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() => Worker(AnalyzerWorkType.Background), TaskCreationOptions.LongRunning);

            OnWorkspaceInitialized(_workspace.Initialized);
        }

        public void OnWorkspaceInitialized(bool isInitialized)
        {
            if (isInitialized)
            {
                var documentIds = QueueDocumentsForDiagnostics();
                _logger.LogInformation($"Solution initialized -> queue all documents for code analysis. Initial document count: {documentIds.Length}.");
            }
        }

        public async Task<ImmutableArray<DocumentDiagnostics>> GetDiagnostics(ImmutableArray<string> documentPaths)
        {
            var documentIds = GetDocumentIdsFromPaths(documentPaths);

            return await GetDiagnosticsByDocumentIds(documentIds, waitForDocuments: true);
        }

        private async Task<ImmutableArray<DocumentDiagnostics>> GetDiagnosticsByDocumentIds(ImmutableArray<DocumentId> documentIds, bool waitForDocuments)
        {
            if (waitForDocuments)
            {
                foreach (var documentId in documentIds)
                {
                    if (!_workQueue.TryPromote(documentId) && !_hasFullDiagnostics.ContainsKey(documentId))
                        QueueForAnalysis(ImmutableArray.Create(documentId), AnalyzerWorkType.Foreground);
                }

                await _workQueue.WaitForegroundWorkComplete();
            }

            return documentIds
                .Where(x => _currentDiagnosticResultLookup.ContainsKey(x))
                .Select(x => _currentDiagnosticResultLookup[x])
                .ToImmutableArray();
        }

        private ImmutableArray<DocumentId> GetDocumentIdsFromPaths(ImmutableArray<string> documentPaths)
        {
            return documentPaths
                .Select(docPath => _workspace.GetDocumentId(docPath))
                .Where(x => x != default)
                .ToImmutableArray();
        }

        private async Task Worker(AnalyzerWorkType workType)
        {
            while (true)
            {
                try
                {
                    var solution = _workspace.CurrentSolution;

                    var currentWorkGroupedByProjects = _workQueue
                        .TakeWork(workType)
                        .Select(documentId => (projectId: solution.GetDocument(documentId)?.Project?.Id, documentId))
                        .Where(x => x.projectId != null)
                        .GroupBy(x => x.projectId, x => x.documentId)
                        .ToImmutableArray();

                    //var tasks = currentWorkGroupedByProjects
                    //    .AsParallel()
                    //    .Select(async projectGroup =>
                    //    {
                    foreach (var projectGroup in currentWorkGroupedByProjects)
                    {
                        var projectPath = solution.GetProject(projectGroup.Key).FilePath;

                        EventIfBackgroundWork(workType, projectPath, ProjectDiagnosticStatus.Started);

                        await AnalyzeProject(solution, projectGroup, workType);

                        EventIfBackgroundWork(workType, projectPath, ProjectDiagnosticStatus.Ready);
                    }
                    //});
                    //
                    //await Task.WhenAll(tasks);

                    _workQueue.WorkComplete(workType);

                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Analyzer worker failed: {ex}");
                }
            }
        }

        private void EventIfBackgroundWork(AnalyzerWorkType workType, string projectPath, ProjectDiagnosticStatus status)
        {
            if (workType == AnalyzerWorkType.Background)
                _forwarder.ProjectAnalyzedInBackground(projectPath, status);
        }

        private void QueueForAnalysis(ImmutableArray<DocumentId> documentIds, AnalyzerWorkType workType)
        {
            _workQueue.PutWork(documentIds, workType);
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs changeEvent)
        {
            switch (changeEvent.Kind)
            {
                case WorkspaceChangeKind.DocumentChanged:
                case WorkspaceChangeKind.DocumentAdded:
                case WorkspaceChangeKind.DocumentReloaded:
                case WorkspaceChangeKind.DocumentInfoChanged:
                    // TODO: Should this alway be background?
                    var isBackground = changeEvent.NewSolution.Projects
                        .Where(x => x.Id == changeEvent.ProjectId)
                        .SelectMany(x => x.Documents)
                        .Where(x => x.Id == changeEvent.DocumentId)
                        .Any(x => x.FilePath != null && x.FilePath.EndsWith("__bg__virtual.cs"));

                    QueueForAnalysis(ImmutableArray.Create(changeEvent.DocumentId), isBackground ? AnalyzerWorkType.Background : AnalyzerWorkType.Foreground);
                    break;
                case WorkspaceChangeKind.DocumentRemoved:
                    if (!_currentDiagnosticResultLookup.TryRemove(changeEvent.DocumentId, out _))
                    {
                        _logger.LogDebug($"Tried to remove non existent document from analysis, document: {changeEvent.DocumentId}");
                    }
                    break;
                case WorkspaceChangeKind.AnalyzerConfigDocumentChanged:
                    _logger.LogDebug($"Analyzer config document {changeEvent.DocumentId} changed, which triggered re-analysis of project {changeEvent.ProjectId}.");
                    QueueForAnalysis(_workspace.CurrentSolution.GetProject(changeEvent.ProjectId).Documents.Select(x => x.Id).ToImmutableArray(), AnalyzerWorkType.Background);
                    break;
                case WorkspaceChangeKind.ProjectAdded:
                case WorkspaceChangeKind.ProjectChanged:
                case WorkspaceChangeKind.ProjectReloaded:
                    _logger.LogDebug($"Project {changeEvent.ProjectId} updated, reanalyzing its diagnostics.");
                    QueueForAnalysis(_workspace.CurrentSolution.GetProject(changeEvent.ProjectId).Documents.Select(x => x.Id).ToImmutableArray(), AnalyzerWorkType.Background);
                    break;
                case WorkspaceChangeKind.SolutionAdded:
                case WorkspaceChangeKind.SolutionChanged:
                case WorkspaceChangeKind.SolutionReloaded:
                    QueueDocumentsForDiagnostics();
                    break;

            }
        }

        public async Task<IEnumerable<Diagnostic>> AnalyzeDocumentAsync(Document document, CancellationToken cancellationToken)
        {
            Project project = document.Project;

            var compilationWithAnalyzers = await GetCompilationWithAnalyzers(project);

            cancellationToken.ThrowIfCancellationRequested();
            return await AnalyzeDocument(project, compilationWithAnalyzers, document);
        }

        public async Task<IEnumerable<Diagnostic>> AnalyzeProjectsAsync(Project project, CancellationToken cancellationToken)
        {
            var diagnostics = new List<Diagnostic>();

            var compilationWithAnalyzers = await GetCompilationWithAnalyzers(project);

            if (compilationWithAnalyzers != null)
                return await compilationWithAnalyzers.GetAllDiagnosticsAsync();

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                diagnostics.AddRange(await AnalyzeDocument(project, compilationWithAnalyzers, document));
            }

            return diagnostics;
        }

        private async Task AnalyzeProject(Solution solution, IGrouping<ProjectId, DocumentId> documentsGroupedByProject, AnalyzerWorkType workType)
        {
            try
            {
                var project = solution.GetProject(documentsGroupedByProject.Key);

                var isInitialParse = _hasHadInitialParse.TryAdd(documentsGroupedByProject.Key, true);
                var isBackgroundInitialParse = workType == AnalyzerWorkType.Background && isInitialParse;

                //var compilation = await project.GetCompilationAsync();

                //if (isBackgroundInitialParse)
                //{
                //    _hasHadInitialParse.TryAdd(documentsGroupedByProject.Key, true);

                //    var all = compilation.GetDiagnostics();
                //    var documentDiagnostics = all
                //        .GroupBy(x => project.GetDocument(x.Location.SourceTree))
                //        .Where(x => x.Key != null);

                //    foreach (var diagnostics in documentDiagnostics)
                //        UpdateCurrentDiagnostics(project, diagnostics.Key, diagnostics);

                //    return;
                //}

                var compilationWithAnalyzers = isBackgroundInitialParse ? null : await GetCompilationWithAnalyzers(project);

                foreach (var documentId in documentsGroupedByProject)
                {
                    await _semaphore.WaitAsync();

                    try
                    {
                        var document = project.GetDocument(documentId);
                        var diagnostics = await AnalyzeDocument(project, compilationWithAnalyzers, document);
                        UpdateCurrentDiagnostics(project, document, diagnostics);

                        if (!isBackgroundInitialParse)
                            _hasFullDiagnostics.TryAdd(documentId, true);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Analysis of project {documentsGroupedByProject.Key} failed, underlaying error: {ex}");
            }
        }

        private async Task<IEnumerable<Diagnostic>> AnalyzeDocument(Project project, CompilationWithAnalyzers compilationWithAnalyzers, Document document)
        {
            try
            {
                // There's real possibility that bug in analyzer causes analysis hang at document.
                var perDocumentTimeout =
                    new CancellationTokenSource(_options.RoslynExtensionsOptions.DocumentAnalysisTimeoutMs);

                var documentSemanticModel = await document.GetSemanticModelAsync(perDocumentTimeout.Token)
                    .ConfigureAwait(false);

                IEnumerable<Diagnostic> diagnostics;

                // Only basic syntax check is available if file is miscellanous like orphan .cs file.
                // Those projects are on hard coded virtual project
                if (project.Name == $"{Configuration.OmniSharpMiscProjectName}.csproj")
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync();

                    diagnostics = syntaxTree.GetDiagnostics();
                }
                else if (compilationWithAnalyzers != null)
                {
                    var semanticDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSemanticDiagnosticsAsync(documentSemanticModel, filterSpan: null, perDocumentTimeout.Token)
                        .ConfigureAwait(false);

                    var syntaxDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSyntaxDiagnosticsAsync(documentSemanticModel.SyntaxTree, perDocumentTimeout.Token)
                        .ConfigureAwait(false);

                    diagnostics = semanticDiagnosticsWithAnalyzers
                        .Concat(syntaxDiagnosticsWithAnalyzers)
                        .Concat(documentSemanticModel.GetDiagnostics());
                }
                else
                {
                    diagnostics = documentSemanticModel.GetDiagnostics();
                }

                return diagnostics;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Analysis of document {document.Name} failed or cancelled by timeout: {ex.Message}");
                return ImmutableArray<Diagnostic>.Empty;
            }
        }

        private async Task<CompilationWithAnalyzers> GetCompilationWithAnalyzers(Project project)
        {
            var compilation = await project.GetCompilationAsync();
            var workspaceAnalyzerOptions = (AnalyzerOptions)_workspaceAnalyzerOptionsConstructor.Invoke(new object[] { project.AnalyzerOptions, project.Solution });

            var allAnalyzers = _providers
                .SelectMany(x => x.CodeDiagnosticAnalyzerProviders)
                .Concat(project.AnalyzerReferences.SelectMany(x => x.GetAnalyzers(project.Language)))
                .Where(x => !CompilationWithAnalyzers.IsDiagnosticAnalyzerSuppressed(x, project.CompilationOptions, OnAnalyzerException))
                .ToImmutableArray();

            if (allAnalyzers.Length == 0) // Analyzers cannot be called with empty analyzer list.
                return null;

            return compilation.WithAnalyzers(allAnalyzers, new CompilationWithAnalyzersOptions(
                workspaceAnalyzerOptions,
                onAnalyzerException: OnAnalyzerException,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false));
        }

        private void OnAnalyzerException(Exception ex, DiagnosticAnalyzer analyzer, Diagnostic diagnostic)
        {
            _logger.LogDebug($"Exception in diagnostic analyzer." +
                $"\n            analyzer: {analyzer}" +
                $"\n            diagnostic: {diagnostic}" +
                $"\n            exception: {ex.Message}");
        }

        private void UpdateCurrentDiagnostics(Project project, Document document, IEnumerable<Diagnostic> diagnosticsWithAnalyzers)
        {
            _currentDiagnosticResultLookup[document.Id] = new DocumentDiagnostics(
                document.Id,
                document.FilePath,
                project.Id,
                project.Name,
                diagnosticsWithAnalyzers
                    .Where(x => x.Severity != DiagnosticSeverity.Hidden)
                    .ToImmutableArray()
            );

            EmitDiagnostics(_currentDiagnosticResultLookup[document.Id]);
        }

        private void EmitDiagnostics(DocumentDiagnostics results)
        {
            _forwarder.Forward(new DiagnosticMessage
            {
                Results = new[]
                {
                    new DiagnosticResult
                    {
                        FileName = results.DocumentPath, QuickFixes = results.Diagnostics
                            .Select(x => x.ToDiagnosticLocation())
                            .ToList()
                    }
                }
            });
        }

        public ImmutableArray<DocumentId> QueueDocumentsForDiagnostics()
        {
            var documentIds = _workspace.CurrentSolution.Projects.SelectMany(x => x.DocumentIds).ToImmutableArray();
            QueueForAnalysis(documentIds, AnalyzerWorkType.Background);
            return documentIds;
        }

        public async Task<ImmutableArray<DocumentDiagnostics>> GetAllDiagnosticsAsync()
        {
            var allDocumentsIds = _workspace.CurrentSolution.Projects.SelectMany(x => x.DocumentIds).ToImmutableArray();
            return await GetDiagnosticsByDocumentIds(allDocumentsIds, waitForDocuments: false);
        }

        public ImmutableArray<DocumentId> QueueDocumentsForDiagnostics(ImmutableArray<ProjectId> projectIds)
        {
            var documentIds = projectIds
                .SelectMany(projectId => _workspace.CurrentSolution.GetProject(projectId).Documents.Select(x => x.Id))
                .ToImmutableArray();
            QueueForAnalysis(documentIds, AnalyzerWorkType.Background);
            return documentIds;
        }

        public void Dispose()
        {
            _workspace.WorkspaceChanged -= OnWorkspaceChanged;
            _workspace.OnInitialized -= OnWorkspaceInitialized;
        }
    }
}
