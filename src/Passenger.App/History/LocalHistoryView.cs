using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.History;

/// <summary>The Local History tool window: a tab for each history Show History opens (a file's, a folder's, the
/// project's), its tabs in the window's header. Show History again for the same thing goes back to its tab; a tab's ×
/// closes it. The tabs are the open folder's, and go with it.</summary>
public sealed class LocalHistoryView : DockPanel
{
    private readonly List<(string Path, string Title, HistoryTab Tab)> histories = [];
    private readonly ToolWindows.ToolTabs.Strip<HistoryTab> tabs = new();
    private readonly ContentControl body = new();
    private readonly EmptyState empty;

    public LocalHistoryView()
    {
        empty = new EmptyState();
        empty.SayWithKeys(AppActions.ShowLocalHistory, keys => HistoryWords.ListsVersionsHere(ActionTips.Named(HistoryWords.ShowLocalHistory, keys)));
        body.Content = empty;
        Children.Add(body);
        tabs.Chosen += (_, tab) => Show(tab);
    }

    /// <summary>The tabs, for the tool window's header.</summary>
    public Control Tabs => tabs;

    public IReadOnlyList<string> Titles => [.. histories.Select(h => h.Title)];

    /// <summary>The tab shown, if any.</summary>
    public HistoryTab? Shown { get; private set; }

    /// <summary>A history's tab: the one open for the path, else a new one after the others, made by <paramref name="make"/>.</summary>
    public HistoryTab Show(string path, string title, Func<HistoryTab> make)
    {
        HistoryTab tab = histories.FirstOrDefault(h => PathNames.Same(h.Path, path)).Tab ?? Add(path, title, make());
        Show(tab);
        return tab;
    }

    /// <summary>Closes a tab, as its × does; the one before it is shown when it was.</summary>
    public void Close(HistoryTab tab)
    {
        int at = histories.FindIndex(h => h.Tab == tab);
        if (at < 0)
        {
            return;
        }
        histories.RemoveAt(at);
        tab.Close();
        if (Shown == tab)
        {
            Show(histories.Count == 0 ? null : histories[Math.Max(0, at - 1)].Tab);
        }
        else
        {
            DrawTabs();
        }
    }

    /// <summary>Every tab goes: the folder they were of was left.</summary>
    public void CloseAll()
    {
        foreach ((_, _, HistoryTab tab) in histories)
        {
            tab.Close();
        }
        histories.Clear();
        Show(null);
    }

    private HistoryTab Add(string path, string title, HistoryTab tab)
    {
        histories.Add((path, title, tab));
        return tab;
    }

    private void Show(HistoryTab? tab)
    {
        Shown = tab;
        body.Content = tab ?? (Control)empty;
        DrawTabs();
    }

    private void DrawTabs()
    {
        tabs.Clear();
        foreach ((string path, string title, HistoryTab tab) in histories)
        {
            ShownWords.Set(tabs.Add(tab, Words.Verbatim(title), () => Close(tab)), ToolTip.TipProperty, Words.Verbatim(path));
        }
        if (Shown is not null)
        {
            tabs.Choice = Shown;
        }
    }
}
