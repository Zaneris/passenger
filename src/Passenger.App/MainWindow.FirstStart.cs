using Avalonia.Controls;
using Passenger.App.Updates;
using Passenger.App.Welcome;

namespace Passenger.App;

/// <summary>The first start's question of the theme and of dark or light (<see cref="FirstStartView"/>), over the person's
/// window before anything else when the state it keeps is new, or when a start an agent made of a new state left it to the
/// person; never in a window nobody sees or that keeps no settings. Each answer is a change of the window's settings, put in
/// force at once and kept as Settings keeps it. While it shows, the title bar holds only the window's own buttons and
/// nothing under the page takes the keys.</summary>
public partial class MainWindow
{
    private FirstStartView? firstStart;

    /// <summary>The first start's question while it shows, else null.</summary>
    public FirstStartView? FirstStart => firstStart;

    /// <summary>The person's window as it starts (App, never a headless or agent's window): the first start's question when
    /// the state it keeps is new, recorded as asked; a start an agent's <c>--mcp-attach</c> made of a new state leaves it to
    /// the person's next start. Whether it shows.</summary>
    public bool ShowFirstStartOnce()
    {
        if (!keepsSettings || Unseen)
        {
            return false;
        }
        State.StateStore store = Store();
        UpdatesState kept = store.Updates.Value;
        if (store.UsedBefore && !kept.FirstStartLeft)
        {
            return false;
        }
        if (Mcp.McpProxy.StartedForAgent)
        {
            store.Updates.Set(kept with { FirstStartLeft = true });
            IdeErrors.Write("First start: started for an agent, so the theme is asked at the person's next start");
            return false;
        }
        if (kept.FirstStartLeft)
        {
            store.Updates.Set(kept with { FirstStartLeft = false });
        }
        ShowFirstStart();
        return true;
    }

    /// <summary>The first start's question over the window, shown now if it is not already.</summary>
    public FirstStartView ShowFirstStart()
    {
        UiThread.Check();
        if (firstStart is { } shown)
        {
            return shown;
        }
        FirstStartView page = new(() => settingsStore.Effective, change => settingsStore.Update(change));
        page.Ended += (_, _) => EndFirstStart();
        Grid.SetRow(page, 1);
        Grid.SetRowSpan(page, 2);
        // What lies under the page takes no focus, so the Welcome screen's search or a restored editor never takes its keys.
        foreach (Control under in Rows.Children.OfType<Control>().Where(c => Grid.GetRow(c) > 0))
        {
            under.IsEnabled = false;
        }
        ToolbarStart.IsVisible = false;
        ToolbarEnd.IsVisible = false;
        Rows.Children.Add(page);
        firstStart = page;
        IdeErrors.Write("First start: the theme and dark or light asked");
        return page;
    }

    /// <summary>The question gone, what it chose in force, the window as it was under it with the keys in its editor or on
    /// the Welcome screen.</summary>
    private void EndFirstStart()
    {
        if (firstStart is not { } page)
        {
            return;
        }
        firstStart = null;
        Rows.Children.Remove(page);
        foreach (Control under in Rows.Children.OfType<Control>().Where(c => Grid.GetRow(c) > 0))
        {
            under.IsEnabled = true;
        }
        ToolbarStart.IsVisible = true;
        ToolbarEnd.IsVisible = true;
        IdeErrors.Write($"First start: {settings.Look?.Shown ?? "Passenger"}, {settings.Theme}");
        if (Editors.Active is not null)
        {
            FocusEditor();
        }
        else if (welcome is { IsEffectivelyVisible: true } shown)
        {
            shown.FocusSearch();
        }
    }
}
