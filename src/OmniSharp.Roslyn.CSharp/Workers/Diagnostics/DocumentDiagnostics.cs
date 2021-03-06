using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

#nullable enable

namespace OmniSharp.Roslyn.CSharp.Services.Diagnostics
{
    public class DocumentDiagnostics
    {
        public DocumentDiagnostics(DocumentId documentId, string? documentPath, ProjectId projectId, string projectName, ImmutableArray<Diagnostic> diagnostics)
        {
            DocumentId = documentId;
            DocumentPath = documentPath;
            ProjectId = projectId;
            ProjectName = projectName;
            Diagnostics = diagnostics;
        }

        public DocumentDiagnostics(DocumentId documentId,
            string? documentPath,
            ProjectId projectId,
            string projectName,
            ImmutableArray<Diagnostic> diagnostics,
            ImmutableArray<Diagnostic>? semanticDiagnostics,
            ImmutableArray<Diagnostic>? syntaxDiagnostics)
            : this (documentId, documentPath, projectId, projectName, diagnostics)
        {
            SemanticDiagnostics = semanticDiagnostics;
            SyntaxDiagnostics = syntaxDiagnostics;
        }

        public DocumentId DocumentId { get; }
        public ProjectId ProjectId { get; }
        public string ProjectName { get; }
        public string? DocumentPath { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public ImmutableArray<Diagnostic>? SemanticDiagnostics { get; set; }
        public ImmutableArray<Diagnostic>? SyntaxDiagnostics { get; set; }
    }
}
