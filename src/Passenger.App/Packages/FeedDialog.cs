using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Welcome;
using Passenger.Packages;

namespace Passenger.App.Packages;

/// <summary>The Sources tab's dialog for a feed, new or edited: its name, URL, user and password, whether it is enabled
/// and allows insecure connections, and the file its credentials are saved in. The password is never shown: left empty,
/// the file keeps the one it has. OK closes it with <see cref="Result"/> once the name is new to the file and a URL or
/// folder is given.</summary>
public sealed class FeedDialog : IdeWindow
{
    private readonly ConfigFile file;
    private readonly ConfigFeed? feed;
    private readonly TextBox name;
    private readonly TextBox url;
    private readonly TextBox user;
    private readonly TextBox password;
    private readonly CheckBox enabled;
    private readonly CheckBox insecure;
    private readonly TextBlock problem;

    public FeedDialog(ConfigFile file, ConfigFeed? feed)
    {
        FormRows.Scope(this);
        this.file = file;
        this.feed = feed;
        Title = feed is null ? "New feed" : $"Editing: {feed.Name}";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        name = new TextBox { Text = feed?.Name ?? "New feed" };
        url = new TextBox { Text = feed?.Value ?? string.Empty, PlaceholderText = "A feed's index.json, a v2 URL, or a folder" };
        user = new TextBox { Text = feed?.Username ?? string.Empty };
        password = new TextBox { PasswordChar = '•', PlaceholderText = feed?.HasPassword == true ? "Unchanged" : string.Empty };
        enabled = new CheckBox { Content = "Enabled", IsChecked = feed is null || !feed.DisabledHere };
        insecure = new CheckBox { Content = "Allow insecure connections", IsChecked = feed?.AllowInsecure == true };
        problem = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        problem.Classes.Add(StyleClasses.Error);

        StackPanel fields = new()
        {
            Spacing = Gaps.Normal,
            Children =
            {
                FormRows.Of("Name", name),
                FormRows.Of("URL", url),
                FormRows.Of("User", user),
                FormRows.Of("Password", password),
                FormRows.Of(null, enabled),
                FormRows.Of(null, insecure),
            },
        };

        TextBlock saved = new()
        {
            Classes = { StyleClasses.Muted, StyleClasses.Caption },
            TextWrapping = TextWrapping.Wrap,
            Text = $"Credentials will be saved to {RecentRow.Home(file.Path)}"
                + (OperatingSystem.IsWindows() ? string.Empty : ", the password in clear text: NuGet encrypts passwords only on Windows"),
        };

        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Accept();
        Content = DialogFooter.Page(new StackPanel
        {
            [DialogFooter.InsetProperty] = true,
            Spacing = FormRows.Between,
            Children =
            {
                fields,
                saved,
                problem,
            },
        }, DialogFooter.OkCancel(ok, () => Close(false)));
        Opened += (_, _) => Dispatcher.UIThread.Post(() => (feed is null ? url : name).Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The feed as the dialog left it, once OK took it.</summary>
    public FeedEdit? Result { get; private set; }

    /// <summary>Why OK did not close it, or null.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    public string FeedName
    {
        get => name.Text ?? string.Empty;
        set => name.Text = value;
    }

    public string Url
    {
        get => url.Text ?? string.Empty;
        set => url.Text = value;
    }

    public string User
    {
        get => user.Text ?? string.Empty;
        set => user.Text = value;
    }

    public string Password
    {
        set => password.Text = value;
    }

    public bool FeedEnabled
    {
        get => enabled.IsChecked == true;
        set => enabled.IsChecked = value;
    }

    public bool AllowInsecure
    {
        get => insecure.IsChecked == true;
        set => insecure.IsChecked = value;
    }

    /// <summary>OK: the feed, unless its name or URL will not do.</summary>
    public void Accept()
    {
        string chosen = FeedName.Trim();
        string? why = chosen.Length == 0 ? "A feed needs a name"
            : Url.Trim().Length == 0 ? "A feed needs a URL or a folder"
            : file.Feeds.Any(f => f.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase) && !f.Name.Equals(feed?.Name, StringComparison.OrdinalIgnoreCase))
                ? $"{Path.GetFileName(file.Path)} has a feed named {chosen} already"
            // NuGet refuses to read a file whose credentials name a user without a password.
            : User.Trim().Length > 0 && password.Text is not { Length: > 0 } && feed?.HasPassword != true ? "A user needs a password"
            : null;
        if (why is not null)
        {
            problem.Text = why;
            problem.IsVisible = true;
            return;
        }
        Result = new FeedEdit(chosen, Url.Trim(), FeedEnabled, AllowInsecure, User.Trim().Length == 0 ? null : User.Trim(), password.Text is { Length: > 0 } secret ? secret : null);
        Close(true);
    }
}
