using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Welcome;
using Passenger.Packages;

namespace Passenger.App.Packages;

/// <summary>The Sources tab's dialog for a feed, new or edited: its name, URL, user and password, whether it is enabled
/// and allows insecure connections, and the file its credentials are saved in. The password is never shown: left empty,
/// the file keeps the one it has. OK closes it with <see cref="Result"/> once the name is new to the file and a URL or
/// folder is given.</summary>
public sealed class FeedDialog : Window
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
        this.file = file;
        this.feed = feed;
        Title = feed is null ? "New feed" : $"Editing: {feed.Name}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        name = new TextBox { Classes = { "search" }, Text = feed?.Name ?? "New feed" };
        url = new TextBox { Classes = { "search" }, Text = feed?.Value ?? string.Empty, PlaceholderText = "A feed's index.json, a v2 URL, or a folder" };
        user = new TextBox { Classes = { "search" }, Text = feed?.Username ?? string.Empty };
        password = new TextBox { Classes = { "search" }, PasswordChar = '•', PlaceholderText = feed?.HasPassword == true ? "Unchanged" : string.Empty };
        enabled = new CheckBox { IsChecked = feed is null || !feed.DisabledHere };
        insecure = new CheckBox { IsChecked = feed?.AllowInsecure == true };
        problem = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));

        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"), RowSpacing = 8 };
        Row(fields, 0, "Name:", name);
        Row(fields, 1, "URL:", url);
        Row(fields, 2, "User:", user);
        Row(fields, 3, "Password:", password);
        Row(fields, 4, "Enabled:", enabled);
        Row(fields, 5, "Allow insecure connections:", insecure);

        TextBlock saved = new()
        {
            Classes = { "muted" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = $"Credentials will be saved to {RecentRow.Home(file.Path)}"
                + (OperatingSystem.IsWindows() ? string.Empty : ", the password in clear text: NuGet encrypts passwords only on Windows"),
        };

        Button ok = new() { Content = "OK", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(false);
        Content = DialogFooter.Page(new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = Title, FontWeight = FontWeight.SemiBold },
                fields,
                saved,
                problem,
            },
        }, DialogFooter.Bar(DialogFooter.Of(ok, cancel)));
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

    private static void Row(Grid grid, int row, string label, Control field)
    {
        TextBlock text = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
    }
}
