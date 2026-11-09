using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>What every refactoring shares: a place as an offset and back, the answer that says why not, and the changed
/// solution as each file's edits, since a handler may read the server's workspace but never change it.</summary>
internal static class Changes
{
    public static Refactored Refused(string problem) => new(problem, []);

    public static async Task<int> OffsetAsync(Document document, Place at, CancellationToken cancel)
    {
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        int line = Math.Clamp(at.Line, 0, text.Lines.Count - 1);
        TextLine row = text.Lines[line];
        return Math.Min(row.Start + Math.Max(0, at.Character), row.End);
    }

    /// <summary>Every file the new solution changes or adds, as edits to the file as the old one has it. A file in several
    /// projects (a project built for two frameworks) is changed once.</summary>
    public static async Task<Refactored> BetweenAsync(Solution before, Solution after, CancellationToken cancel)
    {
        List<FileChange> files = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ProjectChanges project in after.GetChanges(before).GetProjectChanges())
        {
            foreach (DocumentId id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                Document old = before.GetDocument(id)!;
                Document changed = after.GetDocument(id)!;
                if (old.FilePath is not { } path || !seen.Add(path))
                {
                    continue;
                }
                SourceText text = await old.GetTextAsync(cancel).ConfigureAwait(false);
                IEnumerable<TextChange> edits = await changed.GetTextChangesAsync(old, cancel).ConfigureAwait(false);
                files.Add(new FileChange(path, Created: false, [.. edits.Select(e => EditOf(text, e))]));
            }
            foreach (DocumentId id in project.GetAddedDocuments())
            {
                Document added = after.GetDocument(id)!;
                if (added.FilePath is not { } path || !seen.Add(path))
                {
                    continue;
                }
                SourceText text = await added.GetTextAsync(cancel).ConfigureAwait(false);
                files.Add(new FileChange(path, Created: true, [new Edit(new Place(0, 0), new Place(0, 0), text.ToString())]));
            }
        }
        return new Refactored(null, files);
    }

    private static Edit EditOf(SourceText text, TextChange change)
    {
        LinePositionSpan span = text.Lines.GetLinePositionSpan(change.Span);
        return new Edit(PlaceOf(span.Start), PlaceOf(span.End), change.NewText ?? string.Empty);
    }

    /// <summary>The type declared at an offset of a file: the one the caret names, else the innermost one around it.</summary>
    public static async Task<(INamedTypeSymbol? Type, BaseTypeDeclarationSyntax? Declaration)> TypeAtAsync(Document document, int offset, CancellationToken cancel)
    {
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
        SemanticModel model = (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false))!;
        SyntaxToken token = TokenAt(root, offset);
        foreach (BaseTypeDeclarationSyntax declaration in token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>() ?? [])
        {
            if (model.GetDeclaredSymbol(declaration, cancel) is INamedTypeSymbol type)
            {
                return (type, declaration);
            }
        }
        return (null, null);
    }

    /// <summary>A new file ending in a line break, as a file of code does; the edit's line breaks are fitted to the file's.</summary>
    public static async Task<Document> EndingInLineBreakAsync(Document document, CancellationToken cancel)
    {
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        return text.Length == 0 || text[text.Length - 1] == '\n'
            ? document
            : document.WithText(text.WithChanges(new TextChange(new TextSpan(text.Length, 0), "\n")));
    }

    /// <summary>The token at an offset, the name just before it when the caret ends one ("Ord|" in "Ord;").</summary>
    public static SyntaxToken TokenAt(SyntaxNode root, int offset)
    {
        SyntaxToken token = root.FindToken(offset);
        if (offset > 0 && !token.IsKind(SyntaxKind.IdentifierToken) && root.FindToken(offset - 1) is { } previous
            && previous.IsKind(SyntaxKind.IdentifierToken) && previous.Span.End == offset)
        {
            return previous;
        }
        return token;
    }

    /// <summary>A name a C# type or member can have.</summary>
    public static bool IsName(string name) => SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    /// <summary>A node led by as many blank lines as another is, as a member that takes another's place is.</summary>
    public static T WithBlankLinesOf<T>(T node, SyntaxNode model) where T : SyntaxNode
    {
        SyntaxTriviaList leading = model.GetLeadingTrivia();
        int blank = leading.Count - WithoutLeadingBlankLines(model).GetLeadingTrivia().Count;
        return node.WithLeadingTrivia(WithoutLeadingBlankLines(node).GetLeadingTrivia().InsertRange(0, leading.Take(blank)));
    }

    /// <summary>Leading blank lines taken off a node, for one that comes first in what holds it once something before it
    /// went.</summary>
    public static T WithoutLeadingBlankLines<T>(T node) where T : SyntaxNode
    {
        SyntaxTriviaList leading = node.GetLeadingTrivia();
        int skip = 0;
        int i = 0;
        while (i < leading.Count)
        {
            int end = i;
            while (end < leading.Count && leading[end].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                end++;
            }
            if (end < leading.Count && leading[end].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                skip = end + 1;
                i = end + 1;
                continue;
            }
            break;
        }
        return skip == 0 ? node : node.WithLeadingTrivia(leading.Skip(skip));
    }
}
