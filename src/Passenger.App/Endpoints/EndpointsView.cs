using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App.Endpoints;

/// <summary>What the Endpoints window works with: the window's actions' work (a test is one), the open folder's answers
/// to what its requests may take from the person's computer, and the status bar.</summary>
public sealed record EndpointsParts(ActionWork Work, Func<EndpointGrants> Grants, Action<string> Say);

/// <summary>A request as it is to be sent: as written, or as changed in the Interactive Test dialog.</summary>
public sealed record EndpointDraft(string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body);

/// <summary>What became of a send: answered, refused for what the request is (a variable with no value), or never sent
/// (not allowed, stopped, or another under way).</summary>
public enum EndpointSent
{
    Answered,
    Refused,
    Withheld,
}

/// <summary>The Endpoints window: every request of the open folder's .http and .rest files, under its file, with its
/// method; Test sends one as written and Interactive Test opens it in a dialog to change before sending. The answer of
/// the last test shows under the list. The environment chosen gives the requests' <c>{{variables}}</c> their values.</summary>
public sealed class EndpointsView : DockPanel
{
    /// <summary>A file's heading in the list.</summary>
    public sealed record FileRow(string Path, string Name);

    /// <summary>A web project's heading over the routes its code declares.</summary>
    public sealed record ProjectRow(string Path, string Name);

    private readonly ListBox list;
    private readonly TextBlock placeholder;
    private readonly ComboBox environments;
    private readonly EndpointsParts parts;
    private readonly Dictionary<(string File, string Request), bool> outcomes = [];
    private IReadOnlyList<HttpFile> files = [];
    private IReadOnlyList<WebRoute> routes = [];
    private string? root;
    private int reads;
    private bool shown;
    private bool stale;
    private CancellationTokenSource? sending;

    public EndpointsView(EndpointsParts parts)
    {
        this.parts = parts;
        Response = new ResponseView("Test a request to see its response here");
        list = new ListBox
        {
            Classes = { "tree" },
        };
        ReusedRows.Use(list, Row);
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is WebRoute route)
            {
                RouteGoToRequested?.Invoke(this, route);
                return;
            }
            GoToChosen();
        };
        list.KeyDown += (_, e) =>
        {
            if (list.SelectedItem is WebRoute route && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.F4)
            {
                (e.Key == Key.Enter ? RouteSendRequested : RouteGoToRequested)?.Invoke(this, route);
                e.Handled = true;
                return;
            }
            if (list.SelectedItem is not EndpointRequest chosen)
            {
                return;
            }
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                IdeErrors.Observe(TestAsync(chosen));
                e.Handled = true;
            }
            else if (e.Key == Key.F4)
            {
                GoToChosen();
                e.Handled = true;
            }
        };
        ListMenus.Attach(list, () => list.SelectedItem is WebRoute route ?
        [
            .. RouteRequests.Sendable(route) ? (IEnumerable<MenuItem>)[ListMenus.Item("Send Request", "Enter", () => RouteSendRequested?.Invoke(this, route))] : [],
            ListMenus.Item("Go to Route", "F4", () => RouteGoToRequested?.Invoke(this, route)),
        ] : list.SelectedItem is not EndpointRequest chosen ? [] :
        [
            ListMenus.Item("Test", "Enter", () => IdeErrors.Observe(TestAsync(chosen))),
            ListMenus.Item("Interactive Test…", null, () => InteractiveRequested?.Invoke(this, chosen)),
            ListMenus.Item("Go to Request", "F4", GoToChosen),
        ]);
        placeholder = new TextBlock { Text = "No requests: add an .http file (GET https://…)", Classes = { "emptyText" } };

        environments = new ComboBox { MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
        environments.SelectionChanged += (_, _) => EnvironmentChanged?.Invoke(this, EventArgs.Empty);
        ToolTip.SetTip(environments, $"The environment of {HttpVariables.PublicFile} whose variables the requests use");
        Button refresh = ToolButtons.Of(IconKeys.Rerun, "Read the .http files again", () => Reload(root));
        TextBlock label = new() { Text = "Environment", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        StackPanel bar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4), Spacing = 4, Children = { label, environments, refresh } };
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);

        Grid panes = new() { RowDefinitions = new RowDefinitions("*,1,*") };
        Panel top = new() { Children = { list, placeholder } };
        panes.Children.Add(top);
        HairlineDivider divider = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 };
        panes.Children.Add(divider);
        Response[Grid.RowProperty] = 2;
        panes.Children.Add(Response);
        Children.Add(panes);
        Refresh();
    }

    /// <summary>The last test's answer.</summary>
    public ResponseView Response { get; }

    /// <summary>A request to open in the editor at its request line.</summary>
    public event EventHandler<EndpointRequest>? GoToRequested;

    /// <summary>A route whose request is to be sent, from its row.</summary>
    public event EventHandler<WebRoute>? RouteSendRequested;

    /// <summary>A route to open in the editor where its code declares it.</summary>
    public event EventHandler<WebRoute>? RouteGoToRequested;

    /// <summary>The web projects' routes listed, as their code declares them.</summary>
    public IReadOnlyList<WebRoute> Routes => routes;

    /// <summary>Lists the web projects' routes under the .http files' requests.</summary>
    public void ShowRoutes(IReadOnlyList<WebRoute> found)
    {
        UiThread.Check();
        routes = found;
        Refresh();
    }

    /// <summary>A request to test in the Interactive Test dialog.</summary>
    public event EventHandler<EndpointRequest>? InteractiveRequested;

    public event EventHandler? EnvironmentChanged;

    /// <summary>The requests listed, in the files' order.</summary>
    public IReadOnlyList<EndpointRequest> Requests => [.. files.SelectMany(f => f.Requests)];

    /// <summary>The environment chosen, null with none.</summary>
    public string? Environment => environments.SelectedItem as string is { } chosen && chosen != NoEnvironment ? chosen : null;

    private const string NoEnvironment = "No environment";

    /// <summary>Whether a request's last test passed, failed (false) or has not run (null).</summary>
    public bool? Outcome(EndpointRequest request) => outcomes.TryGetValue(OutcomeKey(request), out bool passed) ? passed : null;

    /// <summary>Chooses an environment by name, as the person would.</summary>
    public void ChooseEnvironment(string name) => environments.SelectedItem = name;

    /// <summary>Reads the folder's .http and .rest files again off the UI thread, or as the window is next shown while it
    /// is hidden, so a folder opened with it closed is walked once, by its file index alone; another folder's list goes
    /// at once.</summary>
    public void Reload(string? folder)
    {
        UiThread.Check();
        if (folder != root)
        {
            files = [];
            outcomes.Clear();
            Response.Clear();
            Refresh();
        }
        root = folder;
        int read = ++reads;
        stale = folder is not null && !shown;
        if (folder is null || !shown)
        {
            return;
        }
        IdeErrors.Observe(ReadAsync(folder, read));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        shown = true;
        if (stale)
        {
            Reload(root);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        shown = false;
    }

    private async Task ReadAsync(string folder, int read)
    {
        (List<HttpFile> found, List<string> names) = await BackgroundWork.Run(() =>
        {
            List<string> paths = [.. Workspace.FileIndex.Walk(folder).Select(p => Path.Combine(folder, p))];
            List<HttpFile> read = [.. paths.Where(HttpFile.Is).Order(StringComparer.OrdinalIgnoreCase).Select(TryRead).OfType<HttpFile>()];
            List<string> envFiles = [.. paths.Where(p => Path.GetFileName(p) is HttpVariables.PublicFile or HttpVariables.PrivateFile)];
            return (read, HttpVariables.Environments(envFiles).ToList());
        });
        if (read != reads)
        {
            return;
        }
        files = found;
        string? chosen = environments.SelectedItem as string;
        environments.ItemsSource = (string[])[NoEnvironment, .. names];
        environments.SelectedItem = chosen is not null && names.Contains(chosen) ? chosen : names.Count > 0 ? names[0] : NoEnvironment;
        Refresh();
    }

    private static HttpFile? TryRead(string path)
    {
        try
        {
            return HttpFile.Read(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Refresh()
    {
        object? selected = list.SelectedItem;
        List<object> rows = [];
        foreach (HttpFile file in files.Where(f => f.Requests.Count > 0))
        {
            rows.Add(new FileRow(file.Path, Core.PathNames.Shown(file.Path, root)));
            rows.AddRange(file.Requests);
        }
        foreach (IGrouping<string, WebRoute> project in routes.GroupBy(r => r.Project, Core.PathNames.Comparer))
        {
            rows.Add(new ProjectRow(project.Key, Path.GetFileNameWithoutExtension(project.Key)));
            rows.AddRange(project);
        }
        list.ItemsSource = rows;
        list.SelectedItem = selected is EndpointRequest was ? rows.OfType<EndpointRequest>().FirstOrDefault(r => r.File == was.File && r.Line == was.Line)
            : selected is WebRoute chosen ? rows.OfType<WebRoute>().FirstOrDefault(r => r.Project == chosen.Project && RouteRequests.Title(r) == RouteRequests.Title(chosen) && r.Handler == chosen.Handler)
            : null;
        placeholder.IsVisible = rows.Count == 0;
    }

    /// <summary>Test: the request sent as written, on a client of its own, its answer held to its criteria (an answer
    /// below 400 when it has none); to <paramref name="address"/> in place of the host it names when given (the running
    /// app's), its variables from <paramref name="file"/> as given (one just written) rather than as the list last read it.</summary>
    public async Task TestAsync(EndpointRequest request, string? address = null, HttpFile? file = null)
    {
        UiThread.Check();
        list.SelectedItem = Listed().FirstOrDefault(r => r.File == request.File && r.Line == request.Line);
        IReadOnlyList<Expectation> criteria = request.Expectations.Count > 0 ? request.Expectations : [Expectation.Answered];
        // Sent to the address given (the running app's), whatever host the file names.
        EndpointDraft draft = new(request.Method, address is null ? request.Url : RouteRequests.WithHost(request.Url, address), request.Headers, request.Body);
        switch (await SendAsync(request, draft, new SendOptions(), criteria, Response, TopLevel.GetTopLevel(this) as Window, file))
        {
            case EndpointSent.Answered:
                Record(request, Response.Passed);
                break;
            case EndpointSent.Refused:
                Record(request, false);
                break;
        }
    }

    /// <summary>Sends a request as Test does, from this window or the Interactive Test dialog: as the window's one test
    /// under way (the status bar says its stage and Stop ends it), its env files read off the UI thread, and the person
    /// asked first when it takes something from their computer to a host the folder has not allowed; its body's file is
    /// read only once it may go. What became of it shows in <paramref name="shown"/>.</summary>
    internal async Task<EndpointSent> SendAsync(EndpointRequest request, EndpointDraft draft, SendOptions options,
        IReadOnlyList<Expectation> criteria, ResponseView shown, Window? owner, HttpFile? written = null)
    {
        UiThread.Check();
        if (parts.Work.Begin(AppActions.TestEndpoint, $"Testing {draft.Method} {request.Title}…") is not { } testing)
        {
            parts.Say(ActionWork.Busy(parts.Work.Of(AppActions.TestEndpoint)!));
            return EndpointSent.Withheld;
        }
        using Working held = testing;
        using CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(testing.Stopping);
        sending = mine;
        try
        {
            HttpFile file = written ?? FileOf(request);
            string? environment = Environment;
            string? folder = root;
            (EndpointCall? call, string? problem) = await BackgroundWork.Run(() => EndpointClient.Prepare(request, draft.Method, draft.Url,
                draft.Headers, draft.Body, HttpVariables.For(file, environment, folder), folder));
            mine.Token.ThrowIfCancellationRequested();
            if (call is null)
            {
                shown.Refused(problem!);
                return EndpointSent.Refused;
            }
            EndpointGrants grants = parts.Grants();
            if (grants.Unallowed(call).Count > 0)
            {
                if (owner is null)
                {
                    shown.Refused($"Not sent: nothing could ask whether {call.Host} may have what it reads");
                    return EndpointSent.Withheld;
                }
                testing.Say($"Waiting for your answer: may {request.Title} send what it reads to {call.Host}?");
                EndpointQuestionDialog question = new(request.Title, call.Host, call.Reads);
                Question = question;
                bool allowed;
                try
                {
                    allowed = await question.ShowDialog<bool>(owner);
                }
                finally
                {
                    Question = null;
                }
                mine.Token.ThrowIfCancellationRequested();
                if (!allowed)
                {
                    shown.Refused($"Not sent: what it reads was not allowed to go to {call.Host}");
                    return EndpointSent.Withheld;
                }
                grants.Allow(call, question.Always.IsChecked == true);
            }
            testing.Say($"Sending {call.Method} {call.Url}…");
            shown.Sending($"Sending {call.Method} {call.Url}…");
            EndpointResponse response = await BackgroundWork.Run(() => EndpointClient.SendAsync(call, options, mine.Token), mine.Token);
            shown.Show(response, [.. criteria.Select(c => c.Check(response))]);
            return EndpointSent.Answered;
        }
        catch (OperationCanceledException)
        {
            shown.Refused("Stopped before an answer came");
            return EndpointSent.Withheld;
        }
        finally
        {
            if (sending == mine)
            {
                sending = null;
            }
        }
    }

    /// <summary>The test under way, or null.</summary>
    public Working? Busy => parts.Work.Of(AppActions.TestEndpoint);

    /// <summary>The question a test asks now, for the tests.</summary>
    public EndpointQuestionDialog? Question { get; private set; }

    /// <summary>The request chosen in the list, which Test Request tests.</summary>
    public EndpointRequest? Chosen => list.SelectedItem as EndpointRequest;

    /// <summary>Stops a test under way, as the folder closes.</summary>
    public void Stop() => sending?.Cancel();

    private IEnumerable<EndpointRequest> Listed() => (list.ItemsSource as IEnumerable<object> ?? []).OfType<EndpointRequest>();

    private HttpFile FileOf(EndpointRequest request) =>
        files.FirstOrDefault(f => f.Path == request.File) ?? new HttpFile(request.File, new Dictionary<string, string>(), []);

    /// <summary>A request's last outcome is kept by its name, else its method and URL as written, so it stays with it as
    /// lines move.</summary>
    private static (string File, string Request) OutcomeKey(EndpointRequest request) =>
        (request.File, request.Name.Length > 0 ? "# @name " + request.Name : $"{request.Method} {request.Url}");

    private void Record(EndpointRequest request, bool passed)
    {
        outcomes[OutcomeKey(request)] = passed;
        Refresh();
    }

    private void GoToChosen()
    {
        if (list.SelectedItem is EndpointRequest chosen)
        {
            GoToRequested?.Invoke(this, chosen);
        }
    }

    /// <summary>A row for whichever heading, request or route the list hands its container: a file's or project's icon and
    /// name; a request's Test and Interactive Test, its last outcome, its method and title (a named one's URL after it,
    /// muted); a route's method, template, what declares it (muted) and Send where a request can reach it.</summary>
    private Control Row()
    {
        RowIcon headingIcon = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) };
        TextBlock headingName = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        StackPanel heading = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Margin = new Thickness(8, 0), IsHitTestVisible = false, Children = { headingIcon, headingName } };
        Panel row = new();
        (DockPanel request, Action<EndpointRequest> showRequest) = RequestRow(row);
        (DockPanel route, Action<WebRoute> showRoute) = RouteRow(row);
        row.Children.Add(heading);
        row.Children.Add(request);
        row.Children.Add(route);
        return row.Showing<object>(item =>
        {
            heading.IsVisible = item is FileRow or ProjectRow;
            request.IsVisible = item is EndpointRequest;
            route.IsVisible = item is WebRoute;
            switch (item)
            {
                case FileRow file:
                    headingIcon.DataKey = IconKeys.DocumentText;
                    headingName.Text = file.Name;
                    ToolTip.SetTip(headingName, file.Path);
                    break;
                case ProjectRow project:
                    headingIcon.DataKey = IconKeys.Endpoints;
                    headingName.Text = project.Name;
                    ToolTip.SetTip(headingName, project.Path);
                    break;
                case EndpointRequest shown:
                    showRequest(shown);
                    break;
                case WebRoute shown:
                    showRoute(shown);
                    break;
            }
        });
    }

    /// <summary>A request's line in a row, and what shows a request in it; its buttons act on the request the row shows.</summary>
    private (DockPanel Line, Action<EndpointRequest> Show) RequestRow(Control row)
    {
        Button test = ToolButtons.Of(IconKeys.Run, "Test", () =>
        {
            if (row.DataContext is EndpointRequest request)
            {
                IdeErrors.Observe(TestAsync(request));
            }
        });
        Button interactive = ToolButtons.Of(IconKeys.Edit, "Interactive Test…", () =>
        {
            if (row.DataContext is EndpointRequest request)
            {
                InteractiveRequested?.Invoke(this, request);
            }
        });
        test.Name = "endpointTest";
        ((PathIcon)test.Content!).Bind(PathIcon.ForegroundProperty, test.GetResourceObservable(TokenKeys.Run));
        interactive.Name = "endpointInteractive";
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { test, interactive } };
        DockPanel.SetDock(buttons, Dock.Right);
        RowIcon outcome = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(outcome, Dock.Left);
        Border method = MethodTag(string.Empty);
        DockPanel.SetDock(method, Dock.Left);
        TextBlock title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // A named request shows its URL after the name, muted, ending in an ellipsis first.
        TextBlock url = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        url.Classes.Add("muted");
        // The title takes what the row leaves, so a narrow window ends it in an ellipsis and keeps the buttons.
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(22, 0, 4, 0), Children = { buttons, outcome, method, title, url } };
        string? methodColour = MethodColour(string.Empty);
        return (line, request =>
        {
            bool? passed = Outcome(request);
            outcome.DataKey = passed is not { } done ? null : done ? IconKeys.TestPassed : IconKeys.TestFailed;
            outcome.ForegroundKey = passed is not { } ran ? null : ran ? TokenKeys.Test_Passed : TokenKeys.Test_Failed;
            ShowMethod(method, ref methodColour, request.Method);
            title.Text = request.Title;
            ToolTip.SetTip(line, $"{request.Method} {request.Url}");
            bool named = request.Name.Length > 0;
            title.MaxWidth = named ? 220 : double.PositiveInfinity;
            url.IsVisible = named;
            url.Text = named ? request.Url : string.Empty;
        });
    }

    /// <summary>A route's line in a row, and what shows a route in it: its method, its template, what declares it (muted),
    /// and Send where a request can reach it, which sends the route the row shows.</summary>
    private (DockPanel Line, Action<WebRoute> Show) RouteRow(Control row)
    {
        Button send = ToolButtons.Of(IconKeys.Run, "Send Request", () =>
        {
            if (row.DataContext is WebRoute route)
            {
                RouteSendRequested?.Invoke(this, route);
            }
        });
        send.Name = "routeSend";
        ((PathIcon)send.Content!).Bind(PathIcon.ForegroundProperty, send.GetResourceObservable(TokenKeys.Run));
        DockPanel.SetDock(send, Dock.Right);
        Border method = MethodTag(string.Empty);
        DockPanel.SetDock(method, Dock.Left);
        // The template first: what declares it takes the room it leaves, and goes before the template ends in an ellipsis.
        TextBlock template = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(template, Dock.Left);
        TextBlock kind = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(8, 0, 4, 0), TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        kind.Classes.Add("muted");
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(22, 0, 4, 0), Children = { send, method, template, kind } };
        string? methodColour = MethodColour(string.Empty);
        return (line, route =>
        {
            send.IsVisible = RouteRequests.Sendable(route);
            ShowMethod(method, ref methodColour, route.Kind == RouteKinds.SignalR ? "HUB" : route.Methods.Count == 0 ? "ANY" : route.Methods[0]);
            template.Text = route.Template + (route.Query is { } query ? "?" + query : string.Empty);
            kind.Text = route.Kind;
            ToolTip.SetTip(line, $"{RouteRequests.Title(route)}: {route.Handler}");
        });
    }

    /// <summary>Shows a method in a tag <see cref="MethodTag"/> made, its colour bound anew only for another colour.</summary>
    private static void ShowMethod(Border tag, ref string? colour, string method)
    {
        TextBlock text = (TextBlock)tag.Child!;
        text.Text = method;
        text.Resource(TextBlock.ForegroundProperty, ref colour, MethodColour(method));
    }

    /// <summary>A method's tag, in its method's colour.</summary>
    internal static Border MethodTag(string method)
    {
        TextBlock text = new() { Text = method, FontFamily = CSharp.Fonts.Mono, FontSize = 11, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(MethodColour(method)));
        return new Border { Classes = { "tag" }, MinWidth = 52, Padding = new Thickness(4, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Child = text };
    }

    /// <summary>The colour a method's tag shows its name in.</summary>
    internal static string MethodColour(string method) => method switch
    {
        "GET" => TokenKeys.Test_Passed,
        "POST" => TokenKeys.Link,
        "PUT" => TokenKeys.Warning,
        "PATCH" => TokenKeys.Syntax_Number,
        "DELETE" => TokenKeys.Error,
        _ => TokenKeys.TextMuted,
    };
}
