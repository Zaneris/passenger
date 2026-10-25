using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Roslyn.Extension;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>The Run/Debug Configurations dialog (Run, Edit Configurations): the configurations down the left under Add,
/// Copy and Remove, the chosen one's fields on the right, OK, Cancel and Apply below. A generated one changes in place
/// but for what makes it the one it is (its name, project and launch profile): each field changed from what its source gives
/// is marked, and kept with the folder over that source, or written to its launch profile; Reset puts it back as its source
/// makes it. It edits copies, which Apply and OK hand back with the one chosen.</summary>
public sealed class RunConfigurationsDialog : Window
{
    private readonly IReadOnlyList<Entry> generated;
    private readonly List<Entry> mine;
    private readonly IReadOnlyList<ProjectModel> projects;
    private readonly Action<IReadOnlyList<RunConfiguration>, IReadOnlyList<RunConfiguration>, string?> apply;
    private readonly IReadOnlyList<string> singleFileAppHosts;
    private readonly ListBox list = new() { Classes = { "tree" } };
    private readonly StackPanel variables = new() { Spacing = 4 };
    private readonly StackPanel linkedAppHosts = new() { Spacing = 4 };
    private readonly Control linkedRow;
    private readonly Control aspireRow;
    private readonly CancellationTokenSource aspireLifetime = new();
    private int aspireReading;
    private bool aspireClosed;
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Control form;
    private readonly TextBlock nothing = new() { Text = "No configuration is chosen", Classes = { "muted" }, Margin = new Thickness(0, 8) };

    /// <summary>Each row's label (or a box's own words) with the fields it shows, by the store's names, marked when the
    /// generated configuration chosen has one changed from its source.</summary>
    private readonly List<(TextBlock Label, string Title, string[] Fields)> marks = [];

    /// <summary>What the chosen generated configuration has changed from its source, with Write to Launch Profile and Reset.</summary>
    private readonly StackPanel changes = new() { Spacing = 6 };
    private readonly TextBlock changesText = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>What the last Write to Launch Profile did, said in place of the changes until the configuration changes
    /// again or another is chosen.</summary>
    private string? written;

    /// <summary>Set while the fields show a configuration, so what they hold then is no edit of it, and while the list is
    /// made again under the fields, which show its chosen row already.</summary>
    private bool showing;
    private HashSet<string> runningAppHosts = new(StringComparer.FromComparison(Core.PathNames.Comparison));

    public RunConfigurationsDialog(IReadOnlyList<RunConfiguration> generated, IReadOnlyList<RunConfiguration> own,
        IReadOnlyList<ProjectModel> projects, string? chosen, Action<IReadOnlyList<RunConfiguration>, IReadOnlyList<RunConfiguration>, string?> apply,
        Func<string, bool>? isRunning = null)
    {
        FormRows.Scope(this);
        IsRunning = isRunning ?? (_ => false);
        this.generated = [.. generated.Select(c => new Entry(c with { Source = null }, c.Source ?? c with { Source = null }))];
        mine = [.. own.Select(c => new Entry(c))];
        this.projects = projects;
        singleFileAppHosts = [.. generated.Concat(own).Where(c => c.Aspire && Path.GetExtension(c.Project).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Project).Distinct(StringComparer.Ordinal)];
        this.apply = apply;
        Title = "Run/Debug Configurations";
        Width = 980;
        Height = 660;
        MinWidth = 700;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddButton = ToolButtons.Of(IconKeys.Add, "Add New Configuration");
        CopyButton = ToolButtons.Of(IconKeys.Copy, "Copy Configuration");
        RemoveButton = ToolButtons.Of(IconKeys.Minimize, "Remove Configuration");
        AddButton.Click += (_, _) => ChooseKind();
        CopyButton.Click += (_, _) => Copy();
        RemoveButton.Click += (_, _) => Remove();
        list.ItemTemplate = new FuncDataTemplate<Entry>((row, _) => RowView(row?.Configuration, row?.Configuration.WhyNotHere(projects),
            row is null ? 0 : Math.Max(IsRunning(row.Configuration.Name) ? 1 : 0,
                runningAppHosts.Count(path => Core.PathNames.Same(path, row.Configuration.Project)
                    || row.Configuration.LinkedAppHosts.Any(linked => Core.PathNames.Same(linked, path)))), row?.IsChanged == true));
        list.SelectionChanged += (_, _) =>
        {
            if (!showing)
            {
                written = null;
                ShowChosen();
            }
        };

        NameBox = Field();
        ProjectBox = new ComboBox { Classes = { "form" } };
        FrameworkBox = new ComboBox { Classes = { "form" } };
        ProfileBox = new ComboBox { Classes = { "form" } };
        ProjectBox.ItemTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock { Text = item is ProjectModel p ? p.Name : item?.ToString() });
        ProgramBox = Field("The project's own program");
        // Named, as the boxes a screenshot's --click-name reaches.
        ArgumentsBox = Field();
        ArgumentsBox.Name = "ArgumentsBox";
        WorkingDirectoryBox = Field("The project's output folder");
        BuildBox = new CheckBox { Content = "Build the project first" };
        // Its words a TextBlock of their own, which a change from the source marks as a row's label is marked.
        TextBlock consoleWords = new() { Text = "Use external console" };
        ConsoleBox = new CheckBox { Content = consoleWords, Name = "ConsoleBox" };
        ToolTip.SetTip(ConsoleBox, "Runs and debugs the program in a tab of the Terminal window, a real terminal it reads keys from and draws on, rather than in the Run and Debug windows' consoles");
        BrowserBox = new CheckBox { Content = "After launch" };
        ToolTip.SetTip(BrowserBox, "Opens the URL in the browser once the program listens there");
        BrowserUrlBox = Field();
        AddVariableButton = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Glyph(IconKeys.Add), new TextBlock { Text = "Add variable" } } },
        };
        NameBox.TextChanged += (_, _) => Edit(c => c with { Name = NameBox.Text?.Trim() ?? string.Empty });
        ProjectBox.SelectionChanged += (_, _) => Edit(c => ProjectBox.SelectedItem switch
        {
            ProjectModel project => c with { Project = project.Path, Aspire = c.Kind == RunKind.Project && project.IsAspireHost, LinkedAppHosts = [], Framework = string.Empty, LaunchProfile = string.Empty },
            string file when singleFileAppHosts.Contains(file) => c with { Project = file, Aspire = true, LinkedAppHosts = [], Framework = string.Empty, LaunchProfile = string.Empty },
            _ => c,
        }, reshow: true);
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

        KindText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        ComposeFilesBox = Field("compose.yaml; compose.override.yaml");
        EnvFilesBox = Field("The .env beside the first file");
        ProfilesBox = Field("None");
        ServicesTable.Changed += (_, _) => Edit(c => c with
        {
            ServiceProjects = ServicesTable.Projects(c.ServiceProjects, shownServices),
            ServiceActions = ServicesTable.Actions(),
            // The table says which start: a list kept from before it gives way to its choices.
            Services = [],
        });
        LaunchServiceBox = Field("The first .NET service");
        ModeBox = new ComboBox { Classes = { "form" }, ItemsSource = (string[])[FastMode, ImagesMode] };
        ToolTip.SetTip(ModeBox, "Fast runs each .NET service in its image's runtime stage with Passenger's build mounted in, so a code change needs no image rebuild; the other builds every image as Compose says");
        TextBlock keepWords = new() { Text = "Keep backing services running after Stop" };
        KeepBackingBox = new CheckBox { Content = keepWords };
        TextBlock telemetryWords = new() { Text = "Collect logs and traces with Aspire's dashboard" };
        TelemetryBox = new CheckBox { Content = telemetryWords, Name = "TelemetryBox" };
        ToolTip.SetTip(TelemetryBox, $"Runs Aspire's dashboard ({Containers.ContainerTelemetry.Image}, pulled the first time) beside the services and tells each "
            + ".NET service to send it its OpenTelemetry, unless its own files name where; the Containers window's Telemetry tab and an agent's telemetry read it");
        DockerfileBox = Field("The project's own, beside it");
        ContextBox = Field("The project's DockerfileContext, else the solution's folder");
        BuildArgumentsBox = Field("NAME=value");
        RunArgumentsBox = Field("-p 8080:8080 -e NAME=value --env-file .env");
        ComposeFilesBox.TextChanged += (_, _) => Edit(c => c with { ComposeFiles = Paths(ComposeFilesBox.Text) });
        EnvFilesBox.TextChanged += (_, _) => Edit(c => c with { EnvFiles = Paths(EnvFilesBox.Text) });
        ProfilesBox.TextChanged += (_, _) => Edit(c => c with { Profiles = Names(ProfilesBox.Text) });
        foreach (TextBox read in (TextBox[])[ComposeFilesBox, EnvFilesBox, ProfilesBox])
        {
            // Compose's model of what was typed, once the person moves on from it.
            read.LostFocus += (_, _) => ShowServices();
        }
        LaunchServiceBox.TextChanged += (_, _) => Edit(c => c with { LaunchService = LaunchServiceBox.Text?.Trim() ?? string.Empty });
        ModeBox.SelectionChanged += (_, _) => Edit(c => c with { Mode = ModeBox.SelectedIndex == 1 ? ContainerMode.Images : ContainerMode.Fast });
        KeepBackingBox.IsCheckedChanged += (_, _) => Edit(c => c with { KeepBackingServices = KeepBackingBox.IsChecked == true });
        TelemetryBox.IsCheckedChanged += (_, _) => Edit(c => c with { Telemetry = TelemetryBox.IsChecked == true });
        DockerfileBox.TextChanged += (_, _) => Edit(c => c with { Dockerfile = Full(DockerfileBox.Text) });
        ContextBox.TextChanged += (_, _) => Edit(c => c with { Context = Full(ContextBox.Text) });
        BuildArgumentsBox.TextChanged += (_, _) => Edit(c => c with { BuildArguments = BuildArgumentsBox.Text ?? string.Empty });
        RunArgumentsBox.TextChanged += (_, _) => Edit(c => c with { RunArguments = RunArgumentsBox.Text ?? string.Empty });

        OkButton = new Button { Content = "OK", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        Button applyButton = new() { Content = "Apply" };
        OkButton.Click += (_, _) => Ok();
        cancel.Click += (_, _) => Close(false);
        applyButton.Click += (_, _) => Apply();
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));

        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 6), Children = { AddButton, RemoveButton, CopyButton } };
        DockPanel left = new() { Width = 270 };
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(list);
        Border leftPane = new() { BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable(TokenKeys.Divider));

        // Each kind's page is the rows it takes; each row names the fields it shows, which a change from the source marks.
        Control projectRow = Row("Project", ProjectBox);
        linkedRow = Row("Linked Aspire apps", linkedAppHosts, top: true, fields: ["linkedAppHosts"]);
        AspireTable.Changed += (_, _) => Edit(c => c with
        {
            ServiceProjects = new Dictionary<string, string>(c.ServiceProjects.Concat(AspireTable.NamedProjects()).GroupBy(p => p.Key).Select(g => g.Last()), StringComparer.Ordinal),
            ServiceActions = AspireTable.Actions(every: true),
        });
        aspireRow = Row("Aspire resources", AspireTable, top: true, fields: ["serviceActions", "serviceProjects"]);
        pages =
        [
            (projectRow, [RunKind.Project, RunKind.Dockerfile, RunKind.SdkContainer]),
            (linkedRow, [RunKind.Project]),
            (aspireRow, [RunKind.Project]),
            (Row("Target framework", FrameworkBox, fields: ["framework"]), [RunKind.Project, RunKind.Dockerfile, RunKind.SdkContainer]),
            (Row("Launch profile", ProfileBox), [RunKind.Project]),
            (Row("Exe path", ProgramBox, fields: ["program"]), [RunKind.Project]),
            (Row("Program arguments", ArgumentsBox, fields: ["arguments"]), [RunKind.Project]),
            (Row("Working directory", WorkingDirectoryBox, fields: ["workingDirectory"]), [RunKind.Project]),
            (Row("Compose files", ComposeFilesBox, fields: ["composeFiles"]), [RunKind.Compose]),
            (Row("Env files", EnvFilesBox, fields: ["envFiles"]), [RunKind.Compose]),
            (Row("Profiles", ProfilesBox, fields: ["profiles"]), [RunKind.Compose]),
            (Row("Services", ServicesTable, top: true, fields: ["services", "serviceActions", "serviceProjects"]), [RunKind.Compose]),
            (Row("Dockerfile", DockerfileBox, fields: ["dockerfile"]), [RunKind.Dockerfile]),
            (Row("Context", ContextBox, fields: ["context"]), [RunKind.Dockerfile]),
            (Row("Build arguments", BuildArgumentsBox, fields: ["buildArguments"]), [RunKind.Dockerfile]),
            (Row("Run arguments", RunArgumentsBox, fields: ["runArguments"]), [RunKind.Dockerfile, RunKind.SdkContainer]),
            (Row("Mode", ModeBox, fields: ["mode"]), [RunKind.Compose, RunKind.Dockerfile, RunKind.SdkContainer]),
            (Row(string.Empty, KeepBackingBox, marked: keepWords, fields: ["keepBackingServices"]), [RunKind.Compose]),
            (Row(string.Empty, TelemetryBox, marked: telemetryWords, fields: ["telemetry"]), [RunKind.Compose, RunKind.Dockerfile, RunKind.SdkContainer]),
            (Row("Environment variables", new StackPanel { Spacing = 6, Children = { VariablesHeader(), variables, AddVariableButton } }, top: true, fields: ["environment"]),
                [RunKind.Project, RunKind.Dockerfile, RunKind.SdkContainer]),
            (Row(string.Empty, ConsoleBox, marked: consoleWords, fields: ["console"]), [RunKind.Project]),
            (Row("Open browser", BrowserBox, fields: ["launchBrowser"]), [RunKind.Project, RunKind.Compose, RunKind.Dockerfile, RunKind.SdkContainer]),
            (Row("URL", BrowserUrlBox, fields: ["browserUrl"]), [RunKind.Project, RunKind.Compose, RunKind.Dockerfile, RunKind.SdkContainer]),
            (Row("Launched service", LaunchServiceBox, fields: ["launchService"]), [RunKind.Compose]),
            (Row("Before launch", BuildBox, fields: ["buildBeforeLaunch"]), [RunKind.Project]),
        ];

        WriteButton = new Button { Content = "Write to Launch Profile", Name = "WriteButton" };
        ToolTip.SetShowOnDisabled(WriteButton, true);
        ResetButton = new Button { Content = "Reset to Launch Profile", Name = "ResetButton" };
        WriteButton.Click += (_, _) => WriteToLaunchProfile();
        ResetButton.Click += (_, _) => Reset();
        changes.Children.Add(changesText);
        changes.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { WriteButton, ResetButton } });
        StackPanel rows = new() { Spacing = 10, Children = { Row("Name", NameBox), Row("Kind", KindText), note, changes } };
        foreach ((Control row, _) in pages)
        {
            rows.Children.Add(row);
        }
        form = rows;
        Border editorPane = new()
        {
            Child = new ScrollViewer { Content = new Panel { Margin = new Thickness(20, 16, 20, 16), Children = { form, nothing } } },
        };

        Border bottom = DialogFooter.Bar(DialogFooter.Of(OkButton, cancel, applyButton), problem);

        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(leftPane, Dock.Left);
        root.Children.Add(bottom);
        root.Children.Add(leftPane);
        root.Children.Add(editorPane);
        Content = root;
        Closed += (_, _) =>
        {
            aspireClosed = true;
            aspireLifetime.Cancel();
            aspireLifetime.Dispose();
        };

        List();
        list.SelectedItem = Entries.FirstOrDefault(e => e.Configuration.Name == chosen) ?? Entries.FirstOrDefault();
        ShowChosen();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => list.Focus(), DispatcherPriority.Loaded);
    }

    public Button AddButton { get; }

    public Button CopyButton { get; }

    public Button RemoveButton { get; }

    public Button OkButton { get; }

    /// <summary>Writes a generated configuration's changes into the launch profile it was made from.</summary>
    public Button WriteButton { get; }

    /// <summary>Puts a generated configuration back as its source makes it.</summary>
    public Button ResetButton { get; }

    /// <summary>Writes the changes of the generated configuration of that name, as applied, into its launch profile (the
    /// window's, as one command Undo takes back): the configuration as it then stands, or why not.</summary>
    public Func<string, (RunConfiguration? Configuration, string? Problem)>? WriteToProfile { get; init; }

    /// <summary>What the chosen generated configuration has changed from its source (or what Write to Launch Profile did),
    /// or empty.</summary>
    public string Changes => changes.IsVisible ? changesText.Text ?? string.Empty : string.Empty;

    /// <summary>The rows (their titles) marked as changed from the chosen generated configuration's source.</summary>
    public IReadOnlyList<string> Marked => [.. marks.Where(m => m.Label.Classes.Contains("changed") && m.Label.IsEffectivelyVisible).Select(m => m.Title)];

    public Button AddVariableButton { get; }

    public TextBox NameBox { get; }

    public ComboBox ProjectBox { get; }

    /// <summary>Other AppHosts that start and stop with the chosen configuration.</summary>
    public IReadOnlyList<CheckBox> LinkedAppHostBoxes => [.. linkedAppHosts.Children.OfType<CheckBox>()];

    private Func<RunConfiguration, CancellationToken, Task<IReadOnlyList<AspireRunResource>>>? readAspireServices;
    public Func<RunConfiguration, CancellationToken, Task<IReadOnlyList<AspireRunResource>>>? ReadAspireServices
    {
        get => readAspireServices;
        init
        {
            readAspireServices = value;
            ShowAspireServices();
        }
    }


    /// <summary>An Aspire configuration's .NET resources as its AppHost's code declares them, in the services table a Compose
    /// configuration's are: each one's project, as the AppHost names it, and what Run does with it (Run, Debug, Don't start).</summary>
    public ComposeServicesTable AspireTable { get; } = new("Resource");

    private void ShowAspireServices()
    {
        int generation = ++aspireReading;
        if (Chosen is not { Aspire: true, IsContainer: false } configuration || aspireClosed)
        {
            AspireTable.Say(string.Empty);
            return;
        }
        AspireTable.Say("Reading the AppHost's resources…");
        IdeErrors.Observe(Read());

        async Task Read()
        {
            try
            {
                if (ReadAspireServices is not { } read)
                {
                    throw new InvalidOperationException("Open the AppHost's solution to read its resources");
                }
                IReadOnlyList<AspireRunResource> resources = await read(configuration, aspireLifetime.Token);
                if (aspireClosed || generation != aspireReading || Chosen?.Project != configuration.Project)
                {
                    return;
                }
                bool was = showing;
                showing = true;
                try
                {
                    AspireTable.SetNamed([.. resources.Select(resource => (resource.Name, resource.Project))], projects, Chosen ?? configuration, editable: true);
                }
                finally
                {
                    showing = was;
                }
            }
            catch (OperationCanceledException) when (aspireClosed)
            {
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or Lsp.LanguageServerFailed)
            {
                if (!aspireClosed && generation == aspireReading)
                {
                    AspireTable.Say(error.Message);
                }
            }
        }
    }

    public ComboBox FrameworkBox { get; }

    public ComboBox ProfileBox { get; }

    public TextBox ProgramBox { get; }

    public TextBox ArgumentsBox { get; }

    public TextBox WorkingDirectoryBox { get; }

    public CheckBox BuildBox { get; }

    /// <summary>Use external console: the program runs in a tab of the Terminal window.</summary>
    public CheckBox ConsoleBox { get; }

    /// <summary>Open browser after launch: the browser opens at the URL once the program listens.</summary>
    public CheckBox BrowserBox { get; }

    public TextBox BrowserUrlBox { get; }

    /// <summary>The configuration's kind, which its page follows: a .NET project, Docker Compose, a Dockerfile, an SDK container.</summary>
    public TextBlock KindText { get; }

    /// <summary>A Compose configuration's files, the base first, separated by ';' (relative to the open folder or full).</summary>
    public TextBox ComposeFilesBox { get; }

    public TextBox EnvFilesBox { get; }

    public TextBox ProfilesBox { get; }

    /// <summary>A Compose configuration's services from Compose's own model: each .NET service's project and what Run does
    /// with each.</summary>
    public ComposeServicesTable ServicesTable { get; } = new();

    /// <summary>Reads a Compose configuration's services (Compose's model, through the window's engine); null where there is
    /// no engine to ask.</summary>
    public Func<RunConfiguration, CancellationToken, Task<IReadOnlyList<(Containers.ComposeService Service, string? Project, bool Built)>>>? ReadServices { get; init; }

    /// <summary>The services the table shows, as last read, and the files, env files and profiles they were read for.</summary>
    private IReadOnlyList<(Containers.ComposeService Service, string? Project, bool Built)> shownServices = [];

    private readonly Dictionary<string, IReadOnlyList<(Containers.ComposeService Service, string? Project, bool Built)>> readServices = new(StringComparer.Ordinal);

    private int reading;

    /// <summary>The table filled for the Compose configuration chosen: from what was read for its files before, else read
    /// now, off the UI thread; it says why while it cannot.</summary>
    private void ShowServices()
    {
        if (Chosen is not { Kind: RunKind.Compose } shown)
        {
            return;
        }
        string key = ServicesKey(shown);
        if (readServices.TryGetValue(key, out IReadOnlyList<(Containers.ComposeService, string?, bool)>? known))
        {
            shownServices = known;
            ServicesTable.Set(known, projects, shown, editable: true);
            return;
        }
        if (ReadServices is not { } read)
        {
            ServicesTable.Say("Its services are listed once a container engine answers");
            return;
        }
        ServicesTable.Say("Reading its services from Compose…");
        int mine = ++reading;
        IdeErrors.Observe(Fill());

        async Task Fill()
        {
            string? problem = null;
            IReadOnlyList<(Containers.ComposeService, string?, bool)> services = [];
            try
            {
                services = await Core.BackgroundWork.Run(() => read(shown, CancellationToken.None));
            }
            catch (Exception failed) when (failed is Containers.ContainerCommandException or TimeoutException or IOException or System.Text.Json.JsonException
                or System.ComponentModel.Win32Exception)
            {
                problem = failed.Message;
            }
            if (mine != reading)
            {
                return;
            }
            if (problem is not null)
            {
                ServicesTable.Say($"Compose could not read its files: {problem}");
                return;
            }
            readServices[key] = services;
            if (Chosen is { Kind: RunKind.Compose } still && ServicesKey(still) == key)
            {
                shownServices = services;
                ServicesTable.Set(services, projects, still, editable: true);
            }
        }
    }

    /// <summary>What a Compose configuration's services are read for: its files, env files, profiles and projects chosen.</summary>
    private static string ServicesKey(RunConfiguration shown) =>
        string.Join('|', [.. shown.ComposeFiles, "--", .. shown.EnvFiles, "--", .. shown.Profiles, "--", .. shown.ServiceProjects.Select(p => $"{p.Key}={p.Value}")]);

    public TextBox LaunchServiceBox { get; }

    /// <summary>Fast (Passenger's build in the runtime stage) or Build images, as deployed.</summary>
    public ComboBox ModeBox { get; }

    public CheckBox KeepBackingBox { get; }

    /// <summary>Collect logs and traces: Aspire's dashboard beside a container run's services.</summary>
    public CheckBox TelemetryBox { get; }

    public TextBox DockerfileBox { get; }

    public TextBox ContextBox { get; }

    public TextBox BuildArgumentsBox { get; }

    public TextBox RunArgumentsBox { get; }

    private const string WriteTip = "Applies the changes here, then writes those its launch profile has a place for into the project's Properties/launchSettings.json, "
        + "as one step Undo takes back; the rest stay kept with the folder";

    private const string FastMode = "Fast, from Passenger's build";

    private const string ImagesMode = "Build images, as deployed";

    /// <summary>Each kind's rows of the form, and the kinds that show each.</summary>
    private readonly (Control Row, RunKind[] Kinds)[] pages;

    /// <summary>The open folder, against which the paths typed are read and shown.</summary>
    public string? Folder { get; init; }

    private static string KindName(RunKind kind) => kind switch
    {
        RunKind.Compose => "Docker Compose",
        RunKind.Dockerfile => "Dockerfile",
        RunKind.SdkContainer => "SDK Container",
        _ => ".NET Project",
    };

    /// <summary>A path as the form shows it: relative to the open folder when inside it.</summary>
    private string Shown(string path) => Folder is { } folder && path.Length > 0 && Core.PathNames.IsUnder(path, folder)
        ? Path.GetRelativePath(folder, path).Replace('\\', '/')
        : path;

    /// <summary>A path typed, made full against the open folder.</summary>
    private string Full(string? typed) => typed?.Trim() is not { Length: > 0 } path ? string.Empty
        : Folder is { } folder ? Core.PathNames.Canonical(Path.Combine(folder, path)) : path;

    private List<string> Paths(string? typed) => [.. (typed ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Full)];

    private static List<string> Names(string? typed) => [.. (typed ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Every configuration listed: the generated ones, then the folder's own as they stand in the dialog.</summary>
    public IReadOnlyList<RunConfiguration> Rows => [.. Entries.Select(e => e.Configuration)];

    /// <summary>The configuration chosen in the list, as the fields show it.</summary>
    public RunConfiguration? Chosen => (list.SelectedItem as Entry)?.Configuration;

    public void ShowRunningAppHosts(IEnumerable<string> paths)
    {
        runningAppHosts = new HashSet<string>(paths, StringComparer.FromComparison(Core.PathNames.Comparison));
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
        apply([.. mine.Select(e => e.Configuration)], [.. generated.Select(e => e.Configuration with { Source = e.Source })], Chosen?.Name);
        return true;
    }

    /// <summary>Write to Launch Profile: the dialog applied as Apply does, then the chosen generated configuration's changes
    /// its launch profile has a place for written there; the dialog says what was written and what stays kept here.</summary>
    private void WriteToLaunchProfile()
    {
        if (list.SelectedItem is not Entry { Source: { } before } row || WriteToProfile is not { } write || !Apply())
        {
            return;
        }
        IReadOnlyList<string> changed = ConfigurationEdit.Changed(before, row.Configuration);
        (RunConfiguration? now, string? failed) = write(row.Configuration.Name);
        if (now is null)
        {
            problem.Text = failed ?? $"{row.Configuration.Name} could not be written to its launch profile";
            return;
        }
        row.Source = now.Source ?? now with { Source = null };
        row.Configuration = now with { Source = null };
        IReadOnlyList<string> left = ConfigurationEdit.Changed(row.Source, row.Configuration);
        string done = Titles(changed.Except(left));
        written = (done.Length > 0 ? $"Written to its launch profile in launchSettings.json: {done}." : "Its launch profile in launchSettings.json was written.")
            + (left.Count > 0 ? $" Kept here, as launchSettings.json has no place for it: {Titles(left)}." : string.Empty);
        showing = true;
        try
        {
            List();
        }
        finally
        {
            showing = false;
        }
        ShowChosen();
    }

    /// <summary>Reset to Launch Profile (or its other source): every field of the chosen generated configuration as its
    /// source gives it, kept so once applied.</summary>
    private void Reset()
    {
        if (list.SelectedItem is Entry { Source: { } source })
        {
            Edit(_ => source.Copied(), reshow: true);
        }
    }

    /// <summary>The rows' titles that show the fields named, in the form's order.</summary>
    private string Titles(IEnumerable<string> fields)
    {
        HashSet<string> named = [.. fields];
        return string.Join(", ", marks.Where(m => m.Fields.Any(named.Contains)).Select(m => m.Title).Distinct());
    }

    /// <summary>The first of the folder's own that cannot be kept, and why: a name and a project.</summary>
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
            if (configuration.Kind == RunKind.Compose ? configuration.ComposeFiles.Count == 0 : configuration.Project.Length == 0)
            {
                return (row, configuration.Kind == RunKind.Compose ? $"{configuration.Name} has no compose file to run" : $"{configuration.Name} has no project to run");
            }
            if (configuration.LinkedAppHosts.Count > 0 && (configuration.IsContainer || !configuration.Aspire
                || configuration.LinkedAppHosts.Any(path => Core.PathNames.Same(path, configuration.Project)
                    || (!projects.Any(p => p.IsAspireHost && Core.PathNames.Same(p.Path, path)) && !singleFileAppHosts.Any(p => Core.PathNames.Same(p, path))))))
            {
                return (row, $"{configuration.Name} links an AppHost that is not available in this solution");
            }
        }
        return null;
    }

    /// <summary>Add New Configuration's menu of kinds, under the button.</summary>
    public ContextMenu? AddMenu { get; private set; }

    private void ChooseKind()
    {
        ContextMenu menu = new();
        foreach ((RunKind kind, string title) in (ReadOnlySpan<(RunKind, string)>)[(RunKind.Project, ".NET Project"), (RunKind.Compose, "Docker Compose"),
            (RunKind.Dockerfile, "Dockerfile"), (RunKind.SdkContainer, "SDK Container")])
        {
            MenuItem item = new() { Header = title };
            item.Click += (_, _) =>
            {
                menu.Close();
                Add(kind);
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            if (AddMenu == menu)
            {
                AddMenu = null;
            }
        };
        AddMenu = menu;
        menu.Open(AddButton);
    }

    /// <summary>Add New Configuration: a configuration of the kind chosen named Unnamed, of the first executable project (a
    /// Compose one of the folder's first compose file).</summary>
    private void Add(RunKind kind)
    {
        Entry added = new(new RunConfiguration
        {
            Name = RunConfiguration.UniqueName("Unnamed", Rows.Select(r => r.Name)),
            Kind = kind,
            Project = kind == RunKind.Compose ? string.Empty : projects.FirstOrDefault()?.Path ?? string.Empty,
            Aspire = kind == RunKind.Project && projects.FirstOrDefault()?.IsAspireHost == true,
            ComposeFiles = kind == RunKind.Compose ? [.. generated.Select(g => g.Configuration).FirstOrDefault(g => g.Kind == RunKind.Compose)?.ComposeFiles ?? []] : [],
            LaunchBrowser = kind != RunKind.Project,
            BrowserUrl = kind != RunKind.Project ? Containers.ContainerConfigurations.ServiceUrl : string.Empty,
            RunArguments = kind is RunKind.Dockerfile or RunKind.SdkContainer ? "-P" : string.Empty,
        });
        mine.Add(added);
        List();
        list.SelectedItem = added;
        Dispatcher.UIThread.Post(() => NameBox.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>Copy Configuration: the chosen one, generated or not, as one of the folder's own to change.</summary>
    private void Copy()
    {
        if (list.SelectedItem is not Entry source)
        {
            return;
        }
        Entry copy = new(source.Configuration.Copied() with
        {
            Name = RunConfiguration.UniqueName(source.Configuration.Name, Rows.Select(r => r.Name)),
            Generated = false,
            Unsupported = null,
            Origin = null,
            Source = null,
        });
        mine.Insert(mine.IndexOf(source) is >= 0 and var at ? at + 1 : mine.Count, copy);
        List();
        list.SelectedItem = copy;
    }

    /// <summary>Removes the chosen one of the folder's own; a generated one stays while its source makes it.</summary>
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

    /// <summary>Changes the chosen configuration as a field says, unless the fields are only showing it.</summary>
    private void Edit(Func<RunConfiguration, RunConfiguration> change, bool reshow = false)
    {
        if (showing || list.SelectedItem is not Entry row)
        {
            return;
        }
        RunConfiguration after = change(row.Configuration);
        if (ReferenceEquals(after, row.Configuration))
        {
            return;
        }
        bool renamed = after.Name != row.Configuration.Name;
        bool wasChanged = row.IsChanged;
        row.Configuration = after;
        written = null;
        if (renamed || row.IsChanged != wasChanged)
        {
            // A rename, or a change from the source, shows in the list as it is typed; the fields hold what they show already.
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
        else
        {
            ShowChanges(row);
        }
    }

    /// <summary>A launch profile chosen fills the fields with its values; none keeps them.</summary>
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
            LinkedAppHosts = profile.RunsInContainer ? [] : current.LinkedAppHosts,
            ServiceActions = new Dictionary<string, ServiceStart>(current.ServiceActions),
            ServiceProjects = new Dictionary<string, string>(current.ServiceProjects),
            Unsupported = null,
        };
    }

    /// <summary>Shows the chosen configuration in the fields, a generated one's name, project and launch profile read-only.</summary>
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
            IReadOnlyList<string> files = shown.IsContainer ? [] : singleFileAppHosts;
            bool singleFile = files.Contains(shown.Project);
            object[] choices = [.. projects, .. files];
            ProjectBox.ItemsSource = project is null && !singleFile && shown.Project.Length > 0
                ? (object[])[.. choices, $"{Path.GetFileName(shown.Project)} (not loaded)"] : choices;
            ProjectBox.SelectedIndex = project is not null ? IndexOf(project) : singleFile ? projects.Count + files.ToList().IndexOf(shown.Project)
                : shown.Project.Length > 0 ? choices.Length : -1;
            linkedAppHosts.Children.Clear();
            IEnumerable<(string Path, string Name)> appHosts = projects.Where(p => p.IsAspireHost).Select(p => (p.Path, p.Name))
                .Concat(singleFileAppHosts.Select(path => (path, Path.GetFileNameWithoutExtension(path))));
            foreach ((string path, string name) in appHosts.Where(p => !Core.PathNames.Same(p.Path, shown.Project)))
            {
                CheckBox box = new()
                {
                    Content = name,
                    Tag = path,
                    IsChecked = shown.LinkedAppHosts.Any(linked => Core.PathNames.Same(linked, path)),
                };
                box.IsCheckedChanged += (_, _) => Edit(c => c with
                {
                    LinkedAppHosts = [.. LinkedAppHostBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!)],
                });
                linkedAppHosts.Children.Add(box);
            }
            if (linkedAppHosts.Children.Count == 0)
            {
                linkedAppHosts.Children.Add(new TextBlock { Text = "No other AppHosts in this solution", Classes = { "muted" } });
            }
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
            KindText.Text = KindName(shown.Kind);
            ComposeFilesBox.Text = string.Join("; ", shown.ComposeFiles.Select(Shown));
            EnvFilesBox.Text = string.Join("; ", shown.EnvFiles.Select(Shown));
            ProfilesBox.Text = string.Join(", ", shown.Profiles);
            LaunchServiceBox.Text = shown.LaunchService;
            ModeBox.SelectedIndex = shown.Mode == ContainerMode.Images ? 1 : 0;
            KeepBackingBox.IsChecked = shown.KeepBackingServices;
            TelemetryBox.IsChecked = shown.Telemetry;
            DockerfileBox.Text = Shown(shown.Dockerfile);
            ContextBox.Text = Shown(shown.Context);
            BuildArgumentsBox.Text = shown.BuildArguments;
            RunArgumentsBox.Text = shown.RunArguments;
            foreach ((Control row, RunKind[] kinds) in pages)
            {
                row.IsVisible = kinds.Contains(shown.Kind);
            }
            linkedRow.IsVisible = !shown.IsContainer && shown.Aspire;
            aspireRow.IsVisible = !shown.IsContainer && shown.Aspire;
            variables.Children.Clear();
            foreach ((string name, string value) in shown.Environment)
            {
                AddVariable(name, value, focus: false);
            }
            note.Text = NoteOn(shown, project, shown.WhyNotHere(projects));
            note.IsVisible = note.Text.Length > 0;
            // What makes a generated one the one it is, its link to its source, changes only in a copy.
            foreach (Control identity in (Control[])[NameBox, ProjectBox, ProfileBox])
            {
                identity.IsEnabled = !shown.Generated;
                ToolTip.SetShowOnDisabled(identity, true);
                ToolTip.SetTip(identity, shown.Generated ? $"Its link to its {SourceName(shown)}, which a copy can change" : null);
            }
        }
        finally
        {
            showing = false;
        }
        ShowChanges(list.SelectedItem as Entry);
        ShowServices();
        ShowAspireServices();
    }

    /// <summary>What the fields cannot say: where a generated one comes from and how a change to it is kept, and why one
    /// cannot run here.</summary>
    private static string NoteOn(RunConfiguration shown, ProjectModel? project, string? whyNot)
    {
        string name = project?.Name ?? Path.GetFileNameWithoutExtension(shown.Project);
        string origin = !shown.Generated ? string.Empty
            : shown.Origin is { } container ? container
            : shown.LaunchProfile.Length > 0 ? $"Generated from the launch profile {shown.LaunchProfile} in {name}'s Properties/launchSettings.json"
            : $"Generated for the executable project {name}";
        if (shown.Generated)
        {
            origin += $": a field changed here is kept with the folder over its {SourceName(shown)}, the rest follow it.";
        }
        return whyNot is { } why ? $"{origin} It cannot run here: {why}.".Trim() : origin;
    }

    /// <summary>What a generated configuration is made from, in words: its launch profile, a Docker Compose project, a
    /// compose file, a Dockerfile, or its project.</summary>
    private static string SourceName(RunConfiguration generated) => generated switch
    {
        { LaunchProfile.Length: > 0 } => "launch profile",
        { Kind: RunKind.Compose } when Containers.ContainerConfigurations.IsComposeProject(generated.Project) => "Docker Compose project",
        { Kind: RunKind.Compose } => "compose file",
        { Kind: RunKind.Dockerfile } => "Dockerfile",
        _ => "project",
    };

    /// <summary>Marks the rows whose fields a generated configuration has changed from its source and says which, with
    /// Reset and (made from a project's launch profile) Write to Launch Profile; nothing for one unchanged or the person's own.</summary>
    private void ShowChanges(Entry? row)
    {
        HashSet<string> changed = row?.Source is { } source ? [.. ConfigurationEdit.Changed(source, row.Configuration)] : [];
        string from = row?.Source is { } made ? SourceName(made) : string.Empty;
        foreach ((TextBlock label, _, string[] fields) in marks)
        {
            bool marked = fields.Any(changed.Contains);
            label.Classes.Set("changed", marked);
            ToolTip.SetTip(label, marked ? $"Changed here from what its {from} gives" : null);
        }
        changes.IsVisible = changed.Count > 0 || written is not null;
        changesText.Text = written ?? (changed.Count > 0 ? $"Changed here from its {from}: {Titles(changed)}." : string.Empty);
        ResetButton.Content = $"Reset to {Capitalized(from)}";
        ResetButton.IsVisible = changed.Count > 0;
        // A Docker Compose project's own profiles are left as its project file and compose files are: they hold no project's.
        WriteButton.IsVisible = changed.Count > 0 && WriteToProfile is not null && row!.Configuration is { LaunchProfile.Length: > 0, Kind: not RunKind.Compose };
        if (WriteButton.IsVisible)
        {
            bool writable = Project(row!.Configuration) is { } project
                && LaunchProfile.Read(project).FirstOrDefault(p => p.Name == row.Configuration.LaunchProfile) is { } profile
                && LaunchSettingsEdits.For(project, profile, row.Source!, row.Configuration).Count > 0;
            WriteButton.IsEnabled = writable;
            ToolTip.SetTip(WriteButton, writable ? WriteTip : "launchSettings.json has no place for these changes, which stay kept with the folder");
        }

        static string Capitalized(string words) => string.Join(' ', words.Split(' ').Select(w => w.Length > 0 && char.IsLower(w[0]) ? char.ToUpperInvariant(w[0]) + w[1..] : w));
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
        Button remove = ToolButtons.Of(IconKeys.Close, "Remove Variable");
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

    /// <summary>Whether a run of a configuration (by its name) is up, which its row says; given as the rows are made.</summary>
    private Func<string, bool> IsRunning { get; }

    /// <summary>A list row: the configuration's name ("(running)" after it while a run of it is up), greyed when it cannot run
    /// here (why in its tip), marked as a changed field's row is when it is a generated one changed from its source, and for
    /// a generated one where it came from, muted beside it; where came from gives way first, then the name, each to an ellipsis.</summary>
    private static Control RowView(RunConfiguration? row, string? whyNot, int running, bool changed)
    {
        ShrinkingRow line = new();
        TextBlock name = new()
        {
            Text = running switch { 0 => row?.Name, 1 => $"{row?.Name} (running)", _ => $"{row?.Name} ({running} apps running)" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (whyNot is not null)
        {
            name.Classes.Add("muted");
            ToolTip.SetTip(line, $"{row?.Name} cannot run here: {whyNot}");
        }
        else if (changed)
        {
            name.Classes.Add("changed");
            ToolTip.SetTip(line, $"{row?.Name} is changed here from what its {SourceName(row!)} gives");
        }
        line.Children.Add(name);
        if (row is { Generated: true })
        {
            string from = row.Kind switch
            {
                RunKind.Project when row.Aspire => "Aspire",
                RunKind.Compose when Containers.ContainerConfigurations.IsComposeProject(row.Project) => row.LaunchProfile.Length > 0 ? LaunchProfile.SettingsFile : ".dcproj",
                RunKind.Compose => "compose file",
                RunKind.Dockerfile when row.LaunchProfile.Length == 0 => "Dockerfile",
                RunKind.SdkContainer when row.LaunchProfile.Length == 0 => "SDK container",
                _ => row.LaunchProfile.Length > 0 ? LaunchProfile.SettingsFile : "project",
            };
            line.Children.Add(new TextBlock { Text = from, Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        }
        return line;
    }

    /// <summary>A configuration as the list holds it: the same row while its values change; a generated one's with the
    /// configuration its source makes, which its fields are told apart from.</summary>
    private sealed class Entry(RunConfiguration configuration, RunConfiguration? source = null)
    {
        public RunConfiguration Configuration { get; set; } = configuration;

        public RunConfiguration? Source { get; set; } = source;

        /// <summary>Whether it is a generated one changed from its source.</summary>
        public bool IsChanged => Source is not null && ConfigurationEdit.Between(Source, Configuration) is not null;
    }

    /// <summary>A form's row: its label and the field; given the fields it shows, it is marked (its label, or for a box with
    /// no label <paramref name="marked"/>, its own words) when a generated configuration has one changed.</summary>
    private Control Row(string label, Control editor, bool top = false, TextBlock? marked = null, string[]? fields = null)
    {
        Grid row = FormRows.Of(label, editor, out TextBlock name, top);
        editor.HorizontalAlignment = editor is TextBox or StackPanel or ComposeServicesTable ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        if (fields is not null)
        {
            TextBlock shown = marked ?? name;
            marks.Add((shown, shown.Text ?? label, fields));
        }
        return row;
    }

    private static TextBox Field(string? placeholder = null) => new() { Classes = { "search" }, PlaceholderText = placeholder };

    private static PathIcon Glyph(string key)
    {
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }
}
