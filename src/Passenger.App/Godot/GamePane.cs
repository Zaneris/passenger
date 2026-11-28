using Avalonia;
using Avalonia.Controls;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>How the Game window holds a game: not at all, the game's own window held over it, or its layer drawn in it.</summary>
public enum GamePaneHolds
{
    Nothing,
    Window,
    Layer,
}

/// <summary>The Game window: a Godot game whose configuration draws it in Passenger (<see cref="Run.RunConfiguration.GodotInPane"/>)
/// held over it, the game's own window kept on its place in the pane (<see cref="GameFit"/>) as the pane moves, resizes,
/// shows and hides, and hidden while the main window is minimized (<see cref="GameWindows"/>). The game takes the keys as the person clicks
/// it or the pane takes them; Passenger takes them back as the person clicks anywhere in it, as the pane loses them, and on
/// the keys the game's helper hears for it (<see cref="KeysAsked"/>). On macOS a game draws in it as a layer instead
/// (<see cref="HoldLayer"/>). While it holds no game it says why.</summary>
public sealed partial class GamePane : Border, IOwnKeys
{
    /// <summary>How long a game's window is looked for once its process is known.</summary>
    private static readonly TimeSpan Finding = TimeSpan.FromSeconds(60);

    /// <summary>How long a game's window found is watched closely as its game starts, Godot's own sizing moving it.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(10);

    private readonly EmptyState words;
    private readonly Panel content = new();
    private GameWindows? windows;
    private GodotGame? game;
    private nint window;
    private PixelRect? held;
    private bool shown;
    private TopLevel? top;

    /// <summary>The debugger's session holding the game stopped, while one does: its window let go of meanwhile
    /// (<see cref="GameWindows.Disown"/>), neither held on its place nor given the keys.</summary>
    private Debugging.DebugSession? stoppedBy;

    /// <summary>The wait for a game run on after a stop to take its window back (<see cref="GameWindows.Return"/>), while one
    /// waits: its window still let go of, neither held on its place nor given the keys.</summary>
    private CancellationTokenSource? returning;

    /// <summary>What the pane shows of a game stopped in the debugger: its last picture under a banner saying so.</summary>
    private readonly DockPanel stoppedView = new() { IsVisible = false };
    private readonly Banner stoppedBanner = Banner.Wrapping();
    private readonly Image stoppedPicture = new() { Stretch = Avalonia.Media.Stretch.Uniform };

    public GamePane(string empty)
    {
        Focusable = true;
        // A control draws nothing without one, so the pointer over the game would pass through to what lies under the pane.
        Background = Avalonia.Media.Brushes.Transparent;
        words = new EmptyState { Text = empty };
        content.Children.Add(words);
        stoppedView.Children.Add(stoppedBanner);
        stoppedView.Children.Add(stoppedPicture);
        content.Children.Add(stoppedView);
        Child = content;
        Empty = empty;
        LayoutUpdated += (_, _) =>
        {
            Follow();
            SendSize();
        };
        GotFocus += (_, _) =>
        {
            GiveKeys();
            LayerFocused(true);
        };
        LostFocus += (_, _) =>
        {
            TakeKeysBack();
            LayerFocused(false);
        };
    }

    /// <summary>What it says while it holds no game and none was refused it.</summary>
    public string Empty { get; set; }

    /// <summary>What it says now, while it holds no game; null while one draws over it.</summary>
    public string? Said => words.IsVisible ? words.Text : null;

    /// <summary>The game drawn over it, or null.</summary>
    public GodotGame? Game => game;

    /// <summary>The game's window held over it, once found; 0 before.</summary>
    public nint GameWindow => window;

    /// <summary>The embedding it holds a game through, made from its window as one is first asked for; null where its window
    /// has none (one nobody sees, macOS, Wayland without X11).</summary>
    public GameWindows? Windows(TopLevel main) => windows ??= GameWindows.For(main);

    /// <summary>The game's helper heard the keys that hand the keys back, or the pane gave them back: Passenger takes them.</summary>
    public event EventHandler? KeysAsked;

    /// <summary>Holds a game started to draw over it: its window looked for by the game's process as the game tells it,
    /// then kept on the pane's place until the game ends.</summary>
    public void Hold(GodotGame started)
    {
        UiThread.Check();
        Let();
        game = started;
        words.IsVisible = false;
        started.StateChanged += GameStateChanged;
        started.KeysAsked += GameAskedKeys;
        IdeErrors.Observe(FindAsync(started));
    }

    /// <summary>Says why a game draws in a window of its own, where one asked for this was refused it.</summary>
    public void Refused(string why)
    {
        UiThread.Check();
        if (game is null)
        {
            words.Text = why;
            words.IsVisible = true;
        }
    }

    private async Task FindAsync(GodotGame started)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        while (game == started && window == 0 && started.State != LiveTree.LiveAppState.Ended
            && System.Diagnostics.Stopwatch.GetElapsedTime(began) < Finding)
        {
            if (started.ProcessId is { } process && windows is not null && windows.Find(process) is var found and not 0)
            {
                IdeLog.Write(IdeLog.Ide, $"Game window: {started.Name}'s window 0x{found:X} found, {windows.Seen}");
                window = found;
                if (started.Debugged is { State: Debugging.DebugState.Paused } stopped)
                {
                    DebuggerStopped(stopped);
                }
                Follow(force: true);
                if (IsFocused)
                {
                    GiveKeys();
                }
                await KeepHeldAsync(started, found);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        if (game == started && window == 0)
        {
            IdeLog.Write(IdeLog.Ide, $"Game window: {started.Name}'s window never came: {windows?.Seen}");
            NeverCame(started);
        }
    }

    /// <summary>Holds a game's window found on its place again wherever it goes while its game runs, so it never covers what
    /// lies outside the pane: often while it starts, as Godot sizes its window after making it, then each second, as a slow
    /// start or the game's own code may move it later. Says so in the log the first time.</summary>
    private async Task KeepHeldAsync(GodotGame started, nint found)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        bool told = false;
        while (game == started && window == found)
        {
            await Task.Delay(System.Diagnostics.Stopwatch.GetElapsedTime(began) < Settling ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1));
            if (game != started || window != found || windows is null || LetGo || held is not { } place || !shown || windows.PlaceOf(found) is not { } at || at == place)
            {
                continue;
            }
            if (!told)
            {
                IdeLog.Write(IdeLog.Ide, $"Game window: {started.Name}'s window is {at.Width}x{at.Height} at {at.X},{at.Y}; held on {place.Width}x{place.Height} at {place.X},{place.Y} again");
                told = true;
            }
            windows.Hold(found, place, shown: true);
        }
    }

    /// <summary>The size the game's window opens at, whose shape it shows in here: the whole pane while none is known.</summary>
    public (int Width, int Height) OwnSize { get; set; }

    /// <summary>The game's place on the screen in its pixels, which its window is held on; null while the pane is laid out in
    /// no window or has no size.</summary>
    public PixelRect? ScreenPlace()
    {
        if (TopLevel.GetTopLevel(this) is not { } main || !IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return null;
        }
        return OnScreen(main.RenderScaling);
    }

    /// <summary>The game's place over the pane at a scale, in pixels from the pane's top left.</summary>
    private GamePlace Fitted(double scale) => GameFit.Within(((int)Math.Round(Bounds.Width * scale), (int)Math.Round(Bounds.Height * scale)), OwnSize);

    private PixelRect OnScreen(double scale)
    {
        GamePlace fitted = Fitted(scale);
        PixelPoint pane = this.PointToScreen(new Point(0, 0));
        return new PixelRect(pane.X + fitted.X, pane.Y + fitted.Y, fitted.Width, fitted.Height);
    }

    /// <summary>Says a game's window never came, in Godot's own words where its console has errors: a renderer that could
    /// not start says why only there, and in a dialog of its own.</summary>
    private void NeverCame(GodotGame started)
    {
        string[] errors = [.. (started.Console?.Entries ?? []).Where(e => !e.Warning).TakeLast(2).Select(e => e.Header)];
        words.Text = Refusals.GameWindowNeverCame(started.Name, errors.Length == 0 ? null : string.Join(" ", errors)).Shown;
        words.IsVisible = true;
    }

    /// <summary>The game's window on its place, shown while the pane is and the main window is not minimized.</summary>
    private void Follow(bool force = false)
    {
        if (window == 0 || windows is null || LetGo || TopLevel.GetTopLevel(this) is not { } main)
        {
            return;
        }
        if (!ReferenceEquals(top, main))
        {
            if (top is Window before)
            {
                before.PositionChanged -= Moved;
                before.PropertyChanged -= MainChanged;
            }
            top = main;
            if (main is Window now)
            {
                now.PositionChanged += Moved;
                now.PropertyChanged += MainChanged;
            }
        }
        PixelRect place = OnScreen(main.RenderScaling);
        bool showing = IsEffectivelyVisible && place.Width > 0 && place.Height > 0 && main is not Window { WindowState: WindowState.Minimized };
        if (force || held != place || shown != showing)
        {
            held = place;
            shown = showing;
            windows.Hold(window, place, showing);
        }
    }

    private void Moved(object? sender, PixelPointEventArgs e) => Follow();

    private void MainChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            Follow();
        }
    }

    /// <summary>The game's window given the keys: the pane took them (its action, a click on its tab); a layered game's are
    /// the pane's own.</summary>
    public void GiveKeys()
    {
        if (Layered && !IsFocused)
        {
            Focus();
            return;
        }
        if (window != 0 && windows is not null && shown && !LetGo)
        {
            windows.Focus(window);
        }
    }

    /// <summary>The pane lost the keys within Passenger while the game held them: Passenger takes them back.</summary>
    private void TakeKeysBack()
    {
        if (window != 0 && windows is not null && windows.HasKeys(window))
        {
            KeysAsked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void GameAskedKeys(object? sender, EventArgs e) => UiThread.Post(() => KeysAsked?.Invoke(this, EventArgs.Empty));

    /// <summary>A program the debugger debugs stopped: where it is the game held here, every thread of it held by .NET's
    /// debugger, its window is let go of until it runs on, so the main window's moves among the windows (called in front at
    /// the stop, a click on it) wait for no answer from it, the pane showing its last picture, read from the screen, under a
    /// banner saying it is stopped. Told before anything at the stop moves the main window.</summary>
    public void DebuggerStopped(Debugging.DebugSession session)
    {
        UiThread.Check();
        if (stoppedBy is not null || window == 0 || windows is null || game is not { } stopped || session.Engine != Debugging.DebugEngine.Managed
            || !(session == stopped.Debugged || (stopped.ProcessId is int id && session.ProcessStarted is { IsCompletedSuccessfully: true } started && started.Result == id)))
        {
            return;
        }
        stoppedBy = session;
        if (returning is { } back)
        {
            // Stopped again before its window came back: still let go of, its picture the one kept.
            returning = null;
            back.Cancel();
        }
        else
        {
            // Read from the screen before the window is let go of, while it still shows over the pane's place.
            GamePicture? last = shown && held is { } place ? windows.Picture(place) : null;
            windows.Disown(window);
            ShowStopped(stopped.Name, last);
        }
        session.StateChanged += RunsOn;
    }

    /// <summary>Whether the game's window is let go of: its game stopped in the debugger, or run on and not yet back.</summary>
    private bool LetGo => stoppedBy is not null || returning is not null;

    /// <summary>The game's last picture over its place in the pane, under a banner saying it is stopped in the debugger.</summary>
    private void ShowStopped(string name, GamePicture? last)
    {
        Avalonia.Media.Imaging.Bitmap? was = stoppedPicture.Source as Avalonia.Media.Imaging.Bitmap;
        stoppedPicture.Source = last is null ? null : Bitmap(last);
        was?.Dispose();
        stoppedBanner.Say($"{name} is stopped in the debugger, as it last showed here, and comes back on Continue.");
        stoppedView.IsVisible = true;
    }

    /// <summary>The pane without a stopped game's picture and banner, its picture let go of.</summary>
    private void HideStopped()
    {
        stoppedView.IsVisible = false;
        stoppedBanner.Say(null);
        (stoppedPicture.Source as Avalonia.Media.Imaging.Bitmap)?.Dispose();
        stoppedPicture.Source = null;
    }

    /// <summary>A picture of the screen as a bitmap the pane draws fitted to its place.</summary>
    private static unsafe Avalonia.Media.Imaging.Bitmap Bitmap(GamePicture picture)
    {
        fixed (byte* pixels = picture.Bgra)
        {
            return new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque, (nint)pixels,
                new PixelSize(picture.Width, picture.Height), new Vector(96, 96), picture.Width * 4);
        }
    }

    /// <summary>The stopped game's debugger let it run on, or ended: its window owned again before it runs.</summary>
    private void RunsOn(object? sender, EventArgs e)
    {
        if (sender is Debugging.DebugSession { State: not Debugging.DebugState.Paused } session && session == stoppedBy)
        {
            TakeBack();
        }
    }

    /// <summary>The game's window, let go of while it was stopped, taken back once the game runs on (<see cref="ReturnAsync"/>).</summary>
    private void TakeBack()
    {
        if (stoppedBy is not { } session)
        {
            return;
        }
        session.StateChanged -= RunsOn;
        stoppedBy = null;
        if (window == 0 || windows is not { } embedding)
        {
            HideStopped();
            return;
        }
        CancellationTokenSource back = new();
        returning = back;
        IdeErrors.Observe(ReturnAsync(embedding, window, back));
    }

    /// <summary>The game's window owned again and in front of the main window once the game answers, waited for on a thread
    /// apart from the window's, which the game's thread would hold, then held on its place in place of its picture; a game
    /// stopped again meanwhile keeps its window let go of and its picture.</summary>
    private async Task ReturnAsync(GameWindows embedding, nint taken, CancellationTokenSource back)
    {
        try
        {
            // Unheld: it waits for the game to answer, which no wait for the IDE's quiet waits for.
            bool returned = await BackgroundWork.Unheld(() => embedding.Return(taken, back.Token));
            if (returning != back)
            {
                return;
            }
            returning = null;
            HideStopped();
            if (returned && window == taken)
            {
                if (TopLevel.GetTopLevel(this) is null && held is { } place)
                {
                    shown = false;
                    embedding.Hold(taken, place, false);
                }
                Follow(force: true);
            }
        }
        finally
        {
            back.Dispose();
        }
    }

    private void GameStateChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        if (sender == game && game is { State: LiveTree.LiveAppState.Ended } ended)
        {
            bool came = window != 0 || layered && ended.EmbeddedContext is not null;
            Let();
            if (!came)
            {
                NeverCame(ended);
            }
        }
    });

    /// <summary>Lets go of the game held, if one is: its window no longer followed, the pane saying what it holds none.</summary>
    private void Let()
    {
        if (game is { } was)
        {
            was.StateChanged -= GameStateChanged;
            was.KeysAsked -= GameAskedKeys;
            LetLayer(was);
        }
        if (stoppedBy is { } stopping)
        {
            stopping.StateChanged -= RunsOn;
            stoppedBy = null;
        }
        if (returning is { } back)
        {
            returning = null;
            back.Cancel();
        }
        HideStopped();
        game = null;
        window = 0;
        held = null;
        words.Text = Empty;
        words.IsVisible = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // A window let go of is hidden as it comes back (ReturnAsync).
        if (window != 0 && windows is not null && !LetGo && held is { } place)
        {
            shown = false;
            windows.Hold(window, place, false);
        }
    }
}
