using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Profiling;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Profiler;

namespace Passenger.App.Telemetry;

/// <summary>A running .NET program's System.Diagnostics.Metrics instruments charted over its run (<see cref="LiveMetrics"/>),
/// read only while its host shows it (<see cref="Show"/>): the meters .NET and ASP.NET Core ship, and each meter of the
/// program's own as it is published; or what another source feeds it (<see cref="ShowFed"/>, a Godot game's monitors). An
/// instrument is a row under its meter, its latest value beside a line of its last minutes, a histogram's median with its
/// 95th percentile and how many it measured a second; an interval's series of one instrument are one value, a counter's
/// rates and a gauge's values summed, a histogram's quantiles its highest series'. A program with none to read says why, and
/// one the debugger holds paused, which answers no session's start, is read once it runs (<see cref="Paused"/>).</summary>
public sealed class MetricsPane : DockPanel
{
    /// <summary>How many readings a line keeps: two minutes of the runtime's one a second.</summary>
    private const int Kept = 120;

    /// <summary>Where each shipped meter comes among the headings, the program's own after them.</summary>
    private static readonly Dictionary<string, int> ShippedOrder = LiveMetrics.Shipped.Select((meter, at) => (meter, at)).ToDictionary(m => m.meter, m => m.at, StringComparer.Ordinal);

    private readonly StackPanel rows = new() { Margin = new Thickness(8, 4, 8, 8) };
    private readonly TextBlock waiting = new() { Classes = { StyleClasses.EmptyText } };
    private readonly TextBlock status = new() { Classes = { StyleClasses.Muted }, Margin = new Thickness(8, 6, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, [DockProperty] = Dock.Top };
    private readonly Dictionary<(string Meter, string Instrument), Chart> charts = [];
    private readonly Dictionary<string, TextBlock> headings = new(StringComparer.Ordinal);
    private readonly HashSet<string> meters = new(StringComparer.Ordinal);
    private readonly Debounce widen;
    private LiveMetrics? live;
    private string? key;
    private string name = string.Empty;
    private string said = string.Empty;
    private int process;
    private ProfilePlace? place;

    /// <summary>Each follow begun and each stop counted, so what a follow let go of hears nothing more.</summary>
    private int follows;

    /// <summary>Whether the program's reading waits for the debugger to let it run (<see cref="DebuggerChanged"/>).</summary>
    private bool held;

    /// <summary>Whether the debugger holds a program paused, by its process (in its container, where it has one): its metrics
    /// are read once it runs.</summary>
    public Func<int, ProfilePlace?, bool>? Paused { get; set; }

    public MetricsPane()
    {
        // A meter the program publishes as it runs is read from a moment after, all that came meanwhile at once.
        widen = new Debounce(TimeSpan.FromSeconds(1), () =>
        {
            if (key is not null)
            {
                IdeErrors.Observe(FollowAsync(++follows, again: true));
            }
            return Task.CompletedTask;
        });
        Children.Add(status);
        Children.Add(new Panel { Children = { new ScrollViewer { Content = rows }, waiting } });
        Showing();
    }

    /// <summary>What the pane says of its program: in place of the charts while it has none, over them once it has.</summary>
    public string Said => said;

    /// <summary>The instruments charted, by name, with how many readings each line holds.</summary>
    public IReadOnlyDictionary<string, int> Charted => charts.ToDictionary(chart => chart.Key.Instrument, chart => chart.Value.Line.Count, StringComparer.Ordinal);

    /// <summary>The words shown beside an instrument's line, or null for one not charted.</summary>
    public string? ValueOf(string instrument) => charts.FirstOrDefault(chart => chart.Key.Instrument == instrument).Value?.Value.Text;

    /// <summary>Reads a program's metrics, or nothing (<paramref name="shown"/> null); without a program it says
    /// <paramref name="none"/>. One in a container (<paramref name="where"/>) is read through Passenger's tools there, the
    /// meters .NET ships alone. The same program again leaves its reading as it is.</summary>
    public void Show(string? shown, int? program, string? none = null, ProfilePlace? where = null)
    {
        UiThread.Check();
        if (shown is null)
        {
            if (key is not null)
            {
                Stop();
            }
            Say(none ?? string.Empty);
            return;
        }
        string next = $"{shown}\n{program}\n{where?.Where}";
        if (next == key)
        {
            return;
        }
        Stop();
        key = next;
        name = shown;
        if (program is not int id)
        {
            Say(none ?? $"{shown} runs no .NET program on this machine to read metrics from");
            return;
        }
        process = id;
        place = where;
        meters.UnionWith(LiveMetrics.Shipped);
        if (Paused?.Invoke(id, where) == true)
        {
            held = true;
            Say(Refusals.MetricsWhilePaused(shown));
            return;
        }
        Say($"Waiting for {shown}'s first metrics…");
        IdeErrors.Observe(FollowAsync(++follows, again: false));
    }

    /// <summary>The debugger's sessions changed: a program it held paused is read now it runs, and one it pauses is said to be
    /// paused over its charts until it runs again.</summary>
    public void DebuggerChanged()
    {
        UiThread.Check();
        if (key is null || fed || process == 0)
        {
            return;
        }
        bool paused = Paused?.Invoke(process, place) == true;
        if (paused)
        {
            Say(Refusals.MetricsWhilePaused(name));
        }
        else if (held)
        {
            held = false;
            Say($"Waiting for {name}'s first metrics…");
            IdeErrors.Observe(FollowAsync(++follows, again: false));
        }
        else if (said == Refusals.MetricsWhilePaused(name))
        {
            Say(charts.Count == 0 ? $"Waiting for {name}'s first metrics…" : string.Empty);
        }
    }

    /// <summary>Charts what a source of its own feeds it (<see cref="Feed"/>: a Godot game's monitors), or nothing
    /// (<paramref name="shown"/> null), saying <paramref name="waiting"/> until the first readings come. The same source again
    /// leaves its charts as they are.</summary>
    public void ShowFed(string? shown, string waiting)
    {
        UiThread.Check();
        if (shown is null)
        {
            if (key is not null)
            {
                Stop();
            }
            return;
        }
        string next = $"{shown}\nfed";
        if (next == key)
        {
            return;
        }
        Stop();
        key = next;
        name = shown;
        fed = true;
        Say(waiting);
    }

    /// <summary>Readings a source of its own gave (<see cref="ShowFed"/>), each instrument's added to its line.</summary>
    public void Feed(IReadOnlyList<MetricReading> readings)
    {
        UiThread.Check();
        if (fed)
        {
            Take(readings);
        }
    }

    /// <summary>Whether its readings are fed it (<see cref="ShowFed"/>) rather than read from a program's runtime.</summary>
    private bool fed;

    /// <summary>The source whose readings it is fed, while it is.</summary>
    public string? Feeding => fed ? name : null;

    /// <summary>Begins reading, in place of the session before (<paramref name="again"/>, with the meters published since).
    /// A start that failed is said at once: asked again, saying so, only while the runtime has yet to open its port (in a
    /// container, a few times, its tools saying no more); left to <see cref="DebuggerChanged"/> once the debugger pauses the
    /// program; else said and left. Let go of (<see cref="Stop"/>, a follow after it), it asks nothing more and says nothing.</summary>
    private async Task FollowAsync(int follow, bool again)
    {
        using IDisposable working = BackgroundWork.Scope();
        using CancellationTokenSource asked = new();
        CancellationTokenSource? dropped = asking;
        asking = asked;
        dropped?.Cancel();
        try
        {
            if (again && live is { } before)
            {
                live = null;
                await before.DisposeAsync();
            }
            DateTime first = DateTime.UtcNow;
            bool waited = false;
            for (int attempt = 1; follow == follows; attempt++)
            {
                try
                {
                    LiveMetrics started = place is { } there ? await LiveMetrics.AttachAsync(process, there, Heard(follow), asked.Token)
                        : await LiveMetrics.AttachAsync(process, [.. meters], Heard(follow), _ =>
                        {
                            if (follow == follows)
                            {
                                waited = true;
                                Say(ProgramStarts.Waiting(name, "reading its metrics"));
                            }
                        }, asked.Token);
                    if (follow != follows)
                    {
                        await started.DisposeAsync();
                        return;
                    }
                    live = started;
                    if (waited && charts.Count == 0)
                    {
                        Say($"Waiting for {name}'s first metrics…");
                    }
                    IdeErrors.Observe(EndedAsync(started, follow));
                    return;
                }
                catch (ProfilingException error)
                {
                    if (follow != follows)
                    {
                        return;
                    }
                    if (Paused?.Invoke(process, place) == true)
                    {
                        held = true;
                        Say(Refusals.MetricsWhilePaused(name));
                        return;
                    }
                    bool askAgain = place is null ? SessionStarts.AskAgain(error, DateTime.UtcNow - first, HeldStart.PortLimit) : attempt < ContainerAttempts;
                    if (!askAgain)
                    {
                        Say($"{name}: {error.Message}");
                        return;
                    }
                    Say(place is null ? $"Waiting for {name}'s runtime to open its diagnostic port…" : $"{name}: {error.Message}; asking again…");
                    await Task.Delay(TimeSpan.FromMilliseconds(200), asked.Token);
                }
            }
        }
        catch (OperationCanceledException) when (asked.IsCancellationRequested)
        {
            // Let go of as it asked: the pane follows another program, or none.
        }
        finally
        {
            if (asking == asked)
            {
                asking = null;
            }
        }
    }

    /// <summary>What the follow under way asks with, canceled as the pane lets go of it.</summary>
    private CancellationTokenSource? asking;

    /// <summary>How many times a program in a container is asked for its metrics, whose tools say only that they failed.</summary>
    private const int ContainerAttempts = 15;

    /// <summary>What a session hears, taken on the window's thread while it is still the pane's.</summary>
    private MetricsHeard Heard(int follow) => new(
        readings => UiThread.Post(() =>
        {
            if (follow == follows)
            {
                Take(readings);
            }
        }),
        meter => UiThread.Post(() =>
        {
            if (follow == follows && place is null && meters.Add(meter))
            {
                widen.Restart();
            }
        }),
        why => UiThread.Post(() =>
        {
            if (follow == follows)
            {
                Say($"{name}: {why}");
            }
        }));

    private async Task EndedAsync(LiveMetrics session, int follow)
    {
        await session.Ended;
        UiThread.Post(() =>
        {
            if (follow == follows && live == session)
            {
                live = null;
                Say(charts.Count == 0 ? $"{name} ended before its first metrics" : $"{name} ended: its metrics stop at its end");
            }
        });
    }

    /// <summary>Stops reading, the charts let go.</summary>
    public void Stop()
    {
        UiThread.Check();
        follows++;
        CancellationTokenSource? dropped = asking;
        asking = null;
        dropped?.Cancel();
        widen.Stop();
        if (live is { } session)
        {
            live = null;
            IdeErrors.Observe(session.DisposeAsync().AsTask());
        }
        key = null;
        fed = false;
        held = false;
        process = 0;
        said = string.Empty;
        meters.Clear();
        charts.Clear();
        headings.Clear();
        rows.Children.Clear();
        Showing();
    }

    private void Say(string words)
    {
        said = words;
        Showing();
    }

    /// <summary>The words in the charts' place while there are none, else over them.</summary>
    private void Showing()
    {
        bool none = charts.Count == 0;
        waiting.Text = said;
        waiting.IsVisible = none;
        status.Text = said;
        status.IsVisible = !none && said.Length > 0;
    }

    /// <summary>An interval's readings, one value an instrument added to its line; the rows laid out again only as one comes.</summary>
    /// <summary>An interval's readings as one value an instrument: a counter's rates and a gauge's values summed across its
    /// series, a histogram's highest series' quantiles with how many all measured; a histogram that measured nothing has
    /// none (<see cref="MetricValue.Idle"/>). The Stats tab's lines and an agent's telemetry read the same.</summary>
    public static IReadOnlyList<MetricValue> Combined(IReadOnlyList<MetricReading> readings) =>
    [
        .. readings.GroupBy(reading => (reading.Meter, reading.Instrument)).Select(instrument =>
        {
            MetricReading one = instrument.First();
            MetricReading[] measured = [.. instrument.Where(reading => reading.Count is not 0)];
            double? counted = instrument.Any(reading => reading.Count is not null) ? instrument.Sum(reading => reading.Count ?? 0) : null;
            if (measured.Length == 0)
            {
                return new MetricValue(one.Meter, one.Instrument, one.Unit, one.Kind, 0, null, counted, Idle: true);
            }
            double value = one.Kind == MetricKind.Histogram ? measured.Max(reading => reading.Value) : measured.Sum(reading => reading.Value);
            double? upper = one.Kind == MetricKind.Histogram ? measured.Max(reading => reading.Upper ?? reading.Value) : null;
            return new MetricValue(one.Meter, one.Instrument, one.Unit, one.Kind, value, upper, counted, Idle: false);
        }),
    ];

    private void Take(IReadOnlyList<MetricReading> readings)
    {
        bool came = false;
        bool first = charts.Count == 0;
        foreach (MetricValue instrument in Combined(readings))
        {
            (string, string) named = (instrument.Meter, instrument.Instrument);
            if (instrument.Idle)
            {
                // A histogram that measured nothing this second has no median to draw: its line waits for the next.
                if (charts.TryGetValue(named, out Chart? idle))
                {
                    idle.Value.Text = "none this second";
                }
                continue;
            }
            if (!charts.TryGetValue(named, out Chart? chart))
            {
                charts[named] = chart = new Chart(instrument.Meter, instrument.Instrument, instrument.Unit, instrument.Kind);
                came = true;
            }
            if (instrument.Upper is double high)
            {
                chart.Line.Add(instrument.Value, high);
            }
            else
            {
                chart.Line.Add(instrument.Value);
            }
            string words = instrument.Shown;
            if (chart.Value.Text != words)
            {
                chart.Value.Text = words;
            }
        }
        if (came)
        {
            Lay();
        }
        if (first && charts.Count > 0)
        {
            Say(fed ? $"{name}: sent every second"
                : $"{name}, process {process.ToString(CultureInfo.InvariantCulture)}{(place is null ? string.Empty : " in its container")}: read every second");
        }
    }

    /// <summary>Each meter's heading over its instruments: the shipped meters in their order, then the program's own by name.</summary>
    private void Lay()
    {
        rows.Children.Clear();
        foreach (IGrouping<string, KeyValuePair<(string Meter, string Instrument), Chart>> meter in charts
            .OrderBy(chart => ShippedOrder.GetValueOrDefault(chart.Key.Meter, ShippedOrder.Count))
            .ThenBy(chart => chart.Key.Meter, StringComparer.Ordinal).ThenBy(chart => chart.Key.Instrument, StringComparer.Ordinal)
            .GroupBy(chart => chart.Key.Meter))
        {
            if (!headings.TryGetValue(meter.Key, out TextBlock? heading))
            {
                headings[meter.Key] = heading = new TextBlock { Text = meter.Key, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
            }
            rows.Children.Add(heading);
            foreach (KeyValuePair<(string Meter, string Instrument), Chart> chart in meter)
            {
                rows.Children.Add(chart.Value.Row);
            }
        }
    }

    /// <summary>An instrument's value as a person reads it: a rate a second, a histogram's count a second, median and 95th percentile.</summary>
    internal static string Shown(MetricKind kind, string unit, double value, double? upper, double? counted = null) => kind switch
    {
        MetricKind.Histogram => (counted is double count ? $"{Number(count)}/s, " : string.Empty) + $"median {Amount(value, unit)}"
            + (upper is double high ? $", 95% under {Amount(high, unit)}" : string.Empty),
        MetricKind.Rate => $"{Amount(value, unit)}/s",
        _ => Amount(value, unit),
    };

    /// <summary>A value in its unit's words: seconds under one as milliseconds, bytes as their size, a counted thing's
    /// ({request}) as its number alone.</summary>
    private static string Amount(double value, string unit) => unit switch
    {
        "s" when Math.Abs(value) < 1 => $"{Number(value * 1000)} ms",
        "By" => Sizes.Said((long)Math.Round(value)),
        "" or ['{', .., '}'] => Number(value),
        _ => $"{Number(value)} {unit}",
    };

    private static string Number(double value) => value.ToString(Math.Abs(value) >= 100 ? "N0" : "0.##", CultureInfo.InvariantCulture);

    /// <summary>An instrument's row: its name and latest value, then its line, a histogram's 95th percentile beside its median.</summary>
    private sealed class Chart
    {
        public Chart(string meter, string instrument, string unit, MetricKind kind)
        {
            MetricReading first = new(meter, instrument, unit, kind, string.Empty, 0, null);
            Line = first.Kind == MetricKind.Histogram
                ? new Sparkline(Kept, fromZero: true, fromRight: false, TokenKeys.Link, TokenKeys.Graph_2) { Least = 0 }
                : new Sparkline(Kept, fromZero: true, fromRight: false, TokenKeys.Link) { Least = 0 };
            TextBlock title = new() { Text = first.Instrument, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(title, first.Unit.Length > 0 ? $"{first.Meter}: {first.Instrument} ({first.Unit})" : $"{first.Meter}: {first.Instrument}");
            Value = MutedText.Of(string.Empty);
            Value.FontSize = 11;
            StackPanel words = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Children = { title, Value } };
            Border frame = new() { Classes = { StyleClasses.Framed }, Child = Line, [Grid.ColumnProperty] = 1 };
            // The words a third of a narrow pane's width (a tool window's), at most 280 of a wide one's, the line the rest.
            Row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = 280 }, new ColumnDefinition(2, GridUnitType.Star) },
                Height = 44,
                Margin = new Thickness(0, 2),
                Children = { words, frame },
            };
        }

        public Sparkline Line { get; }
        public TextBlock Value { get; }
        public Grid Row { get; }
    }
}

/// <summary>An instrument's value over an interval, its series taken as one (<see cref="MetricsPane.Combined"/>); a histogram
/// that measured nothing is <see cref="Idle"/>, with no value.</summary>
public sealed record MetricValue(string Meter, string Instrument, string Unit, MetricKind Kind, double Value, double? Upper, double? Counted, bool Idle)
{
    /// <summary>Its value as a person reads it, as the Stats tab says it.</summary>
    public string Shown => Idle ? "none this second" : MetricsPane.Shown(Kind, Unit, Value, Upper, Counted);
}
