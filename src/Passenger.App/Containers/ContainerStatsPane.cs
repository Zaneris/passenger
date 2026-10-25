using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>One reading of <c>docker stats</c> for a container: CPU and memory as percentages, and memory, network and disk
/// in the engine's own words.</summary>
public sealed record ContainerStatsSample(string Name, double Cpu, double MemoryPercent, string Memory, string Network, string Disk, string Processes)
{
    /// <param name="names">The containers' names by the ids they were asked by (Docker's Container is the id asked).</param>
    internal static ContainerStatsSample? Read(JsonElement row, IReadOnlyDictionary<string, string>? names = null)
    {
        string? name = ContainerEngine.Text(row, "Container") is { } asked && names?.GetValueOrDefault(asked) is { } known ? known : ContainerEngine.Text(row, "Name");
        if (name is null)
        {
            return null;
        }
        return new ContainerStatsSample(name, Percent(ContainerEngine.Text(row, "CPUPerc")), Percent(ContainerEngine.Text(row, "MemPerc")),
            ContainerEngine.Text(row, "MemUsage") ?? string.Empty, ContainerEngine.Text(row, "NetIO") ?? string.Empty,
            ContainerEngine.Text(row, "BlockIO") ?? string.Empty, ContainerEngine.Text(row, "PIDs") ?? string.Empty);
    }

    private static double Percent(string? said) =>
        double.TryParse(said?.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
}

/// <summary>A container's or a project's CPU, memory, network and disk over time, from <c>docker stats --format json</c>,
/// streamed only while the tab is shown: a row a container with its latest numbers, the project's sum first, and the CPU
/// and memory of the last minutes drawn under them. A stream that ends while shown says why in their place, from what the
/// CLI said on its standard error, which is read as it comes so a CLI that says much never stalls the numbers.</summary>
public sealed class ContainerStatsPane : DockPanel
{
    /// <summary>A reading of Podman's own stats as one line of Docker's JSON.</summary>
    private const string PodmanLine =
        """{"Name":"{{.Name}}","CPUPerc":"{{.CPUPerc}}","MemPerc":"{{.MemPerc}}","MemUsage":"{{.MemUsage}}","NetIO":"{{.NetIO}}","BlockIO":"{{.BlockIO}}","PIDs":"{{.PIDS}}"}""";

    /// <summary>How many readings the lines keep: about two minutes of the engine's one a second or so.</summary>
    private const int Kept = 120;

    private readonly Func<ContainerEngine> engine;
    private readonly StackPanel table = new() { Margin = new Thickness(8, 6) };
    private readonly Sparkline cpu = new(TokenKeys.Link);
    private readonly Sparkline memory = new(TokenKeys.Test_Passed);
    private readonly TextBlock waiting = new() { Classes = { "emptyText" }, Text = "Waiting for the engine's first reading…" };
    private readonly Dictionary<string, ContainerStatsSample> latest = new(StringComparer.Ordinal);
    private Process? streaming;

    /// <summary>Each stream asked for and each stop counted, so a start that lands after either is let go; and whether the
    /// last asked for is still starting.</summary>
    private int streams;
    private bool forking;
    private readonly HashSet<string> sinceLast = new(StringComparer.Ordinal);
    private string? key;

    /// <summary>Each container's line of the table by its name, and the sum's: kept from one reading to the next, which
    /// changes their words alone (docker stats writes a line a container a second).</summary>
    private readonly Dictionary<string, (Grid Line, TextBlock[] Cells)> lines = new(StringComparer.Ordinal);
    private (Grid Line, TextBlock[] Cells)? sum;

    public ContainerStatsPane(Func<ContainerEngine> engine)
    {
        this.engine = engine;
        Grid charts = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), Height = 72, Margin = new Thickness(8, 0, 8, 8), [DockProperty] = Dock.Bottom };
        charts.Children.Add(Chart("CPU %", cpu, 0));
        charts.Children.Add(Chart("Memory %", memory, 1));
        Children.Add(charts);
        Children.Add(new Panel { Children = { new ScrollViewer { Content = table }, waiting } });
    }

    /// <summary>The latest reading of each container shown.</summary>
    public IReadOnlyList<ContainerStatsSample> Latest => [.. latest.Values];

    /// <summary>What the pane says in place of the numbers (waiting for the first reading, why the stream ended), or null
    /// while it shows them.</summary>
    public string? Said => waiting.IsVisible ? waiting.Text : null;

    /// <summary>How many readings the lines hold.</summary>
    public int Readings => cpu.Count;

    /// <summary>Streams a row's containers' stats, or nothing; the same row again leaves its stream as it is.</summary>
    public void Show(ContainerRow? row)
    {
        UiThread.Check();
        string[] running = row is null ? [] : [.. row.Containers.Where(c => c.IsRunning).Select(c => c.Id)];
        string? next = running.Length == 0 ? null : string.Join(',', running);
        if (next == key && (next is null || forking || streaming is { HasExited: false }))
        {
            return;
        }
        Stop();
        key = next;
        waiting.Text = row is null ? string.Empty : running.Length == 0 ? $"{row.Name} is not running" : "Waiting for the engine's first reading…";
        waiting.IsVisible = true;
        if (next is null)
        {
            return;
        }
        // Each reading named by the container it was asked of: Docker's CLI on Podman's socket cuts a name's first letter.
        Dictionary<string, string> names = row!.Containers.Where(c => c.IsRunning).ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        ProcessStartInfo start;
        try
        {
            // Podman's own stats writes its JSON over several lines, under its own names: a line of Docker's names asked of it.
            string format = engine().Known is { PodmanCli: true } ? PodmanLine : "json";
            start = engine().Command(["stats", "--format", format, .. running]);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
        }
        catch (FileNotFoundException failed)
        {
            waiting.Text = failed.Message;
            return;
        }
        IdeErrors.Observe(StreamAsync(start, names, ++streams));
    }

    /// <summary>Starts a stream, its fork off the window's thread (#441); one let go of as it started (another row chosen,
    /// the tab left) is ended as it comes.</summary>
    private async Task StreamAsync(ProcessStartInfo start, Dictionary<string, string> names, int stream)
    {
        Process process;
        forking = true;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception failed) when (failed is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            if (stream == streams)
            {
                forking = false;
                waiting.Text = failed.Message;
            }
            return;
        }
        if (stream != streams)
        {
            ChildProcesses.Kill(process);
            process.Dispose();
            return;
        }
        forking = false;
        streaming = process;
        // Not background work: it streams for as long as the tab is shown, which no screenshot or settling test waits for.
        IdeErrors.Observe(Task.Run(async () =>
        {
            try
            {
                Task<string?> errors = LastSaidAsync(process.StandardError);
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    ContainerStatsSample[] read = [.. ContainerEngine.JsonLines(line).Select(r => ContainerStatsSample.Read(r, names)).OfType<ContainerStatsSample>()];
                    if (read.Length > 0)
                    {
                        UiThread.Post(() => Take(process, read));
                    }
                }
                await process.WaitForExitAsync();
                // What it said last, unless something it left holds its errors open.
                string? said = await Task.WhenAny(errors, Task.Delay(TimeSpan.FromSeconds(2))) == errors ? await errors : null;
                string why = said is null ? $"docker stats ended with exit code {process.ExitCode}" : $"docker stats ended: {ContainerSecrets.InSaid(said)}";
                UiThread.Post(() => Ended(process, why));
            }
            catch (Exception ended) when (ended is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped under its read.
            }
        }));
    }

    /// <summary>A stream's last line that says something, read to its end.</summary>
    private static async Task<string?> LastSaidAsync(StreamReader reader)
    {
        string? last = null;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Trim() is { Length: > 0 } said)
                {
                    last = said;
                }
            }
        }
        catch (Exception ended) when (ended is IOException or ObjectDisposedException)
        {
            // Stopped under its read.
        }
        return last;
    }

    /// <summary>The stream ended while still the pane's: its numbers, which no longer change, give way to why.</summary>
    private void Ended(Process from, string why)
    {
        if (streaming != from)
        {
            return;
        }
        latest.Clear();
        sinceLast.Clear();
        ClearTable();
        waiting.Text = why;
        waiting.IsVisible = true;
    }

    private void ClearTable()
    {
        table.Children.Clear();
        lines.Clear();
        sum = null;
    }

    /// <summary>Stops the stream, the readings let go.</summary>
    public void Stop()
    {
        streams++;
        forking = false;
        if (streaming is { } process)
        {
            streaming = null;
            ChildProcesses.Kill(process);
            process.Dispose();
        }
        key = null;
        latest.Clear();
        sinceLast.Clear();
        cpu.Clear();
        memory.Clear();
        ClearTable();
    }

    private void Take(Process from, ContainerStatsSample[] read)
    {
        if (streaming != from)
        {
            return;
        }
        bool came = false;
        foreach (ContainerStatsSample sample in read)
        {
            // A name read again begins the next reading; the lines take the sum of the one before.
            if (!sinceLast.Add(sample.Name))
            {
                cpu.Add(latest.Values.Sum(s => s.Cpu));
                memory.Add(latest.Values.Sum(s => s.MemoryPercent));
                sinceLast.Clear();
                sinceLast.Add(sample.Name);
            }
            came |= !latest.ContainsKey(sample.Name);
            latest[sample.Name] = sample;
        }
        waiting.IsVisible = false;
        // The table is laid out again only as a container comes; a reading changes its line's words and the sum's.
        if (table.Children.Count == 0 || came)
        {
            Lay();
        }
        if (sum is { } all)
        {
            Say(all.Cells, "All", Percent(latest.Values.Sum(s => s.Cpu)), Percent(latest.Values.Sum(s => s.MemoryPercent)), string.Empty, string.Empty,
                latest.Values.Sum(s => int.TryParse(s.Processes, CultureInfo.InvariantCulture, out int n) ? n : 0).ToString(CultureInfo.InvariantCulture));
        }
        foreach (ContainerStatsSample sample in read)
        {
            Say(lines[sample.Name].Cells, sample.Name, Percent(sample.Cpu), $"{sample.Memory} ({Percent(sample.MemoryPercent)})", sample.Network, sample.Disk, sample.Processes);
        }
    }

    /// <summary>The table's heading, the sum's line when there are several containers, then each container's in name order,
    /// a line kept for each container it had.</summary>
    private void Lay()
    {
        table.Children.Clear();
        (Grid heading, TextBlock[] titles) = Line(heading: true);
        Say(titles, "Container", "CPU", "Memory", "Network I/O", "Disk I/O", "PIDs");
        table.Children.Add(heading);
        sum = latest.Count > 1 ? sum ?? Line(heading: true) : null;
        if (sum is { } all)
        {
            table.Children.Add(all.Line);
        }
        foreach (string name in latest.Keys.Order(StringComparer.Ordinal))
        {
            if (!lines.TryGetValue(name, out (Grid Line, TextBlock[] Cells) line))
            {
                lines[name] = line = Line(heading: false);
            }
            table.Children.Add(line.Line);
        }
    }

    private static string Percent(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + "%";

    /// <summary>A line's words, each cell changed only where its words did.</summary>
    private static void Say(TextBlock[] cells, params string[] words)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].Text != words[i])
            {
                cells[i].Text = words[i];
            }
        }
    }

    private static (Grid Line, TextBlock[] Cells) Line(bool heading)
    {
        Grid line = new() { ColumnDefinitions = new ColumnDefinitions("2*,*,2*,2*,2*,*"), Height = ListRow.Height };
        TextBlock[] cells = new TextBlock[6];
        for (int i = 0; i < cells.Length; i++)
        {
            TextBlock cell = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = i, Margin = new Thickness(0, 0, 8, 0) };
            if (heading)
            {
                cell.FontWeight = FontWeight.SemiBold;
            }
            cells[i] = cell;
            line.Children.Add(cell);
        }
        return (line, cells);
    }

    private static Control Chart(string title, Sparkline line, int column)
    {
        TextBlock name = new() { Text = title, FontSize = 11, [DockProperty] = Dock.Top };
        name.Classes.Add("muted");
        Border frame = new() { Classes = { "framed" }, Child = line };
        return new DockPanel { [Grid.ColumnProperty] = column, Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 0 ? 4 : 0, 0), Children = { name, frame } };
    }

    /// <summary>A line of the last readings, scaled to the highest of them (at least 1), in a token's colour.</summary>
    private sealed class Sparkline(string brush) : Control
    {
        private readonly List<double> values = [];

        public int Count => values.Count;

        public void Add(double value)
        {
            values.Add(value);
            if (values.Count > Kept)
            {
                values.RemoveAt(0);
            }
            InvalidateVisual();
        }

        public void Clear()
        {
            values.Clear();
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (values.Count < 2 || !this.TryFindResource(brush, ActualThemeVariant, out object? found) || found is not IBrush colour)
            {
                return;
            }
            double top = Math.Max(1, values.Max());
            Rect area = new Rect(Bounds.Size).Deflate(4);
            StreamGeometry path = new();
            using (StreamGeometryContext draw = path.Open())
            {
                for (int i = 0; i < values.Count; i++)
                {
                    Point at = new(area.Left + (area.Width * i / (Kept - 1)), area.Bottom - (area.Height * values[i] / top));
                    if (i == 0)
                    {
                        draw.BeginFigure(at, isFilled: false);
                    }
                    else
                    {
                        draw.LineTo(at);
                    }
                }
                draw.EndFigure(isClosed: false);
            }
            context.DrawGeometry(null, new Pen(colour, 1.5), path);
        }
    }
}
