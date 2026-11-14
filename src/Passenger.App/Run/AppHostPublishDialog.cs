using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.App.Aspire;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>Publish for an Aspire AppHost: through its own publishers (Docker Compose's, Kubernetes', a cloud's, as its code
/// adds them), or one of its deploy steps as the AppHost declares them (read from it as the dialog opens, each greyed with
/// why where a login it needs is missing), into the folder chosen (the one it published to last, else aspire-output beside
/// it), with the command that does the same outside Passenger. Publish closes with the publish to run.</summary>
public sealed class AppHostPublishDialog : IdeWindow
{
    private const string PublishWords = "Its publishers, as its code adds them (Docker Compose files with AddDockerComposeEnvironment), write its files";

    private readonly string project;
    private readonly string open;
    private readonly IReadOnlyList<string> configured;
    private readonly TextBox folderBox = new() { Classes = { StyleClasses.Search } };
    private readonly Button browse = new() { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox stepBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock stepSaid = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock destination = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextTrimming = TextTrimming.PathSegmentEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock command = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button publish = DialogFooter.Default("Publish");
    private readonly CancellationTokenSource closing = new();
    private DeployOffer? offer;

    /// <param name="output">The folder it published to last, or null.</param>
    /// <param name="readSteps">Reads the deploy steps it offers; null offers none.</param>
    /// <param name="configured">What its dotnet run is given for the build configuration chosen.</param>
    public AppHostPublishDialog(string project, string open, string? output, Func<CancellationToken, Task<DeployOffer>>? readSteps = null,
        IReadOnlyList<string>? configured = null)
    {
        FormRows.Scope(this);
        this.project = project;
        this.open = open;
        this.configured = configured ?? [];
        string name = Path.GetFileNameWithoutExtension(project);
        Title = $"Publish {name}";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        problem.Classes.Add(StyleClasses.Error);
        publish.Click += (_, _) => Publish();
        browse.Click += (_, _) => IdeErrors.Observe(BrowseAsync());
        folderBox.TextChanged += (_, _) => Changed();
        stepBox.SelectionChanged += (_, _) => Changed();
        Closed += (_, _) => closing.Cancel();
        ListSteps();
        StackPanel fields = new()
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Children = { FormRows.Of("Run:", stepBox), FormRows.Of(null, stepSaid) } },
                new StackPanel { Children = { FormRows.Of("Output folder:", BrowseRow.Of(folderBox, browse)), FormRows.Of(null, destination) } },
            },
        };
        Content = DialogFooter.Headed($"Publish {name}", command, fields, DialogFooter.OkCancel(publish, () => Close(null), problem));
        OutputFolder = output is null ? "aspire-output" : PublishProfiles.Named(project, output, open).TrimEnd('/');
        if (readSteps is not null)
        {
            IsReadingSteps = true;
            IdeErrors.Observe(ReadStepsAsync(readSteps));
        }
        Changed();
    }

    /// <summary>The output folder as typed: from the AppHost's folder, or in full.</summary>
    public string OutputFolder
    {
        get => folderBox.Text ?? string.Empty;
        set => folderBox.Text = value;
    }

    /// <summary>The folder its files land in, in full; null while none is typed.</summary>
    public string? Destination => OutputFolder.Trim() is { Length: > 0 } typed ? PublishProfiles.Full(project, typed) : null;

    /// <summary>The command that publishes it as Passenger does, there to run outside it.</summary>
    public string Command => command.Text ?? string.Empty;

    /// <summary>Why it cannot publish as it stands; empty when it can.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>Whether its deploy steps are being read from it.</summary>
    public bool IsReadingSteps { get; private set; }

    /// <summary>The steps offered, publish first, each with why it cannot run here; null where it can.</summary>
    public IReadOnlyList<(string Name, string? WhyNot)> Steps =>
        [(AppHostPipeline.Publish, null), .. (offer?.Steps ?? []).Select(step => (step.Name, step.WhyNot))];

    /// <summary>What the dialog says of the steps: the one chosen's words, why none are offered, or why each refused is.</summary>
    public string StepsSaid => stepSaid.Text ?? string.Empty;

    /// <summary>The step chosen by its name; set, it chooses one that can run here, and stays where it cannot.</summary>
    public string Step
    {
        get => (stepBox.SelectedItem as ComboBoxItem)?.Tag is DeployStep step ? step.Name : AppHostPipeline.Publish;
        set
        {
            if (stepBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled && NameOf(item) == value) is { } chosen)
            {
                stepBox.SelectedItem = chosen;
            }
        }
    }

    /// <summary>Why a publish cannot start now (another build runs), which keeps the dialog open saying so; null when it can.</summary>
    public Func<string?>? Busy { get; init; }

    /// <summary>Publish: the dialog closed with the publish to run. Null once closed, else why not.</summary>
    public string? Publish()
    {
        DeployStep? step = (stepBox.SelectedItem as ComboBoxItem)?.Tag as DeployStep;
        string? why = Busy?.Invoke() ?? (Destination is null ? Refusals.NoOutputFolder : step?.WhyNot);
        if (why is not null)
        {
            problem.Text = why;
            return why;
        }
        Close(new PublishRequest(project, null)
        {
            Folder = Destination,
            Kind = PublishKind.AppHost,
            Step = step?.Name ?? AppHostPipeline.Publish,
            Needs = step?.Needs ?? DeployNeeds.None,
        });
        return null;
    }

    private static string NameOf(ComboBoxItem item) => item.Tag is DeployStep step ? step.Name : AppHostPipeline.Publish;

    /// <summary>The steps offered: publish, then each deploy step read, one a login it needs is missing for greyed.</summary>
    private void ListSteps()
    {
        string chosen = Step;
        List<ComboBoxItem> items = [new ComboBoxItem { Content = AppHostPipeline.Publish }];
        foreach (DeployStep step in offer?.Steps ?? [])
        {
            ComboBoxItem item = new() { Content = step.Name, Tag = step, IsEnabled = step.WhyNot is null };
            if ((step.WhyNot ?? step.Description) is { } tip)
            {
                ToolTip.SetTip(item, tip);
                ToolTip.SetShowOnDisabled(item, true);
            }
            items.Add(item);
        }
        stepBox.ItemsSource = items;
        stepBox.SelectedItem = items.FirstOrDefault(item => item.IsEnabled && NameOf(item) == chosen) ?? items[0];
    }

    private async Task ReadStepsAsync(Func<CancellationToken, Task<DeployOffer>> readSteps)
    {
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            offer = await readSteps(closing.Token);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            IsReadingSteps = false;
        }
        ListSteps();
        Changed();
    }

    private void Changed()
    {
        string at = Destination ?? "…";
        DeployStep? step = (stepBox.SelectedItem as ComboBoxItem)?.Tag as DeployStep;
        destination.Text = Destination is null ? string.Empty : $"Writes its files to {at}";
        command.Text = $"dotnet run --project {ChildProcesses.Quote(Path.GetRelativePath(open, project))} --no-launch-profile "
            + string.Concat(configured.Select(word => $"{ChildProcesses.Quote(word)} ")) + "-- "
            + string.Join(' ', AppHostPipeline.RunArguments(step?.Name ?? AppHostPipeline.Publish, Destination is null ? at : Path.GetRelativePath(open, Destination))
                .Select(ChildProcesses.Quote));
        IEnumerable<string> refused = (offer?.Steps ?? []).Where(s => s.Missing is not null).GroupBy(s => s.Missing!)
            .Select(same => DeployLogins.Said([.. same.Select(s => s.Name)], same.Key));
        string chosenSaid = (step is null ? PublishWords : step.Description ?? $"Runs {step.Name} and the steps it runs after, as the AppHost declares them").TrimEnd('.');
        stepSaid.Text = IsReadingSteps ? $"{chosenSaid}. Reading its deploy steps…"
            : offer?.Problem is { } none ? $"{chosenSaid}. No deploy steps: {none}"
            : string.Join('\n', [$"{chosenSaid}.", .. refused]);
        publish.Content = step is null ? "Publish" : "Deploy";
        problem.Text = Destination is null ? Refusals.NoOutputFolder : string.Empty;
        publish.IsEnabled = Destination is not null;
    }

    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to publish to" });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            OutputFolder = PublishProfiles.Named(project, PathNames.Canonical(path), open).TrimEnd('/');
        }
    }
}
