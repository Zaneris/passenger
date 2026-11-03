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
/// program's own as it is published. An instrument is a row under its meter, its latest value beside a line of its last
/// minutes, a histogram's median with its 95th percentile and how many it measured a second; an interval's series of one
/// instrument are one value, a counter's rates and a gauge's values summed, a histogram's quantiles its highest series'. A
/// program with none to read says why.</summary>
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

    /// <summary>Each follow begun and each stop counted, so what a follow let go of hears nothing more.</summary>
    private int follows;

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
    /// <paramref name="none"/>. The same program again leaves its reading as it is.</summary>
    public void Show(string? shown, int? program, string? none = null)
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
        string next = $"{shown}\n{program}";
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
        meters.UnionWith(LiveMetrics.Shipped);
        Say($"Waiting for {shown}'s first metrics…");
        IdeErrors.Observe(FollowAsync(++follows, again: false));
    }

    /// <summary>Begins reading, in place of the session before (<paramref name="again"/>, with the meters published since),
    /// asked again for a few seconds while the runtime has yet to open its port.</summary>
    private async Task FollowAsync(int follow, bool again)
    {
        using IDisposable held = BackgroundWork.Scope();
        if (again && live is { } before)
        {
            live = null;
            await before.DisposeAsync();
        }
        string? failed = null;
        for (int attempt = 0; attempt < 15 && follow == follows; attempt++)
        {
            try
            {
                LiveMetrics started = await LiveMetrics.AttachAsync(process, [.. meters], Heard(follow));
                if (follow != follows)
                {
                    await started.DisposeAsync();
                    return;
                }
                live = started;
                IdeErrors.Observe(EndedAsync(started, follow));
                return;
            }
            catch (ProfilingException error)
            {
                failed = error.Message;
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }
        if (follow == follows && failed is not null)
        {
            Say($"{name}: {failed}");
        }
    }

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
            if (follow == follows && meters.Add(meter))
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
        widen.Stop();
        if (live is { } session)
        {
            live = null;
            IdeErrors.Observe(session.DisposeAsync().AsTask());
        }
        key = null;
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
    private void Take(IReadOnlyList<MetricReading> readings)
    {
        bool came = false;
        bool first = charts.Count == 0;
        foreach (IGrouping<(string Meter, string Instrument), MetricReading> instrument in readings.GroupBy(reading => (reading.Meter, reading.Instrument)))
        {
            MetricReading one = instrument.First();
            MetricReading[] measured = [.. instrument.Where(reading => reading.Count is not 0)];
            double? counted = instrument.Any(reading => reading.Count is not null) ? instrument.Sum(reading => reading.Count ?? 0) : null;
            if (measured.Length == 0)
            {
                // A histogram that measured nothing this second has no median to draw: its line waits for the next.
                if (charts.TryGetValue(instrument.Key, out Chart? idle))
                {
                    idle.Value.Text = "none this second";
                }
                continue;
            }
            double value = one.Kind == MetricKind.Histogram ? measured.Max(reading => reading.Value) : measured.Sum(reading => reading.Value);
            double? upper = one.Kind == MetricKind.Histogram ? measured.Max(reading => reading.Upper ?? reading.Value) : null;
            if (!charts.TryGetValue(instrument.Key, out Chart? chart))
            {
                charts[instrument.Key] = chart = new Chart(one);
                came = true;
            }
            if (upper is double high)
            {
                chart.Line.Add(value, high);
            }
            else
            {
                chart.Line.Add(value);
            }
            string words = Shown(one.Kind, one.Unit, value, upper, counted);
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
            Say($"{name}, process {process.ToString(CultureInfo.InvariantCulture)}: read every second");
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
        "By" => MemoryView.Size((long)Math.Round(value)),
        "" or ['{', .., '}'] => Number(value),
        _ => $"{Number(value)} {unit}",
    };

    private static string Number(double value) => value.ToString(Math.Abs(value) >= 100 ? "N0" : "0.##", CultureInfo.InvariantCulture);

    /// <summary>An instrument's row: its name and latest value, then its line, a histogram's 95th percentile beside its median.</summary>
    private sealed class Chart
    {
        public Chart(MetricReading first)
        {
            Line = first.Kind == MetricKind.Histogram
                ? new Sparkline(Kept, fromZero: true, fromRight: false, TokenKeys.Link, TokenKeys.Graph_2) { Least = 0 }
                : new Sparkline(Kept, fromZero: true, fromRight: false, TokenKeys.Link) { Least = 0 };
            TextBlock title = new() { Text = first.Instrument, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(title, first.Unit.Length > 0 ? $"{first.Meter}: {first.Instrument} ({first.Unit})" : $"{first.Meter}: {first.Instrument}");
            Value = MutedText.Of(string.Empty);
            Value.FontSize = 11;
            StackPanel words = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Children = { title, Value } };
            Border frame = new() { Classes = { StyleClasses.Framed }, Child = Line, [Grid.ColumnProperty] = 1 };
            Row = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), Height = 44, Margin = new Thickness(0, 2), Children = { words, frame } };
        }

        public Sparkline Line { get; }
        public TextBlock Value { get; }
        public Grid Row { get; }
    }
}
