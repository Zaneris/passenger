using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Passenger.Inspector.Wpf;

/// <summary>An agent's input to a WPF app, never the person's pointer or keyboard: a click is UI Automation's pattern (Invoke,
/// Toggle, SelectionItem, ExpandCollapse) on the nearest element with one where WPF's hit test lands, else WPF's mouse events;
/// keys are WPF's key events on the element with the focus, then their text through WPF's text input; the wheel is WPF's wheel
/// event. A window not active is never activated: the focus given there is held here for the keys. A chord with modifiers
/// runs the command its gesture is bound to, as WPF reads modifiers from the keyboard itself.</summary>
internal sealed class Driver(Inspector inspector) : Driving<DependencyObject, Visual>
{
    /// <summary>A wheel's notch, as Windows reports one.</summary>
    private const int Notch = 120;

    /// <summary>The element an agent gave the focus to in a window that was not active, which keys go to from then.</summary>
    private WeakReference<DependencyObject>? given;

    protected override string Hidden => "it or an ancestor is hidden or collapsed";

    protected override DependencyObject? Find(long id) => inspector.Find(id);

    protected override Visual? OpenTop(DependencyObject element) => element is Visual own && inspector.TopLevels().Contains(own) ? own : null;

    protected override Visual? TopOf(DependencyObject element) => OpenTop(element) ?? Inspector.TopOf(element);

    protected override InputDone Click(Visual top, DependencyObject? element, InputAction action)
    {
        Point at = Aim(top, element, action, "click");
        DependencyObject? hit = Hit(top, at.X, at.Y);
        DependencyObject? took = hit ?? element;
        if (action.Modifiers is { Count: > 0 } held && held.Any(h => h != Modifier.Shift))
        {
            throw new Refusal("WPF reads a modifier's state from the keyboard itself, so a click in a WPF app holds none but Shift: click without them");
        }
        switch (action.Button)
        {
            case null or "left":
                for (int i = 0; i < Math.Clamp(action.Count ?? 1, 1, 3); i++)
                {
                    took = ClickOn(took, element, twice: i == 1);
                }
                break;
            case "right":
                took = Menu(took ?? element, at, top);
                break;
            default:
                throw new Refusal(action.Button == "middle" ? "A WPF app takes a left or right click from Passenger, not a middle one" : $"A button is left, right or middle, not '{action.Button}'");
        }
        DependencyObject? shows = element ?? hit;
        return Done(top, at, took, shows is null ? null : Shown.TextOf(shows));
    }

    /// <summary>A left click on what was hit: the nearest element at or around it (within the element named) with a pattern a
    /// click acts through, which it acts on, the element taking the focus as a click gives it; with none, WPF's own mouse
    /// button events on what was hit, which a handler of the app's takes. Says what took it.</summary>
    private DependencyObject? ClickOn(DependencyObject? hit, DependencyObject? named, bool twice)
    {
        if (hit is null)
        {
            return null;
        }
        for (DependencyObject? at = hit; at is not null; at = Shown.Parent(at))
        {
            if (Acted(at, twice))
            {
                return at;
            }
            if (named is not null && at == named)
            {
                break;
            }
        }
        FocusAround(hit);
        Raise(hit, twice);
        return hit;
    }

    /// <summary>Acts on an element through the automation pattern a click acts through, if it has one; false when it has none.</summary>
    private bool Acted(DependencyObject element, bool twice)
    {
        AutomationPeer? peer = element switch
        {
            UIElement ui => UIElementAutomationPeer.CreatePeerForElement(ui),
            ContentElement content => ContentElementAutomationPeer.CreatePeerForElement(content),
            _ => null,
        };
        if (peer is null)
        {
            return false;
        }
        if (element is UIElement { IsEnabled: false } || element is ContentElement { IsEnabled: false })
        {
            throw new Refusal($"{Shown.Called(element)} is disabled: a click would not reach it");
        }
        if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expander && (element is ComboBox or Expander || twice))
        {
            FocusAround(element);
            if (expander.ExpandCollapseState == ExpandCollapseState.Expanded)
            {
                expander.Collapse();
            }
            else
            {
                expander.Expand();
            }
            return true;
        }
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoked)
        {
            FocusAround(element);
            invoked.Invoke();
            return true;
        }
        if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggled)
        {
            FocusAround(element);
            toggled.Toggle();
            return true;
        }
        if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selected)
        {
            FocusAround(element);
            selected.Select();
            // A drop-down's item, chosen, closes its list as a click does.
            if (ItemsControl.ItemsControlFromItemContainer(element) is ComboBox { IsDropDownOpen: true } box)
            {
                box.IsDropDownOpen = false;
            }
            return true;
        }
        return false;
    }

    /// <summary>WPF's mouse button events on an element, as the mouse's would arrive there: the preview down and up, and the
    /// bubbling ones unless the preview was handled; a double click's second press is a double click.</summary>
    private static void Raise(DependencyObject element, bool twice)
    {
        if (element is not IInputElement input)
        {
            return;
        }
        int now = Environment.TickCount;
        foreach ((RoutedEvent preview, RoutedEvent bubble) in new[] { (Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent), (Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent) })
        {
            MouseButtonEventArgs first = new(Mouse.PrimaryDevice, now, MouseButton.Left) { RoutedEvent = preview, Source = input };
            input.RaiseEvent(first);
            if (!first.Handled)
            {
                input.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, now, MouseButton.Left) { RoutedEvent = bubble, Source = input });
            }
        }
        if (twice && Up<Control>(element) is { } control)
        {
            MouseButtonEventArgs preview = new(Mouse.PrimaryDevice, now, MouseButton.Left) { RoutedEvent = Control.PreviewMouseDoubleClickEvent, Source = control };
            control.RaiseEvent(preview);
            if (!preview.Handled)
            {
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, now, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = control });
            }
        }
    }

    /// <summary>A right click: the context menu of the element or its nearest ancestor with one, opened at the point.</summary>
    private static DependencyObject? Menu(DependencyObject? hit, Point at, Visual top)
    {
        for (DependencyObject? element = hit; element is not null; element = Shown.Parent(element))
        {
            if (element is FrameworkElement { ContextMenu: { } menu } owner)
            {
                Point inside = top.TransformToDescendant(owner)?.Transform(at) ?? default;
                if (!Opening(owner, inside))
                {
                    menu.PlacementTarget = owner;
                    menu.Placement = PlacementMode.Relative;
                    menu.HorizontalOffset = inside.X;
                    menu.VerticalOffset = inside.Y;
                    menu.IsOpen = true;
                }
                return owner;
            }
        }
        throw new Refusal($"{(hit is null ? "Nothing there" : Shown.Called(hit))} has no context menu, nor has anything around it");
    }

    /// <summary>WPF's ContextMenuOpening on the element, as a right click raises it (an app may fill or refuse its menu
    /// there); true when a handler took it. Its arguments' constructor is WPF's own, not public.</summary>
    private static bool Opening(FrameworkElement owner, Point inside)
    {
        ContextMenuEventArgs? opening;
        try
        {
            opening = Activator.CreateInstance(typeof(ContextMenuEventArgs), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, [owner, true, inside.X, inside.Y], CultureInfo.InvariantCulture) as ContextMenuEventArgs;
        }
        catch (Exception error) when (error is MissingMethodException or System.Reflection.TargetInvocationException or ArgumentException)
        {
            return false;
        }
        if (opening is null)
        {
            return false;
        }
        opening.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        owner.RaiseEvent(opening);
        return opening.Handled;
    }

    protected override InputDone Scroll(Visual top, DependencyObject? element, InputAction action)
    {
        Point at = Aim(top, element, action, "scroll");
        DependencyObject? hit = Hit(top, at.X, at.Y) ?? element;
        if (hit is not IInputElement input)
        {
            throw new Refusal("Nothing there takes the wheel");
        }
        int now = Environment.TickCount;
        double down = action.DeltaY ?? 0;
        for (int i = 0; i < (int)Math.Round(Math.Abs(down)); i++)
        {
            int delta = down > 0 ? -Notch : Notch;
            MouseWheelEventArgs preview = new(Mouse.PrimaryDevice, now, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = input };
            input.RaiseEvent(preview);
            if (!preview.Handled)
            {
                input.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, now, delta) { RoutedEvent = Mouse.MouseWheelEvent, Source = input });
            }
        }
        // WPF's wheel turns up and down: across, the scroll viewer around moves as many lines as a notch turns.
        double across = action.DeltaX ?? 0;
        if (across != 0 && Up<ScrollViewer>(hit) is { } viewer)
        {
            for (int i = 0; i < (int)Math.Round(Math.Abs(across)) * SystemParameters.WheelScrollLines; i++)
            {
                if (across > 0)
                {
                    viewer.LineRight();
                }
                else
                {
                    viewer.LineLeft();
                }
            }
        }
        (top as UIElement)?.UpdateLayout();
        return Done(top, at, Took(element, hit), Shown.ScrolledOf(hit));
    }

    protected override InputDone Type(Visual top, DependencyObject? element, InputAction action)
    {
        (IInputElement? target, bool gave) = Focused(top, element);
        if (action.Replace == true)
        {
            if (ApplicationCommands.SelectAll.CanExecute(null, target))
            {
                ApplicationCommands.SelectAll.Execute(null, target);
            }
        }
        else if (gave && target is TextBox box)
        {
            // As a person who clicks after the text: what is typed goes at its end, not wherever the caret was left.
            box.CaretIndex = box.Text.Length;
        }
        TextElementEnumerator characters = StringInfo.GetTextElementEnumerator(action.Text ?? string.Empty);
        while (characters.MoveNext())
        {
            string character = characters.GetTextElement();
            switch (character)
            {
                case "\n" or "\r\n" or "\r":
                    Press(top, Key.Enter, ModifierKeys.None);
                    break;
                case "\t":
                    Press(top, Key.Tab, ModifierKeys.None);
                    break;
                default:
                    Text(top, character);
                    break;
            }
        }
        return Into(top);
    }

    protected override InputDone Keys(Visual top, DependencyObject? element, InputAction action)
    {
        Focused(top, element);
        foreach (Chord chord in action.Keys ?? [])
        {
            if (!Enum.TryParse(chord.Key, ignoreCase: true, out Key key) || int.TryParse(chord.Key, out _) || key == Key.None)
            {
                throw new Refusal($"No key '{chord.Key}'");
            }
            ModifierKeys held = Held(chord.Modifiers);
            if (held != ModifierKeys.None && held != ModifierKeys.Shift)
            {
                Command(top, key, held, chord);
                continue;
            }
            Press(top, key, held);
        }
        return Into(top);
    }

    /// <summary>The element keys and text go to in a top level: the one with the keyboard focus there, else the one an agent
    /// gave the focus to while its window was not active, else the one its focus scope holds, else the top level itself.</summary>
    private IInputElement? KeyTarget(Visual top)
    {
        if (Keyboard.FocusedElement is DependencyObject focused && Inspector.TopOf(focused) == top)
        {
            return (IInputElement)focused;
        }
        if (given?.TryGetTarget(out DependencyObject? held) == true && Inspector.TopOf(held) == top && held is UIElement { IsVisible: true, IsEnabled: true })
        {
            return (IInputElement)held;
        }
        return FocusManager.GetFocusedElement(top) ?? top as IInputElement;
    }

    /// <summary>Gives an element the focus as a click or Tab would: WPF's keyboard focus while its window is the active one;
    /// else it is held here for the keys that follow, since WPF would ask Windows for the keyboard and activate the window.</summary>
    private bool GiveFocus(UIElement element)
    {
        if (Window.GetWindow(element) is { IsActive: true })
        {
            given = null;
            return element.Focus();
        }
        given = new WeakReference<DependencyObject>(element);
        return true;
    }

    /// <summary>The focus a click gives: to the element or the nearest focusable one around it.</summary>
    private void FocusAround(DependencyObject element)
    {
        for (DependencyObject? at = element; at is not null; at = Shown.Parent(at))
        {
            if (at is UIElement { Focusable: true, IsEnabled: true } focusable)
            {
                GiveFocus(focusable);
                return;
            }
        }
    }

    /// <summary>A chord with modifiers: the command its gesture is bound to, in an input binding of the focused element or an
    /// ancestor, else among WPF's commands (Select All, Copy, Undo, ...) that the focused element can run.</summary>
    private void Command(Visual top, Key key, ModifierKeys held, Chord chord)
    {
        IInputElement? at = KeyTarget(top);
        for (DependencyObject? element = at as DependencyObject; element is not null; element = Shown.Parent(element))
        {
            InputBindingCollection? bindings = element switch
            {
                UIElement ui => ui.InputBindings,
                ContentElement content => content.InputBindings,
                _ => null,
            };
            foreach (InputBinding binding in bindings?.OfType<InputBinding>() ?? [])
            {
                if (binding.Gesture is KeyGesture gesture && gesture.Key == key && gesture.Modifiers == held && binding.Command is { } command)
                {
                    IInputElement target = binding.CommandTarget ?? (IInputElement)element;
                    if (command is RoutedCommand routed ? routed.CanExecute(binding.CommandParameter, target) : command.CanExecute(binding.CommandParameter))
                    {
                        if (command is RoutedCommand run)
                        {
                            run.Execute(binding.CommandParameter, target);
                        }
                        else
                        {
                            command.Execute(binding.CommandParameter);
                        }
                        return;
                    }
                }
            }
        }
        foreach (RoutedCommand command in Known())
        {
            if (command.InputGestures.OfType<KeyGesture>().Any(g => g.Key == key && g.Modifiers == held) && command.CanExecute(null, at))
            {
                command.Execute(null, at);
                return;
            }
        }
        throw new Refusal($"Nothing in the app takes {string.Join("+", chord.Modifiers.Select(m => m.ToString()).Append(chord.Key))}: no input binding or command it can run has that gesture");
    }

    /// <summary>WPF's own commands, which its controls bind to their usual keys.</summary>
    private static IEnumerable<RoutedCommand> Known()
    {
        foreach (Type owner in new[] { typeof(ApplicationCommands), typeof(EditingCommands), typeof(NavigationCommands), typeof(ComponentCommands), typeof(MediaCommands) })
        {
            foreach (System.Reflection.PropertyInfo property in owner.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (property.GetValue(null) is RoutedCommand command)
                {
                    yield return command;
                }
            }
        }
    }

    /// <summary>A key down and up on the element with the focus (Tab moves the focus as WPF's keyboard navigation does), the
    /// default or cancel button pressed by an Enter or Escape nothing took, then the text the key types when nothing took it.</summary>
    private void Press(Visual top, Key key, ModifierKeys held)
    {
        if (KeyTarget(top) is not { } target)
        {
            return;
        }
        if (key == Key.Tab)
        {
            Tab(top, target, held == ModifierKeys.Shift);
            return;
        }
        PresentationSource source = PresentationSource.FromDependencyObject((DependencyObject)target) ?? PresentationSource.FromVisual(top)
            ?? throw new Refusal("That window is no longer shown");
        int now = Environment.TickCount;
        KeyEventArgs preview = new(Keyboard.PrimaryDevice, source, now, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = target };
        target.RaiseEvent(preview);
        bool taken = preview.Handled;
        if (!taken)
        {
            KeyEventArgs down = new(Keyboard.PrimaryDevice, source, now, key) { RoutedEvent = Keyboard.KeyDownEvent, Source = target };
            target.RaiseEvent(down);
            taken = down.Handled;
        }
        if (!taken && key is Key.Enter or Key.Escape && Accept(top, key == Key.Enter))
        {
            taken = true;
        }
        if (!taken && Typed(key, held) is { } text)
        {
            Text(top, text);
        }
        if (PresentationSource.FromDependencyObject((DependencyObject)target) is { } still)
        {
            KeyEventArgs up = new(Keyboard.PrimaryDevice, still, now, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent, Source = target };
            target.RaiseEvent(up);
            if (!up.Handled)
            {
                target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, still, now, key) { RoutedEvent = Keyboard.KeyUpEvent, Source = target });
            }
        }
    }

    /// <summary>Tab: WPF's keyboard navigation from the element with the focus, the next element taking it as Tab gives it.</summary>
    private void Tab(Visual top, IInputElement target, bool back)
    {
        if (target is not UIElement from)
        {
            return;
        }
        if (Window.GetWindow(from) is { IsActive: true })
        {
            from.MoveFocus(new TraversalRequest(back ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            return;
        }
        // Not active: the next element in tab order is found among those shown, and held as the focus.
        List<UIElement> order = [.. Focusables(top)];
        int at = order.IndexOf(from);
        if (order.Count > 0)
        {
            given = new WeakReference<DependencyObject>(order[((at < 0 ? -1 : at) + (back ? order.Count - 1 : 1)) % order.Count]);
        }
    }

    /// <summary>The elements Tab moves the focus among, in the order WPF's keyboard navigation would visit them when no tab
    /// index is set: the tree's order.</summary>
    private static IEnumerable<UIElement> Focusables(DependencyObject root)
    {
        foreach (DependencyObject child in Descendants(root))
        {
            if (child is UIElement { Focusable: true, IsEnabled: true, IsVisible: true } element && KeyboardNavigation.GetIsTabStop(element))
            {
                yield return element;
            }
        }
    }

    /// <summary>The default (Enter) or cancel (Escape) button of the window, pressed as WPF's access keys press it; false
    /// with none.</summary>
    private bool Accept(Visual top, bool enter)
    {
        foreach (DependencyObject child in Descendants(top))
        {
            if (child is Button { IsEnabled: true, IsVisible: true } button && (enter ? button.IsDefault : button.IsCancel))
            {
                return Acted(button, twice: false);
            }
        }
        return false;
    }

    /// <summary>Text into the element with the focus, as the platform's text input gives it after a key.</summary>
    private void Text(Visual top, string text)
    {
        if (KeyTarget(top) is { } target)
        {
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, target, text));
        }
    }

    /// <summary>The character a key types on a US keyboard, Shift held or not, when it types one.</summary>
    private static string? Typed(Key key, ModifierKeys held)
    {
        bool shift = held == ModifierKeys.Shift;
        return key switch
        {
            >= Key.A and <= Key.Z => ((char)((shift ? 'A' : 'a') + (key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 when !shift => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            Key.Space => " ",
            Key.OemPlus => shift ? "+" : "=",
            Key.OemMinus => shift ? "_" : "-",
            Key.OemComma => shift ? "<" : ",",
            Key.OemPeriod => shift ? ">" : ".",
            _ => null,
        };
    }

    /// <summary>What keys went to (whatever has the focus since, a Tab having moved it) and what it shows now.</summary>
    private InputDone Into(Visual top)
    {
        DependencyObject? focused = KeyTarget(top) as DependencyObject;
        return Done(top, null, focused, focused is null ? null : Shown.TextOf(focused));
    }

    /// <summary>Gives an element the focus, as Tab or a click would, unless it or something inside it has it; with none,
    /// whatever has the focus keeps it. Says what keys go to now, and whether this gave it the focus.</summary>
    private (IInputElement? Target, bool Gave) Focused(Visual top, DependencyObject? element)
    {
        if (element is null || element == top)
        {
            return (KeyTarget(top), false);
        }
        if (element is not UIElement input)
        {
            throw new Refusal($"{Shown.Called(element)} takes no keys");
        }
        if (KeyTarget(top) is DependencyObject now && Shown.IsWithin(now, input))
        {
            return ((IInputElement)now, false);
        }
        if (!input.IsVisible)
        {
            throw new Refusal($"{Shown.Called(element)} is not shown ({Hidden})");
        }
        UIElement? taking = input.Focusable && input.IsEnabled ? input : Descendants(input).OfType<UIElement>().FirstOrDefault(d => d.Focusable && d.IsEnabled && d.IsVisible);
        if (taking is null || !GiveFocus(taking))
        {
            throw new Refusal(input.IsEnabled
                ? $"{Shown.Called(element)} cannot take the keyboard focus (Focusable is false, and nothing inside it can)"
                : $"{Shown.Called(element)} is disabled: it takes no keys");
        }
        return (KeyTarget(top) ?? taking, true);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        int count = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(element) : 0;
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, i);
            yield return child;
            foreach (DependencyObject below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>The nearest element of a type at or around one.</summary>
    private static T? Up<T>(DependencyObject? element) where T : DependencyObject
    {
        for (DependencyObject? at = element; at is not null; at = Shown.Parent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Where the pointer acts (<see cref="Driving{T, TTop}.At"/>), as WPF's point.</summary>
    private Point Aim(Visual top, DependencyObject? element, InputAction action, string doing)
    {
        (double x, double y) = At(top, element, action, doing);
        return new Point(x, y);
    }

    protected override Box Client(Visual top)
    {
        Size size = Shown.ClientSize(top);
        return new Box(0, 0, size.Width, size.Height);
    }

    protected override bool IsShown(DependencyObject element) => element is not UIElement { IsVisible: false };

    protected override bool IsEnabled(DependencyObject element) => element is not UIElement { IsEnabled: false };

    protected override Box? BoxIn(DependencyObject element, Visual top) => Shown.RectIn(element, top) is { } box ? new Box(box.X, box.Y, box.Width, box.Height) : null;

    protected override DependencyObject? Parent(DependencyObject element) => Shown.Parent(element);

    protected override bool Clips(DependencyObject element) => Shown.Clips(element);

    protected override DependencyObject? ScrollerAround(DependencyObject element) => Up<ScrollViewer>(element);

    /// <summary>What a person's pointer at a point would be over: the input element WPF's own hit test finds there (a
    /// hyperlink in a text block included).</summary>
    protected override DependencyObject? Hit(Visual top, double x, double y) =>
        top is UIElement root && root.InputHitTest(new Point(x, y)) is DependencyObject input && !inspector.IsOwn(input) ? input : null;

    protected override bool Within(DependencyObject element, DependencyObject ancestor) => Shown.IsWithin(element, ancestor);

    protected override string Called(DependencyObject element) => Shown.Called(element);

    protected override string Named(DependencyObject element) => Inspector.Named(element);

    private InputDone Done(Visual top, Point? at, DependencyObject? target, string? shows) => new(
        inspector.IdOf(top),
        at?.X,
        at?.Y,
        target is null ? null : inspector.IdOf(target),
        target is null ? null : Inspector.Named(target),
        shows,
        KeyTarget(top) is DependencyObject focused && focused != top ? Inspector.Named(focused) : null);

    /// <summary>The modifiers held, Primary being Ctrl, WPF's command key.</summary>
    private static ModifierKeys Held(IReadOnlyList<Modifier>? modifiers)
    {
        ModifierKeys held = ModifierKeys.None;
        foreach (Modifier modifier in modifiers ?? [])
        {
            held |= modifier switch
            {
                Modifier.Control or Modifier.Primary => ModifierKeys.Control,
                Modifier.Shift => ModifierKeys.Shift,
                Modifier.Alt => ModifierKeys.Alt,
                Modifier.Meta => ModifierKeys.Windows,
                _ => throw new Refusal($"No modifier {modifier}"),
            };
        }
        return held;
    }
}
