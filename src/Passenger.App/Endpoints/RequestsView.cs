using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;

namespace Passenger.App.Endpoints;

/// <summary>The Requests window: the live requests of the web app Run or Debug runs, newest first, each with its method,
/// path, status and time, and under it the database commands it issued with their time and rows, a command its request ran
/// again and again marked as repeated (an N+1) and a slow one as slow. Followed only while the window shows. A view of the
/// <see cref="RequestLog"/>, built as the window is first shown, listing what it heard before.</summary>
public sealed class RequestsView : DockPanel
{
    /// <summary>A command's row, under its request's.</summary>
    public sealed record CommandRow(LiveRequest Request, LiveCommand Command);

    /// <summary>A row listed: a request or a command, which its row shows anew in place as what it shows changes.</summary>
    private sealed class Row(object data) : Observable
    {
        public object Data { get; } = data;

        public void Redraw() => Raise(nameof(Data));
    }

    private readonly ListBox list;
    private readonly TextBlock placeholder;
    private readonly TextBlock following;
    private readonly RequestLog log;
    private readonly ObservableCollection<Row> rows = [];

    /// <summary>Each request listed, its row and how many of its commands are listed under it.</summary>
    private readonly Dictionary<LiveRequest, (Row Row, int Commands)> listed = [];

    public RequestsView(RequestLog log)
    {
        this.log = log;
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
        };
        ReusedRows.Use(list, View);
        list.DoubleTapped += (_, _) => Open();
        list.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F4 && e.KeyModifiers == KeyModifiers.None)
            {
                Open();
                e.Handled = true;
            }
        };
        placeholder = new TextBlock { Text = "No requests yet: run or debug a web app and send it one", Classes = { "emptyText" } };
        following = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        following.Classes.Add("muted");
        Button clear = ToolButtons.Of(IconKeys.Clear, "Clear the requests", log.Clear);
        DockPanel bar = new() { Margin = new Thickness(8, 4) };
        DockPanel.SetDock(clear, Dock.Left);
        bar.Children.Add(clear);
        bar.Children.Add(following);
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(new Panel { Children = { list, placeholder } });
        log.Changed += (_, told) => Take(told);
        log.FollowingChanged += (_, _) => following.Text = log.Following;
        following.Text = log.Following;
        Take(new RequestChanges([], [], [], All: true));
    }

    /// <summary>The window shows, or no longer does: the app is followed only while it does.</summary>
    public event EventHandler<bool>? ShownChanged;

    /// <summary>A request to open where its route's handler is (or, for one that failed, where its exception was thrown).</summary>
    public event EventHandler<LiveRequest>? RequestOpened;

    /// <summary>A command to open its text in an editor tab.</summary>
    public event EventHandler<LiveCommand>? CommandOpened;

    /// <summary>Whether the window shows now.</summary>
    public bool IsShown { get; private set; }

    /// <summary>The rows listed: each request, newest first, followed by its commands.</summary>
    public IReadOnlyList<object> Rows => [.. rows.Select(row => row.Data)];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        IsShown = true;
        ShownChanged?.Invoke(this, true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        IsShown = false;
        ShownChanged?.Invoke(this, false);
    }

    /// <summary>Opens a row as a double click does.</summary>
    public void Open(object row)
    {
        switch (row)
        {
            case LiveRequest request:
                RequestOpened?.Invoke(this, request);
                break;
            case CommandRow command:
                CommandOpened?.Invoke(this, command.Command);
                break;
        }
    }

    private void Open()
    {
        if (list.SelectedItem is Row chosen)
        {
            Open(chosen.Data);
        }
    }

    /// <summary>What a batch changed, and that alone: a request that came put in at the top with its commands, one that changed
    /// drawn again with its new commands put in under it, one that went taken out; every other row and its control kept.</summary>
    private void Take(RequestChanges told)
    {
        if (told.All)
        {
            rows.Clear();
            listed.Clear();
            foreach (LiveRequest request in Enumerable.Reverse(log.Requests))
            {
                Put(rows.Count, request);
            }
        }
        foreach (LiveRequest gone in told.Went)
        {
            if (listed.Remove(gone, out (Row Row, int Commands) was))
            {
                // The oldest are last.
                int at = rows.Count - 1;
                while (at >= 0 && rows[at] != was.Row)
                {
                    at--;
                }
                for (int i = 0; at >= 0 && i <= was.Commands; i++)
                {
                    rows.RemoveAt(at);
                }
            }
        }
        foreach (LiveRequest request in told.Came)
        {
            Put(0, request);
        }
        foreach (LiveRequest request in told.Changed)
        {
            if (!listed.TryGetValue(request, out (Row Row, int Commands) shown) || rows.IndexOf(shown.Row) is not (>= 0 and var at))
            {
                continue;
            }
            // A new command can change those before it (how often its text ran), so each is drawn again where it is shown.
            for (int i = 0; i <= shown.Commands; i++)
            {
                rows[at + i].Redraw();
            }
            for (int i = shown.Commands; i < request.Commands.Count; i++)
            {
                rows.Insert(at + 1 + i, new Row(new CommandRow(request, request.Commands[i])));
            }
            listed[request] = (shown.Row, request.Commands.Count);
        }
        placeholder.IsVisible = rows.Count == 0;
    }

    /// <summary>A request's row and its commands' put in at a place.</summary>
    private void Put(int at, LiveRequest request)
    {
        Row row = new(request);
        rows.Insert(at, row);
        for (int i = 0; i < request.Commands.Count; i++)
        {
            rows.Insert(at + 1 + i, new Row(new CommandRow(request, request.Commands[i])));
        }
        listed[request] = (row, request.Commands.Count);
    }

    /// <summary>A row for whichever request or command the list hands its container, shown anew in place as what it shows
    /// changes.</summary>
    private static Control View()
    {
        RequestLine request = new();
        CommandLine command = new();
        return new Panel { Children = { request.Line, command.Line } }.Showing<Row>(row =>
        {
            request.Line.IsVisible = row.Data is LiveRequest;
            command.Line.IsVisible = row.Data is CommandRow;
            if (row.Data is LiveRequest shown)
            {
                request.Show(shown);
            }
            else if (row.Data is CommandRow listed)
            {
                command.Show(listed.Command);
            }
        });
    }

    /// <summary>A request's line: its method, its path, its status and time, and a mark where it ran a command again and again.</summary>
    private sealed class RequestLine
    {
        private readonly Border method = EndpointsView.MethodTag(string.Empty);
        private readonly TextBlock outcome = Muted(string.Empty);
        private readonly Border repeated = Marked("N+1", TokenKeys.Warning);
        private readonly TextBlock path = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private string? methodColour = EndpointsView.MethodColour(string.Empty);
        private string? outcomeColour;

        public RequestLine()
        {
            DockPanel.SetDock(method, Dock.Left);
            DockPanel.SetDock(outcome, Dock.Right);
            DockPanel.SetDock(repeated, Dock.Right);
            Line = new DockPanel { Height = ListRow.Height, Margin = new Thickness(8, 0, 4, 0), Children = { method, outcome, repeated, path } };
        }

        public DockPanel Line { get; }

        public void Show(LiveRequest request)
        {
            TextBlock verb = (TextBlock)method.Child!;
            verb.Text = request.Method;
            verb.Resource(TextBlock.ForegroundProperty, ref methodColour, EndpointsView.MethodColour(request.Method));
            string time = request.Milliseconds is { } ms ? string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms") : "…";
            outcome.Text = $"{(request.Status is { } status ? status.ToString(CultureInfo.InvariantCulture) : "…")}  {time}";
            outcome.Resource(TextBlock.ForegroundProperty, ref outcomeColour, request.Error is not null || request.Status >= 500 ? TokenKeys.Error : null);
            repeated.IsVisible = request.HasRepeats;
            path.Text = request.Path + request.Query;
            ToolTip.SetTip(Line, (request.Route is { } route ? $"{RouteRequests.Title(route)}: {route.Handler}" : $"{request.Method} {request.Path}{request.Query}") + $" ({request.From})");
        }
    }

    /// <summary>A command's line under its request's: its text, its rows or reads and time, and marks where it was slow or
    /// ran again and again.</summary>
    private sealed class CommandLine
    {
        private readonly TextBlock outcome = Muted(string.Empty);
        private readonly Border slow = Marked("slow", TokenKeys.Error);
        private readonly Border repeated = Marked(string.Empty, TokenKeys.Warning);
        private readonly TextBlock text = new()
        {
            FontFamily = CSharp.Fonts.Mono,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        public CommandLine()
        {
            DockPanel.SetDock(outcome, Dock.Right);
            DockPanel.SetDock(slow, Dock.Right);
            DockPanel.SetDock(repeated, Dock.Right);
            Line = new DockPanel { Height = ListRow.Height, Margin = new Thickness(30, 0, 4, 0), Children = { outcome, slow, repeated, text } };
        }

        public DockPanel Line { get; }

        public void Show(LiveCommand command)
        {
            string rows = command.Rows is { } count and >= 0 ? $"{count} {(count == 1 ? "row" : "rows")} changed  "
                : command.Reads is { } reads ? $"{reads} {(reads == 1 ? "read" : "reads")}  " : string.Empty;
            outcome.Text = command.Error ?? string.Create(CultureInfo.InvariantCulture, $"{rows}{command.Milliseconds:0.#} ms");
            slow.IsVisible = command.Slow;
            repeated.IsVisible = command.Repeated;
            ((TextBlock)repeated.Child!).Text = command.Repeated ? $"×{command.Runs}" : string.Empty;
            text.Text = string.Join(' ', command.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            ToolTip.SetTip(Line, command.Parameters is { Length: > 0 } parameters ? command.Text + "\n" + parameters : command.Text);
        }
    }

    private static TextBlock Muted(string words)
    {
        TextBlock text = new() { Text = words, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        text.Classes.Add("muted");
        return text;
    }

    private static Border Marked(string words, string colour)
    {
        TextBlock text = new() { Text = words, FontSize = 11, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(colour));
        return new Border { Classes = { "tag" }, Padding = new Thickness(4, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = text };
    }
}
