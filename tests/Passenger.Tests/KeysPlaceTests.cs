using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;

namespace Passenger.Tests;

/// <summary>The keys go where the person is: a popup closing gives them back to what had them as it opened.</summary>
public sealed class KeysPlaceTests : IDisposable
{
    private readonly string folder = TestFolders.Create("keys-place-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Rename and Find Action opened from an Explorer row and closed by Escape give the row the keys back, so the
    /// next Down chooses the next row rather than moving the editor's caret.</summary>
    [AvaloniaFact]
    public void A_popup_opened_from_an_Explorer_row_gives_the_row_the_keys_back_as_it_closes()
    {
        foreach (string name in (string[])["Alpha.txt", "Beta.txt", "Gamma.txt", "Delta.txt"])
        {
            TestFolders.Write(folder, name, $"{name}\nsecond line\n");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile("Alpha.txt");
        Ui.Settle();
        string text = window.EditorArea.Active!.Text.Snapshot.ToString();
        Ui.Run(window, AppActions.FocusExplorer);
        ExplorerNode first = window.ExplorerView.Selected ?? throw new Xunit.Sdk.XunitException("The Explorer chose no row as it took the keys");
        Assert.True(RowHasKeys(window, first), $"The Explorer's action left the keys on {Focused(window)}, not its chosen row {first.Name}");

        foreach (string action in (string[])[AppActions.Rename, AppActions.FindAction])
        {
            ExplorerNode was = window.ExplorerView.Selected!;
            Ui.Run(window, action);
            Assert.True(window.IsPopupOpen, $"{window.Actions[action]!.Title} on the Explorer's row {was.Name} opened no popup");
            Ui.Press(window, "ESCAPE");
            Assert.False(window.IsPopupOpen, $"Escape left {window.Actions[action]!.Title}'s popup open");
            Assert.True(RowHasKeys(window, was),
                $"{window.Actions[action]!.Title} closed by Escape gave the keys to {Focused(window)}, not back to the Explorer's row {was.Name} that had them");
            Ui.Press(window, "DOWN");
            ExplorerNode next = window.ExplorerView.Selected!;
            Assert.True(next != was && RowHasKeys(window, next),
                $"Down after {window.Actions[action]!.Title} closed did not choose the Explorer's next row: {next.Name} is chosen, {Focused(window)} has the keys");
            Assert.True(window.EditorArea.Active!.Text.Snapshot.ToString() == text, $"Down after {window.Actions[action]!.Title} closed went into the editor and changed Alpha.txt");
        }
        window.Close();
    }

    /// <summary>Whether the Explorer's container of a row has the keys.</summary>
    private static bool RowHasKeys(MainWindow window, ExplorerNode row)
    {
        ListBox rows = window.ExplorerView.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Rows");
        return window.FocusManager?.GetFocusedElement() is Control focused && focused == rows.ContainerFromItem(row);
    }

    private static string Focused(MainWindow window) => window.FocusManager?.GetFocusedElement() switch
    {
        null => "nothing",
        Control { DataContext: ExplorerNode node } => $"the Explorer's row {node.Name}",
        object other => other.GetType().Name,
    };
}
