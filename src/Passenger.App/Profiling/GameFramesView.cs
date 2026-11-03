using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>A row of a Godot game's frames table: a kind of work (GDScript, the C# the engine called, scripts' calls into
/// Godot's API, a server, the renderer), or one function or stage of it once opened, with its own time, its time with what it
/// called, its calls and its time in Godot's API (milliseconds; null where it has none), and the script function or C#
/// method it opens.</summary>
public sealed class GameRow(string name, string? place, double? own, double? all, int? calls, double? engine, IReadOnlyList<GameRow> children, int depth = 0)
    : Observable, ITreeRow
{
    private bool isExpanded;

    public string Name { get; } = name;

    /// <summary>Where it is ("wander.gd:10"), or what more it says ("GPU 1.2 ms").</summary>
    public string? Place { get; } = place;

    public double? Own { get; } = own;

    public double? All { get; } = all;

    public int? Calls { get; } = calls;

    public double? Engine { get; } = engine;

    public IReadOnlyList<GameRow> Children { get; } = children;

    public int Depth { get; } = depth;

    public ScriptFunction? Function { get; init; }

    public HotSpot? Method { get; init; }

    public bool Expands => Children.Count > 0;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }
}

/// <summary>The profiler window's Frames page for a Godot game: its frames as Godot's own profiler measured them, as bars
/// (<see cref="FrameChart"/>: physics, GDScript, the C# the engine called as sampled in the frame, and the rest Godot's own),
/// what they or the frame chosen cost, and a table of where their time went in both languages: GDScript's functions, the C#
/// methods the engine called sampled in the same frames, scripts' calls into Godot's API, the servers' functions and, where
/// the game draws, the renderer's stages. The two languages are joined by frame, as the page says: Godot measures GDScript
/// by function and frame, never by caller, and the samples see GDScript only as Godot's own code.</summary>
public sealed class GameFramesView : DockPanel
{
    private const double NumberWidth = 92;

    /// <summary>What the page says of how the languages meet, under the summary.</summary>
    public const string Joining = "Godot measures GDScript by function and frame, not by caller, and the C# samples see GDScript only as Godot's own code, "
        + "so the two are joined by frame: a C# method a GDScript function calls counts in that function's time in all, and a GDScript function C# calls "
        + "in the time of C#'s call into Godot (GodotObject.Call, say).";

    private readonly FrameChart chart;
    private readonly TextBlock summary;
    private readonly TreeRows<GameRow> rows = [];
    private readonly ListBox table;
    private readonly TextBlock placeholder;
    private readonly DockPanel shown;
    private IReadOnlyList<GameFrame> frames = [];
    private GameProfile? profile;
    private Func<IReadOnlyList<GameFrame>, GameJoin>? join;
    private GameJoin? joined;
    private double origin;

    public GameFramesView()
    {
        chart = new FrameChart();
        chart.Chosen += (_, _) => Fill();
        ScrollViewer strip = new()
        {
            Content = chart,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        StackPanel legend = new() { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 2, 12, 0) };
        foreach ((string label, string brush) in new[] { ("Physics", TokenKeys.Graph_1), ("GDScript", TokenKeys.Graph_0), ("C# (sampled)", TokenKeys.Graph_2), ("Godot", TokenKeys.TextMuted) })
        {
            Border swatch = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Opacity = brush == TokenKeys.TextMuted ? 0.35 : 1 };
            swatch.Bind(Border.BackgroundProperty, swatch.GetResourceObservable(brush));
            legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { swatch, Muted(label) } });
        }
        summary = Muted(string.Empty);
        summary.Margin = new Thickness(12, 6, 12, 2);
        summary.TextWrapping = TextWrapping.Wrap;
        TextBlock joining = Muted(Joining);
        joining.Margin = new Thickness(12, 0, 12, 6);
        joining.TextWrapping = TextWrapping.Wrap;
        frameLink = new Button { Classes = { StyleClasses.Link }, Margin = new Thickness(12, 0, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(frameLink, "The call tree of the frame alone, both languages together, its costliest function chosen");
        frameLink.Click += (_, _) =>
        {
            if (Opened is { } frame)
            {
                FrameOpened?.Invoke(this, frame);
            }
        };
        Border band = new()
        {
            [DockProperty] = Dock.Top,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Children = { legend, strip, summary, frameLink, joining } },
            Padding = new Thickness(0, 4, 0, 0),
        };
        band.Bind(Border.BorderBrushProperty, band.GetResourceObservable(TokenKeys.Divider));

        ColumnTitles titles = ColumnTitles.OfNumbers(("Function", 0), ("Own time", NumberWidth), ("Time in all", NumberWidth), ("Calls", NumberWidth),
            ("In Godot's API", NumberWidth + 16));
        table = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = rows,
        };
        ReusedRows.Use(table, () => RowOf(titles));
        table.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        });
        table.DoubleTapped += (_, _) => Open();
        TreeKeys.Attach<GameRow>(table, Toggle, OpenRow);
        shown = new DockPanel { Children = { band, titles, table } };
        placeholder = Muted(NoFrames);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextWrapping = TextWrapping.Wrap;
        placeholder.MaxWidth = 560;
        placeholder.TextAlignment = TextAlignment.Center;
        Children.Add(new Panel { Children = { shown, placeholder } });
    }

    /// <summary>What the page says of a game's run that recorded no frames.</summary>
    private const string NoFrames = "No Godot frames in this run. Passenger records a Godot game's own frame profile beside its samples when it profiles one Run, Debug or Profile started.";

    /// <summary>A GDScript function's row opened: its script, at its line.</summary>
    public event EventHandler<(ScriptFunction Function, string Folder)>? FunctionOpened;

    /// <summary>A frame asked opened on what it spent (<see cref="FrameLink"/>): the frame chosen, else the slowest shown.</summary>
    public event EventHandler<GameFrame>? FrameOpened;

    private readonly Button frameLink;

    /// <summary>The link that opens the chosen frame's calls, or the slowest frame's while none is chosen.</summary>
    public Button FrameLink => frameLink;

    /// <summary>The frame the link opens: the one chosen on the chart, else the slowest of those shown; none without frames.</summary>
    public GameFrame? Opened => Chosen ?? (frames.Count > 0 ? frames.MaxBy(f => f.FrameMs) : null);

    /// <summary>A C# method's row opened: its source.</summary>
    public event EventHandler<HotSpot>? MethodOpened;

    /// <summary>The frames shown, in order: the run's, or the stretch's chosen on the timeline.</summary>
    public IReadOnlyList<GameFrame> Frames => frames;

    /// <summary>The frame chosen on the chart, or null for all.</summary>
    public GameFrame? Chosen => chart.Selection is int at ? frames[at] : null;

    public FrameChart Chart => chart;

    /// <summary>The table's rows as listed now: the kinds of work, and the functions of those opened.</summary>
    public IReadOnlyList<GameRow> Rows => rows;

    public ListBox Table => table;

    /// <summary>The line over the table: the frames', or the chosen frame's, times.</summary>
    public string Summary => summary.Text ?? string.Empty;

    /// <summary>What the page says in place of frames, while it shows none.</summary>
    public string? Placeholder => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>Shows a game's frames, all of them chosen, with the C# the engine called in them as <paramref name="joining"/>
    /// joins it; <paramref name="start"/> is where the run began, which their times are said from.</summary>
    public void Show(GameProfile game, IReadOnlyList<GameFrame> shownFrames, Func<IReadOnlyList<GameFrame>, GameJoin> joining, double start)
    {
        profile = game;
        frames = shownFrames;
        join = joining;
        origin = start;
        joined = joining(shownFrames);
        chart.Frames = [.. shownFrames.Select((f, i) => Parts(f, joined.FrameMs[i]))];
        shown.IsVisible = shownFrames.Count > 0;
        placeholder.IsVisible = shownFrames.Count == 0;
        placeholder.Text = game.Why ?? NoFrames;
        Fill();
    }

    /// <summary>A frame's bar, as tall as Godot's time for it: its physics steps, then the GDScript functions' own time, the C#
    /// the engine called as sampled in it, and the rest Godot's own.</summary>
    private static IReadOnlyList<FramePart> Parts(GameFrame frame, double sharp)
    {
        double physics = Math.Max(0, frame.FrameMs - frame.ProcessMs);
        double script = Math.Clamp(frame.ScriptMs, 0, Math.Max(0, frame.FrameMs - physics));
        double csharp = Math.Clamp(sharp, 0, Math.Max(0, frame.FrameMs - physics - script));
        return
        [
            new(physics, TokenKeys.Graph_1),
            new(script, TokenKeys.Graph_0),
            new(csharp, TokenKeys.Graph_2),
            new(Math.Max(0, frame.FrameMs - physics - script - csharp), TokenKeys.TextMuted, 0.35),
        ];
    }

    /// <summary>Opens or closes a kind's row, as a click on its chevron does.</summary>
    public void Toggle(GameRow row)
    {
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            rows.Close(row);
        }
        else if (row.Expands)
        {
            row.IsExpanded = true;
            rows.Open(row, row.Children);
        }
    }

    /// <summary>The table and the summary, for the frame chosen or for all; the kinds holding the most time opened.</summary>
    private void Fill()
    {
        rows.Clear();
        if (profile is not { } game || join is null || joined is null)
        {
            summary.Text = string.Empty;
            return;
        }
        IReadOnlyList<GameFrame> over = Chosen is { } frame ? [frame] : frames;
        GameJoin within = Chosen is not null ? join(over) : joined;
        summary.Text = Said(game, over, within);
        frameLink.Content = Opened is not { } opened ? null
            : Chosen is not null ? $"Open frame {opened.Number}'s calls in the call tree"
            : $"Open the slowest frame's calls in the call tree (frame {opened.Number}, {opened.FrameMs:N1} ms)";
        frameLink.IsVisible = Opened is not null;
        foreach (GameRow kind in Kinds(game, over, within))
        {
            rows.Add(kind);
        }
        foreach (GameRow kind in rows.Where(r => r.Expands && r.Name is Script or Sharp).ToList())
        {
            Toggle(kind);
        }
        table.SelectedItem = rows.FirstOrDefault();
    }

    private const string Script = "GDScript";
    private const string Sharp = "C# the engine called (sampled)";

    /// <summary>The table's kinds of work over some frames, each with its rows.</summary>
    private static IReadOnlyList<GameRow> Kinds(GameProfile game, IReadOnlyList<GameFrame> over, GameJoin within)
    {
        List<GameRow> kinds = [];
        IReadOnlyList<ScriptSpot> spots = game.Spots(over);
        List<GameRow> scripts = [.. spots.Where(s => !s.Function.IsEngineCall).Select(s => new GameRow(s.Function.Name, PlaceOf(s.Function), s.SelfMs, s.TotalMs, s.Calls, s.EngineMs, [], 1) { Function = s.Function })];
        kinds.Add(new GameRow(Script, null, scripts.Sum(s => s.Own), null, null, scripts.Sum(s => s.Engine), scripts));
        List<GameRow> sharp = [.. within.CSharp.Take(200).Select(m => new GameRow(m.Name, m.Name.StartsWith('[') ? "Godot's native code, scripts it ran among it" : null,
            m.Self * within.SampleMs, m.Total * within.SampleMs, null, null, [], 1) { Method = m })];
        kinds.Add(new GameRow(Sharp, null, within.FrameMs.Sum(), null, null, null, sharp));
        List<GameRow> engine = [.. spots.Where(s => s.Function.IsEngineCall).Select(s => new GameRow(s.Function.Name, $"from {Path.GetFileName(s.Function.File)}", s.SelfMs, s.TotalMs, s.Calls, null, [], 1))];
        if (engine.Count > 0)
        {
            kinds.Add(new GameRow("Scripts' calls into Godot's API", null, engine.Sum(e => e.Own), null, engine.Sum(e => e.Calls), null, engine));
        }
        foreach (IGrouping<string, ServerWork> server in GameProfile.Servers(over).GroupBy(s => s.Server))
        {
            List<GameRow> functions = [.. server.Select(f => new GameRow(f.Function, null, f.Ms, null, null, null, [], 1))];
            kinds.Add(new GameRow(server.Key, "server", functions.Sum(f => f.Own), null, null, null, functions));
        }
        IReadOnlyList<RenderArea> rendered = GameProfile.Rendered(over);
        if (rendered.Count > 0)
        {
            List<GameRow> stages = [.. rendered.Select(a => new GameRow(a.Name, $"GPU {a.GpuMs:N1} ms", a.CpuMs, null, null, null, [], 1))];
            kinds.Add(new GameRow("Rendering (CPU)", game.Gpu, stages.Sum(s => s.Own), null, null, null, stages));
        }
        return kinds;
    }

    /// <summary>A script function's place, as the table names it: its file's name and line.</summary>
    private static string PlaceOf(ScriptFunction function) => $"{Path.GetFileName(function.File)}:{function.Line}";

    private string Said(GameProfile game, IReadOnlyList<GameFrame> over, GameJoin within)
    {
        if (over.Count == 0)
        {
            return string.Empty;
        }
        string drawn = !game.Draws ? " It ran headless, so Godot drew nothing: no render stages."
            : over.Any(f => f.Rendered.Count > 0) ? string.Empty
            : " Godot's renderer told no stages of these frames.";
        double physics = over.Sum(f => Math.Max(0, f.FrameMs - f.ProcessMs));
        double script = over.Sum(f => f.ScriptMs);
        double sharp = within.FrameMs.Sum();
        double all = over.Sum(f => f.FrameMs);
        if (Chosen is { } one)
        {
            return $"Frame {one.Number} at {one.StartMs - origin:N0} ms: {one.FrameMs:0.0} ms; physics {physics:0.0} ms, GDScript's own {script:0.0} ms, "
                + $"C# {sharp:0.0} ms (sampled), Godot's own {Math.Max(0, all - physics - script - sharp):0.0} ms.{drawn}";
        }
        double[] times = [.. over.Select(f => f.FrameMs).Order()];
        GameFrame longest = over.MaxBy(f => f.FrameMs)!;
        return $"{game.Engine}, {over.Count:N0} {Plural.Noun(over.Count, "frame")}: {UiFrames.Percentile(times, 0.5):0.0} ms at the median, {longest.FrameMs:0.0} ms at the longest "
            + $"(frame {longest.Number}); physics {physics:N1} ms, GDScript's own {script:N1} ms, C# {sharp:N1} ms (sampled), Godot's own {Math.Max(0, all - physics - script - sharp):N1} ms "
            + $"in all.{drawn} Choose a frame for its own.";
    }

    private void Open()
    {
        if (table.SelectedItem is GameRow row && !OpenRow(row))
        {
            Toggle(row);
        }
    }

    /// <summary>A function's row opened: its script at its line, or its C# source; false for any other row.</summary>
    private bool OpenRow(GameRow row)
    {
        if (row.Function is { } function && profile is { } game)
        {
            FunctionOpened?.Invoke(this, (function, game.Folder));
            return true;
        }
        if (row.Method is { } method && !method.Name.StartsWith('['))
        {
            MethodOpened?.Invoke(this, method);
            return true;
        }
        return false;
    }

    /// <summary>A row for whichever kind or function the list hands its container: its chevron, name and place, and its times
    /// and calls in their columns.</summary>
    private Control RowOf(ColumnTitles titles)
    {
        RowIcon chevron = RowIcon.Chevron();
        Border toggle = new() { Width = 18, Height = ListRow.Height, Background = Brushes.Transparent, Child = chevron };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock place = Muted(string.Empty);
        place.VerticalAlignment = VerticalAlignment.Center;
        place.Margin = new Thickness(8, 0, 0, 0);
        place.TextTrimming = TextTrimming.CharacterEllipsis;
        StackPanel named = new() { Orientation = Orientation.Horizontal, Children = { toggle, name, place } };
        TextBlock[] numbers = [Number(), Number(), Number(), Number()];
        Grid row = titles.Row([named, .. numbers]);
        toggle.PointerPressed += (_, e) =>
        {
            if (row.DataContext is GameRow shown)
            {
                Toggle(shown);
                e.Handled = true;
            }
        };
        return row.Showing<GameRow>(shown =>
        {
            chevron.IsVisible = shown.Children.Count > 0;
            chevron.Turned = shown.IsExpanded;
            name.Text = shown.Name;
            name.FontWeight = shown.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal;
            place.Text = shown.Place ?? string.Empty;
            named.Margin = new Thickness(shown.Depth * 14, 0, 0, 0);
            Put(numbers[0], shown.Own is { } own ? $"{own:N1} ms" : string.Empty);
            Put(numbers[1], shown.All is { } all ? $"{all:N1} ms" : string.Empty);
            Put(numbers[2], shown.Calls is { } calls ? $"{calls:N0}" : string.Empty);
            Put(numbers[3], shown.Engine is { } engine ? $"{engine:N1} ms" : string.Empty);
        });
    }

    private static TextBlock Number() =>
        new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();

    /// <summary>A number in its cell, muted where it is nothing.</summary>
    private static void Put(TextBlock cell, string text)
    {
        cell.Text = text;
        cell.Classes.Set(StyleClasses.Muted, text is "0" or "0.0 ms");
    }

    private static TextBlock Muted(string text)
    {
        TextBlock block = MutedText.Of(text);
        block.FontSize = 12;
        return block;
    }
}
