using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A run of several parts' parts, one row each: the parts it names in the order they start, then the folder's other
/// executable projects, each with what Run and Debug do with it (Start, under the debugger when a .NET project is debugged;
/// Start without debugging; None) and when the parts after it may start (once it has started, ended with exit code 0, said a
/// line matching a pattern, or listens). A row's Details holds the rest of it: a .NET project's launch profile, a script's
/// arguments, a command and its folder, its name, its page, its variables. A .NET project it names that is not loaded keeps
/// its row, unchosen; any other part comes from Add Part and goes with Remove Part. Up and down move a row, which is the
/// order they start in.</summary>
public sealed class RunPartsTable : EditableTable
{
    /// <summary>One part's row: what it runs (a project's file, a package's package.json, empty for a command), its action,
    /// readiness and launch profile boxes, the launch profiles listed (the first, empty, its default), and its details.</summary>
    public sealed class Row(string project, ComboBox action, ComboBox profile, IReadOnlyList<string> profiles, StackPanel view, Button up, Button down)
    {
        public string Project { get; } = project;

        public ComboBox Action { get; } = action;

        public ComboBox Profile { get; } = profile;

        public IReadOnlyList<string> Profiles { get; } = profiles;

        public StackPanel View { get; } = view;

        public Button Up { get; } = up;

        public Button Down { get; } = down;

        /// <summary>The part it was set from or added as, kept for what of it the table does not show; null for a project
        /// the run did not name.</summary>
        public RunPart? Was { get; init; }

        /// <summary>The part as its details have it now.</summary>
        public required RunPart Part { get; set; }

        public required ComboBox Ready { get; init; }

        public required ToggleButton Details { get; init; }

        /// <summary>The pattern and the address its details hold, whichever its readiness waits for being its part's.</summary>
        public string Line { get; set; } = string.Empty;

        public string Address { get; set; } = string.Empty;

        public Control? DetailsView { get; set; }

        /// <summary>Its details' text fields, by their labels.</summary>
        public Dictionary<string, TextBox> Fields { get; } = [];

        /// <summary>Its details' Remove Part, for a part that is no .NET project.</summary>
        public Button? Remove { get; set; }
    }

    /// <summary>Each action's words, as Visual Studio's own table says them, in <see cref="ProjectLaunchAction"/>'s order.</summary>
    public static readonly IReadOnlyList<string> ActionWords = ["Start", "Start without debugging", "None"];

    /// <summary>Each readiness's words, in <see cref="ReadyWhen"/>'s order.</summary>
    public static readonly IReadOnlyList<string> ReadyWords = ["Started", "Exited OK", "Prints a line", "Listening"];

    private const string DefaultProfile = "Default";

    private readonly List<Row> rows = [];

    /// <summary>How a folder a part names is shown in a field, and read from one: relative to the open folder.</summary>
    private Func<string, string> shown = path => path;
    private Func<string?, string> full = path => path ?? string.Empty;
    private Func<RunPart, string> nameOf = part => RunsFile.NameOf(part);

    /// <summary>Its columns: the part, its action (wide enough for its longest), its readiness, and its buttons.</summary>
    public RunPartsTable()
        : base("3*,2*,2*,108", ["Part", "Action", "Ready when", string.Empty])
    {
    }

    public IReadOnlyList<Row> Rows => rows;

    /// <summary>The rows of a run's parts, then of each other project, without raising <see cref="Changed"/>;
    /// <paramref name="profilesOf"/> lists a project's launch profiles, <paramref name="shownAs"/> and <paramref name="fullFrom"/>
    /// write a folder for a field and read one back.</summary>
    public void Set(IReadOnlyList<RunPart> parts, IReadOnlyList<ProjectModel> projects, Func<ProjectModel, IReadOnlyList<string>> profilesOf,
        Func<string, string> shownAs, Func<string?, string> fullFrom, Func<RunPart, string>? named = null) => Setting(() =>
    {
        shown = shownAs;
        full = fullFrom;
        nameOf = named ?? (part => RunsFile.NameOf(part));
        rows.Clear();
        Table.Children.Clear();
        foreach (RunPart part in parts)
        {
            ProjectModel? project = part.Kind == RunPartKind.Project ? projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, part.Project)) : null;
            Add(part, part.Kind == RunPartKind.Project ? project?.Name : nameOf(part), project is null ? [] : profilesOf(project), part);
        }
        foreach (ProjectModel project in projects.Where(p => !parts.Any(s => s.Kind == RunPartKind.Project && Core.PathNames.Same(s.Project, p.Path))))
        {
            Add(new RunPart(project.Path, ProjectLaunchAction.None, string.Empty), project.Name, profilesOf(project), null);
        }
        Ends();
        SayInstead(rows.Count == 0 ? "The folder has no executable project: Add Part adds a script, an install or a command" : null);
    });

    /// <summary>A part put after the last part the run starts, its details shown, as Add Part puts one; raises <see cref="Changed"/>.</summary>
    public void AddPart(RunPart part)
    {
        int at = rows.FindLastIndex(r => r.Was is not null || r.Part.Kind != RunPartKind.Project) + 1;
        Row row = Add(part, nameOf(part), [], part, at);
        row.Details.IsChecked = true;
        Ends();
        SayInstead(null);
        Tell();
    }

    /// <summary>The parts as the table has them, in its order: each row it was set from or added with its values now (one set
    /// to None kept, as Visual Studio keeps it), and each other project set to start.</summary>
    public List<RunPart> Parts()
    {
        List<RunPart> parts = [];
        foreach (Row row in rows)
        {
            bool project = row.Part.Kind == RunPartKind.Project;
            int chosen = row.Action.SelectedIndex;
            ProjectLaunchAction? action = project
                ? chosen is >= 0 and < 3 ? (ProjectLaunchAction)chosen : row.Was?.Action
                : chosen switch { 0 => ProjectLaunchAction.Start, 1 => ProjectLaunchAction.None, _ => row.Was?.Action };
            string profile = row.Profile.SelectedIndex > 0 && row.Profile.SelectedIndex < row.Profiles.Count ? row.Profiles[row.Profile.SelectedIndex] : string.Empty;
            ReadyWhen ready = row.Ready.SelectedIndex is >= 0 and < 4 ? (ReadyWhen)row.Ready.SelectedIndex : row.Part.Ready;
            RunPart now = row.Part with
            {
                Action = action,
                LaunchProfile = project ? profile : row.Part.LaunchProfile,
                Ready = ready,
                ReadyText = ready switch { ReadyWhen.Line => row.Line, ReadyWhen.Listening => row.Address, _ => string.Empty },
            };
            if (row.Was is not null || !project || action is not (ProjectLaunchAction.None or null))
            {
                parts.Add(now);
            }
        }
        return parts;
    }

    private Row Add(RunPart part, string? name, IReadOnlyList<string> profiles, RunPart? was, int at = -1)
    {
        bool project = part.Kind == RunPartKind.Project;
        TextBlock title = new()
        {
            Text = name ?? $"{Path.GetFileNameWithoutExtension(part.Project)} (not loaded)",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Gaps.Normal, 0),
        };
        if (name is null && project)
        {
            title.Classes.Add(StyleClasses.Muted);
        }
        ToolTip.SetTip(title, KindWords(part.Kind));
        bool known = name is not null || !project;
        ComboBox action = new() { Classes = { StyleClasses.Cell }, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = known };
        // An action Passenger does not know is listed as written, so leaving it chosen keeps it.
        string? unknown = was is { Action: null } ? was.Launch?.ActionWritten ?? "?" : null;
        string[] words = project ? [.. ActionWords] : [ActionWords[0], ActionWords[2]];
        action.ItemsSource = unknown is null ? words : (string[])[.. words, $"{unknown} (not known)"];
        action.SelectedIndex = was is null && project ? (int)ProjectLaunchAction.None
            : part.Action is { } given ? project ? (int)given : given == ProjectLaunchAction.None ? 1 : 0
            : words.Length;
        ToolTip.SetTip(action, project
            ? "Start runs it, under the debugger when the run is debugged; Start without debugging runs it either way; None leaves it"
            : "Start runs it, Run and Debug alike; None leaves it");
        ComboBox ready = new()
        {
            Classes = { StyleClasses.Cell },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = known,
            ItemsSource = ReadyWords,
            SelectedIndex = (int)part.Ready,
        };
        ToolTip.SetTip(ready, "When the parts after it may start: once it has started, once it has ended with exit code 0 (a step, as an install), "
            + "once a line of its output matches a pattern, or once something listens at its address (a .NET project naming none: once its host says it has started); "
            + "Details holds the pattern or address");
        // Its default first, then its own profiles, and one named that its launchSettings.json lacks, kept as named.
        List<string> listed = [string.Empty, .. profiles];
        if (project && part.LaunchProfile.Length > 0 && !profiles.Contains(part.LaunchProfile))
        {
            listed.Add(part.LaunchProfile);
        }
        ComboBox profile = new FormDropDown
        {
            IsEnabled = name is not null,
            ItemsSource = (string[])[DefaultProfile, .. listed.Skip(1).Select(p => profiles.Contains(p) ? p : $"{p} (not in its launch settings)")],
            SelectedIndex = Math.Max(0, listed.IndexOf(part.LaunchProfile)),
        };
        ToolTip.SetTip(profile, "The launch profile it starts in: Default takes the first of its launch profiles that runs here, as dotnet run does");
        Button up = ToolButtons.Of(IconKeys.ArrowUp, "Start Earlier");
        Button down = ToolButtons.Of(IconKeys.ArrowDown, "Start Later");
        ToggleButton details = ToolButtons.Toggle(IconKeys.Edit, "Details");
        details.IsEnabled = known;
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Hair, Margin = new Thickness(Gaps.Tight, 0), Children = { up, down, details } };
        StackPanel view = new();
        Row row = new(part.Project, action, profile, listed, view, up, down)
        {
            Was = was,
            Part = part,
            Ready = ready,
            Details = details,
            Line = part.Ready == ReadyWhen.Line ? part.ReadyText : string.Empty,
            Address = part.Ready == ReadyWhen.Listening ? part.ReadyText : string.Empty,
        };
        Control form = DetailsOf(row, profile);
        form.IsVisible = false;
        row.DetailsView = form;
        view.Children.Add(Ruled(title, action, ready, buttons));
        view.Children.Add(form);
        rows.Insert(at < 0 ? rows.Count : at, row);
        Table.Children.Insert(at < 0 ? Table.Children.Count : at, view);
        action.SelectionChanged += (_, _) => Tell();
        ready.SelectionChanged += (_, _) =>
        {
            ShowWaitField(row);
            Tell();
        };
        profile.SelectionChanged += (_, _) => Tell();
        details.IsCheckedChanged += (_, _) => form.IsVisible = details.IsChecked == true;
        up.Click += (_, _) => Move(row, -1);
        down.Click += (_, _) => Move(row, 1);
        return row;
    }

    /// <summary>What a part's kind is called beside its name.</summary>
    public static string KindWords(RunPartKind kind) => kind switch
    {
        RunPartKind.Script => "package.json script",
        RunPartKind.Install => "dependencies' install",
        RunPartKind.Command => "command",
        _ => ".NET project",
    };

    /// <summary>The waits' fields of each row, which show for a readiness that waits for a pattern or an address.</summary>
    private readonly Dictionary<Row, (Control Line, Control Address)> waitFields = [];

    private void ShowWaitField(Row row)
    {
        if (waitFields.TryGetValue(row, out (Control Line, Control Address) fields))
        {
            fields.Line.IsVisible = row.Ready.SelectedIndex == (int)ReadyWhen.Line;
            fields.Address.IsVisible = row.Ready.SelectedIndex == (int)ReadyWhen.Listening;
        }
    }

    /// <summary>A row's details under it: what its kind takes (a launch profile, a script's arguments, a command and its folder),
    /// its name, the pattern or address it waits for, its page and its variables, each edit kept in the row's part.</summary>
    private Control DetailsOf(Row row, ComboBox profile)
    {
        StackPanel form = new() { Spacing = Gaps.Normal, Margin = new Thickness(Gaps.Wide, Gaps.Small, Gaps.Normal, Gaps.Normal) };
        FormRows.Scope(form);
        RunPart part = row.Part;
        TextBox Field(string label, string? text, string placeholder, Func<RunPart, string, RunPart> set) => Typed(label, text, placeholder, typed => row.Part = set(row.Part, typed));
        TextBox Typed(string label, string? text, string placeholder, Action<string> take)
        {
            TextBox box = new() { Text = text, PlaceholderText = placeholder };
            row.Fields[label] = box;
            box.TextChanged += (_, _) =>
            {
                take(box.Text ?? string.Empty);
                Tell();
            };
            return box;
        }
        switch (part.Kind)
        {
            case RunPartKind.Project:
                form.Children.Add(FormRows.Of("Launch profile", profile));
                break;
            case RunPartKind.Script:
                form.Children.Add(FormRows.Of("Arguments", Field("Arguments", part.Arguments, "After the script, to its package manager", (p, t) => p with { Arguments = t })));
                break;
            case RunPartKind.Command:
                form.Children.Add(FormRows.Of("Command", Field("Command", part.Command, "A program and its arguments, as a terminal runs them", (p, t) => p with { Command = t.Trim() })));
                form.Children.Add(FormRows.Of("Folder", Field("Folder", part.Folder.Length > 0 ? shown(part.Folder) : string.Empty, "The open folder",
                    (p, t) => p with { Folder = t.Trim().Length > 0 ? full(t.Trim()) : string.Empty })));
                break;
        }
        form.Children.Add(FormRows.Of("Name", Field("Name", part.Name, nameOf(part with { Name = string.Empty }), (p, t) => p with { Name = t.Trim() })));
        Control line = FormRows.Of("Pattern", Typed("Pattern", row.Line, "A regular expression a line of its output matches", typed => row.Line = typed));
        Control address = FormRows.Of("Address", Typed("Address", row.Address, part.Kind == RunPartKind.Project ? "Where its host says it listens" : "http://localhost:5173",
            typed => row.Address = typed.Trim()));
        form.Children.Add(line);
        form.Children.Add(address);
        waitFields[row] = (line, address);
        ShowWaitField(row);
        form.Children.Add(FormRows.Of("Open page", Field("Open page", part.Browser, part.Kind == RunPartKind.Project ? "As its launch profile says" : "None",
            (p, t) => p with { Browser = t.Trim() })));
        VariablesEditor variables = new();
        variables.Set(part.Environment);
        variables.Changed += (_, _) =>
        {
            if (!variables.Gives(row.Part.Environment))
            {
                row.Part = row.Part with { Environment = variables.Values };
                Tell();
            }
        };
        form.Children.Add(FormRows.Of("Environment variables", variables, top: true));
        if (part.Kind != RunPartKind.Project)
        {
            Button remove = new() { Content = "Remove Part", HorizontalAlignment = HorizontalAlignment.Left };
            remove.Click += (_, _) => Remove(row);
            row.Remove = remove;
            form.Children.Add(remove);
        }
        return form;
    }

    /// <summary>A part Add Part added, or the run named, taken out of the run.</summary>
    private void Remove(Row row)
    {
        int at = rows.IndexOf(row);
        if (at < 0)
        {
            return;
        }
        rows.RemoveAt(at);
        Table.Children.RemoveAt(at);
        waitFields.Remove(row);
        Ends();
        Tell();
    }

    /// <summary>A row moved up or down the order the parts start in.</summary>
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
        Table.Children.RemoveAt(at);
        Table.Children.Insert(to, row.View);
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
}
