using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Workspace;

namespace Passenger.App.Editors;

/// <summary>The tabs a group's strip has no room to show, in its order, each by its tab's icon and title and its file's
/// folder: a name typed narrows them, Up and Down move, Enter or a click goes to the one chosen, Escape leaves.</summary>
public sealed class HiddenTabsPopup : SearchPopup<HiddenTab>
{
    private readonly IReadOnlyList<HiddenTab> hidden;

    public HiddenTabsPopup(IReadOnlyList<OpenDocument> documents, string? root)
        : base(Core.Plural.Of(documents.Count, "Hidden Tab"), "A file's name", PopupFrame.Medium)
    {
        hidden = [.. documents.Select(d => new HiddenTab(d, root is null ? string.Empty : Path.GetDirectoryName(Core.PathNames.Shown(d.Path, root)) ?? string.Empty))];
        QueryChanged();
    }

    protected override void QueryChanged()
    {
        string typed = Query.Trim();
        Show([.. hidden.Where(h => h.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))], "No hidden tab's name holds it");
    }

    /// <summary>A tab's row: its icon, its title and its file's folder, muted.</summary>
    protected override Control Row() => TabRow<HiddenTab>((tab, icon, name, folder) =>
    {
        icon.Data = tab.Icon;
        icon.Foreground = tab.IconBrush;
        name.Text = tab.Name;
        folder.Text = tab.Folder;
    });

    /// <summary>A row of a list of tabs (the hidden tabs', the Switcher's): an icon, a name and its detail at the row's end,
    /// which <paramref name="show"/> fills from each item the list hands the row.</summary>
    internal static Control TabRow<T>(Action<T, RowIcon, TextBlock, TextBlock> show)
        where T : class
    {
        RowIcon icon = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Gaps.Normal, 0), [DockPanel.DockProperty] = Dock.Left };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock after = ListRow.DetailText();
        DockPanel row = new() { Height = ListRow.Height, Children = { icon, after, name } };
        return row.Showing<T>(item => show(item, icon, name, after));
    }
}

/// <summary>A hidden tab's row: its document, named as its tab is, and its file's folder in the open one.</summary>
public sealed record HiddenTab(OpenDocument Document, string Folder)
{
    public string Name => Document.Title;
    public Geometry Icon => Document.Icon;
    public IBrush IconBrush => Document.IconBrush;
}
