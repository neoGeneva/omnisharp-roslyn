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
        private readonly ILogger<CSharpDiagnosticWorkerWithAnalyzers> _logger;

        private readonly ConcurrentDictionary<DocumentId, DocumentDiagnostics> _currentDiagnosticResultLookup =
            new ConcurrentDictionary<DocumentId, DocumentDiagnostics>();
        private readonly ImmutableArray<ICodeActionProvider> _providers;
        private readonly DiagnosticEventForwarder _forwarder;
        private readonly OmniSharpOptions _options;
        private readonly OmniSharpWorkspace _workspace;
        private readonly ConcurrentDictionary<DocumentId, bool> _hasFullDiagnostics =
            new ConcurrentDictionary<DocumentId, bool>();
        private readonly ConcurrentDictionary<DocumentId, int> _timeoutCounts =
            new ConcurrentDictionary<DocumentId, int>();

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
                    .Where(x => !_hasFullDiagnostics.ContainsKey(x) /*&& _workspace.IsDocumentOpen(x)*/)
                    .ToImmutableArray();

                if (!documentsToEnqueue.IsEmpty)
                    await QueueForAnalysis(documentsToEnqueue, WorkPriority.High, awaitResults: true);
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

        private async Task Worker(WorkPriority workPriority)
        {
            var channels = _channels.Take((int)workPriority + 1).ToArray();
            
            while (true)
            {
                TaskCompletionSource<object?>? taskCompletionSource = null;

                try
                {
                    var work = await GetNextWorkItem(channels)
                        .ConfigureAwait(false);

                    taskCompletionSource = work.TaskCompletionSource;

                    await AnalyzeDocuments(work.DocumentIds, workPriority)
                        .ConfigureAwait(false);

                    taskCompletionSource?.SetResult(null);

                    await Task.Delay(50)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Analyzer worker failed: {ex}");

                    taskCompletionSource?.SetException(ex);
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

        private Task QueueForAnalysis(ImmutableArray<DocumentId> documentIds, WorkPriority workPriority, bool awaitResults = false)
        {
            var channel = GetChannel(workPriority);
            var taskCompletionSource = awaitResults
                ? new TaskCompletionSource<object?>()
                : null;

            channel.Writer.TryWrite(new ChannelWork(documentIds, taskCompletionSource));

            return taskCompletionSource?.Task ?? Task.CompletedTask;
        }

        private Channel<ChannelWork> GetChannel(WorkPriority workPriority)
        {
            return _channels[(int)workPriority];
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs changeEvent)
        {
            Project? project;

            switch (changeEvent.Kind)
            {
                case WorkspaceChangeKind.DocumentChanged:
                case WorkspaceChangeKind.DocumentAdded:
                case WorkspaceChangeKind.DocumentReloaded:
                case WorkspaceChangeKind.DocumentInfoChanged:
                    if (changeEvent.DocumentId != null)
                        QueueForAnalysis(ImmutableArray.Create(changeEvent.DocumentId), WorkPriority.Medium);
                    break;
                case WorkspaceChangeKind.DocumentRemoved:
                    if (changeEvent.DocumentId != null && !_currentDiagnosticResultLookup.TryRemove(changeEvent.DocumentId, out _))
                    {
                        _logger.LogDebug($"Tried to remove non existent document from analysis, document: {changeEvent.DocumentId}");
                    }
                    break;
                case WorkspaceChangeKind.AnalyzerConfigDocumentChanged:
                    project = _workspace.CurrentSolution.GetProject(changeEvent.ProjectId);

                    _logger.LogDebug($"Analyzer config document {changeEvent.DocumentId} changed, which triggered re-analysis of project {changeEvent.ProjectId}.");

                    if (project != null)
                        QueueForAnalysis(project.Documents.Select(x => x.Id).ToImmutableArray(), WorkPriority.Medium);

                    break;
                case WorkspaceChangeKind.ProjectAdded:
                case WorkspaceChangeKind.ProjectChanged:
                case WorkspaceChangeKind.ProjectReloaded:
                    project = _workspace.CurrentSolution.GetProject(changeEvent.ProjectId);

                    _logger.LogDebug($"Project {changeEvent.ProjectId} updated, reanalyzing its diagnostics.");

                    if (project != null)
                        QueueForAnalysis(project.Documents.Select(x => x.Id).ToImmutableArray(), WorkPriority.Medium);

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

            return await AnalyzeDocument(document, compilationWithAnalyzers, cancellationToken);
        }

        public async Task<IEnumerable<Diagnostic>> AnalyzeProjectsAsync(Project project, CancellationToken cancellationToken)
        {
            var diagnostics = new List<Diagnostic>();

            var compilationWithAnalyzers = await GetCompilationWithAnalyzers(project, cancellationToken);

            if (compilationWithAnalyzers != null)
                return await compilationWithAnalyzers.GetAllDiagnosticsAsync(cancellationToken);

            foreach (var document in project.Documents)
                diagnostics.AddRange(await AnalyzeDocument(document, compilationWithAnalyzers, cancellationToken));

            return diagnostics;
        }

        private async Task AnalyzeDocuments(IEnumerable<DocumentId> documents, WorkPriority workPriority)
        {
            try
            {
                CompilationWithAnalyzers? compilationWithAnalyzers = null;

                foreach (var documentId in documents)
                {
                    var document = _workspace.CurrentSolution.GetDocument(documentId);

                    if (document == null)
                        return;

                    //var useAnalyzers = _workspace.IsDocumentOpen(documentId);
                    var useAnalyzers = workPriority == WorkPriority.High
                        || _hasFullDiagnostics.ContainsKey(documentId);

                    if (useAnalyzers && compilationWithAnalyzers == null)
                    {
                        compilationWithAnalyzers = await GetCompilationWithAnalyzers(document.Project)
                            .ConfigureAwait(false);
                    }

                    var diagnostics = await AnalyzeDocument(document, useAnalyzers ? compilationWithAnalyzers : null)
                        .ConfigureAwait(false);

                    UpdateCurrentDiagnostics(document, diagnostics);

                    if (useAnalyzers)
                        _hasFullDiagnostics.TryAdd(documentId, true);
                    //else
                    //    _hasFullDiagnostics.TryRemove(documentId, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Analysis of documents failed, underlaying error: {ex}");
            }
        }

        private async Task<IEnumerable<Diagnostic>> AnalyzeDocument(Document document, CompilationWithAnalyzers? compilationWithAnalyzers, CancellationToken cancellationToken = default)
        {
            IEnumerable<Diagnostic>? diagnostics = null;

            // There's real possibility that bug in analyzer causes analysis hang at document.
            using var perDocumentTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (!_timeoutCounts.TryGetValue(document.Id, out var timeoutCount))
                timeoutCount = 0;

            try
            {
                perDocumentTimeout.CancelAfter(_options.RoslynExtensionsOptions.DocumentAnalysisTimeoutMs);

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
                }
                else if (compilationWithAnalyzers != null && timeoutCount < 3 && documentSemanticModel != null)
                {
                    diagnostics = documentSemanticModel.GetDiagnostics(null, perDocumentTimeout.Token);

                    var semanticDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSemanticDiagnosticsAsync(documentSemanticModel, filterSpan: null, perDocumentTimeout.Token)
                        .ConfigureAwait(false);

                    // TODO: This should be calling UpdateCurrentDiagnostics() as it goes
                    diagnostics = diagnostics.Concat(semanticDiagnosticsWithAnalyzers);

                    var syntaxDiagnosticsWithAnalyzers = await compilationWithAnalyzers
                        .GetAnalyzerSyntaxDiagnosticsAsync(documentSemanticModel.SyntaxTree, perDocumentTimeout.Token)
                        .ConfigureAwait(false);

                    diagnostics = diagnostics.Concat(syntaxDiagnosticsWithAnalyzers);
                }
                else if (documentSemanticModel != null)
                {
                    diagnostics = documentSemanticModel.GetDiagnostics(null, perDocumentTimeout.Token);
                }
            }
            catch (Exception ex)
            {
                // TODO: Instead of timing out, maybe let this continue on (in a way that doesn't block background or forground queues)?

                if (perDocumentTimeout.IsCancellationRequested)
                    _timeoutCounts.AddOrUpdate(document.Id, timeoutCount + 1, (id, val) => val + 1);

                _logger.LogError($"Analysis of document {document.Name} failed or cancelled by timeout: {ex.Message}");
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

                    await QueueForAnalysis(project.DocumentIds.ToImmutableArray(), WorkPriority.Medium, awaitResults: true);

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
            QueueForAnalysis(documentIds, WorkPriority.Medium);
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
            public ChannelWork(ImmutableArray<DocumentId> documentIds, TaskCompletionSource<object?>? taskCompletionSource)
            {
                DocumentIds = documentIds;
                TaskCompletionSource = taskCompletionSource;
            }

            public ImmutableArray<DocumentId> DocumentIds { get; }
            public TaskCompletionSource<object?>? TaskCompletionSource { get; }
        }
    }
}
