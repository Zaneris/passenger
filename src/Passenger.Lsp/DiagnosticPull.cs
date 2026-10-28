namespace Passenger.Lsp;

/// <summary>Which of a document's diagnostic sources a pull asks: every one; the document's own, which answer from the
/// document and what it reads; or those that answer from the whole project's analysis
/// (<see cref="LspDocument.ProjectWideSource"/>), which in a large project answer a change long after the rest.</summary>
public enum DiagnosticPull
{
    All,
    Own,
    ProjectWide,
}
