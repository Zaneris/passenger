using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.Containers;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A run of services' services, one row each, a Compose configuration's and an Aspire AppHost's alike: a Compose
/// service as Compose's own model has it, its name and what it runs (its image, or the Dockerfile it is built from), the
/// project a .NET service runs (found from its Dockerfile, or chosen here among the solution's projects); an AppHost's
/// .NET resource by its name, its project the one its AppHost names (<see cref="SetNamed"/>); and what Run does with each:
/// a .NET service Run, Debug or Don't start, any other Start or Don't start. Until the services are read, or when they
/// cannot be, it says why instead.</summary>
public sealed class ComposeServicesTable : Border
{
    /// <summary>One service's row: its name, the project chosen (null for none) and what it does.</summary>
    public sealed record Row(string Service, ComboBox Project, ComboBox Action, Border View)
    {
        /// <summary>Whether it runs a project of the solution: a .NET service.</summary>
        public bool IsDotnet => Project.SelectedIndex > 0;

        /// <summary>The project its AppHost names, which is not chosen here; null for a Compose service.</summary>
        public string? Named { get; init; }
    }

    private const string Columns = "*,*,130";
    private const string NoProject = "None: as Compose says";

    private readonly StackPanel table = new();
    private readonly TextBlock said = new() { Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 6) };
    private readonly List<Row> rows = [];
    private IReadOnlyList<ProjectModel> projects = [];
    private bool setting;

    /// <summary>Styled as the Border it is, so Border.framed frames it.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <param name="named">What a row stands for, the first column's title: a Compose service, an AppHost's resource.</param>
    public ComposeServicesTable(string named = "Service")
    {
        Classes.Add("framed");
        ClipToBounds = true;
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions(Columns) };
        string[] titles = [named, "Project", "Run does"];
        for (int i = 0; i < titles.Length; i++)
        {
            TextBlock title = new() { Text = titles[i], Classes = { "muted" }, Margin = new Thickness(8, 5) };
            Grid.SetColumn(title, i);
            header.Children.Add(title);
        }
        Border headerFrame = new() { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerFrame.Bind(BorderBrushProperty, headerFrame.GetResourceObservable(TokenKeys.Separator));
        DockPanel.SetDock(headerFrame, Dock.Top);
        Child = new DockPanel { Children = { headerFrame, new StackPanel { Children = { table, said } } } };
    }

    /// <summary>A row's project or action changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Row> Rows => rows;

    /// <summary>Why no services are listed (being read, Compose's refusal), or empty when they are.</summary>
    public string Said => said.IsVisible ? said.Text ?? string.Empty : string.Empty;

    /// <summary>Says why there are no rows, in their place.</summary>
    public void Say(string why)
    {
        rows.Clear();
        table.Children.Clear();
        said.Text = why;
        said.IsVisible = true;
    }

    /// <summary>The services as a configuration has them, without raising <see cref="Changed"/>.</summary>
    public void Set(IReadOnlyList<(ComposeService Service, string? Project, bool Built)> services, IReadOnlyList<ProjectModel> solution, RunConfiguration configuration,
        bool editable)
    {
        setting = true;
        try
        {
            projects = solution;
            rows.Clear();
            table.Children.Clear();
            said.IsVisible = services.Count == 0;
            said.Text = services.Count == 0 ? "Compose's model of these files has no services" : string.Empty;
            foreach ((ComposeService service, string? project, bool built) in services)
            {
                string runs = built && service.Dockerfile is { } file ? file : service.Image ?? string.Empty;
                Add(service.Name, runs, project, built, named: false, configuration.ServiceActions.GetValueOrDefault(service.Name, ServiceStart.Run), editable);
            }
        }
        finally
        {
            setting = false;
        }
    }

    /// <summary>An AppHost's .NET resources, each with the project its AppHost names (shown, not chosen here), without
    /// raising <see cref="Changed"/>.</summary>
    public void SetNamed(IReadOnlyList<(string Name, string Project)> named, IReadOnlyList<ProjectModel> solution, RunConfiguration configuration, bool editable)
    {
        setting = true;
        try
        {
            projects = [.. solution, .. named.Select(n => n.Project).Where(p => !solution.Any(s => Core.PathNames.Same(s.Path, p)))
                .Distinct(Core.PathNames.Comparer).Select(ProjectModel.Unevaluated)];
            rows.Clear();
            table.Children.Clear();
            said.IsVisible = named.Count == 0;
            said.Text = named.Count == 0 ? "This AppHost declares no project resources" : string.Empty;
            foreach ((string name, string project) in named)
            {
                Add(name, string.Empty, project, choosable: false, named: true, configuration.ServiceActions.GetValueOrDefault(name, ServiceStart.Run), editable);
            }
        }
        finally
        {
            setting = false;
        }
    }

    /// <summary>Each AppHost resource's project, as its AppHost names it, by its name.</summary>
    public Dictionary<string, string> NamedProjects() =>
        rows.Where(row => row.Named is not null).ToDictionary(row => row.Service, row => row.Named!, StringComparer.Ordinal);

    /// <summary>The projects chosen for services, by name: only those the configuration keeps (a service whose project its
    /// Dockerfile names keeps none unless another was chosen).</summary>
    public Dictionary<string, string> Projects(IReadOnlyDictionary<string, string> before, IReadOnlyList<(ComposeService Service, string? Project, bool Built)> found)
    {
        Dictionary<string, string> chosen = new(before, StringComparer.Ordinal);
        foreach (Row row in rows)
        {
            string? now = row.Project.SelectedIndex > 0 ? projects[row.Project.SelectedIndex - 1].Path : null;
            string? own = found.FirstOrDefault(f => f.Service.Name == row.Service).Project;
            if (now is null || (own is not null && Core.PathNames.Same(now, own) && !before.ContainsKey(row.Service)))
            {
                chosen.Remove(row.Service);
            }
            else
            {
                chosen[row.Service] = now;
            }
        }
        return chosen;
    }

    /// <summary>What each service does, as the configuration keeps it: those that do not simply run, or with
    /// <paramref name="every"/> each (an AppHost's resource kept as Run is not debugged when the AppHost is).</summary>
    public Dictionary<string, ServiceStart> Actions(bool every = false)
    {
        Dictionary<string, ServiceStart> actions = new(StringComparer.Ordinal);
        foreach (Row row in rows)
        {
            ServiceStart action = Action(row);
            if (every || action != ServiceStart.Run)
            {
                actions[row.Service] = action;
            }
        }
        return actions;
    }

    private static ServiceStart Action(Row row) => (row.Action.SelectedItem as string) switch
    {
        "Debug" => ServiceStart.Debug,
        "Don't start" => ServiceStart.DontStart,
        _ => ServiceStart.Run,
    };

    private void Add(string service, string runs, string? project, bool choosable, bool named, ServiceStart action, bool editable)
    {
        StackPanel name = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = service, VerticalAlignment = VerticalAlignment.Center });
        if (runs.Length > 0)
        {
            name.Children.Add(new TextBlock { Text = runs, Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        }
        ComboBox projectBox = new()
        {
            Classes = { "cell" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = (string[])[NoProject, .. projects.Select(p => p.Name)],
            // An AppHost's resource runs the project its AppHost's code names.
            IsEnabled = editable && !named,
            // A service that runs its image has no project to choose.
            IsVisible = choosable || named,
        };
        int found = project is null ? -1 : projects.ToList().FindIndex(p => Core.PathNames.Same(p.Path, project));
        projectBox.SelectedIndex = found + 1;
        ToolTip.SetTip(projectBox, named ? "The project the AppHost runs for this resource, as its code names it"
            : choosable ? "The project this service runs: Run builds it and runs it in the service's container" : "Runs its image: no project of the solution");
        ToolTip.SetShowOnDisabled(projectBox, true);
        ComboBox actionBox = new() { Classes = { "cell" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = editable };
        Grid cells = new() { ColumnDefinitions = new ColumnDefinitions(Columns), MinHeight = ListRow.Height };
        Control[] parts = [name, projectBox, actionBox];
        for (int i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], i);
            cells.Children.Add(parts[i]);
        }
        Border view = new() { Child = cells, BorderThickness = new Thickness(0, 0, 0, 1) };
        view.Bind(BorderBrushProperty, view.GetResourceObservable(TokenKeys.Separator));
        Row row = new(service, projectBox, actionBox, view) { Named = named ? project : null };
        Actions(row, action);
        rows.Add(row);
        table.Children.Add(view);
        projectBox.SelectionChanged += (_, _) =>
        {
            if (!setting)
            {
                setting = true;
                try
                {
                    Actions(row, Action(row));
                }
                finally
                {
                    setting = false;
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
        actionBox.SelectionChanged += (_, _) =>
        {
            if (!setting)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>A row's choices: a .NET service's Run, Debug and Don't start (Debug is kept for when containers are
    /// debugged; Run runs it), any other's Start and Don't start.</summary>
    private static void Actions(Row row, ServiceStart action)
    {
        string[] choices = row.IsDotnet ? ["Run", "Debug", "Don't start"] : ["Start", "Don't start"];
        row.Action.ItemsSource = choices;
        row.Action.SelectedIndex = action switch
        {
            ServiceStart.DontStart => choices.Length - 1,
            ServiceStart.Debug when row.IsDotnet => 1,
            _ => 0,
        };
    }
}
