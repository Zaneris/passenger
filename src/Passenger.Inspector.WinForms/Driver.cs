using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using FormsKeys = System.Windows.Forms.Keys;

namespace Passenger.Inspector.WinForms;

/// <summary>An agent's input to a Windows Forms app, never the person's pointer or keyboard: the messages a mouse or keyboard
/// would bring, posted into the app's own queue for the window under the point or the control with the focus, so its message
/// loop's filters, shortcuts, default button and the controls' own handling all take them as a person's; a tool strip's item
/// is clicked through its own accessible action, as a screen reader's activation is. The modifiers held are the UI thread's
/// own key state while the messages run, which is what Windows Forms reads them from. A window not active is never activated:
/// the control given the focus there becomes its form's active control, and keys go to it.</summary>
internal sealed class Driver(Inspector inspector) : Driving<Component, Control>
{
    /// <summary>A wheel's notch, as Windows reports one.</summary>
    private const int Notch = 120;

    /// <summary>The control an agent gave the focus to in a window that was not active, which keys go to from then.</summary>
    private WeakReference<Control>? given;

    /// <summary>The last input's window, point, what took it and what it shows, read again once the app has taken the input.</summary>
    private (Control Top, Point? At, Component? Took, Component? Shows, bool Scrolled)? last;

    protected override string Hidden => "it or a control around it is hidden";

    protected override Component? Find(long id) => inspector.Find(id);

    protected override Control? OpenTop(Component element) => element is Control control && inspector.TopLevels().Contains(control) ? control : null;

    protected override Control? TopOf(Component element) =>
        OpenTop(element) ?? (Shown.TopOf(element) is { } top && inspector.TopLevels().Contains(top) ? top : null);

    protected override Box Client(Control top) => Shown.Boxed(Shown.ClientOf(top));

    protected override bool IsShown(Component element) => Shown.IsShown(element);

    protected override bool IsEnabled(Component element) => Shown.IsEnabled(element);

    protected override Box? BoxIn(Component element, Control top) => Shown.BoxIn(element, top) is { } box ? Shown.Boxed(box) : null;

    protected override Component? Parent(Component element) => Shown.Parent(element);

    protected override bool Clips(Component element) => element is Control;

    protected override Component? ScrollerAround(Component element)
    {
        for (Component? at = element; at is not null; at = Shown.Parent(at))
        {
            if (at is ScrollableControl { AutoScroll: true } or ListBox or ListView or TreeView or DataGridView or TextBoxBase { Multiline: true })
            {
                return at;
            }
        }
        return null;
    }

    protected override Component? Hit(Control top, double x, double y) => HitAt(top, Spot(x, y));

    protected override bool Within(Component element, Component ancestor) => Shown.Within(element, ancestor);

    protected override string Called(Component element) => Shown.Called(element);

    protected override string Named(Component element) => Inspector.Named(element);

    /// <summary>What a pointer at a point in a top level is over: its deepest control shown there, or a strip's item there.</summary>
    public static Component? HitAt(Control top, Point inTop)
    {
        Point screen = top.PointToScreen(inTop);
        Control at = top;
        while (at.GetChildAtPoint(at.PointToClient(screen), GetChildAtPointSkip.Invisible) is { } child && child != at)
        {
            at = child;
        }
        return at is ToolStrip strip && strip.GetItemAt(strip.PointToClient(screen)) is { } item ? item : at;
    }

    protected override InputDone Click(Control top, Component? element, InputAction action)
    {
        (double x, double y) = At(top, element, action, "click");
        Point point = Spot(x, y);
        Component? hit = HitAt(top, point);
        Component? took = Took(element, hit) ?? element;
        (int down, int up, int button) = action.Button switch
        {
            null or "left" => (Native.LeftDown, Native.LeftUp, Native.LeftHeld),
            "right" => (Native.RightDown, Native.RightUp, Native.RightHeld),
            "middle" => (Native.MiddleDown, Native.MiddleUp, Native.MiddleHeld),
            _ => throw new Refusal($"A button is left, right or middle, not '{action.Button}'"),
        };
        int count = Math.Clamp(action.Count ?? 1, 1, 3);
        Held held = Hold(action.Modifiers);
        Keyboard.Post(held, down: true);
        if (took is ToolStripItem item && button == Native.LeftHeld && count == 1 && held.Keys.Count == 0)
        {
            // An item has no window of its own: its accessible action, as a screen reader's, after what the queue holds.
            top.BeginInvoke(() => item.AccessibilityObject.DoDefaultAction());
        }
        else
        {
            Point screen = top.PointToScreen(point);
            nint window = Deepest((hit as Control ?? (hit as ToolStripItem)?.Owner ?? top).Handle, screen);
            nint spot = Native.Spot(Local(window, screen).X, Local(window, screen).Y);
            Native.PostMessage(window, Native.MouseMove, held.Mouse, spot);
            for (int i = 0; i < count; i++)
            {
                Native.PostMessage(window, i == 1 && down == Native.LeftDown ? Native.LeftDouble : down, held.Mouse | button, spot);
                Native.PostMessage(window, up, held.Mouse, spot);
            }
        }
        Keyboard.Post(held, down: false);
        last = (top, point, took, element ?? hit, false);
        return Done();
    }

    protected override InputDone Scroll(Control top, Component? element, InputAction action)
    {
        (double x, double y) = At(top, element, action, "scroll");
        Point point = Spot(x, y);
        Component? hit = HitAt(top, point) ?? element;
        Point screen = top.PointToScreen(point);
        nint window = Deepest((hit as Control ?? (hit as ToolStripItem)?.Owner ?? top).Handle, screen);
        nint at = Native.Spot(screen.X, screen.Y);
        foreach ((double notches, int message, int sign) in new[] { (action.DeltaY ?? 0, Native.Wheel, -1), (action.DeltaX ?? 0, Native.WheelAcross, 1) })
        {
            for (int i = 0; i < (int)Math.Round(Math.Abs(notches)); i++)
            {
                int delta = Math.Sign(notches) * sign * Notch;
                Native.PostMessage(window, message, (nint)(delta << 16), at);
            }
        }
        last = (top, point, Took(element, hit), hit, true);
        return Done();
    }

    protected override InputDone Type(Control top, Component? element, InputAction action)
    {
        Control target = KeyTarget(top, element);
        string text = action.Text ?? string.Empty;
        if (target is TextBoxBase box)
        {
            if (action.Replace == true)
            {
                box.SelectAll();
                if (text.Length == 0 && box.TextLength > 0)
                {
                    // Nothing to type over the text: it is deleted, as a person's Delete takes a selection.
                    Press(target, FormsKeys.Delete, alt: false);
                }
            }
            else if (element is not null)
            {
                // As a person who clicks after the text: what is typed goes at its end, not wherever the caret was left.
                box.Select(box.TextLength, 0);
            }
        }
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;
                case '\r' or '\n':
                    Press(target, FormsKeys.Enter, alt: false);
                    break;
                case '\t':
                    Press(target, FormsKeys.Tab, alt: false);
                    break;
                default:
                    Native.PostMessage(target.Handle, Native.Char, text[i], 1);
                    break;
            }
        }
        last = (top, null, target, target, false);
        return Done();
    }

    protected override InputDone Keys(Control top, Component? element, InputAction action)
    {
        Control target = KeyTarget(top, element);
        foreach (Chord chord in action.Keys ?? [])
        {
            if (!Enum.TryParse(chord.Key, ignoreCase: true, out FormsKeys key) || int.TryParse(chord.Key, out _) || key == FormsKeys.None)
            {
                throw new Refusal($"No key '{chord.Key}'");
            }
            Held held = Hold(chord.Modifiers);
            Keyboard.Post(held, down: true);
            Press(target, key, chord.Modifiers.Any(m => m.Held() == Modifier.Alt));
            Keyboard.Post(held, down: false);
        }
        last = (top, null, target, target, false);
        return Done();
    }

    /// <summary>A key's press and release, posted to a control's window; the app's message loop makes its character.</summary>
    private static void Press(Control target, FormsKeys key, bool alt)
    {
        int code = (int)(key & FormsKeys.KeyCode);
        Native.PostMessage(target.Handle, alt ? Native.SystemKeyDown : Native.KeyDown, code, Native.KeyWords(code, up: false, alt));
        Native.PostMessage(target.Handle, alt ? Native.SystemKeyUp : Native.KeyUp, code, Native.KeyWords(code, up: true, alt));
    }

    /// <summary>The control keys and text go to in a top level: the one named, given the focus first; else the one with the
    /// focus there, else the one an agent gave the focus to while its window was not active, else its form's active control.</summary>
    private Control KeyTarget(Control top, Component? element)
    {
        if (element is Control named)
        {
            Give(top, named);
            return named;
        }
        if (element is ToolStripItem { Owner: { } strip })
        {
            return strip;
        }
        if (Control.FromChildHandle(Native.GetFocus()) is { } focused && Shown.TopOf(focused) == top)
        {
            return focused;
        }
        if (given?.TryGetTarget(out Control? held) == true && Shown.TopOf(held) == top && held is { Visible: true, Enabled: true })
        {
            return held;
        }
        return (top as ContainerControl)?.ActiveControl ?? top;
    }

    /// <summary>Gives a control the focus as a click or Tab would where its window is the active one; else it becomes its
    /// form's active control and is held here for the keys that follow, as Windows would activate the window for the focus.</summary>
    private void Give(Control top, Control control)
    {
        if (Form.ActiveForm == top && control.CanFocus)
        {
            given = null;
            control.Focus();
            return;
        }
        if (top is ContainerControl container && control.CanSelect)
        {
            container.ActiveControl = control;
        }
        given = new WeakReference<Control>(control);
    }

    /// <summary>The modifiers named, to hold in the UI thread's key state (Windows Forms reads them from it as the messages run,
    /// <see cref="KeyStates"/>), with the state a mouse message says of them; nothing where none is named. Meta is refused: it
    /// opens the system's menu.</summary>
    private static Held Hold(IReadOnlyList<Modifier>? modifiers)
    {
        if (modifiers is not { Count: > 0 })
        {
            return new Held(0, []);
        }
        int mouse = 0;
        List<byte> keys = [];
        foreach (Modifier modifier in modifiers.Select(m => m.Held()))
        {
            (byte key, int flag) = modifier switch
            {
                Modifier.Shift => (Native.ShiftKey, Native.ShiftHeld),
                Modifier.Control => (Native.ControlKey, Native.ControlHeld),
                Modifier.Alt => (Native.AltKey, 0),
                _ => throw new Refusal("A Windows Forms app takes Control, Shift and Alt from Passenger, not the Windows key"),
            };
            mouse |= flag;
            keys.Add(key);
        }
        return new Held(mouse, keys);
    }

    /// <summary>Modifiers held for an input: as a mouse message's state says them, and the keys held down.</summary>
    private sealed record Held(int Mouse, IReadOnlyList<byte> Keys);

    /// <summary>The UI thread's window that holds and lets go of an input's modifiers, made on the first input.</summary>
    private KeyStates Keyboard => keyboard ??= new KeyStates();

    private KeyStates? keyboard;

    /// <summary>A message-only window of the inspector's that holds modifiers down in the UI thread's key state, and lets them
    /// go, as each message posted to it runs: posted among an input's messages, each chord's modifiers are held exactly while
    /// its own messages run, after the chord before let go of its own, whichever loop dispatches them (a modal dialog's too).</summary>
    private sealed class KeyStates : NativeWindow
    {
        /// <summary>The message that changes a key's state: the key, and whether it goes down.</summary>
        private const int Change = 0x8000 + 0x51;

        public KeyStates() => CreateHandle(new CreateParams { Parent = Native.MessageOnly });

        /// <summary>Holds an input's modifiers down, or lets them go, once the messages posted before have run.</summary>
        public void Post(Held held, bool down)
        {
            foreach (byte key in held.Keys)
            {
                Native.PostMessage(Handle, Change, key, down ? 1 : 0);
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg != Change)
            {
                base.WndProc(ref message);
                return;
            }
            Span<byte> state = stackalloc byte[256];
            Native.GetKeyboardState(state);
            byte key = (byte)message.WParam;
            state[key] = message.LParam != 0 ? (byte)(state[key] | Native.Down) : (byte)(state[key] & ~Native.Down);
            Native.SetKeyboardState(state);
        }
    }

    /// <summary>The deepest window shown at a point on the screen inside a window: its child windows', a native control's own
    /// (a combo box's edit box), where a mouse would send its messages.</summary>
    private static nint Deepest(nint window, Point screen)
    {
        for (int depth = 0; depth < Element.DeepestNesting; depth++)
        {
            Point inside = Local(window, screen);
            nint child = Native.ChildWindowFromPointEx(window, new Native.Point(inside.X, inside.Y), Native.SkipHiddenDisabled);
            if (child == 0 || child == window)
            {
                break;
            }
            window = child;
        }
        return window;
    }

    /// <summary>A point on the screen in a window's client area.</summary>
    private static Point Local(nint window, Point screen) =>
        Control.FromHandle(window) is { } control ? control.PointToClient(screen) : Native.ToClient(window, screen);

    private static Point Spot(double x, double y) => new((int)Math.Round(x), (int)Math.Round(y));

    /// <summary>The last input's answer as the app stands now.</summary>
    private InputDone Done() => Since(null);

    /// <summary>What the last input came to, read as the app stands now: once what it posted has run, what it set going.</summary>
    public InputDone Since(InputDone? said)
    {
        if (last is not { } input)
        {
            return said ?? throw new Refusal("No input was given");
        }
        (Control top, Point? at, Component? took, Component? shows, bool scrolled) = input;
        string? text = shows is null ? null : scrolled ? ScrolledOf(shows) : Shown.TextOf(shows);
        Control? focused = (top as ContainerControl)?.ActiveControl;
        return new InputDone(inspector.IdOf(top), at?.X, at?.Y, took is null ? null : inspector.IdOf(took), took is null ? null : Inspector.Named(took), text,
            focused is null ? null : Inspector.Named(focused));
    }

    /// <summary>What a scrolled control around a component shows: its offset in what it scrolls through.</summary>
    private string? ScrolledOf(Component component) => ScrollerAround(component) switch
    {
        ScrollableControl scroller => Words.Scrolled(Shown.Called(scroller), -scroller.AutoScrollPosition.X, -scroller.AutoScrollPosition.Y,
            scroller.DisplayRectangle.Width, scroller.DisplayRectangle.Height, scroller.ClientSize.Width, scroller.ClientSize.Height),
        ListBox list => $"{Shown.Called(list)} at item {list.TopIndex} of {list.Items.Count}",
        { } other => Shown.Called(other),
        null => Shown.TextOf(component),
    };
}
