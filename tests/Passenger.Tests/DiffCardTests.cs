using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A diff of a C# file shows, on its side that is the open file's own text, the card the file's editor shows of
/// the name the pointer rests on, under the name's line in the diff's own window, whether diffs open as tabs of the
/// editors or in windows of their own; its other side, the last commit's text, which no language server has read, shows
/// none.</summary>
public sealed class DiffCardTests : IDisposable
{
    private const string Shop = "public sealed class Shop\n{\n    /// <summary>The price of a number of items.</summary>\n"
        + "    public decimal Price(int count) => count * 2.5m;\n\n    public decimal Total() => Price(3);\n}\n";

    private readonly string folder = TestFolders.Create("diff-card-");

    public DiffCardTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "Shop.csproj"), TestProjects.Library());
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop);
        TestGit.Init(folder);
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop.Replace("2.5m", "3.5m", StringComparison.Ordinal));
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>C# help on a diff's side of the file as one flow, each step the check one test made in an instance of
    /// its own: resting on a name showing its card there and none on the last commit's side, and Go to Declaration by
    /// click and keys landing at the declaration.</summary>
    [AvaloniaFact]
    public void A_diffs_side_of_the_file_shows_a_names_card_and_goes_to_its_declaration_as_the_editor_does()
    {
        Flow.StepApart<DiffCardTests>("resting on a name in a diffs side of the file shows its card and on the last commits side none", t => t.Resting_on_a_name_in_a_diffs_side_of_the_file_shows_its_card_and_on_the_last_commits_side_none());
        Flow.StepApart<DiffCardTests>("go to declaration on a diffs side of the file by click and keys lands at the declaration", t => t.Go_to_declaration_on_a_diffs_side_of_the_file_by_click_and_keys_lands_at_the_declaration());
    }

    private void Resting_on_a_name_in_a_diffs_side_of_the_file_shows_its_card_and_on_the_last_commits_side_none()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile(Path.Combine(folder, "Shop.cs"));
            Ui.Settle();
            // A diff's own window has a layer of its own for the card, so each place is a case of its own.
            foreach (DiffPlace place in (DiffPlace[])[DiffPlace.Tab, DiffPlace.Window])
            {
                window.ApplySettings(window.Settings with { OpenDiffsIn = place });
                IDiffHost diff = DiffFromCommit(window);
                DiffView view = OpenDiffs.ViewOf(diff);
                Assert.True(view.IsEditable, $"The diff of the open Shop.cs, as a {place}, shows its file's side read-only");

                int line = Rest(view.Right, "Price(3)");
                Ui.WaitFor(() => window.Assist.HoverText?.Contains("decimal Shop.Price(int count)", StringComparison.Ordinal) == true,
                    () => $"Price's card over the file's side of the diff as a {place}, where the card says [{window.Assist.HoverText}]");
                Border card = window.Assist.HoverCard!;
                TopLevel shown = TopLevel.GetTopLevel(view)!;
                Assert.True(TopLevel.GetTopLevel(card) == shown, $"Price's card over the diff as a {place} is drawn in {TopLevel.GetTopLevel(card)?.GetType().Name ?? "no window"}, not the diff's");
                Point under = view.Right.TranslatePoint(view.Right.PointOf(view.Right.Document.Snapshot.GetLineStart(line)) + new Vector(0, view.Right.LineHeight), shown)!.Value;
                Point cardAt = card.TranslatePoint(default, shown)!.Value;
                Assert.True(cardAt.Y >= under.Y && cardAt.Y <= under.Y + 4,
                    $"Price's card over the diff as a {place} stands at {cardAt.Y:0.#}, not under its line, which ends at {under.Y:0.#}");

                Rest(view.Left, "Price(3)");
                Ui.WaitFor(() => window.Assist.HoverCard is null, () => $"the card gone as the pointer left the file's side for the last commit's, as a {place}");
                Ui.Hold(TimeSpan.FromMilliseconds(800), "the pointer's rest on the last commit's side, past which an editor's card shows");
                Assert.True(window.Assist.HoverCard is null,
                    $"Resting on Price in the last commit's side of the diff as a {place}, which no language server has read, shows a card: [{window.Assist.HoverText}]");
                diff.Close();
                Ui.Settle();
            }
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>On a diff's side that is the open file's own text, Go to Declaration goes from where it is asked as from the
    /// file's editor: its gesture's keys held over a name underline it, and Cmd-click (Ctrl-click off the Mac) on it and its
    /// keys at the caret each land at the name's declaration in the file, the diff's tab left open behind.</summary>
    private void Go_to_declaration_on_a_diffs_side_of_the_file_by_click_and_keys_lands_at_the_declaration()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            string shop = Path.Combine(folder, "Shop.cs");
            window.ApplySettings(window.Settings with { OpenDiffsIn = DiffPlace.Tab });
            window.OpenFile(shop);
            Ui.Settle();
            IDiffHost diff = DiffFromCommit(window);
            DiffView view = OpenDiffs.ViewOf(diff);
            Assert.True(view.IsEditable, "The diff of the open Shop.cs shows its file's side read-only");
            Avalonia.Input.RawInputModifiers command = OperatingSystem.IsMacOS() ? Avalonia.Input.RawInputModifiers.Meta : Avalonia.Input.RawInputModifiers.Control;
            int call = view.Right.Document.Snapshot.ToString().IndexOf("Price(3)", StringComparison.Ordinal);
            Point on = view.Right.PointOf(call + 2) + new Vector(1, view.Right.LineHeight / 2);

            Ui.Hover(view.Right, on, command);
            Ui.WaitFor(() => view.Right.PointedRange is not null, () => "the held gesture's underline under Price on the diff's side of the file");
            Assert.Equal(new Passenger.Text.TextRange(call, "Price".Length), view.Right.PointedRange);

            Ui.Click(view.Right, on, command);
            Landed(window, "a click with Go to Declaration's gesture on Price in the diff");
            Assert.True(window.EditorArea.Diffs.Count == 1, "Going to the declaration from the diff's tab closed it");

            DiffView again = OpenDiffs.ViewOf(DiffFromCommit(window));
            again.Right.Focus();
            again.Right.GoTo(again.Right.Document.Snapshot.GetPosition(call + 2));
            Ui.Landed(() => again.Right.IsKeyboardFocusWithin, "the diff's side of the file to have the keys");
            Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
            Landed(window, "Go to Declaration's keys on Price in the diff");
        }
        finally
        {
            window.Close();
        }

        static void Landed(MainWindow window, string how) => Ui.WaitFor(
            () => window.EditorArea.Active is { Diff: null } file && file.Name == "Shop.cs" && window.EditorArea.CodeEditor.CaretPosition.Line == 3,
            () => $"{how} to land at its declaration on Shop.cs's line 4, where {window.EditorArea.Active?.Title} is shown, its caret on line {window.EditorArea.CodeEditor.CaretPosition.Line + 1}");
    }

    /// <summary>The pointer brought to rest on some text in a side of the diff, as a person's mouse rests on it; gives the
    /// text's line.</summary>
    private static int Rest(CodeEditor side, string text)
    {
        int at = side.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 2;
        Assert.True(at >= 2, $"'{text}' is not in the diff's side");
        TopLevel top = TopLevel.GetTopLevel(side)!;
        top.MouseMove(side.TranslatePoint(side.PointOf(at) + new Vector(1, side.LineHeight / 2), top)!.Value, Avalonia.Input.RawInputModifiers.None);
        return side.Document.Snapshot.GetLineFromOffset(at);
    }

    /// <summary>Show Diff by a double-click on Shop.cs's row of the Commit window, as the person opens it there.</summary>
    private static IDiffHost DiffFromCommit(MainWindow window)
    {
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Shop.cs"), "Shop.cs listed as changed");
        ListBox changes = commit.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, commit.Rows));
        Ui.DoubleClick(Ui.Row(changes, commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == "Shop.cs")));
        return OpenDiffs.Landed(window, "Shop.cs's diff, from a double-click on its row in the Commit window");
    }
}
