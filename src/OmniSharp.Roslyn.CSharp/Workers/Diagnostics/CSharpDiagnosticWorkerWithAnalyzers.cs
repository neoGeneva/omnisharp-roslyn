// TODO: Try and figure out why it crashes when switch branches (and then can't restart) :(

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniSharp.Helpers;
using OmniSharp.Models.Diagnostics;
using OmniSharp.Models.Events;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Workers.Diagnostics;
using OmniSharp.Services;

#nullable enable

namespace OmniSharp.Roslyn.CSharp.Services.Diagnostics
{
    public class CSharpDiagnosticWorkerWithAnalyzers : ICsDiagnosticWorker, IDisposable
    {
        private readonly Channel<ChannelWork>[] _channels;
        private readonly ConcurrentDictionary<DocumentId, ChannelWork> _workingItems =
            new ConcurrentDictionary<DocumentId, ChannelWork>();

        private readonly ILogger<CSharpDiagnosticWorkerWithAnalyzers> _logger;
        private readonly ConcurrentDictionary<DocumentId, DocumentDiagnostics> _currentDiagnosticResultLookup =
            new ConcurrentDictionary<DocumentId, DocumentDiagnostics>();
        private readonly ImmutableArray<ICodeActionProvider> _providers;
        private readonly DiagnosticEventForwarder _forwarder;
        private readonly OmniSharpOptions _options;
        private readonly OmniSharpWorkspace _workspace;

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

            var workPriorities = Enum.GetValues(typeof(WorkPriority))
                .Cast<WorkPriority>()
                .ToList();

            _channels = workPriorities
                .Select(x => Channel.CreateUnbounded<ChannelWork>())
                .ToArray();

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

            foreach (var priority in workPriorities)
                Task.Factory.StartNew(() => Worker(priority), TaskCreationOptions.LongRunning);

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
                var documentsToEnqueue = documentIds
                    .Where(x => !_currentDiagnosticResultLookup.ContainsKey(x))
                    .ToImmutableArray();

                if (!documentsToEnqueue.IsEmpty)
                    await QueueForAnalysis(null, documentsToEnqueue, WorkPriority.High, awaitResults: true);
            }

            return documentIds
                .Select(x => _currentDiagnosticResultLookup.TryGetValue(x, out var value) ? value : null!)
                .Where(x => x != null)
                .ToImmutableArray();
        }

        private ImmutableArray<DocumentId> GetDocumentIdsFromPaths(ImmutableArray<string> documentPaths)
        {
            return documentPaths
                .Select(docPath => _workspace.GetDocumentId(docPath))
                .Where(x => x != default)
                .ToImmutableArray();
        }

        private async Task Worker(WorkPriority workPriority)
        {
            var channels = _channels.Take((int)workPriority + 1).ToArray();

            while (true)
            {
                TaskCompletionSource<object?>? taskCompletionSource = null;
                IEnumerable<DocumentId>? documentIds = null;

                try
                {
                    var work = await GetNextWorkItem(channels)
                        .ConfigureAwait(false);

                    taskCompletionSource = work.TaskCompletionSource;
                    documentIds = work.DocumentIds;

                    if (taskCompletionSource == null && documentIds != null)
                    {
                        documentIds = documentIds
                            .Where(x => _workingItems.TryAdd(x, work))
                            .ToList();
                    }

                    await AnalyzeDocuments(work.ProjectId, documentIds, work.WorkPriority)
                        .ConfigureAwait(false);

                    taskCompletionSource?.SetResult(null);

                    //await Task.Delay(50)
                    //    .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Analyzer worker failed: {ex}");

                    taskCompletionSource?.SetException(ex);
                }
                finally
                {
                    if (taskCompletionSource == null && documentIds != null)
                    {
                        foreach (var id in documentIds)
                            _workingItems.TryRemove(id, out _);
                    }
                }
            }
        }

        private async ValueTask<ChannelWork> GetNextWorkItem(Channel<ChannelWork>[] channels)
        {
            while (true)
            {
                foreach (var channel in channels)
                {
                    if (channel.Reader.TryRead(out var workItem))
                        return workItem;
                }

                var tasks = channels.Select(x => x.Reader.WaitToReadAsync().AsTask()).ToArray();

                await Task.WhenAny(tasks)
                    .ConfigureAwait(false);
            }
        }

        private Task QueueForAnalysis(ProjectId? projectId, ImmutableArray<DocumentId>? documentIds, WorkPriority workPriority, bool awaitResults = false)
        {
            //workPriority = workPriority == WorkPriority.Medium && _workspace.IsDocumentOpen(documentId) ? WorkPriority.High : workPriority;

            var channel = GetChannel(workPriority);
            var taskCompletionSource = awaitResults
                ? new TaskCompletionSource<object?>()
                : null;

            channel.Writer.TryWrite(new ChannelWork(workPriority, projectId, documentIds, taskCompletionSource));

            return taskCompletionSource?.Task ?? Task.CompletedTask;
        }

        private Channel<ChannelWork> GetChannel(WorkPriority workPriority)
        {
            return _channels[(int)workPriority];
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs changeEvent)
        {
            switch (changeEvent.Kind)
            {
                case WorkspaceChangeKind.DocumentChanged:
                case WorkspaceChangeKind.DocumentAdded:
                case WorkspaceChangeKind.DocumentReloaded:
                case WorkspaceChangeKind.DocumentInfoChanged:
                    if (changeEvent.DocumentId != null)
                        QueueForAnalysis(null, ImmutableArray.Create(changeEvent.DocumentId), WorkPriority.Medium);

                    break;
                case WorkspaceChangeKind.DocumentRemoved:
                    if (changeEvent.DocumentId != null && !_currentDiagnosticResultLookup.TryRemove(changeEvent.DocumentId, out _))
                        _logger.LogDebug($"Tried to remove non existent document from analysis, document: {changeEvent.DocumentId}");

                    break;
                case WorkspaceChangeKind.AnalyzerConfigDocumentChanged:
                    _logger.LogDebug($"Analyzer config document {changeEvent.DocumentId} changed, which triggered re-analysis of project {changeEvent.ProjectId}.");

                    if (changeEvent.ProjectId != null)
                        QueueForAnalysis(changeEvent.ProjectId, null, WorkPriority.Medium);

                    break;
                case WorkspaceChangeKind.ProjectAdded:
                case WorkspaceChangeKind.ProjectChanged:
                case WorkspaceChangeKind.ProjectReloaded:
                    _logger.LogDebug($"Project {changeEvent.ProjectId} updated, reanalyzing its diagnostics.");

                    if (changeEvent.ProjectId != null)
                        QueueForAnalysis(changeEvent.ProjectId, null, WorkPriority.Medium);

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
            var compilationWithAnalyzers = await GetCompilationWithAnalyzers(document.Project, cancellationToken);

            return await AnalyzeDocument(document, WorkPriority.High, compilationWithAnalyzers, cancellationToken);
        }

        public async Task<IEnumerable<Diagnostic>> AnalyzeProjectsAsync(Project project, CancellationToken cancellationToken)
        {
            var diagnostics = new List<Diagnostic>();

            var compilationWithAnalyzers = await GetCompilationWithAnalyzers(project, cancellationToken);

            if (compilationWithAnalyzers != null)
                return await compilationWithAnalyzers.GetAllDiagnosticsAsync(cancellationToken);

            foreach (var document in project.Documents)
                diagnostics.AddRange(await AnalyzeDocument(document, WorkPriority.High, compilationWithAnalyzers, cancellationToken));

            return diagnostics;
        }

        private async Task AnalyzeDocuments(ProjectId? projectId, IEnumerable<DocumentId>? documents, WorkPriority workPriority)
        {
            try
            {
                if (documents != null)
                {
                    var documentsByProject = documents.GroupBy(x => x.ProjectId);

                    foreach (var project in documentsByProject)
                    {
                        CompilationWithAnalyzers? compilationWithAnalyzers = null;

                        foreach (var documentId in project)
                        {
                            var document = _workspace.CurrentSolution.GetDocument(documentId);

                            if (document == null)
                                continue;

                            compilationWithAnalyzers ??= await GetCompilationWithAnalyzers(document.Project)
                                .ConfigureAwait(false);

                            await AnalyzeDocument(document, workPriority, compilationWithAnalyzers)
                                .ConfigureAwait(false);
                        }
                    }
                }
                else if (projectId != null)
                {
                    // TODO: Need to add some form of cancellation here

                    var project = _workspace.CurrentSolution.GetProject(projectId);

                    if (project == null)
                        return;

                    var compilation = await project.GetCompilationAsync()
                        .ConfigureAwait(false);

                    if (compilation == null)
                        return;

                    var all = compilation.GetDiagnostics();
                    var documentDiagnostics = all
                        .GroupBy(x => project.GetDocument(x.Location.SourceTree));

                    foreach (var diagnostics in documentDiagnostics)
                    {
                        if (diagnostics.Key != null)
                            UpdateCurrentDiagnostics(diagnostics.Key, diagnostics);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Analysis of documents failed, underlaying error: {ex}");
            }
        }

        private async Task<IEnumerable<Diagnostic>> AnalyzeDocument(Document document, WorkPriority workPriority, CompilationWithAnalyzers? compilationWithAnalyzers, CancellationToken cancellationToken = default)
        {
            IEnumerable<Diagnostic>? diagnostics = null;

            // There's real possibility that bug in analyzer causes analysis hang at document.
            using var perDocumentTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                perDocumentTimeout.CancelAfter(workPriority switch
                {
                    WorkPriority.Low => 120000,
                    WorkPriority.Medium => 30000,
                    _ => 1000
                });

                var documentSemanticModel = await document.GetSemanticModelAsync(perDocumentTimeout.Token)
                    .ConfigureAwait(false);

                // Only basic syntax check is available if file is miscellanous like orphan .cs file.
                // Those projects are on hard coded virtual project
                if (document.Project.Name == $"{Configuration.OmniSharpMiscProjectName}.csproj")
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync()
                        .ConfigureAwait(false);

                    if (syntaxTree == null)
                        return Enumerable.Empty<Diagnostic>();

                    diagnostics = syntaxTree.GetDiagnostics(perDocumentTimeout.Token);

                    UpdateCurrentDiagnostics(document, diagnostics);
                }
                else if (compilationWithAnalyzers != null && documentSemanticModel != null && workPriority != WorkPriority.Medium)
                {
                    // TODO: Split out basic / analyzer diagnostics, so different priority work threads don't overwrite the wrong data

                    diagnostics = documentSemanticModel.GetDiagnostics(null, perDocumentTimeout.Token);

                    UpdateCurrentDiagnostics(document, diagnostics);

                    var semanticDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSemanticDiagnosticsAsync(documentSemanticModel, filterSpan: null, perDocumentTimeout.Token)
                        .ConfigureAwait(false);

                    diagnostics = diagnostics.Concat(semanticDiagnosticsWithAnalyzers);

                    UpdateCurrentDiagnostics(document, diagnostics);

                    var syntaxDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSyntaxDiagnosticsAsync(documentSemanticModel.SyntaxTree, perDocumentTimeout.Token)
                        .ConfigureAwait(false);

                    diagnostics = diagnostics.Concat(syntaxDiagnosticsWithAnalyzers);

                    UpdateCurrentDiagnostics(document, diagnostics);
                }
                else if (documentSemanticModel != null)
                {
                    diagnostics = documentSemanticModel.GetDiagnostics(null, perDocumentTimeout.Token);

                    UpdateCurrentDiagnostics(document, diagnostics);
                }
            }
            catch (Exception ex)
            {
                if (workPriority != WorkPriority.High || !perDocumentTimeout.IsCancellationRequested)
                {
                    _logger.LogError($"Analysis of document {document.Name} failed or cancelled by timeout: {ex.Message}");
                }
                else
                {
                    _logger.LogWarning($"Analysis of document {document.Name} timed out, sending from {workPriority} to {WorkPriority.Low} priority queue.");

                    await QueueForAnalysis(null, ImmutableArray.Create(document.Id), WorkPriority.Low);
                }
            }

            return diagnostics ?? Enumerable.Empty<Diagnostic>();
        }

        private async Task<CompilationWithAnalyzers?> GetCompilationWithAnalyzers(Project project, CancellationToken cancellationToken = default)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false);

            if (compilation == null)
                return null;

            var workspaceAnalyzerOptions = (AnalyzerOptions)_workspaceAnalyzerOptionsConstructor.Invoke(new object[] { project.AnalyzerOptions, project.Solution });

            var allAnalyzers = _providers
                .SelectMany(x => x.CodeDiagnosticAnalyzerProviders)
                .Concat(project.AnalyzerReferences.SelectMany(x => x.GetAnalyzers(project.Language)))
                .Where(x => project.CompilationOptions != null && !CompilationWithAnalyzers.IsDiagnosticAnalyzerSuppressed(x, project.CompilationOptions, OnAnalyzerException))
                .ToImmutableArray();

            if (allAnalyzers.Length == 0) // Analyzers cannot be called with empty analyzer list.
                return null;

            return compilation.WithAnalyzers(allAnalyzers, new CompilationWithAnalyzersOptions(
                workspaceAnalyzerOptions,
                onAnalyzerException: OnAnalyzerException,
                concurrentAnalysis: false,
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

        private void UpdateCurrentDiagnostics(Document document, IEnumerable<Diagnostic> diagnosticsWithAnalyzers)
        {
            _currentDiagnosticResultLookup[document.Id] = new DocumentDiagnostics(
                document.Id,
                document.FilePath,
                document.Project.Id,
                document.Project.Name,
                diagnosticsWithAnalyzers.ToImmutableArray()
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
            Task.Run(async () =>
            {
                foreach (var project in _workspace.CurrentSolution.Projects)
                {
                    _forwarder.ProjectAnalyzedInBackground(project.FilePath, ProjectDiagnosticStatus.Started);

                    await QueueForAnalysis(project.Id, null, WorkPriority.Medium, awaitResults: true);

                    _forwarder.ProjectAnalyzedInBackground(project.FilePath, ProjectDiagnosticStatus.Ready);
                }
            });

            return _workspace.CurrentSolution.Projects.SelectMany(x => x.DocumentIds).ToImmutableArray();
        }

        public async Task<ImmutableArray<DocumentDiagnostics>> GetAllDiagnosticsAsync()
        {
            var allDocumentsIds = _workspace.CurrentSolution.Projects.SelectMany(x => x.DocumentIds).ToImmutableArray();
            return await GetDiagnosticsByDocumentIds(allDocumentsIds, waitForDocuments: false);
        }

        public ImmutableArray<DocumentId> QueueDocumentsForDiagnostics(ImmutableArray<ProjectId> projectIds)
        {
            var documentIds = projectIds
                .SelectMany(projectId => _workspace.CurrentSolution.GetProject(projectId)?.Documents.Select(x => x.Id) ?? Enumerable.Empty<DocumentId>())
                .ToImmutableArray();

            foreach (var projectId in projectIds)
                QueueForAnalysis(projectId, null, WorkPriority.Medium);

            return documentIds;
        }

        public void Dispose()
        {
            _workspace.WorkspaceChanged -= OnWorkspaceChanged;
            _workspace.OnInitialized -= OnWorkspaceInitialized;
        }


        private enum WorkPriority
        {
            High = 0,
            Medium = 1,
            Low = 2,
        }

        private class ChannelWork
        {
            public ChannelWork(WorkPriority workPriority, ProjectId? projectId, ImmutableArray<DocumentId>? documentIds, TaskCompletionSource<object?>? taskCompletionSource)
            {
                WorkPriority = workPriority;
                ProjectId = projectId;
                DocumentIds = documentIds;
                TaskCompletionSource = taskCompletionSource;
            }

            public WorkPriority WorkPriority { get; }
            public ProjectId? ProjectId { get; }
            public ImmutableArray<DocumentId>? DocumentIds { get; }
            public TaskCompletionSource<object?>? TaskCompletionSource { get; }
        }
    }
}
