using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Languages.TextMate;
using Passenger.Text;

namespace Passenger.Languages;

/// <summary>The colours and folds a language has before any language server answers: C#'s own parse, a TextMate
/// grammar's for the files beside it, a scan of XML's tags or JSON's brackets, or none.</summary>
public static class Highlighters
{
    public static IHighlighter? For(Language language, TextDocument document) =>
        language.Parsed ? new CSharpHighlighter(document)
        : language.Grammar is { } grammar ? TextMateHighlighter.For(grammar, document)
        : null;

    /// <summary>What finds a file's folds: the parse that colours it where that finds them, else its language's scan; null
    /// for a language with neither.</summary>
    public static IFoldFinder? FoldsFor(Language language, TextDocument document, IHighlighter? colours) =>
        colours as IFoldFinder ?? language.Folds?.Invoke(document);
}
