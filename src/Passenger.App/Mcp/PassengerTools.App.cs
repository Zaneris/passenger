using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.Inspector;

namespace Passenger.App.Mcp;

/// <summary>The Avalonia or WPF app Passenger runs or debugs, as an agent tests it: its windows, pictures the app draws of
/// them, its visual tree, clicks, typing, keys and scrolling given as a person's are, and its failed bindings. All through the
/// inspector Run and Debug launch an Avalonia 12 app or a WPF app with (<see cref="LiveApp"/>), the Live Visual Tree's, or for
/// Passenger's own windows the same inspector loaded into Passenger. A WPF app's clicks go through UI Automation and its keys
/// through WPF's input manager (src/Passenger.Inspector.Wpf/Driver.cs).</summary>
public sealed partial class PassengerTools
{
    private const string AppChoice =
        "Which app: run (Run's), debug (Debug's), or ide (Passenger's own windows); by default the one running";

    private const string ControlQuery =
        "An element: @N (an id app_tree shows), or a query of a type, #name, .class and \"text\" it shows (Button#Save, \"Save\", " +
        "TextBlock.title), steps joined by / for one inside another (#Panel/Button), [n] for the nth match; a template's part only by its " +
        "#name or @N";

    private const string WindowChoice = "A window: @N from app_windows, or its type or \"title\"; by default the app's first window";

    /// <summary>How long a tool waits for an app launched a moment ago to show its UI.</summary>
    private static readonly TimeSpan AppUiLimit = TimeSpan.FromSeconds(60);

    [McpServerTool(Name = "app_windows", Title = "The app's windows", ReadOnly = true), Description(
        "The windows of the Avalonia or WPF app that Run or Debug started (an Avalonia 12 app, or a WPF app on Windows, launched with " +
        "Passenger's inspector in it; one still starting is waited for), or Passenger's own with app ide: each with its id (@N, which the " +
        "other app_ tools take), type, title, size in units, pixels a unit, whether it is active and shown, and for a popup (a menu, a " +
        "drop-down) the window it opened from.")]
    public Task<AppWindows> AppWindowsList([Description(AppChoice)] string? app = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        LiveApp live = await InspectedAsync(app, cancel);
        IReadOnlyList<AppWindow> windows = await live.WindowsAsync();
        return new AppWindows(live.Name, live.Framework, live.Avalonia, live.ProcessId, [.. windows.Select(w => new AppWindowInfo(
            Id(w.Id), w.Type, w.Name, w.Title, Math.Round(w.Width, 2), Math.Round(w.Height, 2), w.Scaling, w.Active, w.Visible, w.Owner is long owner ? Id(owner) : null))]);
    });

    [McpServerTool(Name = "app_screenshot", Title = "A picture of the app", ReadOnly = true), Description(
        "A PNG of one of the app's windows (its open popups drawn over it) as the app itself draws it, so it works with the window hidden, " +
        "off the screen or headless; or of one element's part of it, or of an area (x, y, width, height in the window's units). By default at " +
        "the window's own scaling, kept to 1600 pixels a side; scale gives pixels a unit. The text beside it says how its pixels map to the " +
        "window's units, which app_click's x and y take.")]
    public Task<CallToolResult> AppScreenshot(
        [Description(AppChoice)] string? app = null,
        [Description(WindowChoice)] string? window = null,
        [Description(ControlQuery + "; the picture is its part of its window")] string? control = null,
        [Description("Pixels a unit, up to 8")] double? scale = null,
        int? x = null, int? y = null, int? width = null, int? height = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        LiveApp live = await InspectedAsync(app, cancel);
        (List<AppNode> nodes, AppNode chosenWindow) = await TreeOf(live, window, cancel);
        AppNode shown = control is { Length: > 0 } query ? AppQuery.One(Within(nodes, window is null ? null : chosenWindow), query) : chosenWindow;
        Box? area = x is null && y is null && width is null && height is null ? null : new Box(x ?? 0, y ?? 0, width ?? 400, height ?? 300);
        if (scale is <= 0 or > 8)
        {
            throw new ArgumentException("scale is pixels a unit, more than 0 and at most 8");
        }
        Picture picture = await live.CaptureAsync(shown.Element.Id, scale, area);
        string what = shown.Parent is null ? Titled(shown.Window) : $"{AppQuery.Short(shown.Element)} in {Titled(shown.Window)}";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{what}: {picture.Width}x{picture.Height} pixels of the window's area {picture.Area.X:0.#},{picture.Area.Y:0.#} {picture.Area.Width:0.#}x{picture.Area.Height:0.#}, " +
            $"{picture.Scale:0.##} pixels a unit, so the pixel px,py is the window's point {picture.Area.X:0.#}+px/{picture.Scale:0.##}, {picture.Area.Y:0.#}+py/{picture.Scale:0.##}");
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(picture.Png, "image/png"), new TextContentBlock { Text = text }] };
    });

    [McpServerTool(Name = "app_tree", Title = "The app's visual tree", ReadOnly = true), Description(
        "The app's visual tree (the Live Visual Tree's), indented a level a line: each element's type, #name, .classes, \"text\" it shows, @id, " +
        "its box in its window's units (x,y widthxheight), and [hidden], [out of view] (scrolled or clipped away), [disabled], [focused]; with " +
        "properties, their values ({Name=value}). A template's parts are left out (what they hold comes up a level) unless templates is true, " +
        "and what a hidden element holds is counted unless hidden is true. control starts the tree at an element; filter lists only the " +
        "elements a query finds, each with where it is. Every window, unless one is named.")]
    public Task<string> AppTree(
        [Description(AppChoice)] string? app = null,
        [Description(WindowChoice + "; by default every window")] string? window = null,
        [Description(ControlQuery + "; the tree starts there")] string? control = null,
        [Description("A query (as control takes) for the elements to list, in place of the whole tree")] string? filter = null,
        [Description("Levels below where it starts")] int? depth = null,
        [Description("Properties to show the values of (Avalonia's, or a WPF app's dependency properties: IsEnabled, Foreground, Grid.Row, Offset) where an element has them")] IReadOnlyList<string>? properties = null,
        bool templates = false,
        bool hidden = false,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        LiveApp live = await InspectedAsync(app, cancel);
        AppTree tree = await live.AgentTreeAsync(properties ?? []);
        List<AppNode> nodes = AppQuery.Flatten(tree.Roots);
        if (nodes.Count == 0)
        {
            return $"{live.Name} has no window open";
        }
        List<AppNode> scope = window is { Length: > 0 } ? Within(nodes, Window(nodes, window, live)) : nodes;
        List<AppNode> roots = control is { Length: > 0 } query ? [AppQuery.One(scope, query)] : [.. scope.Where(n => n.Parent is null)];
        if (filter is { Length: > 0 } wanted)
        {
            List<AppNode> under = roots.Count == 1 && roots[0].Parent is not null ? Within(scope, roots[0]) : scope;
            List<AppNode> found = AppQuery.Find(under, wanted, templates);
            if (found.Count == 0)
            {
                return $"Nothing in {live.Name} matches {wanted}";
            }
            return string.Join('\n', found.Take(AppQuery.MostLines).Select(AppQuery.Where))
                + (found.Count > AppQuery.MostLines ? $"\n... and {found.Count - AppQuery.MostLines} more" : string.Empty);
        }
        return AppQuery.Write(roots, depth, templates, hidden);
    });

    [McpServerTool(Name = "app_click", Title = "Click in the app", Destructive = true), Description(
        "Clicks in the app as a person's mouse would, through the window's own input (pointer over, press, release; handlers, commands and " +
        "focus all run): at an element's centre (or at x,y from its top left), or at x,y in a window. Its window is not brought forward and " +
        "the real pointer does not move. An element hidden, disabled, scrolled out of view or covered by another is refused, saying which. " +
        "Says what took the click, what it shows now and what has the focus.")]
    public Task<string> AppClick(
        [Description(AppChoice)] string? app = null,
        [Description(ControlQuery)] string? control = null,
        [Description("From the element's left, or the window's")] double? x = null,
        [Description("From the element's top, or the window's")] double? y = null,
        [Description(WindowChoice)] string? window = null,
        [Description("left, right or middle")] string button = "left",
        [Description("2 for a double click")] int count = 1,
        [Description("Held while clicking: ctrl, shift, alt, cmd (meta), or primary (Cmd on macOS, Ctrl elsewhere)")] IReadOnlyList<string>? modifiers = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: true, x, y, cancel);
        InputDone done = await live.InputAsync(new InputAction(InputKinds.Click)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            X = x,
            Y = y,
            Button = button,
            Count = count,
            Modifiers = Modifiers(modifiers),
        });
        string clicked = count switch
        {
            2 => "Double-clicked",
            >= 3 => "Triple-clicked",
            _ => button == "left" ? "Clicked" : $"{char.ToUpperInvariant(button[0])}{button[1..]}-clicked",
        };
        return $"{clicked} {Aimed(element, done)}{Took(element, done, "click")}{Said(done)}";
    });

    [McpServerTool(Name = "app_type", Title = "Type into the app", Destructive = true), Description(
        "Types text into the app as a keyboard would, a key and its text at a time (a newline is Enter, a tab Tab): into an element, given " +
        "the keyboard focus first if it has not (a text box then types at the end of its text), else into whatever has the focus. With " +
        "replace, the platform's Select All goes first, so the text replaces what is there. Says what it shows now.")]
    public Task<string> AppType(
        string text,
        [Description(AppChoice)] string? app = null,
        [Description(ControlQuery)] string? control = null,
        bool replace = false,
        [Description(WindowChoice)] string? window = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: false, null, null, cancel);
        InputDone done = await live.InputAsync(new InputAction(InputKinds.Type)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            Text = text,
            Replace = replace,
        });
        return $"Typed {text.Length} character{(text.Length == 1 ? "" : "s")} into {done.TargetName ?? "the window, nothing having the focus"}{Said(done)}";
    });

    [McpServerTool(Name = "app_key", Title = "Press keys in the app", Destructive = true), Description(
        "Presses keys in the app as a keyboard would, one chord after another (\"Enter\", \"Tab Tab Space\", \"ctrl+A Delete\", " +
        "\"primary+S\" for the platform's Cmd or Ctrl): an element is given the keyboard focus first, else the keys go to whatever has it. " +
        "Keys are Avalonia's names, which WPF's share (Enter, Escape, Back, Delete, Left, PageDown, F5, A, D1 or 1); modifiers ctrl, shift, alt, cmd (meta), " +
        "primary. A printable key types its character when nothing takes the key itself. Key bindings and commands run as they would.")]
    public Task<string> AppKey(
        [Description("Chords separated by spaces, each keys joined by +")] string keys,
        [Description(AppChoice)] string? app = null,
        [Description(ControlQuery)] string? control = null,
        [Description(WindowChoice)] string? window = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        IReadOnlyList<Chord> chords = Chords(keys);
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: false, null, null, cancel);
        InputDone done = await live.InputAsync(new InputAction(InputKinds.Key)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            Keys = chords,
        });
        return $"Pressed {keys.Trim()} in {done.TargetName ?? "the window, nothing having the focus"}{Said(done)}";
    });

    [McpServerTool(Name = "app_scroll", Title = "Scroll in the app", Destructive = true), Description(
        "Turns the mouse wheel over an element (its centre) or at x,y in a window, as a person scrolls: dy notches down (negative up), dx " +
        "right (negative left). The scroll viewer under it scrolls as it would; says where it is now.")]
    public Task<string> AppScroll(
        [Description(AppChoice)] string? app = null,
        [Description(ControlQuery)] string? control = null,
        [Description("Notches down; negative scrolls up. 3 when neither dy nor dx is given")] double? dy = null,
        [Description("Notches right; negative scrolls left")] double? dx = null,
        double? x = null,
        double? y = null,
        [Description(WindowChoice)] string? window = null,
        [Description("Held while scrolling: ctrl, shift, alt, cmd (meta), primary")] IReadOnlyList<string>? modifiers = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: true, x, y, cancel);
        double down = dy ?? (dx is null ? 3 : 0);
        double across = dx ?? 0;
        InputDone done = await live.InputAsync(new InputAction(InputKinds.Scroll)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            X = x,
            Y = y,
            DeltaX = across,
            DeltaY = down,
            Modifiers = Modifiers(modifiers),
        });
        string turned = string.Join(" and ", new[] { Notches(down, "down", "up"), Notches(across, "right", "left") }.Where(n => n.Length > 0));
        string scrolled = $"Scrolled {turned} over {Aimed(element, done)}{Took(element, done, "wheel")}";
        return done.Shows is { } where ? $"{scrolled}: {where}" : $"{scrolled}, which no scroll viewer holds";

        static string Notches(double notches, string ahead, string back) => notches == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(notches):0.#} notch{(Math.Abs(notches) == 1 ? "" : "es")} {(notches < 0 ? back : ahead)}");
    });

    [McpServerTool(Name = "app_binding_errors", Title = "The app's failed bindings", ReadOnly = true), Description(
        "The bindings that have failed in the Avalonia or WPF apps Run and Debug started last (or the one named), as Problems' Bindings tab lists " +
        "them: each once, with its control, property, path, the framework's message (and the nearest members when the path names one its source " +
        "lacks), where in the XAML (the misspelt member when the file has it) and how many times it failed. An app that ended keeps its " +
        "list until the next of its kind starts; Passenger's own (app ide) are those since an agent first looked at its windows.")]
    public Task<IReadOnlyList<AppBindingError>> AppBindingErrors([Description(AppChoice)] string? app = null, CancellationToken cancel = default) => OnUi(cancel, () =>
    {
        IEnumerable<LiveApp> apps = app switch
        {
            null => new[] { Session.RunApp, Session.LiveApp }.OfType<LiveApp>(),
            "run" => [Session.RunApp ?? throw new InvalidOperationException("Run has started no Avalonia or WPF app in this folder")],
            "debug" => [Session.LiveApp ?? throw new InvalidOperationException("Debug has started no Avalonia or WPF app in this folder")],
            "ide" => [LiveApp.Own()],
            _ => throw new ArgumentException($"No app '{app}': name run, debug or ide"),
        };
        List<LiveApp> chosen = [.. apps];
        if (chosen.Count == 0)
        {
            throw new InvalidOperationException("No Avalonia or WPF app has been run or debugged in this folder; run or debug_start one");
        }
        return Task.FromResult<IReadOnlyList<AppBindingError>>([.. chosen.SelectMany(live => live.Bindings.Select(failure =>
        {
            BindingProblem placed = Builds.Placed(live, failure);
            Passenger.App.Run.Problem problem = placed.Problem;
            return new AppBindingError(live == Session.LiveApp ? "debug" : live == Session.RunApp ? "run" : "ide", failure.Level, failure.Control,
                failure.Property, failure.Path, BindingErrors.Message(failure),
                problem.Path is { } file ? $"{Relative(file)}:{problem.Position.Line + 1}:{problem.Position.Column + 1}" : null, failure.Count, failure.Nearest);
        }))]);
    });

    /// <summary>The app a tool acts on, once its UI is up: the one named, or the one running (Run's or Debug's), refusing in
    /// words that say what to do when none is, two are, it is paused, refused by the inspector, or ended.</summary>
    private async Task<LiveApp> InspectedAsync(string? app, CancellationToken cancel)
    {
        LiveApp chosen = app switch
        {
            null => Running(),
            "run" => Session.RunApp ?? throw new InvalidOperationException(Builds.IsRunning
                ? $"What Run started is not {Inspectable}"
                : "Run has started no Avalonia or WPF app; run starts one"),
            "debug" => Session.LiveApp ?? throw new InvalidOperationException(Session.Debug.IsDebugging
                ? $"What is being debugged is not {Inspectable}"
                : "Debug has started no Avalonia or WPF app; debug_start starts one"),
            "ide" => LiveApp.Own(),
            _ => throw new ArgumentException($"No app '{app}': name run, debug or ide"),
        };
        if (chosen == Session.LiveApp && Session.Debug.Session is { State: DebugState.Paused })
        {
            throw new InvalidOperationException($"{chosen.Name} is paused in the debugger, and its UI answers nothing until it runs: debug_continue");
        }
        await Until(() => chosen.State != LiveAppState.Waiting, AppUiLimit, cancel);
        return chosen.State switch
        {
            LiveAppState.Connected => chosen,
            LiveAppState.Waiting => throw new TimeoutException($"{chosen.Name} has not shown its UI in {AppUiLimit.TotalSeconds:0} s"),
            LiveAppState.Refused => throw new InvalidOperationException(chosen.Problem ?? $"{chosen.Name} cannot be inspected"),
            _ => throw new InvalidOperationException($"{chosen.Name} has ended"),
        };
    }

    /// <summary>The app running, Run's or Debug's, when it is one.</summary>
    private LiveApp Running()
    {
        List<LiveApp> live = [.. new[] { Session.RunApp, Session.LiveApp }.OfType<LiveApp>().Where(a => a.State is LiveAppState.Waiting or LiveAppState.Connected)];
        return live switch
        {
            [LiveApp only] => only,
            [LiveApp run, LiveApp debug] => throw new ArgumentException($"Run's {run.Name} and Debug's {debug.Name} are both running: name one with app (run or debug)"),
            _ => throw new InvalidOperationException(WhyNoneRuns()),
        };
    }

    /// <summary>Why no app is there to inspect. The app of a Run or Debug still winding down has ended once its inspector
    /// has gone, though its runner has not yet heard the process end (on Windows the socket closes first).</summary>
    private string WhyNoneRuns()
    {
        LiveApp[] apps = [.. new[] { Session.LiveApp, Session.RunApp }.OfType<LiveApp>()];
        if (apps.FirstOrDefault(a => a.State == LiveAppState.Refused) is { } refused)
        {
            return refused.Problem ?? $"{refused.Name} cannot be inspected";
        }
        LiveApp? launched = (Session.Debug.IsDebugging ? Session.LiveApp : null) ?? (Builds.IsRunning ? Session.RunApp : null);
        if (launched is { State: LiveAppState.Ended })
        {
            return Ended(launched);
        }
        if (Builds.IsRunning || Session.Debug.IsDebugging)
        {
            return $"What is running is not {Inspectable}";
        }
        return apps.FirstOrDefault() is { } ended
            ? Ended(ended)
            : "No Avalonia or WPF app is running: run or debug_start one (Avalonia 12, or WPF on Windows), or name app ide for Passenger's own windows";

        static string Ended(LiveApp app) => $"{app.Name} has ended: run or debug_start starts it again, or name app ide for Passenger's own windows";
    }

    /// <summary>What Passenger inspects, as a refusal says it.</summary>
    private const string Inspectable = "an app Passenger inspects (an Avalonia 12 app, Avalonia.Base.dll beside it, or a WPF app on Windows)";

    /// <summary>The app's tree, flattened, and the window named (else the app's first, or for Passenger's own the window this
    /// agent works in).</summary>
    private async Task<(List<AppNode> Nodes, AppNode Window)> TreeOf(LiveApp live, string? window, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        List<AppNode> nodes = AppQuery.Flatten((await live.AgentTreeAsync([])).Roots);
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException($"{live.Name} has no window open");
        }
        return (nodes, Window(nodes, window, live));
    }

    private AppNode Window(List<AppNode> nodes, string? named, LiveApp live)
    {
        List<AppNode> windows = [.. nodes.Where(n => n.Parent is null)];
        if (named is not { Length: > 0 })
        {
            // Passenger's own: the window the agent works in, whose title is its own.
            return live.Plan is null && windows.FirstOrDefault(w => w.Element.Text == window.Title) is { } own ? own : windows[0];
        }
        return AppQuery.Find(windows, named).FirstOrDefault(n => n.Parent is null)
            ?? throw new ArgumentException($"No window of {live.Name} is {named}: {string.Join("; ", windows.Select(w => Titled(w.Element)))}");
    }

    /// <summary>A window's elements, the window first.</summary>
    private static List<AppNode> Within(List<AppNode> nodes, AppNode? root) =>
        root is null ? nodes : [.. nodes.Where(n => n == root || n.IsInside(root))];

    /// <summary>What an input acts on: its app, the element named (null for a point, or for whatever has the focus) and the
    /// node whose window it is in.</summary>
    private async Task<(LiveApp Live, AppNode? Element, AppNode Window)> Target(string? app, string? control, string? window, bool needsPoint,
        double? x, double? y, CancellationToken cancel)
    {
        LiveApp live = await InspectedAsync(app, cancel);
        (List<AppNode> nodes, AppNode chosen) = await TreeOf(live, window, cancel);
        if (control is { Length: > 0 } query)
        {
            AppNode element = AppQuery.One(Within(nodes, window is null ? null : chosen), query);
            return (live, element, element);
        }
        if (needsPoint && (x is null || y is null))
        {
            throw new ArgumentException("Name a control, or give x and y in the window");
        }
        return (live, null, chosen);
    }

    private static List<string>? Modifiers(IReadOnlyList<string>? names) => names is null ? null : [.. names.Select(Modifier)];

    /// <summary>A modifier's name as the inspector takes it, or an error naming those there are.</summary>
    private static string Modifier(string name) => name.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => "Control",
        "shift" => "Shift",
        "alt" or "option" or "opt" => "Alt",
        "cmd" or "command" or "meta" or "win" or "super" => "Meta",
        "primary" or "cmdorctrl" => "Primary",
        _ => throw new ArgumentException($"No modifier '{name}': there are ctrl, shift, alt, cmd (meta) and primary"),
    };

    /// <summary>Chords as an agent writes them, "ctrl+shift+S Enter", as Avalonia's keys and modifiers.</summary>
    private static List<Chord> Chords(string keys)
    {
        List<Chord> chords = [];
        foreach (string written in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The plus key is a chord's last + when it ends in two, or the whole chord.
            string[] parts = written == "+" ? ["+"] : written.EndsWith("++", StringComparison.Ordinal) ? [.. written[..^2].Split('+'), "+"] : written.Split('+');
            List<string> held = [.. parts[..^1].Select(Modifier)];
            chords.Add(new Chord(KeyName(parts[^1]), held));
        }
        return chords.Count > 0 ? chords : throw new ArgumentException("Name the keys to press");
    }

    /// <summary>A key as Avalonia names it, from what an agent writes: Enter, esc, 1, a, PageDown, F5, +.</summary>
    private static string KeyName(string key)
    {
        string name = key.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "return" => "Enter",
            "backspace" => "Back",
            "del" => "Delete",
            "ins" => "Insert",
            "pgup" => "PageUp",
            "pgdn" => "PageDown",
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            "space" or " " => "Space",
            "+" or "plus" => "OemPlus",
            "-" or "minus" => "OemMinus",
            "," or "comma" => "OemComma",
            "." or "period" => "OemPeriod",
            _ when key.Length == 1 && char.IsAsciiDigit(key[0]) => "D" + key,
            _ => key,
        };
        return Enum.TryParse(name, ignoreCase: true, out Avalonia.Input.Key parsed) && !int.TryParse(name, out _) && parsed != Avalonia.Input.Key.None
            ? parsed.ToString()
            : throw new ArgumentException($"No key '{key}': keys are Avalonia's names (Enter, Escape, Back, Delete, Left, PageDown, F5, A, 1)");
    }

    private static string Id(long id) => "@" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A window as an answer names it: its type, @id and title.</summary>
    private static string Titled(Element window) => window.Text is { Length: > 0 } title ? $"{AppQuery.Short(window)} \"{title}\"" : AppQuery.Short(window);

    /// <summary>Where a pointer input went, as its answer says it.</summary>
    private static string Aimed(AppNode? element, InputDone done)
    {
        string at = done.X is double x && done.Y is double y ? string.Create(CultureInfo.InvariantCulture, $" at {x:0.#},{y:0.#}") : string.Empty;
        return element is null ? $"window {Id(done.Window)}{at}" : $"{AppQuery.Short(element.Element)}{at} in window {Id(done.Window)}";
    }

    /// <summary>What took a pointer's input, when it is not the element named: what is at a point, or an element behind it.</summary>
    private static string Took(AppNode? element, InputDone done, string what) =>
        done.TargetName is { } name && done.Target != element?.Element.Id ? $"; the {what} went to {name}" : string.Empty;

    private static string Said(InputDone done) =>
        (done.Shows is { } shows ? $"; it shows \"{shows}\"" : string.Empty) + (done.Focused is { } focused ? $"; {focused} has the focus" : "; nothing has the focus");
}

/// <summary>An app's windows, with what the app is: its run configuration's name, its framework ("Avalonia 12.1.4", "WPF
/// 10.0.1"), its Avalonia's version (null for WPF) and its process.</summary>
public sealed record AppWindows(string App, string Framework, string? Avalonia, int? ProcessId, IReadOnlyList<AppWindowInfo> Windows);

public sealed record AppWindowInfo(string Id, string Type, string? Name, string? Title, double Width, double Height, double Scaling, bool Active, bool Visible, string? Owner);

/// <summary>A failed binding as an agent reads it: the app (run, debug or ide), Avalonia's level, the control, its property and
/// the binding's path, the message, its place in the XAML (path:line:column), how many times, and the nearest members.</summary>
public sealed record AppBindingError(string App, string Level, string Control, string Property, string Path, string Message, string? Place, int Count,
    IReadOnlyList<string> Nearest);
