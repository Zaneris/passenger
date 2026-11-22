using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Updates;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What's New: the release notes a version carries beside it, shown in the person's window the first time that
/// version starts after an update, once, in a tab of their own, rendered and read-only, in front of the folder it opens
/// (whose own tabs come back behind them); never for a fresh state; and again from Help whenever asked; a patch release's
/// with its minor version's earlier notes beneath. Each window keeps its state in the test's own folder, as the person's
/// keeps theirs, and is a version whose notes the build carries (docs/release-notes/0.3.0.md), so each reads that real
/// file, but where a test gives a folder of notes of its own.</summary>
public sealed class WhatsNewTests : IDisposable
{
    private const string Version = "0.3.0";

    private readonly string state = TestFolders.Create("whatsnew-state-");
    private readonly string folder = TestFolders.Create("whatsnew-folder-");
    private readonly string notes = TestFolders.Create("whatsnew-notes-");

    public WhatsNewTests() => File.WriteAllText(Path.Combine(folder, "Program.cs"), "Console.WriteLine(\"Hello\");\n");

    public void Dispose()
    {
        TestFolders.Delete(state);
        TestFolders.Delete(folder);
        TestFolders.Delete(notes);
    }

    /// <summary>What's New shows once after an update, an older state counting as one, never on a fresh state but
    /// whenever Help asks, a patch release's notes over its minor version's, as one flow, each step the check one test
    /// made in an instance of its own.</summary>
    [AvaloniaFact]
    public void Whats_New_shows_a_releases_notes_once_after_an_update_and_whenever_asked()
    {
        Flow.StepApart<WhatsNewTests>("after an update the notes show once in front of the folder the window opens and not on the next start", t => t.After_an_update_the_notes_show_once_in_front_of_the_folder_the_window_opens_and_not_on_the_next_start());
        Flow.StepApart<WhatsNewTests>("an older Passengers state that kept no record of the notes counts as an update", t => t.An_older_Passengers_state_that_kept_no_record_of_the_notes_counts_as_an_update());
        Flow.StepApart<WhatsNewTests>("a fresh state shows no notes and Help shows them whenever asked", t => t.A_fresh_state_shows_no_notes_and_Help_shows_them_whenever_asked());
        Flow.StepApart<WhatsNewTests>("a patch releases notes show its minor versions earlier notes beneath newest first and a minor releases its own alone", t => t.A_patch_releases_notes_show_its_minor_versions_earlier_notes_beneath_newest_first_and_a_minor_releases_its_own_alone());
    }

    private void After_an_update_the_notes_show_once_in_front_of_the_folder_the_window_opens_and_not_on_the_next_start()
    {
        // The person used an older Passenger here: its folder's file open as they left it, and What's New's record older.
        MainWindow window = Started();
        window.OpenFolder(folder);
        Ui.Settle();
        window.OpenFile(Path.Combine(folder, "Program.cs"));
        Ui.Settle();
        window.Close();
        Ui.Settle();
        File.WriteAllText(Path.Combine(state, "updates.json"), """{ "version": 1, "notesShown": "0.1.0" }""");

        window = Started();
        window.ShowWhatsNewOnce();
        Ui.Settle();
        ShownNotes(window);
        window.OpenFolder(folder);
        Ui.Settle();
        OpenDocument notes = ShownNotes(window);
        Assert.True(window.EditorArea.Documents.Any(d => d.Name == "Program.cs"),
            $"The folder's own tab, Program.cs, did not come back beside the notes: [{Open(window)}]");
        ReleaseNotesView view = Rendered(window);
        string carried = Carried();
        Assert.False(carried.Contains("<!-- Stand-in", StringComparison.Ordinal), "The carried notes are still the stand-in, which a published release refuses");
        string[] missing = [.. Headings(carried).Where(heading => !view.Text.Contains(heading, StringComparison.Ordinal))];
        Assert.True(missing.Length == 0, $"The page does not say what the carried file does, missing its headings [{string.Join(", ", missing)}]; it says:\n{view.Text}");
        Assert.True(!view.Text.Contains("##", StringComparison.Ordinal) && !view.Text.Contains("**", StringComparison.Ordinal) && !view.Text.Contains("](", StringComparison.Ordinal),
            $"The page shows the Markdown's marks rather than drawing them:\n{view.Text}");
        Assert.Equal($"What's New in Passenger {Version}", view.Title);
        Assert.True(notes.IsReadOnly && !notes.HoldsText, "The notes' tab can be edited, or reads as text to edit, though it is the installed app's file");

        // A link opens in the browser wherever its block draws it: the first drawn below its block's first line.
        (MarkdownText linked, string words, Uri address) = view.Blocks
            .SelectMany(block => block.Links.Select(link => (Block: block, link.Words, link.Target)))
            .First(link => link.Block.PointOf(link.Target).Y > link.Block.TextLayout.TextLines[0].Height);
        Assert.True(carried.Contains($"[{words}]({address.OriginalString})", StringComparison.Ordinal),
            $"The page's link '{words}' to {address} is not one the carried file writes");
        Ui.Click(linked, linked.PointOf(address));
        Assert.True(window.Browsed.LastOrDefault() == address, $"A click on the notes' link opened [{string.Join(", ", window.Browsed)}], not {address}");

        Assert.True(window.EditorArea.CloseTab(window.EditorArea.Groups.First(g => g.Has(notes)), notes), "The notes' tab did not close");
        window.Close();
        Ui.Settle();
        Assert.True(Recorded() == Version, $"updates.json records '{Recorded()}' as the notes shown, not {Version}");

        window = Started();
        window.ShowWhatsNewOnce();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.True(!window.EditorArea.Documents.Any(d => d.IsReleaseNotes), $"The notes shown once showed again at the next start: [{Open(window)}]");
        Assert.True(window.EditorArea.Active?.Name == "Program.cs", $"The folder's tab did not come back as it was left: [{Open(window)}]");
        window.Close();
    }

    private void An_older_Passengers_state_that_kept_no_record_of_the_notes_counts_as_an_update()
    {
        // 0.1.0 kept no notes shown; what it did keep (the release it announced) says it ran here.
        File.WriteAllText(Path.Combine(state, "updates.json"), """{ "announced": "0.2.0" }""");
        MainWindow window = Started();
        window.ShowWhatsNewOnce();
        window.OpenFolder(folder);
        Ui.Settle();
        ShownNotes(window);
        window.Close();
        Ui.Settle();
        Assert.True(Recorded() == Version, $"updates.json records '{Recorded()}' as the notes shown, not {Version}");
    }

    private void A_fresh_state_shows_no_notes_and_Help_shows_them_whenever_asked()
    {
        MainWindow window = Started();
        window.ShowWhatsNewOnce();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.True(window.EditorArea.Documents.Count == 0, $"A first start showed notes of what changed, though nothing came before it here: [{Open(window)}]");

        NativeMenu help = Ui.Submenu(window.MenuBar, "Help");
        NativeMenuItem item = help.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "What's New")
            ?? throw new Xunit.Sdk.XunitException($"Help has no What's New: [{string.Join(", ", help.Items.OfType<NativeMenuItem>().Select(i => i.Header))}]");
        Assert.True(item.IsEnabled, "Help, What's New is greyed, though the build carries the notes");
        item.Command!.Execute(null);
        Ui.Settle();
        ShownNotes(window);
        Assert.Contains(Headings(Carried()).First(), Rendered(window).Text);
        window.Close();
        Ui.Settle();
        // The fresh state began on this version, so only a newer one's notes show by themselves.
        Assert.True(Recorded() == Version, $"updates.json records '{Recorded()}' for the fresh state, not the version it began on, {Version}");
    }

    /// <summary>A patch release's page lays its own notes, then its minor version's earlier ones, newest first, each under
    /// its own heading, as one page a screen reader reads whole; a minor release's lays its own alone. Neither takes another
    /// minor version's notes, nor a newer version's the folder carries.</summary>
    private void A_patch_releases_notes_show_its_minor_versions_earlier_notes_beneath_newest_first_and_a_minor_releases_its_own_alone()
    {
        Write("0.3.4", "Passenger 0.3.4 mends the older line.");
        Write("0.4.0", "Passenger 0.4.0 brings mobile apps.");
        Write("0.4.1", "Passenger 0.4.1 fixes what 0.4.0 left.");
        Write("0.4.2", "Passenger 0.4.2 is a later patch.");
        Write("0.5.0", "Passenger 0.5.0 is the next minor version.");

        MainWindow window = Started("0.4.1", notes);
        Assert.Null(window.ShowWhatsNew());
        Ui.Settle();
        Assert.Equal("What's New in 0.4.1", window.EditorArea.Active?.Title);
        ReleaseNotesView view = Rendered(window);
        Assert.Equal("What's New in Passenger 0.4.1", view.Title);
        Assert.True(view.Versions.SequenceEqual(["0.4.1", "0.4.0"]), $"0.4.1's page lays the notes of [{string.Join(", ", view.Versions)}], not 0.4.1's then 0.4.0's");
        string[] laid = [.. view.Blocks.Select(b => b.Shown)];
        int own = Array.IndexOf(laid, "Passenger 0.4.1 fixes what 0.4.0 left.");
        int heading = Array.IndexOf(laid, "What's New in Passenger 0.4.0");
        int earlier = Array.IndexOf(laid, "Passenger 0.4.0 brings mobile apps.");
        Assert.True(own >= 0 && own < heading && heading < earlier,
            $"0.4.1's notes are not above 0.4.0's heading and its notes beneath it, in that order; the page says:\n{view.Text}");
        Assert.True(laid.Count(b => b == "Fixed") == 2 && laid.Count(b => b == "Highlights") == 2,
            $"Each version's sections are not drawn under its own heading; the page says:\n{view.Text}");
        Assert.True(!view.Text.Contains("0.3.4", StringComparison.Ordinal) && !view.Text.Contains("0.4.2", StringComparison.Ordinal) && !view.Text.Contains("0.5.0", StringComparison.Ordinal),
            $"0.4.1's page shows notes of another minor version or a newer one; it says:\n{view.Text}");
        window.Close();
        Ui.Settle();

        window = Started("0.4.0", notes);
        Assert.Null(window.ShowWhatsNew());
        Ui.Settle();
        view = Rendered(window);
        Assert.Equal("What's New in Passenger 0.4.0", view.Title);
        Assert.True(view.Versions.SequenceEqual(["0.4.0"]), $"0.4.0's page lays the notes of [{string.Join(", ", view.Versions)}], not its own alone");
        Assert.True(view.Text.Contains("Passenger 0.4.0 brings mobile apps.", StringComparison.Ordinal) && !view.Text.Contains("What's New in", StringComparison.Ordinal)
            && !view.Text.Contains("0.4.1", StringComparison.Ordinal) && !view.Text.Contains("0.3.4", StringComparison.Ordinal),
            $"0.4.0's page does not show its own notes alone; it says:\n{view.Text}");
        window.Close();
    }

    /// <summary>A version's notes in the test's folder of notes, with the two sections each version's have.</summary>
    private void Write(string version, string lead) =>
        File.WriteAllText(Path.Combine(notes, version + ".md"), $"{lead}\n\n## Highlights\n\n- **{version}'s highlight**, said.\n\n## Fixed\n\n- {version}'s fix.\n");

    /// <summary>A person's window over the test's state, keeping its layout and settings there, the version whose notes the
    /// build carries (or another's it carries, or the given folder holds); no C# server, since nothing here asks one.</summary>
    private MainWindow Started(string version = Version, string? from = null)
    {
        MainWindow window = new()
        {
            Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false,
            Notes = new ReleaseNotes(from ?? ReleaseNotes.CarriedFolder, version),
        };
        window.Show();
        window.KeepLayout();
        window.KeepSettings();
        Ui.Settle();
        return window;
    }

    /// <summary>The notes' tab, which must be the one shown, titled as the notes of the version.</summary>
    private static OpenDocument ShownNotes(MainWindow window)
    {
        OpenDocument? shown = window.EditorArea.Active;
        Assert.True(shown is { IsReleaseNotes: true }, $"The notes are not the tab shown, which is {shown?.Path ?? "none"}: [{Open(window)}]");
        Assert.True(PathNames.Same(shown!.Path, PathNames.Canonical(new ReleaseNotes(ReleaseNotes.CarriedFolder, Version).File)),
            $"The tab shown is {shown.Path}, not the notes the app carries beside it");
        Assert.Equal($"What's New in {Version}", shown.Title);
        return shown;
    }

    /// <summary>The notes drawn in the editor's place, in place of their Markdown's text.</summary>
    private static ReleaseNotesView Rendered(MainWindow window)
    {
        ReleaseNotesView? view = window.GetVisualDescendants().OfType<ReleaseNotesView>().SingleOrDefault(v => v.IsEffectivelyVisible);
        Assert.True(view is not null, "The notes' tab shows no rendered page");
        Assert.False(window.EditorArea.CodeEditor.IsEffectivelyVisible, "The editor shows the notes' Markdown beside their rendered page");
        return view!;
    }

    private static string Open(MainWindow window) => string.Join(", ", window.EditorArea.Documents.Select(d => d.Name));

    /// <summary>The notes the build carries beside it, as written.</summary>
    private static string Carried() => File.ReadAllText(new ReleaseNotes(ReleaseNotes.CarriedFolder, Version).File);

    /// <summary>The words of the notes' section headings, as a person reads them once drawn.</summary>
    private static IEnumerable<string> Headings(string notes) =>
        notes.ReplaceLineEndings("\n").Split('\n').Where(line => line.StartsWith("## ", StringComparison.Ordinal)).Select(line => line[3..].Trim());

    /// <summary>The version updates.json records as the notes shown, once the windows over the state have closed.</summary>
    private string? Recorded() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(state, "updates.json")))?["notesShown"]?.GetValue<string>();
}
