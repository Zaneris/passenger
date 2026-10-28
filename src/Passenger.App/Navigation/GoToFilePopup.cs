using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Explorer;
using Passenger.App.Workspace;

namespace Passenger.App.Navigation;

/// <summary>Go to File: a query over every file in the folder, ranked as it is typed, the recent files when it is
/// empty. Up and Down move through the results, Enter opens one, Escape leaves.</summary>
public sealed partial class GoToFilePopup : SearchPopup<FileHit>
{
    private readonly FileIndex index;
    private readonly IReadOnlyList<string> recent;

    public GoToFilePopup(FileIndex index, IReadOnlyList<string> recent)
        : base("Go to File", "File name, CamelHumps or a path with /", PopupFrame.Wide)
    {
        this.index = index;
        this.recent = recent;
        QueryChanged();
    }

    /// <summary>The place a line after the file's name asked for ("Foo.cs:20", "Foo.cs:20:4", counted from one, as a
    /// stack trace writes it), from the start of its line when no column is given; null for the file alone.</summary>
    public Passenger.Text.TextPosition? At { get; private set; }

    /// <summary>A query's name and the line and column after it: "name:line[:column]".</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<name>.*?):(?<line>\d+)(?::(?<column>\d+))?\s*$")]
    private static partial System.Text.RegularExpressions.Regex Place();

    /// <summary>A query's file name and the place after it (<see cref="At"/>), which Go to Everything's files take too.</summary>
    public static (string Name, Passenger.Text.TextPosition? At) PlaceIn(string query)
    {
        if (Place().Match(query) is { Success: true } place && place.Groups["name"].Value.Trim().Length > 0
            && int.TryParse(place.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture, out int line) && line > 0)
        {
            int column = int.TryParse(place.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture, out int given) && given > 0 ? given : 1;
            return (place.Groups["name"].Value, new Passenger.Text.TextPosition(line - 1, column - 1));
        }
        return (query, null);
    }

    protected override void QueryChanged()
    {
        (string query, At) = PlaceIn(Query);
        string root = index.Root ?? string.Empty;
        IEnumerable<string> paths = query.Trim().Length == 0
            ? recent.Select(full => Core.PathNames.Shown(full, root))
            : index.Search(query);
        Show([.. paths.Select(p => new FileHit(Path.Combine(root, p), p))], query.Trim().Length == 0 ? "No recent files" : "Nothing matches");
    }

    /// <summary>A file's row: its icon, its name and its folder, muted.</summary>
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

/// <summary>One result: the file's name, the folder it is in, and its icon.</summary>
public sealed record FileHit(string FullPath, string RelativePath)
{
    public string Name => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) is { Length: > 0 } folder ? folder : string.Empty;
    public Geometry Icon => FileIcons.ForFile(FullPath);
    public IBrush IconBrush => FileIcons.BrushForFile(FullPath);
}
