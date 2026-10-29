using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>Breakpoints inside a C# lambda or anonymous method whose body begins on a line after the line's code does: a
/// breakpoint on the line stops at the statement the line begins, and one on such a lambda (its column, where its body
/// begins) stops each time the lambda runs. A line's lambdas are read from its file's parse.</summary>
public static class LambdaBreakpoints
{
    /// <summary>A lambda a breakpoint can stop inside: its text's range and where its body begins.</summary>
    public readonly record struct Lambda(TextRange Range, int Body);

    /// <summary>The longest a lambda's name runs before it is cut short.</summary>
    private const int LongestName = 48;

    /// <summary>A C# file's parse: an open file's as it stands, else the disk's, read and parsed off the window's thread as
    /// its project compiles it; null for a file that is not C# or cannot be read.</summary>
    public static async Task<ParsedText?> ParsedAsync(EditorArea editors, string path)
    {
        UiThread.Check();
        if (editors.Find(path) is { } open)
        {
            return open.Syntax?.Current;
        }
        if (Language.ForPath(path) != Language.CSharp)
        {
            return null;
        }
        CSharpParseOptions options = editors.ParseOptions(path);
        ParsedText? read = await BackgroundWork.Run(() => EditorArea.ReadText(path) is { } text
            ? new ParsedText(CSharpSyntaxTree.ParseText(text, options), TextSnapshot.From(text))
            : null);
        // Opened as it was read, its text is the editor's.
        return editors.Find(path) is { } opened ? opened.Syntax?.Current : read;
    }

    /// <summary>The lambdas whose bodies begin on a line (from zero) after its code begins, in the order they begin.</summary>
    public static IReadOnlyList<Lambda> On(ParsedText parsed, int line)
    {
        TextSnapshot text = parsed.Text;
        if (line < 0 || line >= text.LineCount)
        {
            return [];
        }
        LineSpan span = text.GetLine(line);
        int code = TextNavigation.IndentEnd(text, span);
        List<Lambda> found = [];
        foreach (SyntaxNode node in parsed.Root.DescendantNodes(TextSpan.FromBounds(span.Start, span.End)))
        {
            if (node is AnonymousFunctionExpressionSyntax lambda && lambda.Body.SpanStart > code && lambda.Body.SpanStart < span.End)
            {
                found.Add(new Lambda(TextRange.FromBounds(lambda.SpanStart, lambda.Span.End), lambda.Body.SpanStart));
            }
        }
        found.Sort((a, b) => a.Range.Start.CompareTo(b.Range.Start));
        return found;
    }

    /// <summary>The innermost of the lambdas on an offset's line (<see cref="On"/>) holding it, from its first character to
    /// just after its last; null where none does.</summary>
    public static Lambda? At(ParsedText parsed, int offset)
    {
        Lambda? innermost = null;
        foreach (Lambda lambda in On(parsed, parsed.Text.GetLineFromOffset(offset)))
        {
            if (lambda.Range.Start <= offset && offset <= lambda.Range.End && (innermost is not { } outer || lambda.Range.Length < outer.Range.Length))
            {
                innermost = lambda;
            }
        }
        return innermost;
    }

    /// <summary>The lambda a breakpoint at a column (from zero) of a line is inside, where the edits since it was set have
    /// carried it; null once none of the line's holds it.</summary>
    public static Lambda? Of(ParsedText parsed, int line, int column)
    {
        if (line < 0 || line >= parsed.Text.LineCount)
        {
            return null;
        }
        LineSpan span = parsed.Text.GetLine(line);
        return column <= span.Length ? At(parsed, span.Start + column) : null;
    }

    /// <summary>Whether the lambda a breakpoint at a column (from zero) of a line was set inside is gone: none of the line's
    /// holds it, and the code around it reads whole, so no comment or string left open as it is typed hides one still
    /// written there.</summary>
    public static bool Gone(ParsedText parsed, int line, int column)
    {
        if (Of(parsed, line, column) is not null)
        {
            return false;
        }
        SyntaxNode root = parsed.Root;
        int offset = line < parsed.Text.LineCount ? parsed.Text.GetLineStart(line) + Math.Min(column, parsed.Text.GetLine(line).Length) : root.FullSpan.End;
        SyntaxNode around = offset < root.FullSpan.End && root.FindToken(offset).Parent is { } parent
            ? parent.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax) ?? root
            : root;
        return !around.ContainsDiagnostics;
    }

    /// <summary>The breakpoint set inside a lambda, among a file's.</summary>
    public static LineBreakpoint? SetOn(IEnumerable<LineBreakpoint> breakpoints, ParsedText parsed, Lambda lambda) =>
        breakpoints.FirstOrDefault(b => b.Column is int column && Of(parsed, b.Line, column) == lambda);

    /// <summary>The part of a lambda's text on the line its body begins on, as a menu names it: its runs of spaces made one,
    /// and cut short with "…" where it is long or goes on below.</summary>
    public static string Named(TextSnapshot text, Lambda lambda)
    {
        LineSpan span = text.GetLine(text.GetLineFromOffset(lambda.Body));
        int start = Math.Max(lambda.Range.Start, span.Start);
        int end = Math.Min(lambda.Range.End, span.End);
        StringBuilder name = new();
        foreach (char c in text.GetText(start, end - start))
        {
            if (!char.IsWhiteSpace(c))
            {
                name.Append(c);
            }
            else if (name.Length > 0 && name[^1] != ' ')
            {
                name.Append(' ');
            }
        }
        string shown = name.ToString().TrimEnd();
        return shown.Length > LongestName ? $"{shown[..(LongestName - 1)].TrimEnd()}…" : end < lambda.Range.End ? $"{shown}…" : shown;
    }

    /// <summary>A lambda's range as its breakpoint tints it: the part on the line its body begins on.</summary>
    public static TextRange Tinted(TextSnapshot text, Lambda lambda)
    {
        LineSpan span = text.GetLine(text.GetLineFromOffset(lambda.Body));
        return TextRange.FromBounds(Math.Max(lambda.Range.Start, span.Start), Math.Min(lambda.Range.End, span.End));
    }
}
