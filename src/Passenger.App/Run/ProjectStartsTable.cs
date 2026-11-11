using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A multi-project configuration's projects, one row each: the projects it names in the order they start, then the
/// solution's other executable projects, each with what Run and Debug do with it (Start, under the debugger when debugged;
/// Start without debugging; None) and the launch profile of its own it starts in (its default, the first that runs here, or
/// one named). A project it names that is not loaded keeps its row, unchosen. Up and down move a row, which is the order
/// they start in.</summary>
public sealed class ProjectStartsTable : Border
{
    /// <summary>One project's row: its project file, its action and launch profile boxes, and the launch profiles listed, the
    /// first (empty) its default.</summary>
    public sealed record Row(string Project, ComboBox Action, ComboBox Profile, IReadOnlyList<string> Profiles, Border View, Button Up, Button Down)
    {
        /// <summary>The start it was set from, kept for what of it the table does not show; null for a project it did not name.</summary>
        public ProjectStart? Was { get; init; }
    }

    /// <summary>Each action's words, as Visual Studio's own table says them, in <see cref="ProjectLaunchAction"/>'s order.</summary>
    public static readonly IReadOnlyList<string> ActionWords = ["Start", "Start without debugging", "None"];

    /// <summary>The columns: the project, its action (wide enough for its longest), its launch profile, and its moves.</summary>
    private const string Columns = "*,220,*,72";
    private const string DefaultProfile = "Default";

    private readonly StackPanel table = new();
    private readonly TextBlock said = new() { Classes = { StyleClasses.Muted }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 6) };
    private readonly List<Row> rows = [];
    private bool setting;

    /// <summary>Styled as the Border it is, so Border.framed frames it.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    public ProjectStartsTable()
    {
        Classes.Add(StyleClasses.Framed);
        ClipToBounds = true;
        Child = new DockPanel { Children = { ColumnTitles.Of(Columns, ["Project", "Action", "Launch profile", string.Empty]), new StackPanel { Children = { table, said } } } };
    }

    /// <summary>A row's action or launch profile changed, or a row moved.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Row> Rows => rows;

    /// <summary>The rows of a configuration's starts, then of each other project, without raising <see cref="Changed"/>;
    /// <paramref name="profilesOf"/> lists a project's launch profiles.</summary>
    public void Set(IReadOnlyList<ProjectStart> starts, IReadOnlyList<ProjectModel> projects, Func<ProjectModel, IReadOnlyList<string>> profilesOf)
    {
        setting = true;
        try
        {
            rows.Clear();
            table.Children.Clear();
            foreach (ProjectStart start in starts)
            {
                ProjectModel? project = projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, start.Project));
                Add(start.Project, project?.Name, project is null ? [] : profilesOf(project), start);
            }
            foreach (ProjectModel project in projects.Where(p => !starts.Any(s => Core.PathNames.Same(s.Project, p.Path))))
            {
                Add(project.Path, project.Name, profilesOf(project), null);
            }
            Ends();
            said.IsVisible = rows.Count == 0;
            said.Text = rows.Count == 0 ? "The solution has no executable project to start" : string.Empty;
        }
        finally
        {
            setting = false;
        }
    }

    /// <summary>The starts as the table has them, in its order: each row it was set from with its action and launch profile
    /// now (one set to None kept, as Visual Studio keeps it), and each other project set to start.</summary>
    public List<ProjectStart> Starts()
    {
        List<ProjectStart> starts = [];
        foreach (Row row in rows)
        {
            ProjectLaunchAction? action = row.Action.SelectedIndex is >= 0 and < 3 ? (ProjectLaunchAction)row.Action.SelectedIndex : row.Was?.Action;
            string profile = row.Profile.SelectedIndex > 0 && row.Profile.SelectedIndex < row.Profiles.Count ? row.Profiles[row.Profile.SelectedIndex] : string.Empty;
            if (row.Was is { } was)
            {
                starts.Add(was with { Action = action, LaunchProfile = profile });
            }
            else if (action is not (ProjectLaunchAction.None or null))
            {
                starts.Add(new ProjectStart(row.Project, action, profile));
            }
        }
        return starts;
    }

    private void Add(string path, string? name, IReadOnlyList<string> profiles, ProjectStart? start)
    {
        TextBlock title = new() { Text = name ?? $"{Path.GetFileNameWithoutExtension(path)} (not loaded)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        if (name is null)
        {
            title.Classes.Add(StyleClasses.Muted);
        }
        ComboBox action = new() { Classes = { StyleClasses.Cell }, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = name is not null };
        // An action Passenger does not know is listed as written, so leaving it chosen keeps it.
        string? unknown = start is { Action: null } ? start.Launch?.ActionWritten ?? "?" : null;
        action.ItemsSource = unknown is null ? ActionWords : (string[])[.. ActionWords, $"{unknown} (not known)"];
        action.SelectedIndex = start is null ? (int)ProjectLaunchAction.None : start.Action is { } known ? (int)known : ActionWords.Count;
        ToolTip.SetTip(action, "Start runs it, under the debugger when the configuration is debugged; Start without debugging runs it either way; None leaves it");
        // Its default first, then its own profiles, and one named that its launchSettings.json lacks, kept as named.
        List<string> listed = [string.Empty, .. profiles];
        if (start is { LaunchProfile.Length: > 0 } named && !profiles.Contains(named.LaunchProfile))
        {
            listed.Add(named.LaunchProfile);
        }
        ComboBox profile = new()
        {
            Classes = { StyleClasses.Cell },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = name is not null,
            ItemsSource = (string[])[DefaultProfile, .. listed.Skip(1).Select(p => profiles.Contains(p) ? p : $"{p} (not in its launch settings)")],
            SelectedIndex = start is null ? 0 : Math.Max(0, listed.IndexOf(start.LaunchProfile)),
        };
        ToolTip.SetTip(profile, "The launch profile it starts in: Default takes the first of its launch profiles that runs here, as dotnet run does");
        Button up = ToolButtons.Of(IconKeys.ArrowUp, "Start Earlier");
        Button down = ToolButtons.Of(IconKeys.ArrowDown, "Start Later");
        StackPanel moves = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0), Children = { up, down } };
        Grid cells = new() { ColumnDefinitions = new ColumnDefinitions(Columns), MinHeight = ListRow.Height };
        Control[] parts = [title, action, profile, moves];
        for (int i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], i);
            cells.Children.Add(parts[i]);
        }
        Border view = new() { Child = cells, BorderThickness = new Thickness(0, 0, 0, 1) };
        view.Bind(BorderBrushProperty, view.GetResourceObservable(TokenKeys.Separator));
        Row row = new(path, action, profile, listed, view, up, down) { Was = start };
        rows.Add(row);
        table.Children.Add(view);
        action.SelectionChanged += (_, _) => Tell();
        profile.SelectionChanged += (_, _) => Tell();
        up.Click += (_, _) => Move(row, -1);
        down.Click += (_, _) => Move(row, 1);
    }

    /// <summary>A row moved up or down the order the projects start in.</summary>
    private void Move(Row row, int by)
    {
        int at = rows.IndexOf(row);
        int to = at + by;
        if (at < 0 || to < 0 || to >= rows.Count)
        {
            return;
        }
        rows.RemoveAt(at);
        rows.Insert(to, row);
        table.Children.RemoveAt(at);
        table.Children.Insert(to, row.View);
        Ends();
        Tell();
    }

    /// <summary>The first row's Start Earlier and the last's Start Later greyed: there is no further to move.</summary>
    private void Ends()
    {
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Up.IsEnabled = i > 0;
            rows[i].Down.IsEnabled = i < rows.Count - 1;
        }
    }

    private void Tell()
    {
        if (!setting)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
