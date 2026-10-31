using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>A Markdown file's live preview beside its editor: each kind of block a README has, GitHub's tables and task
/// lists, fenced code coloured as the editor colours it, the images of files inside the open folder and none from outside
/// it; following the typing and the editor's scroll; its links opening the browser, another file or a heading; and the
/// choice to show it kept for the next window.</summary>
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
        for (int level = 1; level <= 6; level++)
        {
            Assert.True(words.Any(w => w.Classes.Contains($"mdHeading{level}") && w.Shown == headings[level - 1]),
                $"The preview draws no heading of level {level} saying '{headings[level - 1]}':\n{preview.Text}");
        }
        Assert.DoesNotContain("##", preview.Text, StringComparison.Ordinal);

        MarkdownText said = words.First(w => w.Shown.StartsWith("A shop with", StringComparison.Ordinal));
        Run[] runs = [.. said.Inlines!.OfType<Run>()];
        Assert.True(runs.Any(r => r.Text == "strong words" && r.FontWeight == FontWeight.Bold), "The strong words are not drawn bold");
        Assert.True(runs.Any(r => r.Text == "emphasis" && r.FontStyle == FontStyle.Italic), "The emphasis is not drawn in italics");
        Assert.True(runs.Any(r => r.Text == "code spans" && r.FontFamily == CodeEditor.CodeFont), "The code span is not drawn in the editor's font");

        Assert.True(preview.GetLogicalDescendants().OfType<StackPanel>().Any(l => l.Classes.Contains("mdList")
            && l.GetLogicalDescendants().OfType<StackPanel>().Any(inner => inner.Classes.Contains("mdList"))), "The list inside a list is not drawn as one");
        Border[] tasks = [.. preview.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("mdTask"))];
        Assert.True(tasks.Length == 2 && tasks[0].Classes.Contains("done") && !tasks[1].Classes.Contains("done"),
            $"The task list draws {tasks.Length} checks, done [{string.Join(", ", tasks.Select(t => t.Classes.Contains("done")))}], not one done and one to do");
        Assert.True(words.Any(w => w.Shown == "Ship the shop"), "A task's words still show its [ ] mark");

        Border[] cells = [.. preview.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("mdCell"))];
        Assert.True(cells.Length == 9 && cells.Take(3).All(c => c.Classes.Contains("head")), $"The table draws {cells.Length} cells, not its header's 3 and two rows' 6");
        Assert.Equal(TextAlignment.Center, Cell(cells, "fruit").TextAlignment);
        Assert.Equal(TextAlignment.Right, Cell(cells, "3.50").TextAlignment);
        Assert.Equal(TextAlignment.Left, Cell(cells, "Apple").TextAlignment);

        Assert.True(preview.GetLogicalDescendants().OfType<Border>().Any(b => b.Classes.Contains("mdQuote")
            && b.GetLogicalDescendants().OfType<MarkdownText>().Any(t => t.Shown == "Shops sell things.")), "The block quote is not drawn as one");
        Assert.Contains(preview.GetLogicalDescendants().OfType<Border>(), b => b.Classes.Contains("mdRule"));

        MarkdownCode[] code = [.. preview.GetLogicalDescendants().OfType<MarkdownCode>()];
        Assert.True(code.Select(c => c.Language).SequenceEqual(["C#", "JSON"]), $"The fences are coloured as [{string.Join(", ", code.Select(c => c.Language))}], not C# and JSON");
        IBrush keyword = Brush(window, TokenKeys.Syntax_Keyword);
        IBrush text = Brush(window, TokenKeys.Syntax_String);
        Ui.WaitFor(() => code[0].Runs.Any(r => r.Text == "class" && r.Foreground == keyword), "the C# fence's 'class' in the editor's keyword colour");
        Ui.WaitFor(() => code[1].Runs.Any(r => r.Text?.Contains("shop", StringComparison.Ordinal) == true && r.Foreground == text), "the JSON fence's string in the editor's string colour");

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
        Assert.True(preview.GetLogicalDescendants().OfType<MarkdownText>().Any(w => w.Classes.Contains("mdHeading2") && w.Shown == "Typed heading"),
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
        Ui.Click(right.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabClose") && b.IsEffectivelyVisible));
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
