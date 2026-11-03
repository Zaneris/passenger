using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>Publish for an Aspire AppHost: through its own publishers (Docker Compose's, Kubernetes', a cloud's, as its code
/// adds them), into the folder chosen (the one it published to last, else aspire-output beside it), with the command that
/// does the same outside Passenger. Publish closes with the publish to run.</summary>
public sealed class AppHostPublishDialog : IdeWindow
{
    private readonly string project;
    private readonly string open;
    private readonly TextBox folderBox = new() { Classes = { StyleClasses.Search } };
    private readonly Button browse = new() { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock destination = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextTrimming = TextTrimming.PathSegmentEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock command = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button publish = DialogFooter.Default("Publish");

    /// <param name="output">The folder it published to last, or null.</param>
    public AppHostPublishDialog(string project, string open, string? output)
    {
        FormRows.Scope(this);
        this.project = project;
        this.open = open;
        string name = Path.GetFileNameWithoutExtension(project);
        Title = $"Publish {name}";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));
        publish.Click += (_, _) => Publish();
        browse.Click += (_, _) => IdeErrors.Observe(BrowseAsync());
        folderBox.TextChanged += (_, _) => Changed();
        StackPanel fields = new()
        {
            Spacing = 10,
            Children =
            {
                FormRows.Of("Target:", new TextBlock
                {
                    Text = "Its publishers, as its code adds them (Docker Compose files with AddDockerComposeEnvironment)",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                }),
                new StackPanel { Children = { FormRows.Of("Output folder:", BrowseRow.Of(folderBox, browse)), FormRows.Of(null, destination) } },
            },
        };
        Content = DialogFooter.Headed($"Publish {name}", command, fields, DialogFooter.OkCancel(publish, () => Close(null), problem));
        OutputFolder = output is null ? "aspire-output" : PublishProfiles.Named(project, output, open).TrimEnd('/');
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

    /// <summary>Why a publish cannot start now (another build runs), which keeps the dialog open saying so; null when it can.</summary>
    public Func<string?>? Busy { get; init; }

    /// <summary>Publish: the dialog closed with the publish to run. Null once closed, else why not.</summary>
    public string? Publish()
    {
        string? why = Busy?.Invoke() ?? (Destination is null ? "An output folder is needed" : null);
        if (why is not null)
        {
            problem.Text = why;
            return why;
        }
        Close(new PublishRequest(project, null) { Folder = Destination, Kind = PublishKind.AppHost });
        return null;
    }

    private void Changed()
    {
        string at = Destination ?? "…";
        destination.Text = Destination is null ? string.Empty : $"Writes its files to {at}";
        command.Text = $"dotnet run --project {ChildProcesses.Quote(Path.GetRelativePath(open, project))} --no-launch-profile -- "
            + string.Join(' ', PublishRequest.AppHostPublish(Destination is null ? at : Path.GetRelativePath(open, Destination)).Select(ChildProcesses.Quote));
        problem.Text = Destination is null ? "An output folder is needed" : string.Empty;
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
