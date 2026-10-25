using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Inspector;
using Passenger.Core;

namespace Passenger.App.LiveTree;

/// <summary>What a window lends the overlays: its Run and Debug windows, its actions and the status bar.</summary>
public sealed record OverlaysParts(ToolViews Views, ActionRegistry Actions, Action<string> Say);

/// <summary>Avalonia's renderer debug overlays (its FPS counter, the dirty rectangles each frame redraws, and graphs of past
/// layout and render times) in an app Run, Debug or Profile launched with Passenger's inspector in it: the Run window's toggle
/// sets them in the app its console runs (Run's, else Profile's), the Debug window's in the app being debugged, and the Run
/// menu's in the debugged app, else the run one. The app's code is never changed; what is chosen last is the session's
/// launch option, which the next launch shows from its first window. A UI-thread object of the window.</summary>
public sealed class OverlaysCommands
{
    private static readonly IReadOnlyList<string> Ids = [AppActions.OverlayFps, AppActions.OverlayDirtyRects, AppActions.OverlayLayoutTimeGraph, AppActions.OverlayRenderTimeGraph];

    private readonly OverlaysParts parts;
    private ProjectSession? session;

    public OverlaysCommands(OverlaysParts parts)
    {
        this.parts = parts;
        OverlaysButton run = parts.Views.Run.Overlays;
        OverlaysButton debug = parts.Views.Debug.Overlays;
        run.Shown = () => RunApp()?.Overlays ?? [];
        debug.Shown = () => DebugApp()?.Overlays ?? [];
        run.Toggled += (_, name) => IdeErrors.Observe(ToggleAsync(RunApp(), name));
        debug.Toggled += (_, name) => IdeErrors.Observe(ToggleAsync(DebugApp(), name));
        for (int i = 0; i < OverlaysButton.All.Count; i++)
        {
            (string name, string title) = OverlaysButton.All[i];
            parts.Actions.Add(new IdeAction(Ids[i], $"Avalonia Debug Overlay: {title}", "Run", context => IdeErrors.Observe(ToggleAsync(Front(), name)))
            {
                TitleIn = _ => title,
                WhyNot = _ => Front() is null ? NoApp : null,
                IsChecked = _ => Front()?.Overlays.Contains(name) == true,
                Menus = [new MenuPlace("Run/" + MainMenu.AvaloniaOverlays, 0, i)],
            });
        }
    }

    private const string NoApp = "No Avalonia app with Passenger's inspector in it is running: Run, Debug or Profile one";

    /// <summary>The window follows a session from now: the apps its launches inspect, and when they come and go.</summary>
    public void Bind(ProjectSession next)
    {
        UiThread.Check();
        session = next;
        next.InspectedChanged += (_, _) =>
        {
            if (session == next)
            {
                Follow(next);
            }
        };
        Follow(next);
    }

    /// <summary>Each app the session's launches inspect shows its window's toggle while it is connected.</summary>
    private void Follow(ProjectSession followed)
    {
        foreach (LiveApp app in followed.Inspecting.Select(inspected => inspected.App))
        {
            app.StateChanged -= OnAppState;
            app.StateChanged += OnAppState;
        }
        Show();
    }

    private void OnAppState(object? sender, EventArgs e) => Show();

    private void Show()
    {
        parts.Views.Run.Overlays.IsVisible = RunApp() is not null;
        parts.Views.Debug.Overlays.IsVisible = DebugApp() is not null;
    }

    /// <summary>The Avalonia app the Run window's console runs, while its inspector answers: Run's, else Profile's (a WPF app
    /// has no renderer overlays).</summary>
    private LiveApp? RunApp() => session?.Inspecting.Where(a => a.Kind is RunningKind.Run or RunningKind.Profile).OrderBy(a => a.Kind)
        .Select(a => a.App).FirstOrDefault(app => app is { State: LiveAppState.Connected, Kind: LiveAppKind.Avalonia });

    /// <summary>The Avalonia app being debugged, while its inspector answers.</summary>
    private LiveApp? DebugApp() => session?.LiveApp is { State: LiveAppState.Connected, Kind: LiveAppKind.Avalonia } debugged ? debugged : null;

    /// <summary>The app the Run menu's toggles set: the debugged one, else the Run window's.</summary>
    private LiveApp? Front() => DebugApp() ?? RunApp();

    /// <summary>Turns an overlay on in an app, or off when it shows it; what the app shows then is the launch option from now.
    /// Null once the app has said so, else why not, which the status bar says too.</summary>
    public async Task<string?> ToggleAsync(LiveApp? app, string name)
    {
        UiThread.Check();
        if (app is null || session is not { } current)
        {
            parts.Say(NoApp);
            return NoApp;
        }
        HashSet<string> wanted = [.. app.Overlays];
        if (!wanted.Remove(name))
        {
            wanted.Add(name);
        }
        IReadOnlyList<string> overlays = [.. Wire.OverlayNames.Where(wanted.Contains)];
        current.Overlays = overlays;
        try
        {
            using IDisposable held = BackgroundWork.Scope();
            IReadOnlyList<AppWindow> windows = await app.SetOverlaysAsync(overlays);
            string titles = string.Join(", ", OverlaysButton.All.Where(o => overlays.Contains(o.Name)).Select(o => o.Title));
            parts.Say(overlays.Count == 0 ? $"{app.Name} shows no debug overlays" : $"{app.Name} shows {titles} on {Plural.Of(windows.Count(w => w.Owner is null), "window")}");
            return null;
        }
        catch (LiveAppException failed)
        {
            parts.Say(failed.Message);
            return failed.Message;
        }
    }
}
