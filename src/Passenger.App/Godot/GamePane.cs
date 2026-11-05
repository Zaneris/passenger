using Avalonia;
using Avalonia.Controls;
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
/// held over it, the game's own window kept on the pane's place on the screen as the pane moves, resizes, shows and hides,
/// and hidden while the main window is minimized (<see cref="GameWindows"/>). The game takes the keys as the person clicks
/// it or the pane takes them; Passenger takes them back as the person clicks anywhere in it, as the pane loses them, and on
/// the keys the game's helper hears for it (<see cref="KeysAsked"/>). On macOS a game draws in it as a layer instead
/// (<see cref="HoldLayer"/>). While it holds no game it says why.</summary>
public sealed partial class GamePane : Border, IOwnKeys
{
    /// <summary>How long a game's window is looked for once its process is known.</summary>
    private static readonly TimeSpan Finding = TimeSpan.FromSeconds(60);

    /// <summary>How long a game's window found is watched as its game starts, held on the pane's place again wherever Godot's
    /// own sizing as it starts moves it.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(10);

    private readonly TextBlock words;
    private readonly Panel content = new();
    private GameWindows? windows;
    private GodotGame? game;
    private nint window;
    private PixelRect? held;
    private bool shown;
    private TopLevel? top;

    public GamePane(string empty)
    {
        Focusable = true;
        words = new TextBlock { Classes = { StyleClasses.EmptyText }, Text = empty };
        content.Children.Add(words);
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
                Follow(force: true);
                if (IsFocused)
                {
                    GiveKeys();
                }
                await SettleAsync(started, found);
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

    /// <summary>Holds a game's window found on the pane's place again while its game starts, wherever the game's own sizing
    /// puts it (Godot sizes its window as it starts, after it has made it), saying so in the log the first time.</summary>
    private async Task SettleAsync(GodotGame started, nint found)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        bool told = false;
        while (game == started && window == found && System.Diagnostics.Stopwatch.GetElapsedTime(began) < Settling)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            if (game != started || window != found || windows is null || held is not { } place || !shown || windows.PlaceOf(found) is not { } at || at == place)
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
        words.Text = Refusals.GameWindowNeverCame(started.Name, errors.Length == 0 ? null : string.Join(" ", errors));
        words.IsVisible = true;
    }

    /// <summary>The game's window on the pane's place, shown while the pane is and the main window is not minimized.</summary>
    private void Follow(bool force = false)
    {
        if (window == 0 || windows is null || TopLevel.GetTopLevel(this) is not { } main)
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
        if (window != 0 && windows is not null && shown)
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
        game = null;
        window = 0;
        held = null;
        words.Text = Empty;
        words.IsVisible = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (window != 0 && windows is not null && held is { } place)
        {
            shown = false;
            windows.Hold(window, place, false);
        }
    }
}
