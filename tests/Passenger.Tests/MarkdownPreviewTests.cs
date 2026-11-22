using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Navigation;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>A Markdown file's live preview beside its editor: each kind of block a README has, GitHub's tables and task
/// lists, fenced code coloured as the editor colours it, the images of files inside the open folder and none from
/// outside it; following the typing and the editor's scroll; its links opening the browser, another file or a heading;
/// a typing pause in a long file reading again only what it reached; text nested too deep ending nothing; and the
/// file's headings as its outline in Structure and File Structure.</summary>
[SameOnEverySystem]
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

    /// <summary>A README's blocks and links draw beside its editor and follow its typing, and text nested too deep ends
    /// nothing.</summary>
    [AvaloniaFact]
    public void A_readme_draws_beside_its_editor_and_follows_it()
    {
        Flow.Step(nameof(A_README_shows_each_kind_of_block_beside_its_editor_follows_typing_and_scrolling_and_opens_its_links),
            A_README_shows_each_kind_of_block_beside_its_editor_follows_typing_and_scrolling_and_opens_its_links);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_file_nested_past_what_a_page_reads_shows_the_rest_as_words_and_ends_nothing),
            A_file_nested_past_what_a_page_reads_shows_the_rest_as_words_and_ends_nothing);
    }

    /// <summary>A README's headings nest by level in Structure and lead to their lines, and a typing pause in a long
    /// file reads again only what it reached.</summary>
    [AvaloniaFact]
    public void A_readmes_structure_and_long_previews_follow_their_editor()
    {
        Flow.Step(nameof(A_READMEs_headings_nest_by_level_in_Structure_and_File_Structure_follow_its_typing_and_go_to_their_lines),
            A_READMEs_headings_nest_by_level_in_Structure_and_File_Structure_follow_its_typing_and_go_to_their_lines);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_typing_pause_in_a_long_file_reads_again_only_the_lines_around_what_was_typed),
            A_typing_pause_in_a_long_file_reads_again_only_the_lines_around_what_was_typed);
    }

    private void A_README_shows_each_kind_of_block_beside_its_editor_follows_typing_and_scrolling_and_opens_its_links()
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

        ColouredCode[] code = [.. preview.GetLogicalDescendants().OfType<ColouredCode>()];
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
        // Typing's pause held open, so the heading and its line breaks come within it however slow the machine.
        using (preview.HoldTypingOpen())
        {
            window.KeyTextInput("## Typed heading");
            Ui.Press(window, "ENTER");
            Ui.Press(window, "ENTER");
        }
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

    /// <summary>A page in a folder of its own links to the guide and draws a picture from the open folder's root, a slash
    /// first, as GitHub resolves them: on macOS and Linux too, where such a target also reads as a file's address.</summary>
    /// <summary>What is written deeper than a page reads is its words, and however deep a file goes the window stays.</summary>
    private void A_file_nested_past_what_a_page_reads_shows_the_rest_as_words_and_ends_nothing()
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

    private void A_READMEs_headings_nest_by_level_in_Structure_and_File_Structure_follow_its_typing_and_go_to_their_lines()
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

    /// <summary>A pause in typing in a 600 KB changelog reads again only the lines around what was typed, which the preview
    /// shows, and leaves every other block as it was read: a long file costs a pause what the short block typed in does.</summary>
    private void A_typing_pause_in_a_long_file_reads_again_only_the_lines_around_what_was_typed()
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
                text.Append("\n```csharp\npublic sealed class Widget\n{\n    public void Resize(int width) => Width = width;\n}\n```\n\n| Platform | Fixed |\n| :--- | ---: |\n| Desktop | yes |\n| Phone | yes |\n");
            }
            text.Append('\n');
        }
        return text.ToString();
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
