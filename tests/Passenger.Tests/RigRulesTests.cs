using System.Text.RegularExpressions;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>How a test points at the window, each the one way the rig gives (Rig.cs), read from the tests' code as written.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class RigRulesTests
{
    /// <summary>A test rests the pointer on an editor's text only through Ui.RestOn, which brings the text into view and aims
    /// at it: a point worked out from the editor's PointOf and moved to by hand rests on whatever lies there, past the
    /// editor's foot once rows above lines push the text down, so the card it waits for never comes. A drag (a button
    /// held) and a terminal's cell (PointOf of a line and a column) are passed over.</summary>
    [Fact]
    public void A_test_rests_the_pointer_on_an_editors_text_only_through_Ui_RestOn()
    {
        Regex placed = new(@"(?:Point|var)\s+(\w+)\s*=");
        Regex moved = new(@"MouseMove\(\s*(\w+)\s*[,)]");
        List<string> found = [];
        int read = 0;
        string folder = Path.Combine(Debuggees.Root, "tests", "Passenger.Tests");
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(Debuggees.Root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.EndsWith("/Rig.cs", StringComparison.Ordinal))
            {
                continue;
            }
            read++;
            string text = File.ReadAllText(file);
            Dictionary<string, int> fromText = [];
            int at = 0;
            foreach (string statement in text.Split(';'))
            {
                int line = text.AsSpan(0, at).Count('\n') + 1;
                at += statement.Length + 1;
                if (statement.Contains("Button", StringComparison.Ordinal))
                {
                    continue;
                }
                bool ofText = OfText(statement);
                if (ofText && (statement.Contains("MouseMove(", StringComparison.Ordinal) || statement.Contains("Ui.Hover(", StringComparison.Ordinal)))
                {
                    found.Add($"{relative}:{line + Leading(statement)}");
                }
                else if (placed.Match(statement) is { Success: true } point)
                {
                    // A point worked out from the text is followed to a move; one worked out otherwise ends that.
                    if (ofText)
                    {
                        fromText[point.Groups[1].Value] = line;
                    }
                    else
                    {
                        fromText.Remove(point.Groups[1].Value);
                    }
                }
                else if (moved.Match(statement) is { Success: true } move && fromText.Remove(move.Groups[1].Value))
                {
                    found.Add($"{relative}:{line + Leading(statement)}");
                }
            }
        }
        Assert.True(read > 300, $"Only {read} files of the tests were read: the rule no longer finds them");
        Assert.True(found.Count == 0, "A test rests the pointer on an editor's text at a point it worked out by hand, which rests on whatever lies "
            + $"there once the text is out of view: rest on it through Ui.RestOn(editor, offset):\n{string.Join('\n', found)}");

        // An editor's PointOf of one offset (a terminal's takes a line and a column).
        static bool OfText(string statement)
        {
            int call = statement.IndexOf(".PointOf(", StringComparison.Ordinal);
            if (call < 0)
            {
                return false;
            }
            int depth = 0;
            for (int i = call + ".PointOf(".Length; i < statement.Length; i++)
            {
                switch (statement[i])
                {
                    case '(':
                        depth++;
                        break;
                    case ')' when depth == 0:
                        return true;
                    case ')':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        return false;
                }
            }
            return false;
        }

        // The lines of white space and comments a statement begins with, before its code.
        static int Leading(string statement) => statement.AsSpan(0, statement.Length - statement.TrimStart().Length).Count('\n');
    }
}
