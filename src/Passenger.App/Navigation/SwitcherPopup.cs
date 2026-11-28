using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.App.CSharp;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A row of the Switcher: an open tab (a file's or a diff's) by its icon, its title and its file's folder, or a
/// tool window by its icon, its name and its keys.</summary>
public sealed record SwitcherRow(string Name, string After, OpenDocument? Document, ToolWindow? Window)
{
    /// <summary>The theme's key of its icon: its tab's, or its tool window's.</summary>
    public string? IconKey => Document?.IconKey ?? Window?.Icon;

    /// <summary>The theme's key of its icon's tint, its tab's.</summary>
    public string? IconBrushKey => Document?.IconBrushKey;

    /// <summary>What a screen reader is told it is, after its name: a file's folder, or a tool window.</summary>
    public string Said => Window is null ? After : "tool window";
}

/// <summary>The Switcher's popup: the window's open tabs, the one used last first, then its tool windows, with one chosen.
/// Tab and Down move the choice on, Shift+Tab and Up back, Tab round from either end, a modifier held or not; a name typed
/// narrows the rows; Enter or a double click goes to the one chosen, Escape leaves. Its keys are its own while it has them
/// (<see cref="IOwnKeys"/>), so the keymap runs none of them, and each move of its choice is told a screen reader.</summary>
public sealed class SwitcherPopup : SearchPopup<SwitcherRow>, IOwnKeys
{
    private readonly IReadOnlyList<SwitcherRow> all;
    private readonly int first;

    /// <summary>Whether the popup and its first choice were told, so a move tells the choice alone.</summary>
    private bool told;

    /// <param name="chosen">The row chosen as it opens.</param>
    public SwitcherPopup(IReadOnlyList<SwitcherRow> rows, int chosen)
        : base("Switcher", "A file's or tool window's name", PopupFrame.Medium)
    {
        all = rows;
        first = chosen;
        ShownWords.Set(this, AutomationProperties.NameProperty, Title);
        QueryChanged();
        AttachedToVisualTree += (_, _) => Tell(Urgency.Polite);
    }

    protected override void QueryChanged()
    {
        string typed = Query.Trim();
        Show(typed.Length == 0 ? all : [.. all.Where(row => FuzzyMatch.Score(typed, row.Name) is not null)], "Nothing open has that name",
            typed.Length == 0 ? first : 0);
    }

    protected override Control Row() => HiddenTabsPopup.TabRow<SwitcherRow>((row, icon, name, after) =>
    {
        icon.DataKey = row.IconKey;
        icon.ForegroundKey = row.IconBrushKey;
        name.Text = row.Name;
        after.Text = row.After;
    });

    /// <summary>The choice moved a row on, or back for a negative step, round from either end.</summary>
    public void Move(int step)
    {
        int count = Rows.Count;
        if (count > 0)
        {
            int at = Chosen();
            ChooseAt(at < 0 ? (step > 0 ? 0 : count - 1) : ((at + step) % count + count) % count);
        }
    }

    /// <summary>Tab and Shift+Tab, and Up and Down with a modifier held, which every search popup's own keys pass over.</summary>
    protected override bool TakesKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            Move(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            return true;
        }
        if (e.Key is Key.Up or Key.Down && Rows.Count > 0)
        {
            ChooseAt(Math.Clamp(Chosen() + (e.Key == Key.Up ? -1 : 1), 0, Rows.Count - 1));
            return true;
        }
        return false;
    }

    /// <summary>The keys that move, choose or leave, whatever modifier is held with them.</summary>
    bool IOwnKeys.TakesKey(KeyEventArgs e) => e.Key is Key.Tab or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter or Key.Escape;

    protected override void ChoiceMoved()
    {
        if (told)
        {
            Tell(Urgency.Assertive);
        }
    }

    /// <summary>The choice told a screen reader in place of the one told before, with the popup's name the first time.</summary>
    private void Tell(Urgency urgency)
    {
        if (Selected is not { } row || Announcer.Of(TopLevel.GetTopLevel(this)) is not { } announcer)
        {
            return;
        }
        string choice = PopupPlacement.Chosen(row.Name, row.Said, Chosen(), Rows.Count);
        announcer.Tell(told ? choice : $"{Title.Shown}, {choice}", urgency, this);
        told = true;
    }

    private int Chosen()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], Selected))
            {
                return i;
            }
        }
        return -1;
    }
}
