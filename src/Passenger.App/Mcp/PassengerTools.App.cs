using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Inspector;

namespace Passenger.App.Mcp;

/// <summary>The Avalonia or WPF app Passenger runs or debugs, as an agent tests it: its windows, pictures the app draws
/// of them, its visual tree, clicks, typing, keys and scrolling given as a person's are, and its failed bindings, all
/// through the Live Visual Tree's inspector (<see cref="LiveApp"/>), loaded into Passenger too for its own
/// windows.</summary>
public sealed partial class PassengerTools
{
    // app_windows' description says once what every app_ tool's app and window take; app_tree's, the query's syntax.

    /// <summary>How long a tool waits for an app launched a moment ago to show its UI.</summary>
    private static readonly TimeSpan AppUiLimit = TimeSpan.FromSeconds(60);

    [McpServerTool(Name = McpTools.AppWindows, Title = "The app's windows", ReadOnly = true), Description(
        "The windows of the app (an Avalonia 12 app Run or Debug started, a WPF one on Windows; one starting is waited for): " +
        "each one's @N, type, title, size in units, pixels a unit, whether active and shown, and a popup's owner. Every app_ " +
        "tool's app is run, debug, profile, a debugged program's or an AppHost service's name, or ide for Passenger's own windows, by default the one running; " +
        "its window @N, type or \"title\", by default the first.")]
    public Task<AppWindows> AppWindowsList(string? app = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        LiveApp live = await InspectedAsync(app, cancel);
        IReadOnlyList<AppWindow> windows = await live.WindowsAsync();
        return new AppWindows(live.Name, live.Framework, live.Avalonia, live.ProcessId, [.. windows.Select(w => new AppWindowInfo(
            Id(w.Id), w.Type, w.Name, w.Title, Math.Round(w.Width, 2), Math.Round(w.Height, 2), w.Scaling, w.Active, w.Visible, w.Owner is long owner ? Id(owner) : null))]);
    });

    [McpServerTool(Name = McpTools.AppScreenshot, Title = "A picture of the app", ReadOnly = true), Description(
        "A PNG of an app window (its popups drawn over it) as the app draws it, hidden, off-screen or headless alike; or of " +
        "control's part of it, or an area (x, y, width, height in its units). At the window's scaling up to 1600 pixels a side, " +
        "or scale pixels a unit (8 at most). Its text maps its pixels to the window's units, which app_click takes.")]
    public Task<CallToolResult> AppScreenshot(
        string? app = null,
        string? window = null,
        string? control = null,
        double? scale = null,
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

    [McpServerTool(Name = McpTools.AppTree, Title = "The app's visual tree", ReadOnly = true), Description(
        "The app's visual tree (the Live Visual Tree's), a level a line: each element's type, #name, .classes, \"text\" it " +
        "shows, @id, box in its window's units (x,y widthxheight), and [hidden], [out of view], [disabled], [focused]; with " +
        "properties (IsEnabled, Grid.Row), their values ({Name=value}). A template's parts are left out (what they hold comes " +
        "up a level) unless templates is true, and what a hidden element holds is counted unless hidden is true. control " +
        "starts the tree at an element, depth levels deep; filter lists only the elements a query finds, each with where it " +
        "is. Every window, unless one is named.\n" +
        "Every app_ tool names an element (control, filter) by @N, an id shown here, or by a query: a type, #name, .class and " +
        "\"text\" it shows (Button#Save, \"Save\", TextBlock.title), steps joined by / for one inside another (#Panel/Button), " +
        "[n] for the nth match; a template's part only by its #name or @N.")]
    public Task<string> AppTree(
        string? app = null,
        string? window = null,
        string? control = null,
        string? filter = null,
        int? depth = null,
        IReadOnlyList<string>? properties = null,
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

    [McpServerTool(Name = McpTools.AppDetails, Title = "An element's properties and styles", ReadOnly = true), Description(
        "One element (control) looked at closely, as the Live Visual Tree's details show it: its type, classes and " +
        "pseudo-classes, XAML place (or why none), templated parent and layout (bounds, desired size, margin, border, padding, " +
        "alignment); each property not at its default (every one with defaults) whose name contains properties, with its " +
        "value, type, source (Local, Style, Theme, Template, Inherited...) and what that is with its place; and every style that " +
        "could apply, whether it does and why not, and whether each setter wins, is overridden (by what) or is inactive. " +
        "breakpoint_set with control and property breaks when one changes.")]
    public Task<AppElementDetails> AppDetails(
        string control,
        string? app = null,
        string? window = null,
        string? properties = null,
        bool defaults = false,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        LiveApp live = await InspectedAsync(app, cancel);
        (List<AppNode> nodes, AppNode chosenWindow) = await TreeOf(live, window, cancel);
        AppNode element = AppQuery.One(Within(nodes, window is null ? null : chosenWindow), control);
        ElementDetails details = AppPlaces.InEditor(await live.DetailsAsync(element.Element.Id), Editors);
        string typed = properties?.Trim() ?? string.Empty;
        BreakpointStore breakpoints = Session.Debug.Breakpoints;
        DebugSession? debugged = Session.Debug.SessionOf(live);
        LayoutInfo layout = details.Layout;
        return new AppElementDetails(
            AppQuery.Short(element.Element),
            details.Type,
            details.FullType,
            details.Name,
            details.Classes,
            details.PseudoClasses,
            details.Source is { } source ? Placed(source) : null,
            details.Source is null ? details.NoSource : null,
            details.TemplatedParent,
            new AppLayout(Boxed(layout.Bounds), layout.InWindow is { } inWindow ? Boxed(inWindow) : null,
                string.Create(CultureInfo.InvariantCulture, $"{layout.DesiredWidth:0.##}x{layout.DesiredHeight:0.##}"),
                Sides(layout.Margin), layout.Border is { } border ? Sides(border) : null, layout.Padding is { } padding ? Sides(padding) : null,
                $"{layout.HorizontalAlignment}, {layout.VerticalAlignment}"),
            [.. details.Properties
                .Where(p => (defaults || p.Source != ValueSources.Default) && (typed.Length == 0 || p.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new AppProperty(p.Name, p.Value, p.Type, p.Source, p.Detail, p.Place is { } at ? Placed(at) : null,
                    debugged is not null && breakpoints.FindProperty(debugged, details.Id, p.Name) is not null ? true : null))],
            [.. details.Styles.Select(s => new AppStyle(s.Selector, s.Kind, s.Origin, s.Place is { } at ? Placed(at) : null, s.Applies, s.WhyNot,
                [.. s.Setters.Select(setter => new AppSetter(setter.Property, setter.Value, setter.State, AppPlaces.OverriddenBy(setter), setter.Place is { } placed ? Placed(placed) : null))]))]);

        static string Boxed(Box box) => string.Create(CultureInfo.InvariantCulture, $"{box.X:0.##},{box.Y:0.##} {box.Width:0.##}x{box.Height:0.##}");

        static string Sides(Edges edges) => string.Create(CultureInfo.InvariantCulture, $"{edges.Left:0.##},{edges.Top:0.##},{edges.Right:0.##},{edges.Bottom:0.##}");
    });

    /// <summary>A place in the app's XAML as every other tool gives one: the folder's path, the line (none for a root the build
    /// recorded by its file alone) and the column, the editor's (<see cref="AppPlaces"/>).</summary>
    private string Placed(SourcePlace place)
    {
        string file = Relative(PathNames.Canonical(place.File));
        return place.Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"{file}:{place.Line}:{place.Column}") : file;
    }

    [McpServerTool(Name = McpTools.AppClick, Title = "Click in the app", Destructive = true), Description(
        "Clicks as a mouse would (button left, right or middle; count 2 a double click), through the window's own input, " +
        "handlers, commands and focus all running: at control's centre (or x,y from its top left), or x,y in the window, " +
        "neither bringing it forward nor moving the real pointer, modifiers held as app_key names them. One hidden, disabled, " +
        "out of view or covered is refused, saying which. Says what took the click, what it shows now and what has the focus.")]
    public Task<string> AppClick(
        string? app = null,
        string? control = null,
        double? x = null,
        double? y = null,
        string? window = null,
        string button = "left",
        int count = 1,
        IReadOnlyList<string>? modifiers = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: true, x, y, cancel);
        (InputDone? sent, string? paused) = await SendAsync(live, new InputAction(InputKinds.Click)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            X = x,
            Y = y,
            Button = button,
            Count = count,
            Modifiers = Modifiers(modifiers),
        }, cancel);
        string clicked = count switch
        {
            2 => "Double-clicked",
            >= 3 => "Triple-clicked",
            _ => button == "left" ? "Clicked" : $"{char.ToUpperInvariant(button[0])}{button[1..]}-clicked",
        };
        if (sent is not { } done)
        {
            return $"{clicked} {Named(element)}, and {paused}";
        }
        return $"{clicked} {Aimed(element, done)}{Took(element, done, "click")}{Said(done)}";
    });

    [McpServerTool(Name = McpTools.AppType, Title = "Type into the app", Destructive = true), Description(
        "Types text as a keyboard would, a key at a time (a newline Enter, a tab Tab): into control, given the focus first (a " +
        "text box at its end), else into what has the focus; with replace, Select All first. Says what it shows now. With app " +
        "ide, Passenger's editor types at the caret open put it at, leaving the file unsaved.")]
    public Task<string> AppType(
        string text,
        string? app = null,
        string? control = null,
        bool replace = false,
        string? window = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: false, null, null, cancel);
        (InputDone? sent, string? paused) = await SendAsync(live, new InputAction(InputKinds.Type)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            Text = text,
            Replace = replace,
        }, cancel);
        if (sent is not { } done)
        {
            return $"Typed into {Named(element)}, and {paused}";
        }
        return $"Typed {Plural.Of(text.Length, "character")} into {done.TargetName ?? "the window, nothing having the focus"}{Said(done)}";
    });

    [McpServerTool(Name = McpTools.AppKey, Title = "Press keys in the app", Destructive = true), Description(
        "Presses keys as a keyboard would, chords split by spaces, a chord's keys joined by + (\"Enter\", \"Tab Tab Space\", " +
        "\"ctrl+A Delete\", \"primary+S\"), into control, given the focus first, else what has it. Keys by Avalonia's names, " +
        "which WPF shares (Escape, Back, PageDown, F5, A, D1 or 1); modifiers ctrl, shift, alt, cmd (meta), primary (Cmd on " +
        "macOS, Ctrl elsewhere). Key bindings and commands run; a printable key nothing takes types its character.")]
    public Task<string> AppKey(
        string keys,
        string? app = null,
        string? control = null,
        string? window = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        IReadOnlyList<Chord> chords = Chords(keys);
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: false, null, null, cancel);
        (InputDone? sent, string? paused) = await SendAsync(live, new InputAction(InputKinds.Key)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            Keys = chords,
        }, cancel);
        if (sent is not { } done)
        {
            return $"Pressed {keys.Trim()} in {Named(element)}, and {paused}";
        }
        return $"Pressed {keys.Trim()} in {done.TargetName ?? "the window, nothing having the focus"}{Said(done)}";
    });

    [McpServerTool(Name = McpTools.AppScroll, Title = "Scroll in the app", Destructive = true), Description(
        "Turns the mouse wheel over control's centre or at x,y in the window, as a person scrolls: dy notches down, dx right, " +
        "negative up or left (3 down by default), modifiers held as app_key names them. Says where the scroll viewer under it " +
        "is now.")]
    public Task<string> AppScroll(
        string? app = null,
        string? control = null,
        double? dy = null,
        double? dx = null,
        double? x = null,
        double? y = null,
        string? window = null,
        IReadOnlyList<string>? modifiers = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        (LiveApp live, AppNode? element, AppNode target) = await Target(app, control, window, needsPoint: true, x, y, cancel);
        double down = dy ?? (dx is null ? 3 : 0);
        double across = dx ?? 0;
        (InputDone? sent, string? paused) = await SendAsync(live, new InputAction(InputKinds.Scroll)
        {
            Window = target.Window.Id,
            Element = element?.Element.Id,
            X = x,
            Y = y,
            DeltaX = across,
            DeltaY = down,
            Modifiers = Modifiers(modifiers),
        }, cancel);
        if (sent is not { } done)
        {
            return $"Scrolled over {Named(element)}, and {paused}";
        }
        string turned = string.Join(" and ", new[] { Notches(down, "down", "up"), Notches(across, "right", "left") }.Where(n => n.Length > 0));
        string scrolled = $"Scrolled {turned} over {Aimed(element, done)}{Took(element, done, "wheel")}";
        return done.Shows is { } where ? $"{scrolled}: {where}" : $"{scrolled}, which no scroll viewer holds";

        static string Notches(double notches, string ahead, string back) => notches == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(notches):0.#} notch{(Math.Abs(notches) == 1 ? "" : "es")} {(notches < 0 ? back : ahead)}");
    });

    [McpServerTool(Name = McpTools.AppBindingErrors, Title = "The app's failed bindings", ReadOnly = true), Description(
        "The failed bindings of the apps Run and Debug started last and an AppHost's services (or app's), each once: control, property, path, the " +
        "framework's message (with the nearest members when the source lacks the one named), XAML place and count. An ended " +
        "app's stay until the next of its kind starts; past an app's first 1000, a last entry of level Untold counts the rest.")]
    public Task<IReadOnlyList<AppBindingError>> AppBindingErrors(string? app = null, CancellationToken cancel = default) => OnUi(cancel, () =>
    {
        IEnumerable<LiveApp> apps = app switch
        {
            null => Session.InspectedApps,
            "run" => [RunApp() ?? throw new InvalidOperationException("Run has started no Avalonia or WPF app in this folder")],
            "debug" => [DebugApp() ?? throw new InvalidOperationException("Debug has started no Avalonia or WPF app in this folder")],
            "ide" => [LiveApp.Own()],
            _ => [NamedApp(app)],
        };
        List<LiveApp> chosen = [.. apps];
        if (chosen.Count == 0)
        {
            throw new InvalidOperationException("No Avalonia or WPF app has been run or debugged in this folder; run or debug_start one");
        }
        return Task.FromResult<IReadOnlyList<AppBindingError>>([.. chosen.SelectMany(live =>
        {
            string which = Session.Inspecting.FirstOrDefault(a => a.App == live) is { } inspected ? AppName(inspected) : "ide";
            IEnumerable<AppBindingError> listed = live.Bindings.Select(failure =>
            {
                BindingProblem placed = Builds.Placed(live, failure);
                Passenger.App.Run.Problem problem = placed.Problem;
                return new AppBindingError(which, failure.Level, failure.Control, failure.Property, failure.Path, BindingErrors.Message(failure),
                    problem.Path is { } file ? $"{Relative(file)}:{problem.Position.Line + 1}:{problem.Position.Column + 1}" : null, failure.Count, failure.Nearest);
            });
            return live.Untold.Bindings == 0 ? listed : listed.Concat(BindingErrors.Untold(live).Take(1).Select(untold =>
                new AppBindingError(which, "Untold", string.Empty, string.Empty, string.Empty, $"{untold.Said}: {untold.Why}", null, live.Untold.Bindings, [])));
        })]);
    });

    /// <summary>The app a tool acts on, once its UI is up: the one named, or the one running of those the folder inspects
    /// (<see cref="ProjectSession.Inspecting"/>), refusing in words that say what to do when none is, two are, it is paused,
    /// refused by the inspector, or ended.</summary>
    private async Task<LiveApp> InspectedAsync(string? app, CancellationToken cancel)
    {
        LiveApp chosen = app switch
        {
            null => Running(),
            "run" => RunApp() ?? throw new InvalidOperationException(Runs(RunningKind.Run)
                ? $"What Run started is not {Inspectable}, or Hot Reload runs it"
                : "Run has started no Avalonia or WPF app; run starts one"),
            "debug" => DebugApp() ?? throw new InvalidOperationException(Runs(RunningKind.Debug)
                ? $"What is being debugged is not {Inspectable}"
                : "Debug has started no Avalonia or WPF app; debug_start starts one"),
            "ide" => LiveApp.Own(),
            _ => NamedApp(app),
        };
        if (Session.Debug.SessionOf(chosen) is { State: DebugState.Paused })
        {
            throw new InvalidOperationException($"{chosen.Name} is paused in the debugger, and its UI answers nothing until it runs: debug_continue");
        }
        await When(() => chosen.State != LiveAppState.Waiting, AppUiLimit, cancel, On(h => chosen.StateChanged += h, h => chosen.StateChanged -= h));
        return chosen.State switch
        {
            LiveAppState.Connected => chosen,
            LiveAppState.Waiting => throw new TimeoutException($"{chosen.Name} has not shown its UI in {AppUiLimit.TotalSeconds:0} s"),
            LiveAppState.Refused => throw new InvalidOperationException(chosen.Problem ?? $"{chosen.Name} cannot be inspected"),
            _ => throw new InvalidOperationException($"{chosen.Name} has ended"),
        };
    }

    /// <summary>Whether the folder runs a program of that kind now (<see cref="ProjectSession.Running"/>).</summary>
    private bool Runs(RunningKind kind) => Session.Running.Any(p => p.Kind == kind);

    /// <summary>The app Run started last, inspected; null when it started none.</summary>
    private LiveApp? RunApp() => Session.Inspecting.FirstOrDefault(a => a.Kind == RunningKind.Run)?.App;

    /// <summary>The app Debug started that a tool means by debug: the chosen Debug tab's while it is debugged (a stop chooses
    /// its tab), else the only one debugged, else the chosen tab's, ended; null when no tab has one.</summary>
    private LiveApp? DebugApp()
    {
        List<InspectedApp> apps = [.. Session.Inspecting.Where(a => a.Tab is not null)];
        return apps.FirstOrDefault(a => a.Tab is { IsLive: true } tab && tab == Session.Debug.Chosen)?.App
            ?? (apps.Where(a => a.Tab is { IsLive: true }).ToList() is [var only] ? only.App : null)
            ?? Session.LiveApp;
    }

    /// <summary>The app a name gives: a Debug tab's (debug_state's sessions), else another the folder inspects by the name
    /// an answer gives it (profile, an AppHost's service), refusing a name none has or a program that is not an inspected app.</summary>
    private LiveApp NamedApp(string name)
    {
        ToolWindows.DebugTab? tab = Session.Debug.Tabs.FirstOrDefault(t => t.Name == name)
            ?? Session.Debug.Tabs.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (tab is not null)
        {
            return tab.App ?? throw new InvalidOperationException($"{tab.Name} is debugged, and is not {Inspectable}");
        }
        IReadOnlyList<InspectedApp> others = [.. Session.Inspecting.Where(a => a.Tab is null && a.Kind != RunningKind.Run)];
        return (others.FirstOrDefault(a => a.Name == name) ?? others.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))?.App
            ?? throw new ArgumentException($"No app '{name}': name run, debug, ide or an app the folder runs{Listed()}");

        string Listed() => Session.Inspecting.Where(a => a.Kind != RunningKind.Run).Select(a => a.Tab?.Name ?? a.Name).ToList() is { Count: > 0 } names
            ? $" ({string.Join(", ", names)})"
            : string.Empty;
    }

    /// <summary>How an answer names a Debug tab's app: debug, or with several debugged its tab's name.</summary>
    private string AppName(ToolWindows.DebugTab tab) => Session.Debug.Apps.Skip(1).Any() ? tab.Name : "debug";

    /// <summary>How an answer names an inspected app, as app takes it back.</summary>
    private string AppName(InspectedApp app) => app.Tab is { } tab ? AppName(tab) : app.Name;

    /// <summary>The app running of those the folder inspects, when it is one; with several debugged, the chosen Debug tab's
    /// stands for Debug's.</summary>
    private LiveApp Running()
    {
        List<InspectedApp> live = [.. Session.Inspecting.Where(a => a.App.State is LiveAppState.Waiting or LiveAppState.Connected)];
        if (live.FirstOrDefault(a => a.Tab is { } tab && tab == Session.Debug.Chosen) is { } chosen)
        {
            live.RemoveAll(a => a.Tab is not null && a != chosen);
        }
        return live switch
        {
            [InspectedApp only] => only.App,
            [] => throw new InvalidOperationException(WhyNoneRuns()),
            _ => throw new ArgumentException($"{string.Join(" and ", live.Select(a => $"{a.App.Name} ({AppName(a)})"))} are running: name one with app"),
        };
    }

    /// <summary>Why no app is there to inspect. The app of a program still winding down has ended once its inspector has
    /// gone, though its runner has not yet heard the process end (on Windows the socket closes first).</summary>
    private string WhyNoneRuns()
    {
        IReadOnlyList<InspectedApp> apps = Session.Inspecting;
        if (apps.FirstOrDefault(a => a.App.State == LiveAppState.Refused) is { } refused)
        {
            return refused.App.Problem ?? $"{refused.App.Name} cannot be inspected";
        }
        IReadOnlyList<RunningProgram> running = Session.Running;
        if (running.Select(p => p.App).FirstOrDefault(a => a is { State: LiveAppState.Ended }) is { } launched)
        {
            return Ended(launched);
        }
        if (running.Count > 0)
        {
            return $"What is running is not {Inspectable}, or Hot Reload runs it";
        }
        return apps.FirstOrDefault() is { } ended
            ? Ended(ended.App)
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

    private static List<Modifier>? Modifiers(IReadOnlyList<string>? names) => names is null ? null : [.. names.Select(ModifierNamed)];

    /// <summary>A modifier as an agent names it, or an error naming those there are.</summary>
    private static Modifier ModifierNamed(string name) => name.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => Modifier.Control,
        "shift" => Modifier.Shift,
        "alt" or "option" or "opt" => Modifier.Alt,
        "cmd" or "command" or "meta" or "win" or "super" => Modifier.Meta,
        "primary" or "cmdorctrl" => Modifier.Primary,
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
            List<Modifier> held = [.. parts[..^1].Select(ModifierNamed)];
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

    /// <summary>Input into the app, and what it did; or, when it ran the debugged app into a breakpoint, where that paused it,
    /// said at once rather than the answer waited out, which the app's paused UI gives only once it runs again.</summary>
    private async Task<(InputDone? Done, string? Paused)> SendAsync(LiveApp live, InputAction input, CancellationToken cancel)
    {
        if (Session.Debug.SessionOf(live) is not { } debugged)
        {
            return (await live.InputAsync(input), null);
        }
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<InputDone> sent = live.InputAsync(input, giveUp.Token);
        await When(() => sent.IsCompleted || debugged.State == DebugState.Paused, LiveApp.AnswerLimit, cancel, Ends(sent), StateOf(debugged));
        if (sent.IsCompleted || debugged.State != DebugState.Paused)
        {
            return (await sent, null);
        }
        giveUp.Cancel();
        try
        {
            await sent;
        }
        catch (OperationCanceledException)
        {
            // Given up on: its answer comes once the program runs again, to no one.
        }
        // Its stop chooses its tab, whose stop the debugger reads; looked at every 50 ms, a stop's reading raising nothing as it lands.
        await Until(() => Session.Debug.Session == debugged && Session.Debug.Stop is not null, TimeSpan.FromSeconds(5), cancel);
        DebugSnapshot stop = await DebugSnapshotAsync(debugged);
        return (null, $"that paused the program{(stop.Reason is { } why ? $" ({why})" : string.Empty)}{(stop.Location is { } at ? $" at {at}" : string.Empty)} before it was done: "
            + "debug_state shows where, debug_continue lets it go on");
    }

    /// <summary>The element an input went to as the agent named it, or the window.</summary>
    private static string Named(AppNode? element) => element is null ? "the window" : AppQuery.Short(element.Element);

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

/// <summary>A failed binding as an agent reads it: the app (run, debug or, with several debugged, its Debug tab's name, an
/// AppHost service's name, or ide), Avalonia's level, the control, its property and the binding's path, the message, its place in the XAML
/// (path:line:column), how many times, and the nearest members.</summary>
public sealed record AppBindingError(string App, string Level, string Control, string Property, string Path, string Message, string? Place, int Count,
    IReadOnlyList<string> Nearest);

/// <summary>An element as app_details reads it, as the Live Visual Tree's details show it: what it is, where its XAML is (path:line:column)
/// or why none, the template it is a part of, its layout, its properties with their sources and its styles with why each applies or not.</summary>
public sealed record AppElementDetails(string Element, string Type, string FullType, string? Name, IReadOnlyList<string> Classes, IReadOnlyList<string> PseudoClasses,
    string? Xaml, string? NoXaml, string? TemplatedParent, AppLayout Layout, IReadOnlyList<AppProperty> Properties, IReadOnlyList<AppStyle> Styles);

/// <summary>Where an element is laid out: its bounds in its parent and in its window (x,y wxh), its desired size, its margin, border and
/// padding (left,top,right,bottom; none when it has no such property) and its alignment.</summary>
public sealed record AppLayout(string Bounds, string? InWindow, string Desired, string Margin, string? Border, string? Padding, string Alignment);

/// <summary>A property's value and type, its source (<see cref="ValueSources"/>) and what that is, where it is written, and whether a
/// property breakpoint (Break When Value Changes) is on it.</summary>
public sealed record AppProperty(string Name, string Value, string Type, string Source, string? Detail, string? Place, bool? Breakpoint);

/// <summary>A style that could apply to an element: its selector, kind, origin and place, whether it applies and why not, its setters.</summary>
public sealed record AppStyle(string Selector, string Kind, string Origin, string? Place, bool Applies, string? WhyNot, IReadOnlyList<AppSetter> Setters);

/// <summary>A style's setter: the property and value, whether it wins, is overridden (by what) or inactive, and where it is written.</summary>
public sealed record AppSetter(string Property, string Value, string State, string? By, string? Place);
