using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Passenger.App.Aspire;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;
using Passenger.Editor;

namespace Passenger.App.Telemetry;

/// <summary>The two pages of a run's telemetry.</summary>
public enum TelemetryPage
{
    Logs,
    Traces,
}

/// <summary>A run's structured logs and traces as its dashboard keeps them (<see cref="TelemetrySource"/>), one view for
/// every kind of run, its two pages in one place through <see cref="Both"/>. A page streams only while its host shows it
/// (<see cref="Watch"/>), every resource's or one's; a log's trace and a span's logs are a click apart.</summary>
public sealed class TelemetryView : IDisposable
{
    private readonly OutputView logDetails = new(toolbar: false);
    private readonly OutputView spanDetails = new(toolbar: false);
    private readonly TextBlock logsStatus = Said();
    private readonly TextBlock tracesStatus = Said();
    private readonly Button logTrace;
    private readonly Button spanLogs;
    private readonly object gate = new();
    private IReadOnlyList<AspireTrace> traces = [];

    /// <summary>The lists' items, kept by their keys through each snapshot (<see cref="RunRows.Put"/>), so a click survives one.</summary>
    private readonly ObservableCollection<RunItem<AspireStructuredLog>> logItems = [];
    private readonly ObservableCollection<RunItem<AspireTrace>> traceItems = [];
    private readonly ObservableCollection<RunItem<SpanRow>> spanItems = [];
    private CancellationTokenSource? reading;
    private (TelemetrySource Source, TelemetryPage Page, string? Resource)? watched;
    private (TelemetrySource Source, string? Resource)? shownFor;
    private Action? pending;

    /// <summary>Log batches heard and not yet listed, each with the stream it came on and the resource it is shown for,
    /// listed together as one landing.</summary>
    private readonly List<(AspireLogBatch Batch, string? Resource, CancellationToken Stream)> heardLogs = [];

    /// <summary>The logs stream whose first landing lists its logs anew: a stream read again sends what is kept from the start.</summary>
    private CancellationToken freshLogs;
    private bool queued;
    private bool disposed;
    private DockPanel? both;
    private Action<TelemetryPage>? choose;

    /// <summary>A log's trace, or a span's logs, asked for before the other page streamed them: chosen as they come.</summary>
    private (string TraceId, string? SpanId)? wantedTrace;
    private (string TraceId, string? SpanId)? wantedLogs;

    /// <summary>The pages, their two buttons (a log's trace, a span's logs) running a host's actions of those ids through
    /// <paramref name="request"/> when it gives them, else this view's own.</summary>
    public TelemetryView(string? showTrace = null, string? showLogs = null, Func<string, bool>? request = null)
    {
        StructuredLogs = List(logItems, log => log.ToString(), "Structured logs");
        TraceList = List(traceItems, trace => trace.ToString(), "Traces");
        SpanList = List(spanItems, span => new string(' ', Math.Min(span.Depth, 32) * 2) + span.Span, "Trace span hierarchy");
        StructuredLogs.SelectionChanged += (_, _) => ShowLog();
        TraceList.SelectionChanged += (_, _) => ShowTrace();
        SpanList.SelectionChanged += (_, _) => ShowSpan();
        logTrace = PageTool(IconKeys.Locate, "Show the structured log's trace and span", showTrace, "telemetryShowTrace", ShowSelectedTrace, request);
        spanLogs = PageTool(IconKeys.DocumentText, "Show a retained log of this trace and span", showLogs, "telemetryShowLogs", ShowRelatedLogs, request);
        logTrace.IsEnabled = false;
        spanLogs.IsEnabled = false;
        foreach (OutputView output in new[] { logDetails, spanDetails })
        {
            output.LinkClicked += (_, link) => LinkClicked?.Invoke(this, link);
            output.Links.Folders = () => Folders?.Invoke() ?? [];
        }
        LogsPage = WithHeader(PageBar(logsStatus, logTrace), Split(StructuredLogs, logDetails));
        Grid traceBody = new() { ColumnDefinitions = new ColumnDefinitions("*,1,2*") };
        traceBody.Children.Add(new Border { Classes = { "framed" }, Child = TraceList });
        traceBody.Children.Add(new HairlineDivider(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 });
        Control spanBody = Split(SpanList, spanDetails);
        spanBody[Grid.ColumnProperty] = 2;
        traceBody.Children.Add(spanBody);
        TracesPage = WithHeader(PageBar(tracesStatus, spanLogs), traceBody);
        Clear();
    }

    public ListBox StructuredLogs { get; }
    public ListBox TraceList { get; }
    public ListBox SpanList { get; }

    /// <summary>The structured logs listed, oldest first.</summary>
    public IReadOnlyList<AspireStructuredLog> Logs => [.. logItems.Select(item => item.Data)];

    /// <summary>The traces listed, newest first.</summary>
    public IReadOnlyList<AspireTrace> Traces => [.. traceItems.Select(item => item.Data)];

    /// <summary>The chosen trace's spans as a tree, each at its depth.</summary>
    public IReadOnlyList<SpanRow> Spans => [.. spanItems.Select(item => item.Data)];

    public AspireStructuredLog? SelectedLog => (StructuredLogs.SelectedItem as RunItem<AspireStructuredLog>)?.Data;
    public AspireTrace? SelectedTrace => (TraceList.SelectedItem as RunItem<AspireTrace>)?.Data;
    public SpanRow? SelectedSpan => (SpanList.SelectedItem as RunItem<SpanRow>)?.Data;

    /// <summary>The list item that shows a record (a log; a trace or a span by its id), whose row a click finds.</summary>
    public object? ItemOf(object record) => record switch
    {
        AspireStructuredLog log => logItems.FirstOrDefault(item => ReferenceEquals(item.Data, log))
            ?? logItems.FirstOrDefault(item => JsonElement.DeepEquals(item.Data.Data, log.Data)),
        AspireTrace trace => traceItems.FirstOrDefault(item => item.Key == trace.TraceId),
        SpanRow span => spanItems.FirstOrDefault(item => item.Key == span.Span.SpanId),
        _ => null,
    };

    /// <summary>The logs page: the retained logs, the chosen one's details, its trace a click away.</summary>
    public Control LogsPage { get; }

    /// <summary>The traces page: the retained traces, the chosen one's spans as a tree, a span's details and logs.</summary>
    public Control TracesPage { get; }

    /// <summary>What the logs page says: how many are retained, or why there are none.</summary>
    public string LogsStatus => logsStatus.Text ?? string.Empty;

    /// <summary>What the traces page says.</summary>
    public string TracesStatus => tracesStatus.Text ?? string.Empty;

    /// <summary>The folders a path in a record's details is a link from.</summary>
    public Func<IReadOnlyList<string>>? Folders { get; set; }

    /// <summary>A link in a record's details clicked.</summary>
    public event EventHandler<LinkTarget>? LinkClicked;

    /// <summary>The other page asked for (a log's trace, a span's logs), which the host shows.</summary>
    public event EventHandler<TelemetryPage>? PageWanted;

    /// <summary>The page shown in <see cref="Both"/>, which a host with one place for telemetry gives it.</summary>
    public TelemetryPage Shown { get; private set; }

    /// <summary>Both pages in one place, their tabs over them as a tool window's are.</summary>
    public Control Both
    {
        get
        {
            if (both is null)
            {
                ToggleButton logs = ToolTabs.Tab("Structured logs", Shown == TelemetryPage.Logs);
                ToggleButton spans = ToolTabs.Tab("Traces", Shown == TelemetryPage.Traces);
                logs.Name = "telemetryLogs";
                spans.Name = "telemetryTraces";
                ContentControl page = new() { Content = Shown == TelemetryPage.Logs ? LogsPage : TracesPage };
                logs.Click += (_, _) => Choose(TelemetryPage.Logs);
                spans.Click += (_, _) => Choose(TelemetryPage.Traces);
                PageWanted += (_, wanted) => Choose(wanted);
                StackPanel strip = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { logs, spans } };
                Border tabBar = ToolBars.Top(strip);
                both = WithHeader(tabBar, page);
                choose = Choose;

                void Choose(TelemetryPage chosen)
                {
                    Shown = chosen;
                    logs.IsChecked = chosen == TelemetryPage.Logs;
                    spans.IsChecked = chosen == TelemetryPage.Traces;
                    page.Content = chosen == TelemetryPage.Logs ? LogsPage : TracesPage;
                    ShownChanged?.Invoke(this, chosen);
                }
            }
            return both;
        }
    }

    /// <summary>The page <see cref="Both"/> shows was chosen, which the host streams.</summary>
    public event EventHandler<TelemetryPage>? ShownChanged;

    /// <summary>Shows a page in <see cref="Both"/>.</summary>
    public void ShowPage(TelemetryPage page)
    {
        _ = Both;
        choose!(page);
    }

    /// <summary>Streams a page from the source (every resource's records, or <paramref name="resource"/>'s alone), or nothing:
    /// called by the host as what it shows changes. The same source, page and resource again keep the stream.</summary>
    public void Watch(TelemetrySource? source, TelemetryPage? page, string? resource = null)
    {
        UiThread.Check();
        (TelemetrySource, TelemetryPage, string?)? next = !disposed && source is not null && page is { } shown ? (source, shown, resource) : null;
        if (reading is not null && watched == next)
        {
            return;
        }
        Stop();
        // Another run's or resource's records are none of these: the pages start empty for them, a page switch keeps them.
        if (next is { } coming && shownFor != (coming.Item1, coming.Item3))
        {
            Clear();
            shownFor = (coming.Item1, coming.Item3);
        }
        watched = next;
        if (next is not { } stream)
        {
            return;
        }
        reading = new CancellationTokenSource();
        IdeErrors.Observe(WatchAsync(stream.Item1, stream.Item2, stream.Item3, reading.Token));
    }

    /// <summary>Empties both pages and stops streaming.</summary>
    public void Clear()
    {
        UiThread.Check();
        Stop();
        watched = null;
        shownFor = null;
        logItems.Clear();
        traces = [];
        traceItems.Clear();
        spanItems.Clear();
        logDetails.Clear();
        spanDetails.Clear();
        wantedTrace = null;
        wantedLogs = null;
        logsStatus.Text = "Structured logs stream here while this page is shown.";
        tracesStatus.Text = "Traces stream here while this page is shown.";
    }

    /// <summary>Says why there is nothing to stream (no run collects telemetry), in place of the pages' words.</summary>
    public void Unavailable(string why)
    {
        Clear();
        logsStatus.Text = why;
        tracesStatus.Text = why;
    }

    private void Stop()
    {
        reading?.Cancel();
        reading?.Dispose();
        reading = null;
    }

    private async Task WatchAsync(TelemetrySource source, TelemetryPage page, string? resource, CancellationToken cancel)
    {
        TextBlock label = page == TelemetryPage.Traces ? tracesStatus : logsStatus;
        label.Text = "Connecting to dashboard telemetry…";
        try
        {
            if (page == TelemetryPage.Logs)
            {
                freshLogs = cancel;
                await AspireTelemetry.WatchLogsAsync(source.Endpoint, source.ApiKey, batch => Heard(batch, resource, cancel), cancel).ConfigureAwait(false);
            }
            else
            {
                await AspireTelemetry.WatchTracesAsync(source.Endpoint, source.ApiKey, all => Post(() =>
                {
                    if (disposed || cancel.IsCancellationRequested)
                    {
                        return;
                    }
                    IReadOnlyList<AspireTrace> found = resource is null ? all : [.. all.Where(trace => trace.Spans.Any(span => TelemetrySource.Of(span.Service, span.Source, resource)))];
                    traces = found;
                    AspireTrace? chosen = SelectedTrace;
                    RunRows.Put(traceItems, Keyed(found, trace => trace.TraceId));
                    // Only a chosen trace a snapshot changed (a late span) is walked again, the span chosen kept.
                    if (!ReferenceEquals(SelectedTrace, chosen))
                    {
                        ShowTrace(again: true);
                    }
                    tracesStatus.Text = $"Live · {traces.Count} retained traces";
                    if (wantedTrace is { } wanted && traces.FirstOrDefault(trace => trace.TraceId == wanted.TraceId) is { } listed)
                    {
                        wantedTrace = null;
                        Choose(listed, wanted.SpanId);
                    }
                }, cancel), cancel).ConfigureAwait(false);
            }
            UiThread.Post(() =>
            {
                if (!disposed && !cancel.IsCancellationRequested)
                {
                    label.Text = "Telemetry ended. Refresh to reconnect.";
                }
            });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or TimeoutException or ArgumentException
            or NotSupportedException or System.Net.Http.HttpRequestException)
        {
            string reason = source.Unread(error);
            UiThread.Post(() =>
            {
                if (!disposed && !cancel.IsCancellationRequested)
                {
                    label.Text = $"{reason}. Refresh to reconnect.";
                }
            });
        }
    }

    /// <summary>The latest snapshot onto the UI thread, one at a time: a burst of records lands as its last.</summary>
    private void Post(Action update, CancellationToken cancel)
    {
        lock (gate)
        {
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            pending = update;
            if (queued)
            {
                return;
            }
            queued = true;
        }
        UiThread.Post(Land);
    }

    /// <summary>A log batch heard on the reader's thread, listed with those heard since at the next landing.</summary>
    private void Heard(AspireLogBatch batch, string? resource, CancellationToken stream)
    {
        lock (gate)
        {
            if (stream.IsCancellationRequested)
            {
                return;
            }
            heardLogs.Add((batch, resource, stream));
            if (queued)
            {
                return;
            }
            queued = true;
        }
        UiThread.Post(Land);
    }

    /// <summary>What came since the last landing, on the UI thread: the log batches in order, then the latest traces.</summary>
    private void Land()
    {
        Action? apply;
        (AspireLogBatch Batch, string? Resource, CancellationToken Stream)[] logs;
        lock (gate)
        {
            apply = pending;
            pending = null;
            logs = [.. heardLogs];
            heardLogs.Clear();
            queued = false;
        }
        if (logs.Length > 0)
        {
            ListLogs(logs);
        }
        apply?.Invoke();
    }

    /// <summary>Lists what log batches changed, costing what came, never what is kept: each log added after those listed
    /// (keyed by its arrival, given off the UI thread), each evicted taken from the front, where the oldest are; a stream's
    /// first batch, what it keeps from the start, lists its logs anew, the log chosen kept chosen.</summary>
    private void ListLogs(IReadOnlyList<(AspireLogBatch Batch, string? Resource, CancellationToken Stream)> heard)
    {
        bool listed = false;
        foreach ((AspireLogBatch batch, string? resource, CancellationToken stream) in heard)
        {
            if (disposed || stream.IsCancellationRequested)
            {
                continue;
            }
            listed = true;
            AspireStructuredLog? chosen = null;
            if (stream == freshLogs)
            {
                freshLogs = default;
                chosen = SelectedLog;
                logItems.Clear();
            }
            foreach (AspireStructuredLog log in batch.Added)
            {
                if (resource is null || TelemetrySource.Of(log.ServiceName, log.ResourceName, resource))
                {
                    RunItem<AspireStructuredLog> item = new(log.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), log);
                    logItems.Add(item);
                    if (chosen is not null && JsonElement.DeepEquals(log.Data, chosen.Data))
                    {
                        chosen = null;
                        StructuredLogs.SelectedItem = item;
                    }
                }
            }
            foreach (AspireStructuredLog gone in batch.Evicted)
            {
                if (logItems.Count > 0 && ReferenceEquals(logItems[0].Data, gone))
                {
                    logItems.RemoveAt(0);
                }
            }
        }
        if (!listed)
        {
            return;
        }
        logsStatus.Text = $"Live · {logItems.Count} retained structured logs";
        if (wantedLogs is { } wanted && Correlated(wanted.TraceId, wanted.SpanId) is { } found)
        {
            wantedLogs = null;
            StructuredLogs.SelectedItem = ItemOf(found);
        }
    }

    /// <summary>The chosen log's trace, its span chosen, on the traces page.</summary>
    public void ShowSelectedTrace()
    {
        if (SelectedLog is { } log)
        {
            SelectTrace(log.TraceId, log.SpanId);
        }
    }

    /// <summary>A log of the chosen span's trace and span, on the logs page.</summary>
    public void ShowRelatedLogs()
    {
        if (SelectedTrace is { } trace && SelectedSpan is { } row)
        {
            SelectLogs(trace.TraceId, row.Span.SpanId);
        }
    }

    /// <summary>A trace on the traces page, its span chosen: at once when it is listed, else as the page streams it.</summary>
    public void SelectTrace(string? traceId, string? spanId = null)
    {
        UiThread.Check();
        if (traceId is null)
        {
            return;
        }
        if (traces.FirstOrDefault(item => item.TraceId == traceId) is { } trace)
        {
            Choose(trace, spanId);
        }
        else
        {
            wantedTrace = (traceId, spanId);
        }
        PageWanted?.Invoke(this, TelemetryPage.Traces);
    }

    /// <summary>A trace's (and span's) log on the logs page: at once when it is listed, else as the page streams it.</summary>
    public void SelectLogs(string traceId, string? spanId = null)
    {
        UiThread.Check();
        if (Correlated(traceId, spanId) is { } log)
        {
            StructuredLogs.SelectedItem = ItemOf(log);
        }
        else
        {
            wantedLogs = (traceId, spanId);
        }
        PageWanted?.Invoke(this, TelemetryPage.Logs);
    }

    private AspireStructuredLog? Correlated(string traceId, string? spanId) => logItems.Select(item => item.Data)
        .FirstOrDefault(item => item.TraceId == traceId && (spanId is null || item.SpanId == spanId));

    private void Choose(AspireTrace trace, string? spanId)
    {
        TraceList.SelectedItem = ItemOf(trace);
        if (spanId is not null && spanItems.FirstOrDefault(item => item.Key == spanId) is { } span)
        {
            SpanList.SelectedItem = span;
        }
    }

    private void ShowLog()
    {
        logDetails.Clear();
        logTrace.IsEnabled = false;
        if (SelectedLog is not { } log)
        {
            return;
        }
        logDetails.Append(JsonSerializer.Serialize(log.Data, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        AppendSource(logDetails, log.Data);
        AppendException(logDetails, log.Data);
        logTrace.IsEnabled = !string.IsNullOrEmpty(log.TraceId);
    }

    /// <summary>The chosen trace's spans: anew as it is chosen, the first span chosen; or <paramref name="again"/>, as a
    /// snapshot brings it more, the span chosen kept.</summary>
    private void ShowTrace(bool again = false)
    {
        if (!again)
        {
            spanDetails.Clear();
        }
        if (SelectedTrace is not { } trace)
        {
            spanItems.Clear();
            return;
        }
        List<SpanRow> rows = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        Dictionary<string, AspireSpan[]> children = trace.Spans.GroupBy(span => span.ParentSpanId ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        HashSet<string> ids = trace.Spans.Select(span => span.SpanId).ToHashSet(StringComparer.Ordinal);
        foreach (AspireSpan root in trace.Spans.Where(span => span.ParentSpanId is null || !ids.Contains(span.ParentSpanId)))
        {
            Walk(root);
        }
        foreach (AspireSpan orphan in trace.Spans)
        {
            Walk(orphan);
        }
        RunRows.Put(spanItems, Keyed(rows, row => row.Span.SpanId));
        if (!again || SpanList.SelectedItem is null)
        {
            SpanList.SelectedItem = spanItems.FirstOrDefault();
        }

        void Walk(AspireSpan root)
        {
            Stack<(AspireSpan Span, int Depth)> pending = new();
            pending.Push((root, 0));
            while (pending.TryPop(out (AspireSpan Span, int Depth) next))
            {
                if (!visited.Add(next.Span.SpanId))
                {
                    continue;
                }
                rows.Add(new(next.Span, next.Depth));
                if (children.TryGetValue(next.Span.SpanId, out AspireSpan[]? descendants))
                {
                    foreach (AspireSpan child in descendants.Reverse())
                    {
                        pending.Push((child, next.Depth + 1));
                    }
                }
            }
        }
    }

    private void ShowSpan()
    {
        spanDetails.Clear();
        spanLogs.IsEnabled = false;
        if (SelectedSpan is { } row)
        {
            spanDetails.Append(JsonSerializer.Serialize(row.Span.Data, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            AppendSource(spanDetails, row.Span.Data);
            AppendException(spanDetails, row.Span.Data);
            spanLogs.IsEnabled = true;
        }
    }

    private static void AppendException(OutputView output, JsonElement data)
    {
        if (AspireJson.String(data, "exception") is { Length: > 0 } exception)
        {
            output.Append($"\n{exception}\n", error: true);
        }
        foreach (JsonProperty attribute in AspireJson.Properties(data, "attributes"))
        {
            if (attribute.Name == "exception.stacktrace" && attribute.Value.ValueKind == JsonValueKind.String)
            {
                output.Append($"\n{attribute.Value.GetString()}\n", error: true);
            }
        }
    }

    private static void AppendSource(OutputView output, JsonElement data)
    {
        JsonProperty[] attributes = AspireJson.Properties(data, "attributes").ToArray();
        string? path = attributes.FirstOrDefault(item => item.Name is "code.file.path" or "code.filepath").Value.ValueKind == JsonValueKind.String
            ? attributes.First(item => item.Name is "code.file.path" or "code.filepath").Value.GetString() : null;
        JsonElement line = attributes.FirstOrDefault(item => item.Name is "code.line.number" or "code.lineno").Value;
        if (path is not null && int.TryParse(line.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : line.ToString(), out int number) && number > 0)
        {
            output.Append($"\nSource: at telemetry in {path}:line {number}\n");
        }
    }

    private static TextBlock Said() => new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };

    /// <summary>A page's toolbar button: the host's action of that id when it gives one (shown with its work, its tip naming
    /// the action's keys), else the view's own.</summary>
    private static Button PageTool(string icon, string tip, string? action, string name, Action own, Func<string, bool>? request)
    {
        if (action is null)
        {
            Button button = ToolButtons.Of(icon, tip, own);
            button.Name = name;
            return button;
        }
        return ToolButtons.Of(action, icon, tip, asked =>
        {
            if (request?.Invoke(asked) != true)
            {
                own();
            }
        });
    }

    /// <summary>A page's bar: what it says, its buttons at the right.</summary>
    private static DockPanel PageBar(TextBlock said, params Button[] buttons)
    {
        StackPanel right = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right };
        foreach (Button button in buttons)
        {
            right.Children.Add(button);
        }
        return new DockPanel { Height = 32, Margin = new Thickness(8, 0), Children = { right, said } };
    }

    private static ListBox List<T>(ObservableCollection<RunItem<T>> items, Func<T, string> text, string name)
        where T : class
    {
        ListBox list = new()
        {
            Classes = { "tree" },
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<RunItem<T>>((item, _) => item is null ? null : Row(item, text), supportsRecycling: false),
        };
        AutomationProperties.SetName(list, name);
        return list;
    }

    /// <summary>A record's row, its words shown anew in place as its item changes (a trace's late spans).</summary>
    private static Control Row<T>(RunItem<T> item, Func<T, string> text)
        where T : class
    {
        TextBlock words = new() { Text = text(item.Data), VerticalAlignment = VerticalAlignment.Center };
        void Changed(object? sender, EventArgs e) => words.Text = text(item.Data);
        words.AttachedToVisualTree += (_, _) =>
        {
            item.Changed -= Changed;
            item.Changed += Changed;
            words.Text = text(item.Data);
        };
        words.DetachedFromVisualTree += (_, _) => item.Changed -= Changed;
        return new ShrinkingRow { Height = ListRow.Height, Children = { words } };
    }

    /// <summary>Each record with its key, a key that recurs numbered after its first, as <see cref="RunRows.Put"/> takes them.</summary>
    private static IReadOnlyList<(string Key, T Data)> Keyed<T>(IEnumerable<T> records, Func<T, string> key)
    {
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        List<(string, T)> keyed = [];
        foreach (T record in records)
        {
            string named = key(record);
            int count = seen.GetValueOrDefault(named);
            seen[named] = count + 1;
            keyed.Add((count == 0 ? named : $"{named}#{count}", record));
        }
        return keyed;
    }

    private static DockPanel WithHeader(Control header, Control body)
    {
        DockPanel.SetDock(header, Dock.Top);
        return new DockPanel { Children = { header, body } };
    }

    private static Control Split(Control top, Control bottom)
    {
        Grid split = new() { RowDefinitions = new RowDefinitions("*,1,*") };
        split.Children.Add(new Border { Classes = { "framed" }, Child = top });
        split.Children.Add(new HairlineDivider(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 });
        bottom[Grid.RowProperty] = 2;
        split.Children.Add(bottom);
        return split;
    }

    public void Dispose()
    {
        UiThread.Check();
        disposed = true;
        Stop();
    }

    public sealed record SpanRow(AspireSpan Span, int Depth);
}
