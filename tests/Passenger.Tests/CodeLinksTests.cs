using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Addresses written in a file's comments and strings, as a person meets them in the editor: each is underlined as a
/// link, found by the language's own reading of what is a comment or a string, and Go to Declaration's gesture opens it in
/// the browser, where a plain click only places the caret; the hover card says where it leads.</summary>
public sealed class CodeLinksTests : IDisposable
{
    private const string Issue = "https://github.com/Zaneris/passenger/issues/1903";
    private const string Docs = "https://learn.microsoft.com/dotnet/";
    private const string Wiki = "https://en.wikipedia.org/wiki/C_(programming_language)";

    private readonly string folder = TestFolders.Create("code-links-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Lines from zero: a comment's address, a comment of words alone, a string's address, an address in code the
    /// build leaves out, file addresses, one on this machine and one on a share, an Aspire service's address, which service
    /// discovery reads and no browser opens, and an address holding parentheses inside a pair that wraps it.</summary>
    private string Code => string.Join('\n',
        $"// See {Issue} for why.",                                     // 0
        "class Links",
        "{",
        "    // No address on this line, only words: www and http.",  // 3
        $"    const string Docs = \"{Docs}\";",                         // 4
        "#if NEVER",
        "    https://example.com/never",                                // 6
        "#endif",
        $"    // Kept in {new Uri(Path.Combine(folder, "Links.cs")).AbsoluteUri}, never file://server/share/Links.cs.", // 8
        "    const string Api = \"https+http://apiservice\";",         // 9
        $"    // The language (see {Wiki}).",                           // 10
        "}",
        "");

    private MainWindow Open(out CodeEditor editor)
    {
        File.WriteAllText(Path.Combine(folder, "Links.cs"), Code);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("Links.cs");
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        return window;
    }

    [AvaloniaFact]
    public void An_address_in_a_comment_or_a_string_is_a_link_and_none_is_found_in_words_alone_code_left_out_or_a_services_address()
    {
        MainWindow window = Open(out CodeEditor editor);
        try
        {
            TextSnapshot text = editor.Document.Snapshot;
            Assert.Equal((new TextRange(Place(text, 0, Issue), Issue.Length), new Uri(Issue)), Linked(editor, Place(text, 0, Issue) + 3));
            Assert.Equal((new TextRange(Place(text, 4, Docs), Docs.Length), new Uri(Docs)), Linked(editor, Place(text, 4, Docs) + 10));
            foreach (int line in (int[])[1, 3, 6, 9])
            {
                LineSpan span = text.GetLine(line);
                for (int offset = span.Start; offset < span.End; offset++)
                {
                    Assert.True(editor.LinkOn(offset) is null, $"Line {line + 1}, '{text.GetLineText(line)}', has a link at column {offset - span.Start + 1}");
                }
            }
            string local = Path.Combine(folder, "Links.cs");
            int file = Place(text, 8, "file:///");
            Assert.True(editor.LinkOn(file + 2)?.Target is ProgramFileLink { Path: { } path } && PathNames.Same(path, local),
                $"The comment's file address of {local} is no link to it: {editor.LinkOn(file + 2)?.Target}");
            Assert.True(editor.LinkOn(Place(text, 8, "file://server") + 2) is null, "A share's file address is a link, which drawing it would reach");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_declarations_gesture_on_a_comments_address_opens_it_in_the_browser_where_a_plain_click_places_the_caret()
    {
        MainWindow window = Open(out CodeEditor editor);
        try
        {
            TextSnapshot text = editor.Document.Snapshot;
            int at = Place(text, 0, Issue) + 8;
            Point on = editor.PointOf(at) + new Point(1, editor.LineHeight / 2);
            Ui.Click(editor, on);
            Assert.True(window.Browsed.Count == 0, $"A plain click on a comment's address opened {string.Join(", ", window.Browsed)}");
            Assert.True(editor.Selections.Primary.Caret == at, $"A plain click on a comment's address left the caret at {editor.Selections.Primary.Caret}, not {at}");

            Ui.Click(editor, on, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
            Assert.True(window.Browsed.SequenceEqual([new Uri(Issue)]), $"Go to Declaration's click on a comment's address opened [{string.Join(", ", window.Browsed)}]");

            editor.Selections = SelectionSet.Single(Place(text, 4, Docs) + 12);
            Ui.Settle();
            Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
            Assert.True(window.Browsed.SequenceEqual([new Uri(Issue), new Uri(Docs)]), $"Go to Declaration's keys on a string's address opened [{string.Join(", ", window.Browsed)}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An address holding parentheses is linked whole, the ')' that wraps it and the sentence's '.' left out, and
    /// Go to Declaration's click on its parentheses opens the whole address.</summary>
    [AvaloniaFact]
    public void An_address_holding_parentheses_is_linked_and_opened_whole_without_the_pair_that_wraps_it()
    {
        MainWindow window = Open(out CodeEditor editor);
        try
        {
            TextSnapshot text = editor.Document.Snapshot;
            int start = Place(text, 10, Wiki);
            int inside = Place(text, 10, "programming") + 2;
            Assert.Equal((new TextRange(start, Wiki.Length), new Uri(Wiki)), Linked(editor, inside));
            Assert.True(editor.LinkOn(start + Wiki.Length) is null, "The ')' that wraps the comment's address is part of its link");

            Point on = editor.PointOf(inside) + new Point(1, editor.LineHeight / 2);
            Ui.Click(editor, on, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
            Assert.True(window.Browsed.SequenceEqual([new Uri(Wiki)]), $"Go to Declaration's click on the address's parentheses opened [{string.Join(", ", window.Browsed)}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The card the pointer's rest shows says the address and the keys that open it; with the gesture held the
    /// address takes the hand.</summary>
    [AvaloniaFact]
    public void A_comments_address_says_where_it_leads_when_the_pointer_rests_on_it_and_takes_the_hand_with_the_gesture_held()
    {
        MainWindow window = Open(out CodeEditor editor);
        try
        {
            TextSnapshot text = editor.Document.Snapshot;
            int start = Place(text, 0, Issue);
            Point on = editor.PointOf(start + 8) + new Point(1, editor.LineHeight / 2);
            Ui.Hover(editor, on);
            Ui.WaitFor(() => window.Assist.HoverText is not null, "the card of a comment's address");
            Assert.Contains(Issue, window.Assist.HoverText, StringComparison.Ordinal);
            Assert.Contains(window.Actions.ShortcutsShown(AppActions.GoToDeclaration)!, window.Assist.HoverText, StringComparison.Ordinal);
            Assert.False(editor.ShowsHand, "A comment's address took the hand with no key held, where a click only places the caret");

            Ui.Hover(editor, on + new Point(2, 0), OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
            Ui.WaitFor(() => editor.PointedRange is not null, "the held gesture to reach the comment's address");
            Assert.True(editor.PointedRange == new TextRange(start, Issue.Length) && editor.ShowsHand,
                $"With the gesture held a comment's address reached {editor.PointedRange} and the hand is {(editor.ShowsHand ? "shown" : "not shown")}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The offset where some text first stands on a line (from zero).</summary>
    private static int Place(TextSnapshot text, int line, string what)
    {
        int column = text.GetLineText(line).IndexOf(what, StringComparison.Ordinal);
        Assert.True(column >= 0, $"'{what}' is not on line {line + 1}");
        return text.GetLineStart(line) + column;
    }

    /// <summary>The link at an offset and the web address it opens.</summary>
    private static (TextRange, Uri?) Linked(CodeEditor editor, int offset) =>
        editor.LinkOn(offset) is { } link ? (link.Range, (link.Target as WebLink)?.Address) : default;
}
