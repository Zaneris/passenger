using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>The Game window's game on macOS, as Godot's editor holds one it embeds: drawn as the layer the game's embedded
/// display shares (<see cref="GameLayer"/>), told the display's state and then the size of its place (<see cref="Fitted"/>)
/// in pixels at the window's scale, and given what the person does over it, the pointer and the keys, in Godot's input codec
/// (<see cref="GodotInputCodec"/>) through its debugger's <c>embed:</c> messages, with the window's focus and the pointer's
/// coming and going. The mouse mode the game asks for is followed: the pointer hidden, and held at the pane's middle while
/// captured, its moves told as relative ones; Alt+Escape lets a captured pointer go, as in Godot's editor.</summary>
public sealed partial class GamePane
{
    private const int MouseEnter = 0;
    private const int MouseExit = 1;
    private const int FocusIn = 2;
    private const int FocusOut = 3;
    private const int ApplicationFocusIn = 2016;
    private const int ApplicationFocusOut = 2017;
    private const int Visible = 0;
    private const int Hidden = 1;
    private const int Captured = 2;
    private const int ConfinedHidden = 4;

    private GameLayer? layer;
    private bool layered;
    private (int Width, int Height) sentSize;
    private double toldScale;
    private Point? lastPointer;
    private int mouseMode;
    private double wheelX;
    private double wheelY;
    private Window? activated;

    /// <summary>Whether the pane's game draws in it as a layer (macOS's embedded display), not as a window of its own over it.</summary>
    public bool Layered => layered && game is not null;

    /// <summary>The keys Passenger keeps while the pane's layered game has the keys: those that take them back.</summary>
    public Func<KeyEventArgs, bool>? Keeps { get; set; }

    /// <summary>The mouse mode the layered game last asked for, as Godot numbers it.</summary>
    public int MouseMode => mouseMode;

    /// <summary>Holds a game started on macOS's embedded display at the pane's size: shown as its layer once the game tells
    /// the context it shares it through, until the game ends.</summary>
    public void HoldLayer(GodotGame started)
    {
        UiThread.Check();
        Let();
        game = started;
        layered = true;
        words.IsVisible = false;
        if (layer is null)
        {
            layer = new GameLayer();
            content.Children.Insert(0, layer);
        }
        layer.IsVisible = true;
        sentSize = default;
        started.StateChanged += GameStateChanged;
        started.EmbeddedContextTold += ContextTold;
        started.MouseModeAsked += MouseModeTold;
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            activated = window;
            window.Activated += WindowActivated;
            window.Deactivated += WindowDeactivated;
        }
        if (started.EmbeddedContext is not null)
        {
            ContextTold(started, EventArgs.Empty);
        }
    }

    /// <summary>The scale of the window the pane is in: every size the embedded display is told is in its pixels.</summary>
    private double Scale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void ContextTold(object? sender, EventArgs e)
    {
        if (sender != game || game is not { EmbeddedContext: { } shared } told || layer is null)
        {
            return;
        }
        IdeLog.Write(IdeLog.Ide, $"Game window: {told.Name}'s layer shared through context {shared}");
        toldScale = 0;
        SendSize();
        if (IsFocused)
        {
            told.Embed(GodotWords.EmbedWindowEvent, (long)FocusIn);
        }
    }

    /// <summary>What the embedded display is told, as Godot's editor tells it: its state at the window's scale as that comes
    /// or changes, then the size of the game's place in pixels at that scale as it changes, which the display divides by the
    /// scale it holds. The state's dots per inch are AppKit's 72 a point.</summary>
    private void SendSize()
    {
        if (!Layered || game is not { EmbeddedContext: { } shared } told || layer is null)
        {
            return;
        }
        double scale = Scale;
        if (scale != toldScale)
        {
            toldScale = scale;
            sentSize = default;
            layer.Show(shared, scale);
            told.Embed(GodotWords.EmbedState, GodotInputCodec.DisplayState((float)scale, (float)(72 * scale), (float)scale, layer.DisplayId()));
        }
        GamePlace place = Fitted(scale);
        layer.Fit(new Point(place.X / scale, place.Y / scale));
        if (place.Width > 0 && place.Height > 0 && (place.Width, place.Height) != sentSize)
        {
            sentSize = (place.Width, place.Height);
            told.Embed(GodotWords.EmbedWindowSize, new GodotMath(GodotType.Vector2I, [place.Width, place.Height]));
        }
    }

    private void LayerFocused(bool focused)
    {
        if (!Layered)
        {
            return;
        }
        game!.Embed(GodotWords.EmbedWindowEvent, (long)(focused ? FocusIn : FocusOut));
        // The pointer is the person's while the pane does not have the keys, as Godot's editor gives it back.
        FollowMouse(focused ? mouseMode : Visible);
    }

    private void WindowActivated(object? sender, EventArgs e) => game?.Embed(GodotWords.EmbedNotification, (long)ApplicationFocusIn);

    private void WindowDeactivated(object? sender, EventArgs e) => game?.Embed(GodotWords.EmbedNotification, (long)ApplicationFocusOut);

    private void MouseModeTold(object? sender, int mode)
    {
        if (sender != game)
        {
            return;
        }
        mouseMode = mode;
        if (IsFocused)
        {
            FollowMouse(mode);
        }
    }

    /// <summary>The pointer as a mouse mode has it over the pane: hidden for hidden, captured and confined-hidden, held at the
    /// middle while captured.</summary>
    private void FollowMouse(int mode)
    {
        Cursor = mode is Hidden or Captured or ConfinedHidden ? new Cursor(StandardCursorType.None) : null;
        if (mode == Captured)
        {
            Recenter();
        }
    }

    /// <summary>The pointer put back at the pane's middle, where a captured pointer's next move is measured from.</summary>
    private void Recenter()
    {
        if (TopLevel.GetTopLevel(this) is not { } main)
        {
            return;
        }
        Point middle = new(Bounds.Width / 2, Bounds.Height / 2);
        GameLayer.Warp(this.PointToScreen(middle), main.RenderScaling);
        lastPointer = middle;
    }

    public bool TakesKey(KeyEventArgs e) => Layered && IsFocused && Keeps?.Invoke(e) != true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Layered || e.Handled)
        {
            return;
        }
        if (mouseMode == Captured && e.Key == Key.Escape && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Godot's editor's Release Mouse: the game told it is visible again.
            mouseMode = Visible;
            FollowMouse(Visible);
            game!.Embed(GodotWords.EmbedMouseMode, (long)Visible);
            e.Handled = true;
            return;
        }
        SendKey(e, pressed: true);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (Layered && !e.Handled)
        {
            SendKey(e, pressed: false);
            e.Handled = true;
        }
    }

    private void SendKey(KeyEventArgs e, bool pressed)
    {
        (uint code, byte location) = GodotInputCodec.KeyOf(e.Key.ToString());
        (uint physical, byte side) = GodotInputCodec.KeyOf(e.PhysicalKey.ToString());
        uint unicode = pressed && e.KeySymbol is { Length: > 0 } symbol && !char.IsControl(symbol[0]) ? (uint)char.ConvertToUtf32(symbol, 0) : 0;
        game!.Embed(GodotWords.EmbedEvent, GodotInputCodec.Key(ModifiersOf(e.KeyModifiers), pressed, echo: false, code, physical, code, unicode, location != 0 ? location : side));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (Layered)
        {
            game!.Embed(GodotWords.EmbedWindowEvent, (long)MouseEnter);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (Layered)
        {
            game!.Embed(GodotWords.EmbedWindowEvent, (long)MouseExit);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Layered)
        {
            return;
        }
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        if (GameFit.Over(point.Position.X, point.Position.Y, Fitted(Scale), Scale))
        {
            SendButton(point, e.KeyModifiers, pressed: true, e.ClickCount == 2);
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!Layered)
        {
            return;
        }
        SendButton(e.GetCurrentPoint(this), e.KeyModifiers, pressed: false, doubleClick: false);
        e.Handled = true;
    }

    private void SendButton(PointerPoint point, KeyModifiers modifiers, bool pressed, bool doubleClick)
    {
        int button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 3,
            PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => 8,
            PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => 9,
            _ => 0,
        };
        if (button == 0)
        {
            return;
        }
        (float x, float y) = Pixels(point.Position);
        game!.Embed(GodotWords.EmbedEvent, GodotInputCodec.MouseButton(ModifiersOf(modifiers), pressed, doubleClick, (byte)button, x, y, MaskOf(point.Properties)));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!Layered)
        {
            return;
        }
        PointerPoint point = e.GetCurrentPoint(this);
        Point at = point.Position;
        Point from = lastPointer ?? at;
        double scale = PointerScale;
        (float x, float y) = Pixels(mouseMode == Captured && IsFocused ? from : at);
        game!.Embed(GodotWords.EmbedEvent, GodotInputCodec.MouseMotion(ModifiersOf(e.KeyModifiers), x, y,
            (float)((at.X - from.X) * scale), (float)((at.Y - from.Y) * scale), MaskOf(point.Properties)));
        lastPointer = at;
        if (mouseMode == Captured && IsFocused)
        {
            Recenter();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!Layered)
        {
            return;
        }
        PointerPoint point = e.GetCurrentPoint(this);
        (float x, float y) = Pixels(point.Position);
        // Each whole step of the wheel a press and release of its button, as Godot's own takes a wheel.
        wheelY += e.Delta.Y;
        wheelX += e.Delta.X;
        for (; Math.Abs(wheelY) >= 1; wheelY -= Math.Sign(wheelY))
        {
            Wheel(wheelY > 0 ? 4 : 5);
        }
        for (; Math.Abs(wheelX) >= 1; wheelX -= Math.Sign(wheelX))
        {
            Wheel(wheelX > 0 ? 6 : 7);
        }
        e.Handled = true;

        void Wheel(int button)
        {
            GodotModifiers modifiers = ModifiersOf(e.KeyModifiers);
            byte held = MaskOf(point.Properties);
            game!.Embed(GodotWords.EmbedEvent, GodotInputCodec.MouseButton(modifiers, pressed: true, doubleClick: false, (byte)button, x, y, held));
            game.Embed(GodotWords.EmbedEvent, GodotInputCodec.MouseButton(modifiers, pressed: false, doubleClick: false, (byte)button, x, y, held));
        }
    }

    /// <summary>A place over the pane as the game's display takes it, from the game's top left and inside it.</summary>
    private (float X, float Y) Pixels(Point at)
    {
        (double x, double y) = GameFit.Inside(at.X, at.Y, Fitted(Scale), Scale);
        return ((float)(x * PointerScale), (float)(y * PointerScale));
    }

    /// <summary>What the display takes the pointer in, for each of the pane's logical units: points, which is 1, from Godot
    /// 4.7, before that its window's pixels.</summary>
    private double PointerScale => GodotExecutables.TakesPointerInPoints(game?.Version) ? 1 : Scale;

    private static GodotModifiers ModifiersOf(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Shift) ? GodotModifiers.Shift : GodotModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Control) ? GodotModifiers.Ctrl : GodotModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Alt) ? GodotModifiers.Alt : GodotModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Meta) ? GodotModifiers.Meta : GodotModifiers.None);

    private static byte MaskOf(PointerPointProperties held) => (byte)((held.IsLeftButtonPressed ? GodotInputCodec.MaskOf(1) : 0)
        | (held.IsRightButtonPressed ? GodotInputCodec.MaskOf(2) : 0) | (held.IsMiddleButtonPressed ? GodotInputCodec.MaskOf(3) : 0)
        | (held.IsXButton1Pressed ? GodotInputCodec.MaskOf(8) : 0));

    /// <summary>Lets go of a layered game: its layer shown no more, the pointer the person's again.</summary>
    private void LetLayer(GodotGame was)
    {
        was.EmbeddedContextTold -= ContextTold;
        was.MouseModeAsked -= MouseModeTold;
        if (activated is { } window)
        {
            window.Activated -= WindowActivated;
            window.Deactivated -= WindowDeactivated;
            activated = null;
        }
        if (layer is not null)
        {
            layer.Show(0, 1);
            layer.IsVisible = false;
        }
        layered = false;
        mouseMode = Visible;
        Cursor = null;
        lastPointer = null;
    }
}
