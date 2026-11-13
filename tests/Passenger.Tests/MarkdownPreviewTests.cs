using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Navigation;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>A Markdown file's live preview beside its editor: each kind of block a README has, GitHub's tables and task
/// lists, fenced code coloured as the editor colours it, the images of files inside the open folder and none from outside
/// it; following the typing and the editor's scroll, the editor following the wheel over it; its links opening the
/// browser, another file or a heading; the choice to show it kept for the next window; and the file's headings as its
/// outline in Structure and File Structure.</summary>
public sealed class MarkdownPreviewTests
{
    /// <summary>A shop folder with a README of every kind of block, an image inside it and one beside it, outside.</summary>
    private static (string Folder, string Readme) Shop()
    {
        string root = TestFolders.Create("markdown-");
        string folder = Path.Combine(root, "shop");
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        File.WriteAllBytes(Path.Combine(folder, "images", "inside.png"), Png(64, 32));
        File.WriteAllBytes(Path.Combine(root, "secret.png"), Png(40, 40));
        File.WriteAllText(Path.Combine(folder, "docs", "guide.md"), "# Guide\n\nThe guide.\n");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "Console.WriteLine(\"Hello\");\n");
        StringBuilder readme = new("""
            # Shop

            A shop with **strong words**, *emphasis* and `code spans`. See the [website](https://example.com/shop), the [guide](docs/guide.md) or [the prices](#prices).

            ![the inside picture](images/inside.png)

            ## Getting started

            1. Clone it
            2. Build it
               - with `dotnet build`
               - or with **just**
            3. Run it

            ### Tasks

            - [x] Write the shop
            - [ ] Ship the shop

            #### Prices

            | Item | Kind | Price |
            | :--- | :---: | ---: |
            | Apple | fruit | 1.20 |
            | Bread | bakery | 3.50 |

            ##### A quote

            > Shops sell things.

            ###### Code

            ```csharp
            public sealed class Shop
            {
                public decimal Total(int count) => count * 1.20m;
            }
            ```

            ```json
            { "name": "shop", "open": true }
            ```

            ```python
            def total(count):
                return count * 1.2
            ```

            ---

            From elsewhere: ![a secret](../secret.png) and from the web ![a badge](https://example.com/badge.png).

            """);
        // Enough below the blocks for any of them to come to the preview's top.
        for (int section = 1; section <= 40; section++)
        {
            readme.Append($"## Section {section}\n\nWhat section {section} says, at some length so that it fills a line or two of the preview.\n\n");
        }
        string path = Path.Combine(folder, "README.md");
        File.WriteAllText(path, readme.ToString().ReplaceLineEndings("\n"));
        return (folder, PathNames.Canonical(path));
    }

    private static byte[] Png(int width, int height)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(SKColors.SteelBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [AvaloniaFact]
    public void A_README_shows_each_kind_of_block_beside_its_editor_follows_typing_and_scrolling_and_opens_its_links()
    {
        (string folder, string readme) = Shop();
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Assert.Null(window.OpenFile(readme));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        MarkdownPreview preview = Shown(group);
        Assert.True(group.Editor.IsEffectivelyVisible, "The README's editor went for its preview, which stands beside it");

        MarkdownText[] words = [.. preview.GetLogicalDescendants().OfType<MarkdownText>()];
        string[] headings = ["Shop", "Getting started", "Tasks", "Prices", "A quote", "Code"];
        string[] levels = [StyleClasses.MdHeading1, StyleClasses.MdHeading2, StyleClasses.MdHeading3, StyleClasses.MdHeading4, StyleClasses.MdHeading5, StyleClasses.MdHeading6];
        for (int level = 1; level <= 6; level++)
        {
            Assert.True(words.Any(w => w.Classes.Contains(levels[level - 1]) && w.Shown == headings[level - 1]),
                $"The preview draws no heading of level {level} saying '{headings[level - 1]}':\n{preview.Text}");
        }
        Assert.DoesNotContain("##", preview.Text, StringComparison.Ordinal);

        MarkdownText said = words.First(w => w.Shown.StartsWith("A shop with", StringComparison.Ordinal));
        Run[] runs = [.. said.Inlines!.OfType<Run>()];
        Assert.True(runs.Any(r => r.Text == "strong words" && r.FontWeight == FontWeight.Bold), "The strong words are not drawn bold");
        Assert.True(runs.Any(r => r.Text == "emphasis" && r.FontStyle == FontStyle.Italic), "The emphasis is not drawn in italics");
        Assert.True(runs.Any(r => r.Text == "code spans" && r.FontFamily == CodeEditor.CodeFont), "The code span is not drawn in the editor's font");

        Assert.True(preview.GetLogicalDescendants().OfType<StackPanel>().Any(l => l.Classes.Contains(StyleClasses.MdList)
            && l.GetLogicalDescendants().OfType<StackPanel>().Any(inner => inner.Classes.Contains(StyleClasses.MdList))), "The list inside a list is not drawn as one");
        Border[] tasks = [.. preview.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.MdTask))];
        Assert.True(tasks.Length == 2 && tasks[0].Classes.Contains(StyleClasses.Done) && !tasks[1].Classes.Contains(StyleClasses.Done),
            $"The task list draws {tasks.Length} checks, done [{string.Join(", ", tasks.Select(t => t.Classes.Contains(StyleClasses.Done)))}], not one done and one to do");
        Assert.True(words.Any(w => w.Shown == "Ship the shop"), "A task's words still show its [ ] mark");

        Border[] cells = [.. preview.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.MdCell))];
        Assert.True(cells.Length == 9 && cells.Take(3).All(c => c.Classes.Contains(StyleClasses.Head)), $"The table draws {cells.Length} cells, not its header's 3 and two rows' 6");
        Assert.Equal(TextAlignment.Center, Cell(cells, "fruit").TextAlignment);
        Assert.Equal(TextAlignment.Right, Cell(cells, "3.50").TextAlignment);
        Assert.Equal(TextAlignment.Left, Cell(cells, "Apple").TextAlignment);

        Assert.True(preview.GetLogicalDescendants().OfType<Border>().Any(b => b.Classes.Contains(StyleClasses.MdQuote)
            && b.GetLogicalDescendants().OfType<MarkdownText>().Any(t => t.Shown == "Shops sell things.")), "The block quote is not drawn as one");
        Assert.Contains(preview.GetLogicalDescendants().OfType<Border>(), b => b.Classes.Contains(StyleClasses.MdRule));

        MarkdownCode[] code = [.. preview.GetLogicalDescendants().OfType<MarkdownCode>()];
        Assert.True(code.Select(c => c.Language).SequenceEqual(["C#", "JSON", "Python"]), $"The fences are coloured as [{string.Join(", ", code.Select(c => c.Language))}], not C#, JSON and Python");
        IBrush keyword = Brush(window, TokenKeys.Syntax_Keyword);
        IBrush text = Brush(window, TokenKeys.Syntax_String);
        Ui.WaitFor(() => code[0].Runs.Any(r => r.Text == "class" && r.Foreground == keyword), "the C# fence's 'class' in the editor's keyword colour");
        Ui.WaitFor(() => code[1].Runs.Any(r => r.Text?.Contains("shop", StringComparison.Ordinal) == true && r.Foreground == text), "the JSON fence's string in the editor's string colour");
        Ui.WaitFor(() => code[2].Runs.Any(r => r.Text == "def" && r.Foreground == keyword), "the Python fence's 'def' in the editor's keyword colour");

        MarkdownPicture picture = Assert.Single(preview.GetLogicalDescendants().OfType<MarkdownPicture>());
        Assert.True(PathNames.Same(picture.File, PathNames.Canonical(Path.Combine(folder, "images", "inside.png"))), $"The picture drawn is {picture.File}, not the folder's own");
        Ui.WaitFor(() => picture.Shown is { PixelSize: { Width: 64, Height: 32 } }, "the folder's own picture read and drawn");
        MarkdownText elsewhere = words.First(w => w.Shown.StartsWith("From elsewhere", StringComparison.Ordinal));
        Assert.True(elsewhere.Shown.Contains("a secret", StringComparison.Ordinal) && elsewhere.Shown.Contains("a badge", StringComparison.Ordinal),
            $"The pictures from outside the folder are not their words: '{elsewhere.Shown}'");

        Uri website = new("https://example.com/shop");
        Ui.Click(said, said.PointOf(website));
        Assert.True(window.Browsed.LastOrDefault() == website, $"A click on the website's link opened [{string.Join(", ", window.Browsed)}]");

        Ui.Click(said, said.PointOf(new Uri("#prices", UriKind.Relative)));
        Control prices = preview.Heading("prices") ?? throw new Xunit.Sdk.XunitException("The Prices heading has no anchor 'prices'");
        Assert.True(Math.Abs(preview.Offset.Y - preview.TopOf(prices)) < 1,
            $"The link to #prices left the preview at {preview.Offset.Y:0}, not its heading's {preview.TopOf(prices):0}");
        CodeEditor editor = group.Editor;
        Assert.True(editor.Document.Snapshot.GetLineText(editor.LinesShown.First) == "#### Prices",
            $"The link to #prices left the editor's top line at '{editor.Document.Snapshot.GetLineText(editor.LinesShown.First)}', not its heading");

        int line = editor.Document.Snapshot.ToString().Split('\n').ToList().IndexOf("## Section 12");
        Scroll(editor, line);
        Assert.Equal(line, editor.LinesShown.First);
        Control twelve = preview.BlockAt(line) ?? throw new Xunit.Sdk.XunitException($"No block draws line {line}");
        Assert.True(Math.Abs(preview.Offset.Y - preview.TopOf(twelve)) < 1,
            $"With the editor's top at Section 12 the preview stands at {preview.Offset.Y:0}, not that heading's {preview.TopOf(twelve):0}");
        Scroll(editor, 0);
        Assert.Equal(0, preview.Offset.Y);

        Control[] before = [.. preview.Blocks];
        editor.Focus();
        Ui.PlaceAfter(editor, "inside.png)\n\n");
        window.KeyTextInput("## Typed heading");
        Ui.Press(window, "ENTER");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.True(preview.GetLogicalDescendants().OfType<MarkdownText>().Any(w => w.Classes.Contains(StyleClasses.MdHeading2) && w.Shown == "Typed heading"),
            $"The heading typed does not show in the preview:\n{preview.Text}");
        Control[] made = [.. preview.Blocks.Where(b => !before.Contains(b))];
        Assert.True(made.Length == 1 && before.All(preview.Blocks.Contains),
            $"A heading typed made {made.Length} blocks' controls and kept {before.Count(preview.Blocks.Contains)} of {before.Length}, not one and all");

        Ui.Click(said, said.PointOf(new Uri("docs/guide.md", UriKind.Relative)));
        Assert.True(PathNames.Same(window.EditorArea.Active?.Path ?? "", PathNames.Canonical(Path.Combine(folder, "docs", "guide.md"))),
            $"The guide's link opened {window.EditorArea.Active?.Path ?? "nothing"}");
        window.Close();
    }

    /// <summary>A '\r' standing alone in a Markdown file with '\n' line ends (one pasted in from an old Mac file) ends a line
    /// for the preview and not for the document: the headings after it are still in Structure, nested by level, beside the
    /// preview, and each goes to its own line in the editor, as the document counts its lines.</summary>
    [AvaloniaFact]
    public void Headings_after_a_carriage_return_standing_alone_are_in_Structure_and_go_to_their_lines()
    {
        string folder = TestFolders.Create("markdown-return-");
        File.WriteAllText(Path.Combine(folder, "NOTES.md"), "# One\rwritten on an old Mac\n\n## Two\n\nWords.\n\n# Three\n");
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Assert.Null(window.OpenFile("NOTES.md"));
        Ui.Settle();
        Ui.Run(window, AppActions.ShowStructure);
        StructureView structure = window.StructureView;
        Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "Three"),
            () => $"the headings in Structure: {string.Join(", ", structure.Rows.Select(r => r.Name))} ({structure.Said})");
        Assert.Equal([("One", 0), ("Two", 1), ("Three", 0)], structure.Rows.Select(r => (r.Name, r.Depth)));
        Assert.True(Shown(window.EditorArea.ActiveGroup).GetLogicalDescendants().OfType<MarkdownText>().Any(t => t.Shown == "Three"), "The preview does not show the last heading");

        CodeEditor editor = window.EditorArea.CodeEditor;
        ListBox list = structure.GetVisualDescendants().OfType<ListBox>().First();
        foreach ((string name, string written) in (ReadOnlySpan<(string, string)>)[("Three", "# Three"), ("Two", "## Two")])
        {
            Ui.Click(Ui.Row(list, structure.Rows.First(r => r.Name == name)));
            Ui.WaitFor(() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line) == written,
                () => $"the row of {name} to go to its line, not to line {editor.CaretPosition.Line}, '{editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}'");
        }
        window.Close();
    }

    /// <summary>A page in a folder of its own links to the guide and draws a picture from the open folder's root, a slash
    /// first, as GitHub resolves them: on macOS and Linux too, where such a target also reads as a file's address.</summary>
    /// <summary>What is written deeper than a page reads is its words, and however deep a file goes the window stays.</summary>
    [AvaloniaFact]
    public void A_file_nested_past_what_a_page_reads_shows_the_rest_as_words_and_ends_nothing()
    {
        string folder = TestFolders.Create("markdown-deep-");
        string path = Path.Combine(folder, "deep.md");
        string quotes = new('>', 100_000);
        string lists = string.Concat(Enumerable.Repeat("- ", 100_000));
        string links = new string('[', 100_000) + "linked" + string.Concat(Enumerable.Repeat("](b)", 100_000));
        File.WriteAllText(path, $"{quotes} quoted\n\n{lists}listed\n\n{links}\n");
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Assert.Null(window.OpenFile(PathNames.Canonical(path)));
        Ui.Settle();
        MarkdownPreview preview = Shown(window.EditorArea.ActiveGroup);
        foreach (string written in (string[])["quoted", "listed", "linked"])
        {
            Assert.Contains(written, preview.Text, StringComparison.Ordinal);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void A_READMEs_headings_nest_by_level_in_Structure_and_File_Structure_follow_its_typing_and_go_to_their_lines()
    {
        (string folder, string readme) = Shop();
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Assert.Null(window.OpenFile(readme));
        Ui.Settle();
        Ui.Run(window, AppActions.ShowStructure);
        StructureView structure = window.StructureView;
        Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "Section 40"), () => $"the README's headings in Structure: {Listed(structure.Rows)} ({structure.Said})");
        (string, int)[] nested = [("Shop", 0), ("Getting started", 1), ("Tasks", 2), ("Prices", 3), ("A quote", 4), ("Code", 5), ("Section 1", 1)];
        Assert.Equal(nested, structure.Rows.Take(7).Select(r => (r.Name, r.Depth)));
        Assert.Equal(46, structure.Rows.Count);

        CodeEditor editor = window.EditorArea.CodeEditor;
        // Escape gives the keys back to the editor from the Structure window, which took them.
        Ui.Press(window, "ESCAPE");
        Assert.True(editor.IsKeyboardFocusWithin, "Escape in the Structure window left the keys away from the README's editor");
        Ui.PlaceAfter(editor, "- [ ] Ship the shop\n\n");
        window.KeyTextInput("### Shipping");
        Ui.Press(window, "ENTER");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "Shipping"), () => $"Structure to follow the heading typed: {Listed(structure.Rows)}");
        Assert.Equal([("Tasks", 2), ("Shipping", 2), ("Prices", 3)], structure.Rows.Skip(2).Take(3).Select(r => (r.Name, r.Depth)));

        ListBox list = structure.GetVisualDescendants().OfType<ListBox>().First();
        Ui.Click(Ui.Row(list, structure.Rows.First(r => r.Name == "A quote")));
        Ui.WaitFor(() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line) == "##### A quote",
            () => $"the heading's row to go to its line, not to '{editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}'");

        Ui.Run(window, AppActions.FileStructure);
        SymbolPopup popup = window.Popup as SymbolPopup
            ?? throw new Xunit.Sdk.XunitException($"File Structure on the README opened {window.Popup?.GetType().Name ?? "nothing"}; the status bar said: {window.Status}");
        Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Name == "Section 12"), () => $"the README's headings in File Structure: {Listed(popup.Rows)}");
        window.KeyTextInput("Section 12");
        Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any() && popup.Rows[^1].Name == "Section 12", () => $"File Structure filtered to Section 12, not [{Listed(popup.Rows)}]");
        if (popup.Selected?.Name == "Shop")
        {
            // The heading it is under comes first, for where the match is.
            Ui.Press(window, "DOWN");
        }
        Assert.Equal("Section 12", popup.Selected?.Name);
        Ui.Press(window, "ENTER");
        Assert.Equal("## Section 12", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line));
        window.Close();

        static string Listed(IEnumerable<SymbolRow> rows) => string.Join(", ", rows.Select(r => $"{new string(' ', r.Depth)}{r.Name}"));
    }

    [AvaloniaFact]
    public void A_link_and_an_image_written_from_the_folders_root_open_and_draw_the_root_folders_files_on_every_system()
    {
        (string folder, _) = Shop();
        string notes = Path.Combine(folder, "notes", "README.md");
        Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
        File.WriteAllText(notes, "# Notes\n\nRead [the guide](/docs/guide.md) first.\n\n![the inside picture](/images/inside.png)\n");
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Assert.Null(window.OpenFile(PathNames.Canonical(notes)));
        Ui.Settle();
        MarkdownPreview preview = Shown(window.EditorArea.ActiveGroup);

        MarkdownText said = preview.GetLogicalDescendants().OfType<MarkdownText>().First(w => w.Shown.StartsWith("Read", StringComparison.Ordinal));
        Uri guide = new("/docs/guide.md", UriKind.Relative);
        Assert.True(said.Links.Any(l => l.Words == "the guide" && l.Target == guide),
            $"The link written from the folder's root is drawn as words, not a link: [{string.Join(", ", said.Links.Select(l => $"{l.Words} -> {l.Target}"))}]");
        MarkdownPicture picture = preview.GetLogicalDescendants().OfType<MarkdownPicture>().SingleOrDefault()
            ?? throw new Xunit.Sdk.XunitException($"The picture written from the folder's root is not drawn:\n{preview.Text}");
        Assert.True(PathNames.Same(picture.File, PathNames.Canonical(Path.Combine(folder, "images", "inside.png"))), $"The picture drawn is {picture.File}, not the root's images/inside.png");

        Ui.Click(said, said.PointOf(guide));
        Assert.True(PathNames.Same(window.EditorArea.Active?.Path ?? "", PathNames.Canonical(Path.Combine(folder, "docs", "guide.md"))),
            $"The link to /docs/guide.md opened {window.EditorArea.Active?.Path ?? "nothing"}, not the root's docs/guide.md");
        window.Close();
    }

    /// <summary>The README's page, kept with its tab, draws its picture again each time the tab comes back: from another
    /// Markdown file, whose page the preview showed meanwhile, and from a C# file, beside which it showed none.</summary>
    [AvaloniaFact]
    public void A_pages_picture_draws_again_each_time_its_tab_is_left_and_come_back_to()
    {
        (string folder, string readme) = Shop();
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        Assert.Null(window.OpenFile(readme));
        Ui.Settle();
        Pictured("as the README is first shown");
        foreach (string other in new[] { Path.Combine("docs", "guide.md"), "Program.cs", Path.Combine("docs", "guide.md") })
        {
            Assert.Null(window.OpenFile(PathNames.Canonical(Path.Combine(folder, other))));
            Ui.Settle();
            Assert.Null(window.OpenFile(readme));
            Ui.Settle();
            Pictured($"as the README comes back from {other}");
        }
        window.Close();

        void Pictured(string when)
        {
            MarkdownPicture picture = Assert.Single(Shown(window.EditorArea.ActiveGroup).GetLogicalDescendants().OfType<MarkdownPicture>());
            Ui.WaitFor(() => picture.Shown is { PixelSize: { Width: 64, Height: 32 } },
                $"the README's picture drawn {when} (its words stand in its place: the decode it asked for was let go)", TimeSpan.FromSeconds(10));
        }
    }

    [AvaloniaFact]
    public void The_toggle_hides_and_shows_the_preview_each_split_has_its_own_and_the_next_window_keeps_the_choice()
    {
        (string folder, string readme) = Shop();
        string state = TestFolders.Create("markdown-state-");
        MainWindow window = Started(state, folder);
        Assert.Null(window.OpenFile(readme));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        Shown(group);
        Assert.True(group.PreviewToggle.IsVisible && group.PreviewToggle.IsChecked == true, "The README's tab strip has no pressed preview toggle");

        WeakReference<MarkdownPreview> closed = SplitAndClose(window);
        // Avalonia's hold gesture keeps the pressed close button, and so the group, until a frame fires its timer.
        Ui.WaitFor(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !closed.TryGetTarget(out _);
        }, "the closed split's preview to be let go (still alive, it is held by the file it showed or the window)", TimeSpan.FromSeconds(10));
        group = Assert.Single(window.EditorArea.Groups);

        Ui.Click(group.PreviewToggle);
        Assert.True(group.Markdown is null && group.PreviewToggle.IsChecked == false, "The toggle left the preview shown");
        Assert.Null(window.OpenFile(Path.Combine(folder, "Program.cs")));
        Ui.Settle();
        Assert.False(group.PreviewToggle.IsVisible, "A C# file's tab strip shows the Markdown preview's toggle");
        window.Close();
        Ui.Settle();

        window = Started(state, folder);
        Assert.Null(window.OpenFile(readme));
        Ui.Settle();
        group = window.EditorArea.ActiveGroup;
        Assert.True(group.Markdown is null && group.PreviewToggle.IsChecked == false, "The next window showed the preview the last one hid");
        NativeMenu view = Ui.Submenu(window.MenuBar, "View");
        NativeMenuItem item = view.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Markdown Preview")
            ?? throw new Xunit.Sdk.XunitException($"View has no Markdown Preview: [{string.Join(", ", view.Items.OfType<NativeMenuItem>().Select(i => i.Header))}]");
        item.Command!.Execute(null);
        Ui.Settle();
        Shown(group);
        window.Close();
        Ui.Settle();

        window = Started(state, folder);
        Assert.Null(window.OpenFile(readme));
        Ui.Settle();
        Shown(window.EditorArea.ActiveGroup);
        window.Close();
    }

    /// <summary>A preview draws the blocks near its sight alone, so showing a 600 KB changelog's costs the window's thread
    /// about what a 60 KB one's does, and its page is kept with its tab: shown again deep in the file, it makes no block
    /// again.</summary>
    [AvaloniaFact]
    public void A_long_changelogs_preview_costs_the_window_what_a_short_ones_does_and_its_tab_shown_again_makes_no_block()
    {
        string folder = TestFolders.Create("markdown-long-");
        File.WriteAllText(Path.Combine(folder, "WARM.md"), Changelog(2_000));
        File.WriteAllText(Path.Combine(folder, "SHORT.md"), Changelog(60_000));
        File.WriteAllText(Path.Combine(folder, "LONG.md"), Changelog(600_000));
        MainWindow window = Ui.OpenFolder(folder, width: 1200, height: 800, languageServer: false);
        Assert.Null(window.OpenFile("WARM.md"));
        Ui.Settle();
        Assert.True(window.Perform(AppActions.MarkdownPreview));
        Previewed(window, "WARM.md");
        Assert.True(window.Perform(AppActions.MarkdownPreview));
        (long shortCost, int shortDrawn) = Previewed(window, "SHORT.md");
        Assert.True(window.Perform(AppActions.MarkdownPreview));
        (long longCost, int longDrawn) = Previewed(window, "LONG.md");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Turning the preview on allocated {shortCost >> 10} KB on the window's thread and drew {shortDrawn} blocks for 60 KB, {longCost >> 10} KB and {longDrawn} for 600 KB");
        Assert.True(longDrawn <= shortDrawn,
            $"The 600 KB changelog's preview drew {longDrawn} blocks where the 60 KB one's, of the same first page, drew {shortDrawn}: it draws blocks out of its sight");
        Assert.True(longCost < shortCost * 3 / 2,
            $"The preview of a 600 KB changelog allocated {longCost >> 10} KB on the window's thread, of a 60 KB one {shortCost >> 10} KB: the window pays for what is off screen");

        EditorGroup group = window.EditorArea.ActiveGroup;
        MarkdownPreview preview = Shown(group);
        CodeEditor editor = group.Editor;
        int line = editor.Document.Snapshot.ToString().Split('\n').ToList().FindIndex(l => l.StartsWith("## [7.0.0]", StringComparison.Ordinal));
        Scroll(editor, line);
        Control seven = preview.BlockAt(line) ?? throw new Xunit.Sdk.XunitException($"With the editor's top at line {line} the preview draws no block of it");
        Assert.True(Math.Abs(preview.Offset.Y - preview.TopOf(seven)) < 1,
            $"With the editor's top at version 7.0.0 the preview stands at {preview.Offset.Y:0}, not that heading's {preview.TopOf(seven):0}");
        Control[] drawn = [.. preview.Blocks];
        Assert.Null(window.OpenFile("SHORT.md"));
        Ui.Settle();
        Assert.Null(window.OpenFile("LONG.md"));
        Ui.Settle();
        Assert.Same(preview, Shown(group));
        Control[] made = [.. preview.Blocks.Where(b => !drawn.Contains(b))];
        Assert.True(made.Length == 0, $"Showing the long changelog's tab again made {made.Length} blocks' controls, where its page had them drawn");
        Assert.True(Math.Abs(preview.Offset.Y - preview.TopOf(seven)) < 1,
            $"Shown again, the preview stands at {preview.Offset.Y:0}, not version 7.0.0's {preview.TopOf(seven):0}, where the editor's top is");
        window.Close();

        // Markdown Preview turned on over a file open in the editor: what the preview alone costs.
        static (long Cost, int Drawn) Previewed(MainWindow window, string name)
        {
            Assert.Null(window.OpenFile(name));
            Ui.Settle();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(window.Perform(AppActions.MarkdownPreview));
            Ui.Settle();
            long cost = GC.GetAllocatedBytesForCurrentThread() - before;
            return (cost, Shown(window.EditorArea.ActiveGroup).Blocks.Count);
        }
    }

    /// <summary>A pause in typing in a 600 KB changelog reads again only the lines around what was typed, which the preview
    /// shows, and leaves every other block as it was read: a long file costs a pause what the short block typed in does.</summary>
    [AvaloniaFact]
    public void A_typing_pause_in_a_long_file_reads_again_only_the_lines_around_what_was_typed()
    {
        string folder = TestFolders.Create("markdown-typed-");
        File.WriteAllText(Path.Combine(folder, "LONG.md"), Changelog(600_000));
        MainWindow window = Ui.OpenFolder(folder, width: 1200, height: 800, languageServer: false);
        // The preview shows beside a Markdown file unless turned off.
        Assert.Null(window.OpenFile("LONG.md"));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        MarkdownPreview preview = Shown(group);
        CodeEditor editor = group.Editor;
        string[] lines = editor.Document.Snapshot.ToString().Split('\n');
        Assert.True(preview.LinesRead == lines.Length, $"The preview's first read made {preview.LinesRead} of the file's {lines.Length} lines strings");

        int line = Array.FindIndex(lines, l => l.StartsWith("## [5.0.0]", StringComparison.Ordinal));
        Scroll(editor, line);
        editor.Focus();
        Ui.PlaceAfter(editor, "## [5.0.0] - 2026-01-");
        window.KeyTextInput("typed ");
        Ui.Settle();

        Control heading = preview.BlockAt(line) ?? throw new Xunit.Sdk.XunitException($"No block draws line {line}");
        Assert.True(heading.GetLogicalDescendants().Prepend(heading).OfType<MarkdownText>().Any(t => t.Shown.StartsWith("[5.0.0] - 2026-01-typed ", StringComparison.Ordinal)),
            $"The heading typed in does not show what was typed:\n{preview.Text}");
        Assert.True(preview.LinesRead < 50,
            $"A pause in typing in one heading of a {lines.Length}-line file read {preview.LinesRead} lines again, so a long file costs every pause its whole text");
        window.Close();
    }

    /// <summary>The wheel turned over the preview of a long note, its benchmark tables wider than the preview, moves what it
    /// shows one way only, by a step each turn, to the note's end and back to its start, the editor's top line following
    /// the line at the preview's top: a block laid out as it comes into sight never pulls the preview back to the editor.</summary>
    [AvaloniaFact]
    public void The_wheel_over_a_long_notes_preview_moves_it_a_step_at_a_time_to_its_end_and_back_the_editor_following()
    {
        (MainWindow window, MarkdownPreview preview, CodeEditor editor) = Verifying();
        double step = double.NaN;
        int turns = 0;
        foreach (int down in (ReadOnlySpan<int>)[1, -1])
        {
            int line = editor.LinesShown.First;
            while (down > 0 ? preview.Offset.Y < Bottom(preview) - 0.5 : preview.Offset.Y > 0.5)
            {
                Assert.True(++turns < 1000, $"The preview never came to the note's {(down > 0 ? "end" : "start")}, standing at {preview.Offset.Y:0} of {Bottom(preview):0}");
                (Control seen, double was) = Nearest(preview);
                double offset = preview.Offset.Y;
                Wheel(window, preview, down);
                double moved = (was - (seen.TranslatePoint(default, preview)?.Y ?? double.NaN)) * down;
                Assert.True(down > 0 ? preview.Offset.Y > offset : preview.Offset.Y < offset,
                    $"Turn {turns} of the wheel {(down > 0 ? "down" : "up")} took the preview from {offset:0} to {preview.Offset.Y:0}: what was read jumped away");
                if (double.IsNaN(step))
                {
                    step = moved;
                    Assert.True(step > 0, $"The wheel's first turn moved the block at the preview's top {moved:0.0}");
                }
                bool atEnd = down > 0 ? preview.Offset.Y >= Bottom(preview) - 0.5 : preview.Offset.Y <= 0.5;
                Assert.True(Math.Abs(moved - step) < 1 || (atEnd && moved > 0 && moved < step + 1),
                    $"Turn {turns} of the wheel {(down > 0 ? "down" : "up")} moved the block at the preview's top {moved:0.0}, not a step's {step:0.0}: the reader loses their place");
                Assert.True(editor.LinesShown.First == preview.TopLine,
                    $"With line {preview.TopLine} at the preview's top the editor's top line is {editor.LinesShown.First}: the editor does not follow the preview");
                Together(preview, editor, $"after turn {turns} of the wheel over the preview");
                Assert.True(down > 0 ? editor.LinesShown.First >= line : editor.LinesShown.First <= line,
                    $"Turn {turns} took the editor's top line from {line} to {editor.LinesShown.First}, against the wheel");
                line = editor.LinesShown.First;
            }
        }
        window.Close();
    }

    /// <summary>Once the wheel has moved the preview, the editor scrolled by its own wheel leads again, the preview following
    /// it to the block at its top line.</summary>
    [AvaloniaFact]
    public void The_editor_scrolled_after_its_preview_was_leads_the_preview_again()
    {
        (MainWindow window, MarkdownPreview preview, CodeEditor editor) = Verifying();
        for (int turn = 0; turn < 20; turn++)
        {
            Wheel(window, preview, 1);
        }
        int was = editor.LinesShown.First;
        Assert.True(was > 0, "Twenty turns of the wheel over the preview left the editor at its top");
        Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
        for (int turn = 0; turn < 10; turn++)
        {
            window.MouseWheel(over, new Vector(0, -1));
            Ui.Settle();
            Together(preview, editor, $"after turn {turn + 1} of the wheel over the editor");
        }
        Assert.True(editor.LinesShown.First > was, $"Ten turns of the wheel down over the editor took its top line from {was} to {editor.LinesShown.First}");
        window.Close();
    }

    /// <summary>A key typed in the editor, the preview moved by the wheel, leaves the preview standing where the wheel left
    /// it once the typing is drawn, and the editor where it was.</summary>
    [AvaloniaFact]
    public void A_key_typed_in_the_editor_leaves_the_preview_where_the_wheel_left_it()
    {
        (MainWindow window, MarkdownPreview preview, CodeEditor editor) = Verifying();
        for (int turn = 0; turn < 30; turn++)
        {
            Wheel(window, preview, 1);
        }
        (int first, int last) = editor.LinesShown;
        TextSnapshot text = editor.Document.Snapshot;
        int line = Enumerable.Range(first + 2, Math.Max(0, last - first - 4)).FirstOrDefault(l => text.GetLineText(l).StartsWith("What check", StringComparison.Ordinal), -1);
        Assert.True(line >= 0, $"No check's words stand in the editor's lines {first} to {last}");
        double offset = preview.Offset.Y;
        Ui.Click(editor, editor.PointOf(text.GetLineStart(line) + text.GetLineText(line).Length) + new Point(-2, editor.LineHeight / 2));
        window.KeyTextInput(" Typed.");
        Ui.Settle();
        Assert.True(preview.Text.Contains(" Typed.", StringComparison.Ordinal), $"The preview does not show what was typed:\n{preview.Text}");
        Assert.True(Math.Abs(preview.Offset.Y - offset) < 0.5,
            $"A key typed in the editor moved the preview from {offset:0} to {preview.Offset.Y:0}, away from where the wheel left it");
        Assert.True(editor.LinesShown.First == first, $"A key typed in the editor's sight moved its top line from {first} to {editor.LinesShown.First}");
        window.Close();
    }

    /// <summary>A note open with its preview, in a window narrow enough that its benchmark tables are wider than the preview.</summary>
    private static (MainWindow Window, MarkdownPreview Preview, CodeEditor Editor) Verifying()
    {
        string folder = TestFolders.Create("markdown-scroll-");
        File.WriteAllText(Path.Combine(folder, "VERIFICATION.md"), Verification());
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700, languageServer: false);
        Assert.Null(window.OpenFile("VERIFICATION.md"));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        return (window, Shown(group), group.Editor);
    }

    /// <summary>A release's verification note: each check a heading, its words, a list with code spans, now and then a fence,
    /// and every third a table of benchmarks with more columns than a narrow preview has room for.</summary>
    private static string Verification()
    {
        StringBuilder text = new("# Verification\n\nWhat was checked before the release, and how it was checked.\n\n");
        for (int check = 1; check <= 24; check++)
        {
            text.Append($"## Check {check}\n\nWhat check {check} looked at, written at some length so that its words wrap over a line or two of the preview.\n\n");
            text.Append($"- Ran `dotnet test --filter Check{check}` on each system\n- Read the log for `warning` and `error`\n- Compared the times with the last release\n\n");
            if (check % 4 == 0)
            {
                text.Append($"```sh\njust test Check{check} --gate\njust test Check{check} --report\n```\n\n");
            }
            if (check % 3 == 0)
            {
                text.Append("| Benchmark | Method | Runtime | Mean | Error | StdDev | Median | Ratio | Gen0 | Allocated |\n| :--- | :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n");
                for (int row = 0; row < 6; row++)
                {
                    text.Append($"| Parse{row} | ReadEveryBlockOfTheFile | .NET 11.0 | {row}.123 ms | 0.0{row}1 ms | 0.0{row}2 ms | {row}.120 ms | 1.0{row} | 12.{row} | 1,024.{row} KB |\n");
                }
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    /// <summary>A turn of the wheel over the preview's middle, down or up, and what it starts let land.</summary>
    private static void Wheel(MainWindow window, MarkdownPreview preview, int down)
    {
        window.MouseWheel(preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), window)!.Value, new Vector(0, -down));
        Ui.Settle();
    }

    /// <summary>Fails unless the block drawing the editor's top line (or, for a blank line, the block after it) is the first
    /// to reach below the preview's top.</summary>
    private static void Together(MarkdownPreview preview, CodeEditor editor, string when)
    {
        int top = editor.LinesShown.First;
        Control? first = preview.Blocks.FirstOrDefault(b => b.TranslatePoint(default, preview)!.Value.Y + b.Bounds.Height > 0.5);
        Control? at = preview.BlockAt(top);
        TextSnapshot text = editor.Document.Snapshot;
        bool blankBefore = at is null && first is not null && preview.LineOf(first) > top
            && Enumerable.Range(top, preview.LineOf(first) - top).All(l => string.IsNullOrWhiteSpace(text.GetLineText(l)));
        Assert.True(first is not null && (at == first || blankBefore),
            $"{when} the editor's top line is {top} ('{text.GetLineText(top)}') while the block at the preview's top begins at line {(first is null ? -1 : preview.LineOf(first))}: the two do not stand together");
    }

    /// <summary>The farthest the preview scrolls.</summary>
    private static double Bottom(MarkdownPreview preview) => Math.Max(0, preview.Extent.Height - preview.Viewport.Height);

    /// <summary>The block drawn in the preview's sight whose top stands nearest the preview's top, and where it stands.</summary>
    private static (Control Block, double Y) Nearest(MarkdownPreview preview) =>
        preview.Blocks.Select(b => (Block: b, Y: b.TranslatePoint(default, preview)!.Value.Y))
            .Where(s => s.Y < preview.Viewport.Height && s.Y + s.Block.Bounds.Height > 0)
            .MinBy(s => Math.Abs(s.Y));

    /// <summary>A changelog of about a size, as a long project's grows: each version a heading, its fixes in a list, and now
    /// and then a fence of code and a table.</summary>
    private static string Changelog(int size)
    {
        StringBuilder text = new("# Changelog\n\n");
        for (int version = 999; text.Length < size; version--)
        {
            text.Append($"## [{version / 100}.{version / 10 % 10}.{version % 10}] - 2026-01-{(version % 28) + 1:00}\n\n### Fixed\n\n");
            for (int item = 0; item < 5; item++)
            {
                int issue = (version * 10) + item;
                text.Append($"- Fixed issue #{issue} where the **widget** would sometimes not render on `resize` ([#{issue}](https://example.com/pull/{issue}))\n");
            }
            if (version % 10 == 0)
            {
                text.Append("\n```csharp\npublic sealed class Widget\n{\n    public void Resize(int width) => Width = width;\n}\n```\n\n| Platform | Fixed |\n| :--- | ---: |\n| macOS | yes |\n| Windows | yes |\n");
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A window keeping its state in the test's folder, on the folder.</summary>
    private static MainWindow Started(string state, string folder)
    {
        MainWindow window = new() { Width = 1400, Height = 900, StateFolder = state, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.KeepSettings();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>The file shown split right, each split with a preview of its own, then the split's tab closed by its close
    /// button: the split's preview, held by nothing here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<MarkdownPreview> SplitAndClose(MainWindow window)
    {
        EditorGroup left = window.EditorArea.ActiveGroup;
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        EditorGroup right = window.EditorArea.ActiveGroup;
        MarkdownPreview split = Shown(right);
        Assert.True(right != left && Shown(left) != split, "The two splits of the README do not each have a preview of their own");
        Ui.Click(right.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains(StyleClasses.TabClose) && b.IsEffectivelyVisible));
        window.MouseMove(new Point(1, 1));
        Ui.Settle();
        Assert.Single(window.EditorArea.Groups);
        return new WeakReference<MarkdownPreview>(split);
    }

    /// <summary>The preview beside a group's editor, which must be shown.</summary>
    private static MarkdownPreview Shown(EditorGroup group)
    {
        MarkdownPreview? preview = group.Markdown;
        Assert.True(preview is { IsEffectivelyVisible: true }, $"{group.Active?.Name ?? "The group"} shows no preview beside its editor");
        Assert.True(preview!.Document == group.Active, $"The preview shows {preview.Document?.Name ?? "nothing"}, not {group.Active?.Name}");
        return preview;
    }

    private static MarkdownText Cell(Border[] cells, string words) =>
        cells.Select(c => c.Child).OfType<MarkdownText>().FirstOrDefault(t => t.Shown == words)
        ?? throw new Xunit.Sdk.XunitException($"No cell says '{words}'");

    private static IBrush Brush(MainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? found) && found is IBrush brush ? brush : throw new Xunit.Sdk.XunitException($"No {key} brush");

    /// <summary>The editor scrolled so a line (from zero) is its top one, as its scrollbar would.</summary>
    private static void Scroll(CodeEditor editor, int line)
    {
        int offset = editor.Document.Snapshot.GetLineStart(line);
        editor.ScrollTo(new Vector(0, editor.PointOf(offset).Y + editor.ScrollOffset.Y));
        Ui.Settle();
    }
}
