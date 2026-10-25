using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>Clone Repository: a repository's URL and the folder to clone it into, git's progress while it clones,
/// and why it could not when it fails, the URL left to correct. Cancel while it clones stops git and removes what it had
/// made; a clone that succeeds closes the dialog with the folder it made in <see cref="Cloned"/>.</summary>
public sealed partial class CloneDialog : Window
{
    private readonly TextBox url;
    private readonly TextBox parent;
    private readonly TextBlock into;
    private readonly ProgressBar bar;
    private readonly TextBlock progress;
    private readonly TextBlock problem;
    private readonly Button clone;
    private readonly Button browse;
    private readonly StackPanel working;
    private CancellationTokenSource? cloning;

    /// <param name="folder">The folder offered to clone into.</param>
    public CloneDialog(string folder)
    {
        Title = "Clone Repository";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        url = new TextBox { Classes = { "search" }, PlaceholderText = "https://github.com/owner/repository.git" };
        parent = new TextBox { Classes = { "search" }, Text = folder };
        url.TextChanged += (_, _) => ShowDestination();
        parent.TextChanged += (_, _) => ShowDestination();
        browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => IdeErrors.Observe(BrowseAsync());
        into = new TextBlock { Classes = { "muted" }, FontSize = 12, TextTrimming = TextTrimming.PathSegmentEllipsis, Margin = new Thickness(0, 6, 0, 0) };
        bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4 };
        progress = new TextBlock { Classes = { "muted" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        working = new StackPanel { Spacing = 6, IsVisible = false, Children = { bar, progress } };
        problem = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));

        clone = new Button { Content = "Clone", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        clone.Click += (_, _) => IdeErrors.Observe(CloneAsync());
        cancel.Click += (_, _) => Cancel();
        Closing += (_, _) => cloning?.Cancel();

        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        Add(fields, new TextBlock { Text = "URL:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }, 0, 0);
        Add(fields, url, 0, 1);
        Grid where = BrowseRow.Of(parent, browse);
        where.Margin = new Thickness(0, 10, 0, 0);
        Add(fields, new TextBlock { Text = "Directory:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 0) }, 1, 0);
        Add(fields, where, 1, 1);
        Add(fields, into, 2, 1);

        Content = DialogFooter.Page(new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Clone Repository", FontWeight = FontWeight.SemiBold },
                fields,
                working,
                problem,
            },
        }, DialogFooter.Bar(DialogFooter.Of(clone, cancel)));
        ShowDestination();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => url.Focus(), DispatcherPriority.Loaded);
    }

    public string Url
    {
        get => url.Text ?? string.Empty;
        set => url.Text = value;
    }

    /// <summary>The folder the repository is cloned into a folder of its own in.</summary>
    public string Folder
    {
        get => parent.Text ?? string.Empty;
        set => parent.Text = value;
    }

    /// <summary>Where the repository goes: a folder named as it is, in <see cref="Folder"/>; null until both say.</summary>
    public string? Destination =>
        RepositoryName(Url) is { } name && Folder.Trim() is { Length: > 0 } folder ? Path.Combine(Path.GetFullPath(folder), name) : null;

    /// <summary>Why the last clone could not be made, or null.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    /// <summary>What git said last of its progress.</summary>
    public string? Progress => progress.Text;

    public bool IsCloning => cloning is not null;

    /// <summary>The folder cloned into, canonical, once the clone is made; null until then, and when given up.</summary>
    public string? Cloned { get; private set; }

    /// <summary>The name git gives the folder of a repository cloned from a URL: its last part, without .git.</summary>
    public static string? RepositoryName(string url)
    {
        string trimmed = url.Trim().TrimEnd('/', '\\');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].TrimEnd('/', '\\');
        }
        string name = trimmed[(trimmed.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        return name is "" or "." or ".." ? null : name;
    }

    /// <summary>Clones, as the Clone button does: the dialog closes once the clone is made, or says why it was not.</summary>
    public async Task CloneAsync()
    {
        if (cloning is not null)
        {
            return;
        }
        if (WhyNot() is { } refused)
        {
            Fail(refused);
            return;
        }
        string destination = Destination!;
        bool existed = Directory.Exists(destination);
        using CancellationTokenSource cancel = new();
        cloning = cancel;
        Fail(null);
        Busy(true, $"Cloning into {destination}…");
        try
        {
            GitResult result = await Git.CloneAsync(Url.Trim(), destination, UiThread.Marshal<string>(ShowProgress), cancel.Token);
            if (result.Succeeded)
            {
                Cloned = PathNames.Canonical(destination);
                cloning = null;
                Close(Cloned);
                return;
            }
            Fail($"Could not clone: {Said(result)}");
        }
        catch (OperationCanceledException)
        {
            // Stopped: git is ended, and what it made goes.
            await RemoveAsync(destination, existed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The folder to clone into could not be made.
            Fail($"Could not clone: {error.Message}");
        }
        finally
        {
            cloning = null;
            Busy(false, null);
        }
    }

    /// <summary>Cancel: stops a clone under way and removes what it made, and closes the dialog.</summary>
    public void Cancel()
    {
        cloning?.Cancel();
        Close(null);
    }

    private string? WhyNot()
    {
        if (Url.Trim().Length == 0)
        {
            return "A repository URL is needed";
        }
        if (Folder.Trim().Length == 0)
        {
            return "A directory to clone into is needed";
        }
        if (Destination is not { } destination)
        {
            return $"'{Url.Trim()}' names no repository to clone";
        }
        // Git on Windows says a missing folder "does not appear to be a git repository", which hides the plain reason.
        if (Path.IsPathFullyQualified(Url.Trim()) && !Directory.Exists(Url.Trim()) && !File.Exists(Url.Trim()))
        {
            return $"{Url.Trim()} does not exist";
        }
        if (File.Exists(destination))
        {
            return $"{destination} is a file";
        }
        return Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()
            ? $"{destination} already exists and is not empty"
            : null;
    }

    /// <summary>What git said of why it failed: its fatal and error lines on, without their prefixes.</summary>
    private static string Said(GitResult result)
    {
        string[] lines = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int first = Array.FindIndex(lines, l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal));
        if (first < 0)
        {
            return result.Message;
        }
        return string.Join(' ', lines[first..].Select(l => Prefix().Replace(l, string.Empty)));
    }

    [GeneratedRegex("^(fatal|error): ")]
    private static partial Regex Prefix();

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();

    private void ShowProgress(string line)
    {
        if (cloning is null)
        {
            return;
        }
        progress.Text = line;
        if (Percent().Match(line) is { Success: true } percent)
        {
            bar.IsIndeterminate = false;
            bar.Value = int.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }

    private void ShowDestination()
    {
        into.Text = Destination is { } destination ? $"Clones into {destination}" : string.Empty;
    }

    private void Fail(string? why)
    {
        problem.Text = why;
        problem.IsVisible = why is not null;
    }

    private void Busy(bool busy, string? saying)
    {
        working.IsVisible = busy;
        progress.Text = saying;
        bar.IsIndeterminate = true;
        url.IsEnabled = !busy;
        parent.IsEnabled = !busy;
        browse.IsEnabled = !busy;
        clone.IsEnabled = !busy;
    }

    /// <summary>What a clone stopped part way made: the folder, or what it put in one that was there and empty. Git
    /// writes its objects read-only, which Windows will not delete as they are.</summary>
    private static Task RemoveAsync(string destination, bool existed) => BackgroundWork.Run(() =>
    {
        try
        {
            if (!Directory.Exists(destination))
            {
                return true;
            }
            foreach (string file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            if (!existed)
            {
                Directory.Delete(destination, recursive: true);
                return true;
            }
            foreach (string entry in Directory.EnumerateFileSystemEntries(destination))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"A clone stopped part way left {destination}: {error.Message}");
            return false;
        }
    });

    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Directory to clone into" });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            Folder = path;
        }
    }

    private static void Add(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
