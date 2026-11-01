using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.Navigation;
using Passenger.App.Workspace;

namespace Passenger.App.Editors;

/// <summary>The tabs a group's strip has no room to show, in its order, each by its file's icon, name and folder: a name
/// typed narrows them, Up and Down move, Enter or a click goes to the one chosen, Escape leaves.</summary>
public sealed class HiddenTabsPopup : SearchPopup<FileHit>
{
    private readonly IReadOnlyList<FileHit> hidden;

    public HiddenTabsPopup(IReadOnlyList<OpenDocument> documents, string? root)
        : base(Core.Plural.Of(documents.Count, "Hidden Tab"), "A file's name", PopupFrame.Medium)
    {
        hidden = [.. documents.Select(d => new FileHit(d.Path, root is null ? d.Name : Core.PathNames.Shown(d.Path, root)))];
        QueryChanged();
    }

    protected override void QueryChanged()
    {
        string typed = Query.Trim();
        Show([.. hidden.Where(h => h.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))], "No hidden tab's name holds it");
    }

    /// <summary>A tab's row: its file's icon, its name and its folder, muted.</summary>
    protected override Control Row()
    {
        RowIcon icon = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock folder = MutedText.Of(string.Empty);
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8, Height = ListRow.Height, Children = { icon, name, folder } };
        return row.Showing<FileHit>(hit =>
        {
            icon.Data = hit.Icon;
            icon.Foreground = hit.IconBrush;
            name.Text = hit.Name;
            folder.Text = hit.Folder;
        });
    }
}
