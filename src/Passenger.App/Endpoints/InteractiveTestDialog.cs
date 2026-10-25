using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Endpoints;

/// <summary>Interactive Test: a request of an .http file opened to change before it goes (method, URL, headers, body),
/// the criteria its answer is held to, and how it is sent (timeout, redirects, untrusted certificates). Send sends it on
/// a client of its own, as many times as asked, each answer shown beside it; the file itself is never changed.</summary>
public sealed class InteractiveTestDialog : Window
{
    private readonly EndpointRequest request;
    private readonly EndpointsView endpoints;
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
    private static readonly string[] TabNames = ["Params", "Headers", "Body", "Expected"];
    private readonly ToggleButton[] tabs;
    private readonly Control[] pages;

    /// <summary>The request opened, sent through the Endpoints window in the variables of the environment chosen
    /// there.</summary>
    public InteractiveTestDialog(EndpointRequest request, EndpointsView endpoints)
    {
        this.request = request;
        this.endpoints = endpoints;
        string? environment = endpoints.Environment;
        Title = $"Interactive Test: {request.Title}";
        Width = 1040;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Method = new ComboBox { ItemsSource = HttpFile.Methods, SelectedItem = HttpFile.Methods.Contains(request.Method) ? request.Method : "GET", Width = 110 };
        Url = Mono(request.Url, multiline: false);
        Params = new KeyValueGrid("Key", "Value");
        Params.Set(SplitQuery(request.Url).Pairs.Select(p => (true, p.Key, p.Value)));
        Headers = new KeyValueGrid("Header", "Value");
        Headers.Set(request.Headers.Select(h => (true, h.Key, h.Value)));
        Body = Mono(request.Body, multiline: true);
        Body.PlaceholderText = "The request's body; < ./file.json sends that file";
        Criteria = Mono(string.Join("\n", request.Expectations.Select(e => e.ToString())), multiline: true);
        Criteria.PlaceholderText = "status 2xx\ntime < 500\nheader Content-Type contains json\njson $.id exists";
        Timeout = new NumericUpDown { Minimum = 1, Maximum = 600, Value = 30, Increment = 5, FormatString = "0", Width = 120 };
        FollowRedirects = new CheckBox { Content = "Follow redirects", IsChecked = true };
        AcceptAnyCertificate = new CheckBox { Content = "Accept any certificate" };
        ToolTip.SetTip(AcceptAnyCertificate, "Take a certificate the system does not trust, as a development server's own");

        // The URL's query and the Params table are one: typing in either rewrites the other.
        Url.TextChanged += (_, _) => FollowUrl();
        Params.Changed += (_, _) => FollowParams();
        Params.Changed += (_, _) => Count();
        Headers.Changed += (_, _) => Count();
        Criteria.TextChanged += (_, _) => Count();

        DockPanel urlRow = new() { Children = { Method, Url } };
        DockPanel.SetDock(Method, Dock.Left);
        Method.Margin = new Thickness(0, 0, 8, 0);

        pages =
        [
            Page(Params, "The URL's query: {{variables}} are resolved as it is sent, a row turned off is left out"),
            Page(Headers, null),
            Page(Body, null),
            Page(Criteria, "One criterion a line; with none, an answer below 400 passes"),
        ];
        tabs = [.. TabNames.Select((name, i) => ToolWindows.ToolTabs.Tab(name, i == 0))];
        for (int i = 0; i < tabs.Length; i++)
        {
            int page = i;
            tabs[i].Click += (_, _) => ShowPage(page);
        }
        StackPanel tabRow = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        tabRow.Children.AddRange(tabs);
        Panel pageArea = new();
        pageArea.Children.AddRange(pages);

        Grid fields = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 6 };
        Add(fields, urlRow, 0);
        Add(fields, Caption(environment is null ? "No environment: {{variables}} take the file's values" : $"Variables from the {environment} environment and the file"), 1);
        Add(fields, tabRow, 2);
        Add(fields, pageArea, 3);
        // Wrapped, so a narrow request pane puts the check boxes under the timeout rather than past the divider.
        WrapPanel options = new()
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 16,
            LineSpacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Timeout (s)", VerticalAlignment = VerticalAlignment.Center }, Timeout } },
                FollowRedirects,
                AcceptAnyCertificate,
            },
        };
        Add(fields, options, 4);
        // A request with a body and no query opens on its body, as that is what there is to change.
        ShowPage(request.Body.Length > 0 && SplitQuery(request.Url).Pairs.Count == 0 ? 2 : 0);
        Count();

        Response = new ResponseView("Send to see the response");
        Grid panes = new() { ColumnDefinitions = new ColumnDefinitions("*,1,*"), Margin = new Thickness(0) };
        fields.Margin = new Thickness(16, 16, 12, 12);
        panes.Children.Add(fields);
        panes.Children.Add(new HairlineDivider(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 });
        Response[Grid.ColumnProperty] = 2;
        Response.Margin = new Thickness(4, 8, 8, 8);
        panes.Children.Add(Response);

        Send = new Button { Content = "Send", IsDefault = true, Classes = { "primary" } };
        Button close = new() { Content = "Close", IsCancel = true };
        Send.Click += (_, _) => IdeErrors.Observe(SendAsync());
        close.Click += (_, _) => Close();
        // A send under way is the window's test, which ends with the dialog.
        Closed += (_, _) => endpoints.Stop();
        Content = DialogFooter.Page(panes, DialogFooter.Bar(DialogFooter.Of(Send, close), problem));
        Paint(problem, TokenKeys.Error);
    }

    public ComboBox Method { get; }
    public TextBox Url { get; }
    public KeyValueGrid Params { get; }
    public KeyValueGrid Headers { get; }
    public TextBox Body { get; }
    public TextBox Criteria { get; }
    public NumericUpDown Timeout { get; }
    public CheckBox FollowRedirects { get; }
    public CheckBox AcceptAnyCertificate { get; }
    public Button Send { get; }
    public ResponseView Response { get; }

    /// <summary>Why the request as it stands cannot go, empty when it can.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>Sends the request as the dialog has it on a new client and shows the answer held to the criteria; while
    /// one is under way, says so.</summary>
    public async Task SendAsync()
    {
        UiThread.Check();
        problem.Text = string.Empty;
        IReadOnlyList<KeyValuePair<string, string>> headers = Headers.Enabled;
        if (headers.Any(h => h.Key.Length == 0))
        {
            problem.Text = $"The header with the value \"{headers.First(h => h.Key.Length == 0).Value}\" has no name";
            ShowPage(1);
            return;
        }
        List<Expectation> criteria = [];
        foreach (string line in Lines(Criteria.Text))
        {
            if (Expectation.Problem(line) is { } why)
            {
                problem.Text = why;
                return;
            }
            criteria.Add(Expectation.Parse(line)!);
        }
        string method = Method.SelectedItem as string ?? "GET";
        EndpointDraft draft = new(method, Url.Text ?? string.Empty, headers, (Body.Text ?? string.Empty).Replace("\r\n", "\n"));
        SendOptions options = new()
        {
            Timeout = TimeSpan.FromSeconds((double)(Timeout.Value ?? 30)),
            FollowRedirects = FollowRedirects.IsChecked == true,
            AcceptAnyCertificate = AcceptAnyCertificate.IsChecked == true,
        };
        if (endpoints.Busy is { } under)
        {
            problem.Text = ActionWork.Busy(under);
            return;
        }
        await endpoints.SendAsync(request, draft, options, criteria.Count > 0 ? criteria : [Expectation.Answered], Response, this);
    }

    /// <summary>Shows a tab's page: 0 Params, 1 Headers, 2 Body, 3 Expected.</summary>
    public void ShowPage(int page)
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].IsChecked = i == page;
            pages[i].IsVisible = i == page;
        }
    }

    /// <summary>The page shown.</summary>
    public int PageShown => Array.FindIndex(pages, p => p.IsVisible);

    /// <summary>The tabs' names as shown, each with how many rows or criteria it holds: Params (2).</summary>
    public IReadOnlyList<string> TabTitles => [.. tabs.Select(t => (t.Content as TextBlock)?.Text ?? string.Empty)];

    private void Count()
    {
        int[] counts = [Params.Enabled.Count, Headers.Enabled.Count, 0, Lines(Criteria.Text).Count()];
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].Content is TextBlock title)
            {
                title.Text = counts[i] > 0 ? $"{TabNames[i]} ({counts[i]})" : TabNames[i];
            }
        }
    }

    /// <summary>The URL typed: the table takes its query's pairs, the rows turned off kept after them.</summary>
    /// <remarks>TextBox raises TextChanged after the change, so the URL the table just wrote comes back here later: a query
    /// that already is the table's rows on leaves the table alone, and the row being typed in keeps the keys.</remarks>
    private void FollowUrl()
    {
        List<(string Key, string Value)> query = SplitQuery(Url.Text ?? string.Empty).Pairs;
        if (query.SequenceEqual(Params.Enabled.Select(p => (p.Key, p.Value))))
        {
            return;
        }
        List<(bool, string, string)> off = [.. Params.Pairs.Where(p => !p.On)];
        Params.Set([.. query.Select(p => (true, p.Key, p.Value)), .. off]);
        Count();
    }

    /// <summary>The table changed: the URL's query is its rows turned on, the rest of the URL as it was.</summary>
    private void FollowParams()
    {
        string url = JoinQuery(SplitQuery(Url.Text ?? string.Empty).Base, Params.Enabled);
        if (url != Url.Text)
        {
            Url.Text = url;
        }
    }

    /// <summary>A URL's part before its query, and the query's pairs as written, {{variables}} and escapes kept.</summary>
    internal static (string Base, List<(string Key, string Value)> Pairs) SplitQuery(string url)
    {
        int mark = url.IndexOf('?');
        if (mark < 0)
        {
            return (url, []);
        }
        List<(string, string)> pairs = [];
        foreach (string part in url[(mark + 1)..].Split('&'))
        {
            if (part.Length > 0)
            {
                int equals = part.IndexOf('=');
                pairs.Add(equals < 0 ? (part, string.Empty) : (part[..equals], part[(equals + 1)..]));
            }
        }
        return (url[..mark], pairs);
    }

    internal static string JoinQuery(string head, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        string query = string.Join("&", pairs.Select(p => p.Value.Length == 0 ? p.Key : $"{p.Key}={p.Value}"));
        return query.Length == 0 ? head : $"{head}?{query}";
    }

    private static Control Page(Control content, string? caption)
    {
        if (caption is null)
        {
            return content;
        }
        TextBlock note = Caption(caption);
        note.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(note, Dock.Bottom);
        return new DockPanel { Children = { note, content } };
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

    private static TextBox Mono(string text, bool multiline)
    {
        TextBox box = new() { Text = text, FontFamily = CSharp.Fonts.Mono, FontSize = 12, AcceptsReturn = multiline, TextWrapping = TextWrapping.NoWrap };
        if (multiline)
        {
            box.VerticalContentAlignment = VerticalAlignment.Top;
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        }
        return box;
    }

    private static TextBlock Caption(string text)
    {
        TextBlock caption = new() { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        Paint(caption, TokenKeys.TextMuted);
        return caption;
    }

    private static void Paint(TextBlock text, string token) => text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(token));

    private static void Add(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}
