using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>Every drop-down's search: in a long list, Settings' window font among every family installed, letters typed
/// narrow the list to the items holding them, said over it, Backspace takes one back, Escape all, and Enter chooses the
/// first item left; a short list, the terminal's cursor style, keeps the jump to the item a letter starts.</summary>
public sealed class DropDownSearchTests : IDisposable
{
    private readonly string folder = TestFolders.Create("dropdown-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_long_drop_down_narrows_to_the_items_holding_what_is_typed_and_enter_chooses_the_first_left()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        SettingsDialog dialog = window.ShowSettings();
        dialog.Page = SettingsDialog.Appearance;
        Ui.Settle();
        ComboBox box = SettingsTests.Editor<ComboBox>(dialog, "Font family");
        IReadOnlyList<string> installed = Ui.Await(Declared.WindowFontFamily.InstalledAsync(), "the installed font families");
        Ui.WaitFor(() => box.ItemCount > installed.Count, "the installed families to fill Settings' list");
        Assert.SkipWhen(box.ItemCount <= DropDownSearch.LongList, $"This machine has {box.ItemCount} window fonts, a list a letter only jumps in");
        string[] words = [.. box.Items.Select(i => i?.ToString() ?? string.Empty)];
        // A family no other's name holds, so the search leaves it alone.
        string target = installed.LastOrDefault(n => n.Length >= 4 && words.Count(w => w.Contains(n, StringComparison.CurrentCultureIgnoreCase)) == 1)
            ?? throw new Xunit.Sdk.XunitException($"No family's name is its own: {string.Join(", ", installed)}");
        object? before = box.SelectedItem;
        box.Focus();
        Ui.Settle();

        dialog.KeyTextInput(target[..3].ToLowerInvariant());
        Ui.Settle();
        Assert.True(box.IsDropDownOpen, "Letters typed in a long drop-down left it shut");
        Assert.Equal(target[..3].ToLowerInvariant(), DropDownSearch.Query(box));
        Assert.Same(before, box.SelectedItem);
        Assert.All(Shown(box), w => Assert.Contains(target[..3], w, StringComparison.CurrentCultureIgnoreCase));
        Assert.True(Said(box) is { } said && said.Contains(target[..3].ToLowerInvariant(), StringComparison.Ordinal),
            $"The list does not say what it is narrowed to: '{Said(box)}'");

        dialog.KeyTextInput(target[3..]);
        Ui.Settle();
        Assert.Equal([target], Shown(box));
        Ui.Press(dialog, "back");
        Assert.Equal(target[..^1].ToLowerInvariant(), DropDownSearch.Query(box).ToLowerInvariant());
        dialog.KeyTextInput(target[^1..]);
        Ui.Settle();
        Ui.Press(dialog, "enter");
        Assert.True(!box.IsDropDownOpen && box.SelectedItem?.ToString() == target,
            $"Enter after typing {target} left {box.SelectedItem} chosen, the list {(box.IsDropDownOpen ? "open" : "shut")}");
        Assert.Equal(string.Empty, DropDownSearch.Query(box));

        box.Focus();
        dialog.KeyTextInput("zzqx");
        Ui.Settle();
        Ui.Press(dialog, "escape");
        Assert.True(box.IsDropDownOpen && DropDownSearch.Query(box).Length == 0 && Shown(box).Count > 1,
            "Escape did not give back the whole list before shutting it");
        Ui.Press(dialog, "escape");
        Assert.False(box.IsDropDownOpen, "A second Escape left the list open");

        dialog.Page = SettingsDialog.TerminalPage;
        Ui.Settle();
        ComboBox cursor = SettingsTests.Editor<ComboBox>(dialog, "Cursor style");
        cursor.Focus();
        dialog.KeyTextInput("u");
        Ui.Settle();
        Assert.True(cursor.SelectedItem?.ToString() == "Underline" && DropDownSearch.Query(cursor).Length == 0,
            $"A letter in a short drop-down chose {cursor.SelectedItem} and searched for '{DropDownSearch.Query(cursor)}', rather than jumping to Underline");
        dialog.Close();
        window.Close();
    }

    /// <summary>What the drop-down's list says over its rows of the search, while it says it.</summary>
    private static string? Said(ComboBox box) =>
        box.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault()?.Child?.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.IsVisible && t.Text?.Contains('\u201c', StringComparison.Ordinal) == true)?.Text;

    /// <summary>The words of the drop-down's rows shown now.</summary>
    private static List<string> Shown(ComboBox box) =>
        [.. Enumerable.Range(0, box.ItemCount).Where(i => box.ContainerFromIndex(i) is { IsVisible: true }).Select(i => box.Items[i]?.ToString() ?? string.Empty)];
}
