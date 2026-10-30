using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;

namespace Passenger.Inspector;

/// <summary>An agent's clicks, typing, keys and scrolling, given to a window as its platform gives a person's: raw input
/// into the window's own way in (what <c>ITopLevelImpl.Input</c> calls), so Avalonia hit tests, routes, tracks the pointer
/// over, focuses, recognises gestures and runs key bindings and commands as it would. The pointer is a mouse of the
/// inspector's own, apart from the person's (whose real pointer never moves), and nothing activates the window, so an app on
/// the screen never takes the person's focus; keys go to the element given the keyboard focus, active window or not.</summary>
internal sealed class Driver(Inspector inspector) : Driving<Visual, TopLevel>
{
    /// <summary>The window's input root, which raw input names and Avalonia 12 keeps internal (its presentation source).</summary>
    private static readonly PropertyInfo? InputRoot = typeof(TopLevel).GetProperty("InputRoot", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>The physical key each key is on, on a QWERTY keyboard.</summary>
    private static readonly Dictionary<Key, PhysicalKey> Physical = PhysicalKeys();

    private readonly MouseDevice mouse = new(new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true));
    private readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>Moved on a second a click, so a click is never the second of a double click with the one before it.</summary>
    private ulong apart;

    private ulong Now => (ulong)clock.ElapsedMilliseconds + apart;

    protected override string Hidden => "IsVisible is false on it or an ancestor";

    protected override Visual? Find(long id) => inspector.Find(id);

    protected override TopLevel? OpenTop(Visual element) => element as TopLevel;

    protected override TopLevel? TopOf(Visual element) => element as TopLevel ?? TopLevel.GetTopLevel(element);

    protected override void Ready(TopLevel top)
    {
        if (top.PlatformImpl is null)
        {
            throw new Refusal("That window has closed");
        }
        // Before anything is focused there; keys going to a focus elsewhere are weighed as each is sent.
        Refuse(top, keys: false);
    }

    protected override InputDone Click(TopLevel top, Visual? element, InputAction action)
    {
        Point at = Aim(top, element, action, "click");
        RawInputModifiers held = Held(action.Modifiers);
        (RawPointerEventType down, RawPointerEventType up) = action.Button switch
        {
            null or "left" => (RawPointerEventType.LeftButtonDown, RawPointerEventType.LeftButtonUp),
            "right" => (RawPointerEventType.RightButtonDown, RawPointerEventType.RightButtonUp),
            "middle" => (RawPointerEventType.MiddleButtonDown, RawPointerEventType.MiddleButtonUp),
            _ => throw new Refusal($"A button is left, right or middle, not '{action.Button}'"),
        };
        Visual? hit = Hit(top, at.X, at.Y);
        IInputRoot root = Root(top);
        apart += 1000;
        Send(top, new RawPointerEventArgs(mouse, Now, root, RawPointerEventType.Move, at, held));
        // A press that closes the window (a button that acts on press) leaves nothing to release.
        for (int i = 0; i < Math.Clamp(action.Count ?? 1, 1, 3) && top.PlatformImpl is not null; i++)
        {
            Send(top, new RawPointerEventArgs(mouse, Now, root, down, at, held));
            if (top.PlatformImpl is not null)
            {
                Send(top, new RawPointerEventArgs(mouse, Now, root, up, at, held));
            }
        }
        return Done(top, at, Took(element, hit), Shown.TextOf(element ?? hit ?? top));
    }

    protected override InputDone Scroll(TopLevel top, Visual? element, InputAction action)
    {
        Point at = Aim(top, element, action, "scroll");
        Visual? hit = Hit(top, at.X, at.Y);
        IInputRoot root = Root(top);
        RawInputModifiers held = Held(action.Modifiers);
        Send(top, new RawPointerEventArgs(mouse, Now, root, RawPointerEventType.Move, at, held));
        // Avalonia's wheel turns up and left with positive deltas, the way the content moves.
        Send(top, new RawMouseWheelEventArgs(mouse, Now, root, at, new Vector(-(action.DeltaX ?? 0), -(action.DeltaY ?? 0)), held));
        return Done(top, at, Took(element, hit), Shown.ScrolledOf(hit ?? element));
    }

    protected override InputDone Type(TopLevel top, Visual? element, InputAction action)
    {
        (Visual? focused, bool gave) = Focus(element);
        if (action.Replace == true)
        {
            Press(top, Hotkeys.SelectAll);
        }
        else if (gave && focused is TextBox)
        {
            // As a person who clicks after the text: what is typed goes at its end, not wherever the caret was left.
            Press(top, Hotkeys.End);
        }
        TextElementEnumerator characters = StringInfo.GetTextElementEnumerator(action.Text ?? string.Empty);
        while (characters.MoveNext())
        {
            string character = characters.GetTextElement();
            switch (character)
            {
                case "\n" or "\r\n" or "\r":
                    Press(top, (Key.Enter, RawInputModifiers.None));
                    break;
                case "\t":
                    Press(top, (Key.Tab, RawInputModifiers.None));
                    break;
                default:
                    if (KeyOf(character) is { } key)
                    {
                        Press(top, key, character);
                    }
                    else if (top.PlatformImpl is not null)
                    {
                        Send(top, new RawTextInputEventArgs(Keyboard(), Now, Root(top), character));
                    }
                    break;
            }
        }
        return Into(top, focused);
    }

    protected override InputDone Keys(TopLevel top, Visual? element, InputAction action)
    {
        (Visual? focused, _) = Focus(element);
        foreach (Chord chord in action.Keys ?? [])
        {
            if (!Enum.TryParse(chord.Key, ignoreCase: true, out Key key) || int.TryParse(chord.Key, out _))
            {
                throw new Refusal($"No key '{chord.Key}'");
            }
            RawInputModifiers held = Held(chord.Modifiers);
            string? symbol = (held & (RawInputModifiers.Control | RawInputModifiers.Alt | RawInputModifiers.Meta)) == 0
                && Physical.TryGetValue(key, out PhysicalKey physical) && physical.ToQwertyKeySymbol((held & RawInputModifiers.Shift) != 0) is { } said
                && said.All(c => c >= ' ' && c != '\u007f')
                    ? said
                    : null;
            Press(top, (key, held), symbol);
        }
        return Into(top, focused);
    }

    /// <summary>What keys went to (whatever since has the focus, a Tab having moved it) and what it shows now.</summary>
    private InputDone Into(TopLevel top, Visual? focused) => Done(top, null, focused, focused is null ? null : Shown.TextOf(focused));

    /// <summary>A key down and up, and between them the text it types when nothing took the key itself; nothing more once
    /// the key has closed its window (Enter on a dialog), as its platform sends a closed window nothing.</summary>
    private void Press(TopLevel top, (Key Key, RawInputModifiers Held) key, string? text = null)
    {
        if (top.PlatformImpl is null)
        {
            return;
        }
        IKeyboardDevice keyboard = Keyboard();
        IInputRoot root = Root(top);
        PhysicalKey physical = Physical.GetValueOrDefault(key.Key);
        RawKeyEventArgs down = new(keyboard, Now, root, RawKeyEventType.KeyDown, key.Key, key.Held, physical, text);
        Send(top, down);
        if (text is not null && !down.Handled && top.PlatformImpl is not null)
        {
            Send(top, new RawTextInputEventArgs(keyboard, Now, root, text));
        }
        if (top.PlatformImpl is not null)
        {
            Send(top, new RawKeyEventArgs(keyboard, Now, root, RawKeyEventType.KeyUp, key.Key, key.Held, physical, text));
        }
    }

    /// <summary>The key a typed character is on, with Shift for a capital: letters, digits and the space bar; others are
    /// typed as text alone, as an input method gives them.</summary>
    private static (Key, RawInputModifiers)? KeyOf(string character) => character switch
    {
        [char c] when c is >= 'a' and <= 'z' => (Key.A + (c - 'a'), RawInputModifiers.None),
        [char c] when c is >= 'A' and <= 'Z' => (Key.A + (c - 'A'), RawInputModifiers.Shift),
        [char c] when c is >= '0' and <= '9' => (Key.D0 + (c - '0'), RawInputModifiers.None),
        " " => (Key.Space, RawInputModifiers.None),
        _ => null,
    };

    /// <summary>Gives an element the keyboard focus, as Tab or a click would, unless it or something inside it has it;
    /// with none, whatever has the focus keeps it. Says what has it, and whether this gave it.</summary>
    private (Visual? Focused, bool Gave) Focus(Visual? element)
    {
        if (element is null or TopLevel)
        {
            return (Shown.Focused() as Visual, false);
        }
        if (element is not InputElement input)
        {
            throw new Refusal($"{Shown.Called(element)} takes no keys");
        }
        if (input.IsKeyboardFocusWithin)
        {
            return (Shown.Focused() as Visual, false);
        }
        if (!input.IsEffectivelyVisible)
        {
            throw new Refusal($"{Shown.Called(element)} is not shown ({Hidden})");
        }
        InputElement? taking = input.Focusable && input.IsEffectivelyEnabled
            ? input
            : input.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(d => d.Focusable && d.IsEffectivelyEnabled && d.IsEffectivelyVisible);
        if (taking is null || !taking.Focus(NavigationMethod.Pointer))
        {
            throw new Refusal(input.IsEffectivelyEnabled
                ? $"{Shown.Called(element)} cannot take the keyboard focus (Focusable is false, and nothing inside it can)"
                : $"{Shown.Called(element)} is disabled: it takes no keys");
        }
        return (taking, true);
    }

    /// <summary>Where the pointer acts (<see cref="Driving{T, TTop}.At"/>), as Avalonia's point.</summary>
    private Point Aim(TopLevel top, Visual? element, InputAction action, string doing)
    {
        (double x, double y) = At(top, element, action, doing);
        return new Point(x, y);
    }

    protected override Box Client(TopLevel top) => new(0, 0, top.ClientSize.Width, top.ClientSize.Height);

    protected override bool IsShown(Visual element) => element.IsEffectivelyVisible;

    protected override bool IsEnabled(Visual element) => element is not InputElement { IsEffectivelyEnabled: false };

    protected override Box? BoxIn(Visual element, TopLevel top) => Shown.RectIn(element, top) is { } box ? new Box(box.X, box.Y, box.Width, box.Height) : null;

    protected override Visual? Parent(Visual element) => element.GetVisualParent();

    protected override bool Clips(Visual element) => element.ClipToBounds;

    protected override Visual? ScrollerAround(Visual element) => element.FindAncestorOfType<ScrollViewer>(includeSelf: true);

    /// <summary>What a person's pointer at a point would be over: what Avalonia's own hit test finds, disabled or not.</summary>
    protected override Visual? Hit(TopLevel top, double x, double y) => top.InputHitTest(new Point(x, y), enabledElementsOnly: false) as Visual;

    protected override bool Within(Visual element, Visual ancestor) => element == ancestor || ancestor.IsVisualAncestorOf(element);

    protected override string Called(Visual element) => Shown.Called(element);

    protected override string Named(Visual element) => Inspector.Named(element);

    private InputDone Done(TopLevel top, Point? at, Visual? target, string? shows) => new(
        inspector.IdOf(top),
        at?.X,
        at?.Y,
        target is null ? null : inspector.IdOf(target),
        target is null ? null : Inspector.Named(target),
        shows,
        Shown.Focused() is Visual focused ? Inspector.Named(focused) : null);

    /// <summary>The modifiers held, Primary being the platform's command key (Cmd on macOS, Ctrl elsewhere).</summary>
    private static RawInputModifiers Held(IReadOnlyList<Modifier>? modifiers)
    {
        RawInputModifiers held = RawInputModifiers.None;
        foreach (Modifier modifier in modifiers ?? [])
        {
            held |= modifier switch
            {
                Modifier.Control => RawInputModifiers.Control,
                Modifier.Shift => RawInputModifiers.Shift,
                Modifier.Alt => RawInputModifiers.Alt,
                Modifier.Meta => RawInputModifiers.Meta,
                Modifier.Primary => Raw(Hotkeys.Command),
                _ => throw new Refusal($"No modifier {modifier}"),
            };
        }
        return held;
    }

    private static RawInputModifiers Raw(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : 0)
        | (modifiers.HasFlag(KeyModifiers.Shift) ? RawInputModifiers.Shift : 0)
        | (modifiers.HasFlag(KeyModifiers.Alt) ? RawInputModifiers.Alt : 0)
        | (modifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : 0);

    private static IInputRoot Root(TopLevel top) =>
        InputRoot?.GetValue(top) as IInputRoot ?? throw new Refusal("The inspector cannot reach this window's input in this Avalonia");

    private static IKeyboardDevice Keyboard() =>
        AvaloniaLocator.Current.GetService<IKeyboardDevice>() ?? throw new Refusal("The app has no keyboard device");

    /// <summary>Raw input into the window, as its platform delivers it; on the UI thread it is handled before this returns.
    /// Each is weighed as it goes, since one before it (a key that opens a dialog) may have moved the focus.</summary>
    private static void Send(TopLevel top, RawInputEventArgs input)
    {
        if (top.PlatformImpl?.Input is not { } deliver)
        {
            throw new Refusal("That window has closed");
        }
        Refuse(top, keys: input is RawKeyEventArgs or RawTextInputEventArgs);
        deliver(input);
    }

    /// <summary>Refuses input the app keeps from agents (<see cref="Agent.Guard"/>): into a top level it names, or, for keys
    /// and text, to the focus held in one, wherever Avalonia routes them.</summary>
    private static void Refuse(TopLevel top, bool keys)
    {
        if (Agent.Refusing is not { } refusing)
        {
            return;
        }
        string? why = refusing(top);
        if (why is null && keys && Shown.Focused() is Visual focused && TopLevel.GetTopLevel(focused) is { } holding && holding != top)
        {
            why = refusing(holding);
        }
        if (why is not null)
        {
            throw new Refusal(why);
        }
    }

    private static Dictionary<Key, PhysicalKey> PhysicalKeys()
    {
        Dictionary<Key, PhysicalKey> keys = [];
        foreach (PhysicalKey physical in Enum.GetValues<PhysicalKey>())
        {
            if (physical != PhysicalKey.None)
            {
                keys.TryAdd(physical.ToQwertyKey(), physical);
            }
        }
        return keys;
    }

    /// <summary>The platform's own keys for what typing needs, from the app's hotkey configuration (Avalonia's platform
    /// settings, as its text boxes read them): the command modifier, Select All, and the end of a text box's text.</summary>
    private static class Hotkeys
    {
        private static Avalonia.Input.Platform.PlatformHotkeyConfiguration? Configuration =>
            AvaloniaLocator.Current.GetService<Avalonia.Platform.IPlatformSettings>()?.HotkeyConfiguration;

        public static KeyModifiers Command => Configuration?.CommandModifiers ?? KeyModifiers.Control;

        public static (Key, RawInputModifiers) SelectAll => Chord(Configuration?.SelectAll.FirstOrDefault(), new KeyGesture(Key.A, Command));

        public static (Key, RawInputModifiers) End => Chord(Configuration?.MoveCursorToTheEndOfDocument.FirstOrDefault(), new KeyGesture(Key.End, KeyModifiers.Control));

        private static (Key, RawInputModifiers) Chord(KeyGesture? gesture, KeyGesture otherwise)
        {
            KeyGesture chosen = gesture ?? otherwise;
            return (chosen.Key, Raw(chosen.KeyModifiers));
        }
    }
}
