using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>What the Publish dialog asks to be saved: the profile's name and file (one there already, or where a new one
/// goes), the options, what the file said of them and whether they differ from it, and whether a new profile takes the
/// place of a file of its name.</summary>
public sealed record PublishChoice(string Project, string Profile, string File, PublishOptions Options, PublishOptions? Before, IReadOnlyDictionary<string, string> Raw,
    bool MultiTargeted, bool Changed, bool Replacing);

/// <summary>Publish for one project, to a folder: a publish profile of the project's (its .pubxml files, each read by
/// MSBuild) or a new one, and what it publishes (configuration, framework, deployment, runtime, folder, single-file,
/// ReadyToRun, trimming), with the dotnet publish command that does the same. Save writes the profile (asking before it
/// changes a file there); Publish saves it and closes with the publish to run. A profile that publishes elsewhere than
/// to a folder (Web Deploy, FTP, Azure) is listed and cannot be chosen yet.</summary>
public sealed class PublishDialog : Window
{
    private const string Portable = "Portable";
    private const string FrameworkDependent = "Framework-dependent";
    private const string SelfContainedMode = "Self-contained";
    private readonly ProjectModel project;
    private readonly string open;
    private readonly bool multi;
    private readonly Func<string?, string?, string?, string?, CancellationToken, Task<PropertiesAnswer>> read;
    private readonly Func<PublishChoice, Task<string?>> save;

    private readonly ComboBox profileBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox nameBox = new() { Classes = { "search" } };
    private readonly Control nameRow;
    private readonly ComboBox configurationBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox frameworkBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox deploymentBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { FrameworkDependent, SelfContainedMode } };
    private readonly ComboBox runtimeBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox folderBox = new() { Classes = { "search" } };
    private readonly Button browse = new() { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock destination = new() { Classes = { "muted" }, FontSize = 12, TextTrimming = TextTrimming.PathSegmentEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock copied = new() { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), IsVisible = false };
    private readonly CheckBox singleFileBox = new() { Content = "Produce a single file" };
    private readonly CheckBox readyToRunBox = new() { Content = "Compile ReadyToRun" };
    private readonly CheckBox trimBox = new() { Content = "Trim unused code" };
    private readonly TextBlock command = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button publish = new() { Content = "Publish", IsDefault = true, Classes = { "primary" } };
    private readonly Button saveButton = new() { Content = "Save" };

    private IReadOnlyList<PublishProfileFile> profiles = [];
    private IReadOnlyDictionary<string, string> raw = new Dictionary<string, string>();
    private PublishOptions? loaded;
    private string? loadedDestination;

    /// <summary>Where Visual Studio copies the profile's app on to (a web profile's PublishUrl), which dotnet publish does not.</summary>
    private string? visualStudioCopy;

    /// <summary>Whether the folder is the profile's own (named in it, or typed here) rather than MSBuild's default.</summary>
    private bool folderNamed;

    /// <summary>The folder's text as the dialog last showed it itself.</summary>
    private string? shownFolder;
    private bool filling;
    private bool reading;
    private bool saving;
    private int generation;
    private CancellationTokenSource? reads;

    /// <param name="read">The project evaluated with a profile, configuration, framework and runtime as global properties.</param>
    /// <param name="save">Writes a profile, asking first where it changes a file there: null once written (or nothing to write),
    /// else why not.</param>
    public PublishDialog(ProjectModel project, string open, string? chosen, Func<string?, string?, string?, string?, CancellationToken, Task<PropertiesAnswer>> read,
        Func<PublishChoice, Task<string?>> save)
    {
        FormRows.Scope(this);
        this.project = project;
        this.open = open;
        this.read = read;
        this.save = save;
        multi = project.TargetFrameworks.Count > 1;
        string name = System.IO.Path.GetFileNameWithoutExtension(project.Path);
        Title = $"Publish {name}";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));

        Button cancel = new() { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        publish.Click += (_, _) => IdeErrors.Observe(PublishAsync());
        saveButton.Click += (_, _) => IdeErrors.Observe(SaveAsync());
        browse.Click += (_, _) => IdeErrors.Observe(BrowseAsync());
        Closing += (_, _) => reads?.Cancel();

        frameworkBox.ItemsSource = project.TargetFrameworks;
        nameRow = Row("Name:", nameBox);
        StackPanel options = new() { Spacing = 6, Children = { singleFileBox, readyToRunBox, trimBox } };
        StackPanel fields = new()
        {
            Spacing = 10,
            Children =
            {
                Row("Profile:", profileBox),
                nameRow,
                Row("Target:", new TextBlock { Text = "Folder", VerticalAlignment = VerticalAlignment.Center }),
                Row("Configuration:", configurationBox),
            },
        };
        if (multi)
        {
            fields.Children.Add(Row("Target framework:", frameworkBox));
        }
        fields.Children.Add(Row("Deployment:", deploymentBox));
        fields.Children.Add(Row("Target runtime:", runtimeBox));
        fields.Children.Add(new StackPanel { Children = { Row("Output folder:", BrowseRow.Of(folderBox, browse)), FormRows.Of(null, destination), FormRows.Of(null, copied) } });
        fields.Children.Add(Row("Options:", options, top: true));

        StackPanel header = new()
        {
            Margin = new Thickness(20, 16, 20, 12),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = $"Publish {name} to a folder", FontWeight = FontWeight.SemiBold },
                command,
            },
        };
        Border top = new() { BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        top.Bind(Border.BorderBrushProperty, top.GetResourceObservable(TokenKeys.Divider));
        Border bottom = DialogFooter.Bar(DialogFooter.Of(publish, cancel, saveButton), problem);
        // Inset as the page above it is, so Publish ends where its fields do and a problem starts where its labels do.
        bottom.Padding = new Thickness(20, 10);
        DockPanel root = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Padding = new Thickness(20, 14, 20, 16), Child = fields });
        Content = root;

        profileBox.SelectionChanged += (_, _) =>
        {
            if (!filling)
            {
                IdeErrors.Observe(LoadAsync(Chosen));
            }
        };
        nameBox.TextChanged += (_, _) => Changed();
        configurationBox.SelectionChanged += (_, _) => Changed(evaluate: true);
        frameworkBox.SelectionChanged += (_, _) => Changed(evaluate: true);
        runtimeBox.SelectionChanged += (_, _) =>
        {
            if (!filling && runtimeBox.SelectedItem is Portable && SelfContained)
            {
                // A self-contained app is one system's: going portable makes it framework-dependent.
                filling = true;
                deploymentBox.SelectedItem = FrameworkDependent;
                filling = false;
            }
            Changed(evaluate: true);
        };
        deploymentBox.SelectionChanged += (_, _) =>
        {
            if (!filling && SelfContained && Runtime is null)
            {
                // A self-contained app carries one system's runtime: this machine's unless another is chosen.
                filling = true;
                Choose(runtimeBox, PublishProfiles.CommonRuntimes[0]);
                filling = false;
            }
            Changed(evaluate: true);
        };
        folderBox.TextChanged += (_, _) =>
        {
            // Raised after the text is set, so the folder the dialog showed itself is told from one typed by its text.
            if (folderBox.Text != shownFolder)
            {
                folderNamed = true;
            }
            Changed();
        };
        foreach (CheckBox box in (CheckBox[])[singleFileBox, readyToRunBox, trimBox])
        {
            box.IsCheckedChanged += (_, _) => Changed();
        }
        ListProfiles(chosen);
        IdeErrors.Observe(LoadAsync(Chosen));
    }

    /// <summary>The project's profiles as the dialog found them.</summary>
    public IReadOnlyList<PublishProfileFile> Profiles => profiles;

    /// <summary>The profile chosen, or null for a new one.</summary>
    public PublishProfileFile? Chosen => (profileBox.SelectedItem as ComboBoxItem)?.Tag as PublishProfileFile;

    /// <summary>The name the profile is saved under: the one chosen, or the one typed for a new one.</summary>
    public string ProfileName
    {
        get => Chosen?.Name ?? (nameBox.Text ?? string.Empty).Trim();
        set => nameBox.Text = value;
    }

    public string Configuration
    {
        get => configurationBox.SelectedItem as string ?? "Release";
        set => Choose(configurationBox, value);
    }

    public string? Framework
    {
        get => multi ? frameworkBox.SelectedItem as string : null;
        set => frameworkBox.SelectedItem = value;
    }

    /// <summary>The runtime chosen, or null for a portable app.</summary>
    public string? Runtime
    {
        get => runtimeBox.SelectedItem is string runtime && runtime != Portable ? runtime : null;
        set => Choose(runtimeBox, value ?? Portable);
    }

    public bool SelfContained
    {
        get => deploymentBox.SelectedItem is SelfContainedMode;
        set => deploymentBox.SelectedItem = value ? SelfContainedMode : FrameworkDependent;
    }

    public bool SingleFile
    {
        get => singleFileBox.IsChecked == true && singleFileBox.IsEnabled;
        set => singleFileBox.IsChecked = value;
    }

    public bool ReadyToRun
    {
        get => readyToRunBox.IsChecked == true && readyToRunBox.IsEnabled;
        set => readyToRunBox.IsChecked = value;
    }

    public bool Trimmed
    {
        get => trimBox.IsChecked == true && trimBox.IsEnabled;
        set => trimBox.IsChecked = value;
    }

    /// <summary>The output folder as the profile names it.</summary>
    public string OutputFolder
    {
        get => folderBox.Text ?? string.Empty;
        set => folderBox.Text = value;
    }

    /// <summary>The folder the app lands in, in full, as MSBuild resolves it; null while it is being read.</summary>
    public string? Destination { get; private set; }

    /// <summary>What the dialog says under the folder of where the app lands.</summary>
    public string DestinationSaid => copied.IsVisible ? $"{destination.Text}\n{copied.Text}" : destination.Text ?? string.Empty;

    /// <summary>The dotnet publish command that publishes as the dialog does.</summary>
    public string Command => command.Text ?? string.Empty;

    /// <summary>Why the dialog cannot publish as it stands, or what it is reading; empty when neither.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>Whether a profile, or the folder it lands in, is being read.</summary>
    public bool IsReading => reading;

    /// <summary>Chooses a profile by name, or a new one for null, and reads it.</summary>
    public void ChooseProfile(string? name)
    {
        profileBox.SelectedItem = profileBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as PublishProfileFile)?.Name == name || name is null && i.Tag is null);
    }

    /// <summary>Why a publish cannot start now (another build runs), which keeps the dialog open saying so; null when it can.</summary>
    public Func<string?>? Busy { get; init; }

    /// <summary>Publish: the profile saved, then the dialog closed with the publish to run. Null once closed, else why not.</summary>
    public async Task<string?> PublishAsync()
    {
        if (Busy?.Invoke() is { } busy)
        {
            Say(busy);
            return busy;
        }
        if (await SaveProfileAsync() is { } refused)
        {
            return refused;
        }
        PublishOptions options = Options();
        Close(new PublishRequest(project.Path, ProfileName, options.Configuration, multi ? options.Framework : null));
        return null;
    }

    /// <summary>Save: the profile written, the dialog left open on it. Null once saved, else why not.</summary>
    public async Task<string?> SaveAsync()
    {
        if (await SaveProfileAsync() is { } refused)
        {
            return refused;
        }
        string saved = ProfileName;
        ListProfiles(saved);
        await LoadAsync(Chosen);
        return null;
    }

    private async Task<string?> SaveProfileAsync()
    {
        if (saving)
        {
            return "Saving already";
        }
        if (WhyNot() is { } why)
        {
            Say(why);
            return why;
        }
        string name = ProfileName;
        PublishProfileFile? there = Chosen ?? profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        string file = there?.Path ?? System.IO.Path.Combine(PublishProfiles.Folder(project.Path, null), $"{name}.pubxml");
        PublishOptions options = Options();
        PublishChoice choice = new(project.Path, there?.Name ?? name, file, options, loaded, Chosen is null ? new Dictionary<string, string>() : raw, multi,
            Chosen is null || options != loaded, Chosen is null && there is not null);
        saving = true;
        Enable();
        string? refused;
        // Not background work: the save may wait on the person's answer, which nothing settling waits for.
        try
        {
            refused = await save(choice);
        }
        finally
        {
            saving = false;
        }
        Enable();
        Say(refused);
        return refused;
    }

    /// <summary>The options as the dialog shows them, the folder null while it is MSBuild's default.</summary>
    private PublishOptions Options() => new(Configuration, Framework, Runtime, SelfContained && Runtime is not null, SingleFile, ReadyToRun, Trimmed,
        folderNamed && OutputFolder.Trim() is { Length: > 0 } folder ? folder : null);

    private string? WhyNot()
    {
        if (reading)
        {
            return "Reading the profile…";
        }
        if (loaded is null)
        {
            return Problem.Length > 0 ? Problem : "The profile could not be read";
        }
        if (Chosen is null)
        {
            string name = ProfileName;
            if (name.Length == 0)
            {
                return "A new profile needs a name";
            }
            if (name.IndexOfAny([.. System.IO.Path.GetInvalidFileNameChars(), '/', '\\', ';', '%', '$']) >= 0 || name.StartsWith('.'))
            {
                return $"'{name}' cannot name a profile: it is its file's name";
            }
        }
        if (folderNamed && OutputFolder.Trim().Length == 0)
        {
            return "An output folder is needed";
        }
        return null;
    }

    /// <summary>The profiles in the project's folder for them, then New profile; the one named chosen, else the first that
    /// publishes to a folder, else New profile.</summary>
    private void ListProfiles(string? chosen)
    {
        filling = true;
        try
        {
            profiles = PublishProfiles.Find(PublishProfiles.Folder(project.Path, null));
            List<ComboBoxItem> items = [];
            foreach (PublishProfileFile each in profiles)
            {
                ComboBoxItem item = new() { Content = each.Remote is null ? each.Name : $"{each.Name} ({each.Remote})", Tag = each, IsEnabled = each.Remote is null };
                if (each.Remote is { } method)
                {
                    ToolTip.SetTip(item, $"{each.Name} publishes by {method}: Passenger publishes to a folder only, for now");
                }
                items.Add(item);
            }
            items.Add(new ComboBoxItem { Content = "New profile" });
            profileBox.ItemsSource = items;
            profileBox.SelectedItem = items.FirstOrDefault(i => i.Tag is PublishProfileFile p && string.Equals(p.Name, chosen, StringComparison.OrdinalIgnoreCase) && p.Remote is null)
                ?? items.FirstOrDefault(i => i.Tag is PublishProfileFile { Remote: null })
                ?? items[^1];
            nameBox.Text = PublishProfiles.NewName(profiles);
        }
        finally
        {
            filling = false;
        }
    }

    /// <summary>Reads a profile (or the project's defaults, for a new one) through MSBuild and shows what it publishes.</summary>
    private async Task LoadAsync(PublishProfileFile? profile)
    {
        reads?.Cancel();
        CancellationTokenSource cancel = reads = new CancellationTokenSource();
        int mine = ++generation;
        reading = true;
        loaded = null;
        Destination = null;
        nameRow.IsVisible = profile is null;
        // Said where the folder will be, muted: reading is no problem.
        Say(null);
        visualStudioCopy = null;
        copied.IsVisible = false;
        destination.Text = $"Reading {(profile is null ? $"how {System.IO.Path.GetFileNameWithoutExtension(project.Path)} publishes" : profile.Name)}…";
        Enable();
        using IDisposable held = BackgroundWork.Scope();
        IReadOnlyDictionary<string, string> said;
        try
        {
            said = profile is null ? new Dictionary<string, string>() : PubXml.Read(await File.ReadAllTextAsync(profile.Path, cancel.Token));
        }
        catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            Landed($"{profile!.Name}.pubxml does not read: {error.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        string? framework = multi ? said.GetValueOrDefault("TargetFramework") is { Length: > 0 } named ? named : project.TargetFrameworks[0] : null;
        string? configuration = profile is null || !said.ContainsKey("Configuration") ? said.GetValueOrDefault("LastUsedBuildConfiguration") ?? "Release" : null;
        PropertiesAnswer answer;
        try
        {
            answer = await read(profile?.Name, configuration, framework, null, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (mine != generation)
        {
            return;
        }
        if (answer.Problem is { } failed)
        {
            Landed(failed);
            return;
        }
        raw = said;
        PublishOptions options = PublishProfiles.Options(answer.Values, said, framework);
        loaded = options;
        loadedDestination = PublishProfiles.FolderOf(project.Path, answer.Values);
        visualStudioCopy = PublishProfiles.VisualStudioCopy(project.Path, answer.Values);
        filling = true;
        try
        {
            List<string> configurations = ["Release", "Debug"];
            if (!configurations.Contains(options.Configuration, StringComparer.OrdinalIgnoreCase))
            {
                configurations.Add(options.Configuration);
            }
            configurationBox.ItemsSource = configurations;
            Choose(configurationBox, options.Configuration);
            frameworkBox.SelectedItem = options.Framework;
            List<string> runtimes = [Portable];
            foreach (string runtime in (answer.Values.GetValueOrDefault("RuntimeIdentifiers") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat(PublishProfiles.CommonRuntimes).Append(options.Runtime ?? Portable))
            {
                if (!runtimes.Contains(runtime, StringComparer.OrdinalIgnoreCase))
                {
                    runtimes.Add(runtime);
                }
            }
            runtimeBox.ItemsSource = runtimes;
            Choose(runtimeBox, options.Runtime ?? Portable);
            deploymentBox.SelectedItem = options.SelfContained ? SelfContainedMode : FrameworkDependent;
            singleFileBox.IsChecked = options.SingleFile;
            readyToRunBox.IsChecked = options.ReadyToRun;
            trimBox.IsChecked = options.Trimmed;
            folderNamed = options.Folder is not null;
            ShowFolder(options.Folder ?? (loadedDestination is { } at ? PublishProfiles.Named(project.Path, at, open) : string.Empty));
            Destination = loadedDestination;
        }
        finally
        {
            filling = false;
        }
        Landed(null);

        void Landed(string? why)
        {
            if (mine != generation)
            {
                return;
            }
            reading = false;
            Say(why);
            Enable();
            Changed();
        }
    }

    /// <summary>The options changed: what can be chosen follows, the command says them, and where the app lands is worked out
    /// again by MSBuild when it is its default (a configuration or runtime of its own).</summary>
    private void Changed(bool evaluate = false)
    {
        if (filling)
        {
            return;
        }
        Enable();
        PublishOptions options = Options();
        PublishRequest request = new(project.Path, ProfileName.Length > 0 ? ProfileName : null, options.Configuration, options.Framework);
        command.Text = $"dotnet publish {ChildProcesses.Quote(System.IO.Path.GetRelativePath(open, project.Path))} {string.Join(' ', request.Command().Select(ChildProcesses.Quote))}";
        if (folderNamed)
        {
            Destination = loaded is not null && options.Folder == loaded.Folder ? loadedDestination
                : OutputFolder.Trim() is { Length: > 0 } typed && !typed.Contains("$(", StringComparison.Ordinal) ? PublishProfiles.Full(project.Path, typed) : null;
        }
        else if (evaluate && loaded is not null && !reading)
        {
            IdeErrors.Observe(DefaultFolderAsync());
        }
        ShowDestination();
        if (!reading && !saving)
        {
            Say(WhyNot());
        }
    }

    /// <summary>MSBuild's default folder for the options as they stand, shown once worked out.</summary>
    private async Task DefaultFolderAsync()
    {
        int mine = ++generation;
        CancellationToken cancel = (reads ??= new CancellationTokenSource()).Token;
        using IDisposable held = BackgroundWork.Scope();
        PropertiesAnswer answer;
        try
        {
            answer = await read(Chosen?.Name, Configuration, Framework, Runtime, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (mine != generation || folderNamed || folderBox.Text != shownFolder || answer.Problem is not null || PublishProfiles.FolderOf(project.Path, answer.Values) is not { } at)
        {
            return;
        }
        ShowFolder(PublishProfiles.Named(project.Path, at, open));
        Destination = at;
        ShowDestination();
    }

    /// <summary>Where the app lands, said under the folder, and under that where Visual Studio copies it on to for a web
    /// profile of its, which dotnet publish leaves where it landed.</summary>
    private void ShowDestination()
    {
        destination.Text = Destination is { } at ? $"Publishes to {at}" : string.Empty;
        copied.Text = visualStudioCopy is { } copy ? $"Visual Studio copies it on to {copy}; dotnet publish leaves it here" : null;
        copied.IsVisible = visualStudioCopy is not null && Destination is not null;
    }

    /// <summary>What can be chosen now: nothing while reading or saving; single-file and ReadyToRun with a runtime;
    /// trimming for a self-contained app.</summary>
    private void Enable()
    {
        bool idle = !reading && !saving && loaded is not null;
        foreach (Control control in (Control[])[configurationBox, frameworkBox, deploymentBox, runtimeBox, folderBox, browse, nameBox])
        {
            control.IsEnabled = idle;
        }
        profileBox.IsEnabled = !saving;
        bool runtime = Runtime is not null;
        singleFileBox.IsEnabled = idle && runtime;
        readyToRunBox.IsEnabled = idle && runtime;
        trimBox.IsEnabled = idle && runtime && SelfContained;
        ToolTip.SetTip(singleFileBox, runtime ? null : "A single file is one system's: choose a target runtime");
        ToolTip.SetTip(readyToRunBox, runtime ? null : "ReadyToRun code is one system's: choose a target runtime");
        ToolTip.SetTip(trimBox, runtime && SelfContained ? null : "Only a self-contained app is trimmed");
        publish.IsEnabled = idle && WhyNot() is null;
        saveButton.IsEnabled = publish.IsEnabled;
    }

    private void Say(string? said) => problem.Text = said;

    /// <summary>Shows a folder the dialog worked out (the profile's, or MSBuild's default), which is not one typed.</summary>
    private void ShowFolder(string folder)
    {
        shownFolder = folder;
        folderBox.Text = folder;
    }

    private static void Choose(ComboBox box, string value) =>
        box.SelectedItem = box.Items.OfType<string>().FirstOrDefault(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase)) ?? box.SelectedItem;

    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to publish to" });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            OutputFolder = PublishProfiles.Named(project.Path, PathNames.Canonical(path), open);
        }
    }

    private static Control Row(string label, Control editor, bool top = false) => FormRows.Of(label, editor, top);
}
