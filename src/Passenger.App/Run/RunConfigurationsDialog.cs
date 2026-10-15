using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Workspace;

namespace Passenger.App.Run;

/// <summary>Rider's Run/Debug Configurations dialog (Run, Edit Configurations): the configurations down the left under
/// Add, Copy and Remove, the chosen one's fields on the right, OK, Cancel and Apply below. The generated ones are marked so
/// and change only by copying them, as Rider shows launch profiles; the folder's own change in place. It edits a copy of
/// the folder's own, which Apply and OK hand back with the one chosen.</summary>
public sealed class RunConfigurationsDialog : Window
{
    private const double LabelWidth = 170;

    private readonly IReadOnlyList<Entry> generated;
    private readonly List<Entry> mine;
    private readonly IReadOnlyList<ProjectModel> projects;
    private readonly Action<IReadOnlyList<RunConfiguration>, string?> apply;
    private readonly ListBox list = new() { Classes = { "tree" } };
    private readonly StackPanel variables = new() { Spacing = 4 };
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Control form;
    private readonly TextBlock nothing = new() { Text = "No configuration is chosen", Classes = { "muted" }, Margin = new Thickness(0, 8) };

    /// <summary>Set while the fields show a configuration, so what they hold then is no edit of it, and while the list is
    /// made again under the fields, which show its chosen row already.</summary>
    private bool showing;

    public RunConfigurationsDialog(IReadOnlyList<RunConfiguration> generated, IReadOnlyList<RunConfiguration> own,
        IReadOnlyList<ProjectModel> projects, string? chosen, Action<IReadOnlyList<RunConfiguration>, string?> apply)
    {
        this.generated = [.. generated.Select(c => new Entry(c))];
        mine = [.. own.Select(c => new Entry(c))];
        this.projects = projects;
        this.apply = apply;
        Title = "Run/Debug Configurations";
        Width = 980;
        Height = 660;
        MinWidth = 700;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Chrome"));

        AddButton = Tool("Icon.Add", "Add New Configuration");
        CopyButton = Tool("Icon.Copy", "Copy Configuration");
        RemoveButton = Tool("Icon.Minimize", "Remove Configuration");
        AddButton.Click += (_, _) => Add();
        CopyButton.Click += (_, _) => Copy();
        RemoveButton.Click += (_, _) => Remove();
        list.ItemTemplate = new FuncDataTemplate<Entry>((row, _) => RowView(row?.Configuration, row?.Configuration.WhyNotHere(projects)));
        list.SelectionChanged += (_, _) =>
        {
            if (!showing)
            {
                ShowChosen();
            }
        };

        NameBox = Field();
        ProjectBox = new ComboBox { MinWidth = 280 };
        FrameworkBox = new ComboBox { MinWidth = 160 };
        ProfileBox = new ComboBox { MinWidth = 220 };
        ProjectBox.ItemTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock { Text = item is ProjectModel p ? p.Name : item?.ToString() });
        ProgramBox = Field("The project's own program");
        ArgumentsBox = Field();
        WorkingDirectoryBox = Field("The project's output folder");
        BuildBox = new CheckBox { Content = "Build the project first" };
        ConsoleBox = new CheckBox { Content = "Use external console" };
        ToolTip.SetTip(ConsoleBox, "Runs and debugs the program in a tab of the Terminal window, a real terminal it reads keys from and draws on, rather than in the Run and Debug windows' consoles");
        BrowserBox = new CheckBox { Content = "After launch" };
        ToolTip.SetTip(BrowserBox, "Opens the URL in the browser once the program listens there");
        BrowserUrlBox = Field();
        AddVariableButton = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Glyph("Icon.Add"), new TextBlock { Text = "Add variable" } } },
        };
        NameBox.TextChanged += (_, _) => Edit(c => c with { Name = NameBox.Text?.Trim() ?? string.Empty });
        ProjectBox.SelectionChanged += (_, _) => Edit(c => ProjectBox.SelectedItem is ProjectModel project ? c with { Project = project.Path, Framework = string.Empty, LaunchProfile = string.Empty } : c, reshow: true);
        FrameworkBox.SelectionChanged += (_, _) => Edit(c => c with { Framework = FrameworkBox.SelectedIndex <= 0 ? string.Empty : FrameworkBox.SelectedItem as string ?? string.Empty });
        ProfileBox.SelectionChanged += (_, _) => Edit(FromProfile, reshow: true);
        ProgramBox.TextChanged += (_, _) => Edit(c => c with { Program = ProgramBox.Text?.Trim() ?? string.Empty });
        ArgumentsBox.TextChanged += (_, _) => Edit(c => c with { Arguments = ArgumentsBox.Text ?? string.Empty });
        WorkingDirectoryBox.TextChanged += (_, _) => Edit(c => c with { WorkingDirectory = WorkingDirectoryBox.Text?.Trim() ?? string.Empty });
        BuildBox.IsCheckedChanged += (_, _) => Edit(c => c with { BuildBeforeLaunch = BuildBox.IsChecked == true });
        ConsoleBox.IsCheckedChanged += (_, _) => Edit(c => c with { Console = ConsoleBox.IsChecked == true ? ConsoleKind.Terminal : ConsoleKind.Internal });
        BrowserBox.IsCheckedChanged += (_, _) => Edit(c => c with { LaunchBrowser = BrowserBox.IsChecked == true });
        BrowserUrlBox.TextChanged += (_, _) => Edit(c => c with { BrowserUrl = BrowserUrlBox.Text?.Trim() ?? string.Empty });
        AddVariableButton.Click += (_, _) => AddVariable(string.Empty, string.Empty, focus: true);

        OkButton = new Button { Content = "OK", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        Button applyButton = new() { Content = "Apply" };
        OkButton.Click += (_, _) => Ok();
        cancel.Click += (_, _) => Close(false);
        applyButton.Click += (_, _) => Apply();
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable("Passenger.Editor.Error"));

        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 6), Children = { AddButton, RemoveButton, CopyButton } };
        DockPanel left = new() { Width = 270 };
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(list);
        Border leftPane = new() { BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable("Passenger.Divider"));

        form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Row("Name", NameBox),
                note,
                Row("Project", ProjectBox),
                Row("Target framework", FrameworkBox),
                Row("Launch profile", ProfileBox),
                Row("Exe path", ProgramBox),
                Row("Program arguments", ArgumentsBox),
                Row("Working directory", WorkingDirectoryBox),
                Row("Environment variables", new StackPanel { Spacing = 6, Children = { VariablesHeader(), variables, AddVariableButton } }, top: true),
                Row(string.Empty, ConsoleBox),
                Row("Open browser", BrowserBox),
                Row("URL", BrowserUrlBox),
                Row("Before launch", BuildBox),
            },
        };
        Border editorPane = new()
        {
            Classes = { "dialogPage" },
            Child = new ScrollViewer { Content = new Panel { Margin = new Thickness(20, 16, 20, 16), Children = { form, nothing } } },
        };

        DockPanel bottomBar = new();
        StackPanel buttons = DialogFooter.Of(OkButton, cancel, applyButton);
        DockPanel.SetDock(buttons, Dock.Right);
        bottomBar.Children.Add(buttons);
        bottomBar.Children.Add(problem);
        Border bottom = new() { Classes = { "footerBar" }, Child = bottomBar };

        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(leftPane, Dock.Left);
        root.Children.Add(bottom);
        root.Children.Add(leftPane);
        root.Children.Add(editorPane);
        Content = root;

        List();
        list.SelectedItem = Entries.FirstOrDefault(e => e.Configuration.Name == chosen) ?? Entries.FirstOrDefault();
        ShowChosen();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => list.Focus(), DispatcherPriority.Loaded);
    }

    public Button AddButton { get; }

    public Button CopyButton { get; }

    public Button RemoveButton { get; }

    public Button OkButton { get; }

    public Button AddVariableButton { get; }

    public TextBox NameBox { get; }

    public ComboBox ProjectBox { get; }

    public ComboBox FrameworkBox { get; }

    public ComboBox ProfileBox { get; }

    public TextBox ProgramBox { get; }

    public TextBox ArgumentsBox { get; }

    public TextBox WorkingDirectoryBox { get; }

    public CheckBox BuildBox { get; }

    /// <summary>Rider's Use external console: the program runs in a tab of the Terminal window.</summary>
    public CheckBox ConsoleBox { get; }

    /// <summary>Rider's Open browser, After launch: the browser opens at the URL once the program listens.</summary>
    public CheckBox BrowserBox { get; }

    public TextBox BrowserUrlBox { get; }

    /// <summary>Every configuration listed: the generated ones, then the folder's own as they stand in the dialog.</summary>
    public IReadOnlyList<RunConfiguration> Rows => [.. Entries.Select(e => e.Configuration)];

    /// <summary>The configuration chosen in the list, as the fields show it.</summary>
    public RunConfiguration? Chosen => (list.SelectedItem as Entry)?.Configuration;

    private IReadOnlyList<Entry> Entries => list.ItemsSource as IReadOnlyList<Entry> ?? [];

    /// <summary>Why the last Apply or OK was refused, or empty.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>What the fields cannot say of the configuration chosen: where a generated one comes from, why one cannot
    /// run here.</summary>
    public string Note => note.Text ?? string.Empty;

    /// <summary>The environment table's rows as they stand: each variable's name and value boxes.</summary>
    public IReadOnlyList<(TextBox Name, TextBox Value)> Variables =>
        [.. variables.Children.OfType<Grid>().Select(g => ((TextBox)g.Children[0], (TextBox)g.Children[1]))];

    /// <summary>Chooses a configuration in the list by name; false when none has it.</summary>
    public bool Choose(string name)
    {
        if (Entries.FirstOrDefault(e => e.Configuration.Name == name) is not { } row)
        {
            return false;
        }
        list.SelectedItem = row;
        return true;
    }

    /// <summary>OK: the configurations applied, and the dialog closed; one that cannot be kept (a name missing or taken)
    /// is said at the bottom and the dialog stays.</summary>
    public void Ok()
    {
        if (Apply())
        {
            Close(true);
        }
    }

    private bool Apply()
    {
        if (Unkeepable() is { } why)
        {
            problem.Text = why.Problem;
            list.SelectedItem = why.Row;
            return false;
        }
        problem.Text = string.Empty;
        apply([.. mine.Select(e => e.Configuration)], Chosen?.Name);
        return true;
    }

    /// <summary>The first of the folder's own that cannot be kept, and why: Rider's own checks, a name and a project.</summary>
    private (Entry Row, string Problem)? Unkeepable()
    {
        HashSet<string> names = new(generated.Select(g => g.Configuration.Name), StringComparer.OrdinalIgnoreCase);
        foreach (Entry row in mine)
        {
            RunConfiguration configuration = row.Configuration;
            if (configuration.Name.Length == 0)
            {
                return (row, "A configuration needs a name");
            }
            if (!names.Add(configuration.Name))
            {
                return (row, $"Two configurations are named {configuration.Name}");
            }
            if (configuration.Project.Length == 0)
            {
                return (row, $"{configuration.Name} has no project to run");
            }
        }
        return null;
    }

    /// <summary>Rider's Add New Configuration: a .NET project configuration named Unnamed, of the first executable project.</summary>
    private void Add()
    {
        Entry added = new(new RunConfiguration
        {
            Name = RunConfiguration.UniqueName("Unnamed", Rows.Select(r => r.Name)),
            Project = projects.FirstOrDefault()?.Path ?? string.Empty,
        });
        mine.Add(added);
        List();
        list.SelectedItem = added;
        Dispatcher.UIThread.Post(() => NameBox.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>Rider's Copy Configuration: the chosen one, generated or not, as one of the folder's own to change.</summary>
    private void Copy()
    {
        if (list.SelectedItem is not Entry source)
        {
            return;
        }
        Entry copy = new(source.Configuration with
        {
            Name = RunConfiguration.UniqueName(source.Configuration.Name, Rows.Select(r => r.Name)),
            Environment = new Dictionary<string, string>(source.Configuration.Environment),
            Generated = false,
            Unsupported = null,
        });
        mine.Insert(mine.IndexOf(source) is >= 0 and var at ? at + 1 : mine.Count, copy);
        List();
        list.SelectedItem = copy;
    }

    /// <summary>Removes the chosen one of the folder's own; a generated one stays, its project's to change.</summary>
    private void Remove()
    {
        if (list.SelectedItem is not Entry removed || mine.IndexOf(removed) is not (>= 0 and var at))
        {
            return;
        }
        mine.RemoveAt(at);
        List();
        list.SelectedItem = mine.Count > 0 ? mine[Math.Min(at, mine.Count - 1)] : generated.LastOrDefault();
    }

    /// <summary>Lists the configurations again, after one is added, copied, removed or renamed.</summary>
    private void List()
    {
        object? keep = list.SelectedItem;
        list.ItemsSource = (Entry[])[.. generated, .. mine];
        list.SelectedItem = keep;
    }

    /// <summary>Changes the chosen one of the folder's own as a field says, unless the fields are only showing it.</summary>
    private void Edit(Func<RunConfiguration, RunConfiguration> change, bool reshow = false)
    {
        if (showing || list.SelectedItem is not Entry row || !mine.Contains(row))
        {
            return;
        }
        RunConfiguration after = change(row.Configuration);
        if (ReferenceEquals(after, row.Configuration))
        {
            return;
        }
        bool renamed = after.Name != row.Configuration.Name;
        row.Configuration = after;
        if (renamed)
        {
            // A rename shows in the list as it is typed; the fields hold what they show already.
            showing = true;
            try
            {
                List();
            }
            finally
            {
                showing = false;
            }
        }
        if (reshow)
        {
            ShowChosen();
        }
    }

    /// <summary>A launch profile chosen fills the fields with its values, as choosing one in Rider's dialog does; none
    /// keeps them.</summary>
    private RunConfiguration FromProfile(RunConfiguration current)
    {
        if (ProfileBox.SelectedIndex <= 0 || Project(current) is not { } project
            || LaunchProfile.Read(project).FirstOrDefault(p => p.Name == ProfileBox.SelectedItem as string) is not { } profile)
        {
            return current with { LaunchProfile = string.Empty };
        }
        return RunConfiguration.From(project, profile) with
        {
            Name = current.Name,
            Framework = current.Framework,
            BuildBeforeLaunch = current.BuildBeforeLaunch,
            Console = current.Console,
            Unsupported = null,
        };
    }

    /// <summary>Shows the chosen configuration in the fields, read-only when it is generated.</summary>
    private void ShowChosen()
    {
        RunConfiguration? shown = Chosen;
        form.IsVisible = shown is not null;
        nothing.IsVisible = shown is null;
        CopyButton.IsEnabled = shown is not null;
        RemoveButton.IsEnabled = shown is { Generated: false };
        if (shown is null)
        {
            return;
        }
        showing = true;
        try
        {
            ProjectModel? project = Project(shown);
            NameBox.Text = shown.Name;
            ProjectBox.ItemsSource = project is null && shown.Project.Length > 0 ? (object[])[.. projects, $"{Path.GetFileName(shown.Project)} (not loaded)"] : projects;
            ProjectBox.SelectedIndex = project is null ? (shown.Project.Length > 0 ? projects.Count : -1) : IndexOf(project);
            IReadOnlyList<string> frameworks = project?.TargetFrameworks ?? [];
            FrameworkBox.ItemsSource = (string[])[frameworks.Count > 0 ? $"Default ({frameworks[0]})" : "Default", .. frameworks];
            FrameworkBox.SelectedIndex = shown.Framework.Length == 0 ? 0 : Math.Max(0, frameworks.ToList().IndexOf(shown.Framework) + 1);
            IReadOnlyList<LaunchProfile> profiles = project is null ? [] : LaunchProfile.Read(project);
            ProfileBox.ItemsSource = (string[])["None", .. profiles.Select(p => p.Name)];
            ProfileBox.SelectedIndex = Math.Max(0, profiles.ToList().FindIndex(p => p.Name == shown.LaunchProfile) + 1);
            ProgramBox.Text = shown.Program;
            ArgumentsBox.Text = shown.Arguments;
            WorkingDirectoryBox.Text = shown.WorkingDirectory;
            BuildBox.IsChecked = shown.BuildBeforeLaunch;
            ConsoleBox.IsChecked = shown.Console == ConsoleKind.Terminal;
            BrowserBox.IsChecked = shown.LaunchBrowser;
            BrowserUrlBox.Text = shown.BrowserUrl;
            variables.Children.Clear();
            foreach ((string name, string value) in shown.Environment)
            {
                AddVariable(name, value, focus: false);
            }
            note.Text = NoteOn(shown, project, shown.WhyNotHere(projects));
            note.IsVisible = note.Text.Length > 0;
            bool editable = !shown.Generated;
            foreach (Control field in (Control[])[NameBox, ProjectBox, FrameworkBox, ProfileBox, ProgramBox, ArgumentsBox, WorkingDirectoryBox, ConsoleBox, BrowserBox, BrowserUrlBox, BuildBox, AddVariableButton, variables])
            {
                field.IsEnabled = editable;
            }
        }
        finally
        {
            showing = false;
        }
    }

    /// <summary>What the fields cannot say: where a generated one comes from, and why one cannot run here.</summary>
    private static string NoteOn(RunConfiguration shown, ProjectModel? project, string? whyNot)
    {
        string name = project?.Name ?? Path.GetFileNameWithoutExtension(shown.Project);
        string origin = !shown.Generated ? string.Empty
            : shown.LaunchProfile.Length > 0 ? $"Generated from the launch profile {shown.LaunchProfile} in {name}'s Properties/launchSettings.json. Copy it to change it."
            : $"Generated for the executable project {name}. Copy it to change it.";
        return whyNot is { } why ? $"{origin} It cannot run here: {why}.".Trim() : origin;
    }

    private int IndexOf(ProjectModel project)
    {
        for (int i = 0; i < projects.Count; i++)
        {
            if (projects[i].Path == project.Path)
            {
                return i;
            }
        }
        return -1;
    }

    private ProjectModel? Project(RunConfiguration configuration) =>
        projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, configuration.Project));

    /// <summary>A row of the environment table: the variable's name and value, and the button that takes it out.</summary>
    private void AddVariable(string name, string value, bool focus)
    {
        TextBox nameBox = Field("Name");
        TextBox valueBox = Field("Value");
        nameBox.Text = name;
        valueBox.Text = value;
        Button remove = Tool("Icon.Close", "Remove Variable");
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("240,8,*,4,Auto") };
        Grid.SetColumn(valueBox, 2);
        Grid.SetColumn(remove, 4);
        row.Children.Add(nameBox);
        row.Children.Add(valueBox);
        row.Children.Add(remove);
        variables.Children.Add(row);
        nameBox.TextChanged += (_, _) => Edit(WithVariables);
        valueBox.TextChanged += (_, _) => Edit(WithVariables);
        remove.Click += (_, _) =>
        {
            variables.Children.Remove(row);
            Edit(WithVariables);
        };
        if (focus)
        {
            Dispatcher.UIThread.Post(() => nameBox.Focus(), DispatcherPriority.Loaded);
        }

        RunConfiguration WithVariables(RunConfiguration current)
        {
            Dictionary<string, string> environment = [];
            foreach ((TextBox each, TextBox its) in Variables)
            {
                if (each.Text?.Trim() is { Length: > 0 } named)
                {
                    environment[named] = its.Text ?? string.Empty;
                }
            }
            return current.Environment.Count == environment.Count && !current.Environment.Except(environment).Any() ? current : current with { Environment = environment };
        }
    }

    private static Control VariablesHeader()
    {
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("240,8,*") };
        TextBlock name = new() { Text = "Name", Classes = { "muted" } };
        TextBlock value = new() { Text = "Value", Classes = { "muted" } };
        Grid.SetColumn(value, 2);
        header.Children.Add(name);
        header.Children.Add(value);
        return header;
    }

    /// <summary>A list row: the configuration's name, greyed when it cannot run here (why in its tip), and for a generated
    /// one where it came from, muted beside it.</summary>
    private static Control RowView(RunConfiguration? row, string? whyNot)
    {
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        TextBlock name = new() { Text = row?.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (whyNot is not null)
        {
            name.Classes.Add("muted");
            ToolTip.SetTip(line, $"{row?.Name} cannot run here: {whyNot}");
        }
        line.Children.Add(name);
        if (row is { Generated: true })
        {
            line.Children.Add(new TextBlock { Text = row.LaunchProfile.Length > 0 ? "launchSettings.json" : "project", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        }
        return line;
    }

    /// <summary>A configuration as the list holds it: the same row while its values change.</summary>
    private sealed class Entry(RunConfiguration configuration)
    {
        public RunConfiguration Configuration { get; set; } = configuration;
    }

    private static Control Row(string label, Control editor, bool top = false)
    {
        TextBlock name = new() { Text = label, Width = LabelWidth, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(0, top ? 4 : 0, 0, 0) };
        DockPanel row = new();
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        row.Children.Add(editor);
        editor.HorizontalAlignment = editor is TextBox or StackPanel ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        return row;
    }

    private static TextBox Field(string? placeholder = null) => new() { Classes = { "search" }, PlaceholderText = placeholder };

    private static Button Tool(string icon, string tip)
    {
        Button button = new() { Classes = { "tool" }, Content = Glyph(icon) };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static PathIcon Glyph(string key)
    {
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }
}
