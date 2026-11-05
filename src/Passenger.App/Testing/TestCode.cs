using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Workspace.GDScript;
using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.App.Testing;

/// <summary>A test's code as a number that moves with what it says and stays with how it is laid out, by which a result
/// is known to be of the code there now: FNV-1a, the same in every process, unlike <see cref="HashCode"/>. A token goes
/// in whole, a literal's whitespace being what it says, each set apart from the next; a comment goes in without its
/// whitespace, and what else lies between tokens not at all.</summary>
public static class TestCode
{
    /// <summary>A C# method's code, from its attributes to its end, as Roslyn's tokens give it.</summary>
    public static ulong Of(MethodDeclarationSyntax method)
    {
        Hash hash = new();
        SyntaxToken first = method.GetFirstToken();
        SyntaxToken last = method.GetLastToken();
        foreach (SyntaxToken token in method.DescendantTokens())
        {
            if (token != first)
            {
                Between(ref hash, token.LeadingTrivia);
            }
            hash.Token(token.Text);
            if (token != last)
            {
                Between(ref hash, token.TrailingTrivia);
            }
        }
        return hash.Value;
    }

    private static void Between(ref Hash hash, SyntaxTriviaList trivia)
    {
        foreach (SyntaxTrivia said in trivia)
        {
            if (!said.IsKind(SyntaxKind.WhitespaceTrivia) && !said.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                hash.Comment(said.ToFullString());
            }
        }
    }

    /// <summary>A GDScript function's code, as Godot's tokens give it: its indentation as the blocks it makes, whatever it
    /// is written with, and lines inside brackets run on as Godot reads them, but for a lambda's there, whose blocks are
    /// only their indentation.</summary>
    public static ulong OfScript(string function)
    {
        string code = function.Replace("\r\n", "\n", StringComparison.Ordinal);
        GDScriptTokenizer tokens = new(code);
        Hash hash = new();
        int read = 0;
        int depth = 0;
        int lambda = 0;
        while (true)
        {
            tokens.Multiline = depth > 0;
            GDScriptToken token = tokens.Scan();
            if (token.Kind is K.Newline or K.Indent or K.Dedent)
            {
                hash.Mark((char)token.Kind);
                continue;
            }
            if (token.Start > read)
            {
                hash.Comment(code.AsSpan(read, token.Start - read));
            }
            if (token.Kind == K.Eof)
            {
                return hash.Value;
            }
            if (lambda > 0 && LineIndent(code, token.Start) is >= 0 and int indent)
            {
                hash.Mark((char)indent);
            }
            int end = Math.Min(token.End, code.Length);
            hash.Token(code.AsSpan(token.Start, end - token.Start));
            read = Math.Max(read, end);
            depth = Math.Max(0, depth + token.Kind switch
            {
                K.ParenthesisOpen or K.BracketOpen or K.BraceOpen => 1,
                K.ParenthesisClose or K.BracketClose or K.BraceClose => -1,
                _ => 0,
            });
            lambda = depth < lambda ? 0 : token.Kind == K.Func && depth > 0 && lambda == 0 ? depth : lambda;
        }
    }

    /// <summary>How far a token is indented where it begins its line; -1 where it does not.</summary>
    private static int LineIndent(string code, int token)
    {
        int start = token;
        while (start > 0 && code[start - 1] is ' ' or '\t')
        {
            start--;
        }
        return start == 0 || code[start - 1] == '\n' ? token - start : -1;
    }

    private struct Hash()
    {
        public ulong Value { get; private set; } = 14695981039346656037;

        public void Mark(char mark) => Value = (Value ^ mark) * 1099511628211;

        public void Token(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
            {
                Mark(c);
            }
            Mark('\0');
        }

        public void Comment(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c))
                {
                    Mark(c);
                }
            }
        }
    }
}
