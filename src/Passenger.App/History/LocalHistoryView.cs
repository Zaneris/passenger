using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.Core;

namespace Passenger.App.History;

/// <summary>Rider's Local History tool window: a tab for each history Show History opens (a file's, a folder's, the
/// project's), its tabs in the window's header. Show History again for the same thing goes back to its tab; a tab's ×
/// closes it. The tabs are the open folder's, and go with it.</summary>
public sealed class LocalHistoryView : DockPanel
{
    private readonly List<(string Path, string Title, HistoryTab Tab)> histories = [];
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl body = new();
    private readonly TextBlock empty;

    public LocalHistoryView()
    {
        empty = new TextBlock
        {
            Text = "Show History on a file or folder lists its versions here",
            Classes = { "emptyHint" },
        };
        body.Content = empty;
        Children.Add(body);
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
        tabs.Children.Clear();
        foreach ((string path, string title, HistoryTab tab) in histories)
        {
            tabs.Children.Add(Tab(title, path, tab));
        }
    }

    private ToggleButton Tab(string title, string tip, HistoryTab tab)
    {
        PathIcon glyph = new() { Classes = { "small" } };
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable("Icon.Close"));
        Button close = new() { Classes = { "tabClose" }, Content = glyph, Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, e) =>
        {
            Close(tab);
            e.Handled = true;
        };
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }, close },
        };
        ToggleButton button = new() { Classes = { "panelTab" }, IsChecked = tab == Shown, Content = content };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => Show(tab);
        return button;
    }
}
