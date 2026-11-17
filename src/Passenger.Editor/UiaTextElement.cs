using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor.UiAutomation;

/// <summary>A control drawing its own text (the code editor, the terminal) as UI Automation reads it on Windows: a document
/// with the text pattern, its ranges (<see cref="UiaTextRange"/>) read from <see cref="AccessibleText"/> as asked, and the
/// caret's line as its value. Avalonia's bridge has no text pattern, so the control's peer is Avalonia's own for a native
/// window, held over the control (<see cref="UiaTextWindow"/>), whose element this is: focus and text are one element.
/// UI Automation asks on threads of its own; each answer is read on the UI thread, where the control is. A client keeps
/// the element and its ranges as long as it likes, so they hold the control only weakly, answering
/// UIA_E_ELEMENTNOTAVAILABLE once it is let go of.</summary>
[GeneratedComClass]
internal sealed partial class UiaTextElement :
    IRawElementProviderSimple,
    IRawElementProviderFragment,
    IRawElementProviderFragmentRoot,
    ITextProvider2,
    IValueProvider
{
    /// <summary>Avalonia's peer for a native window within its own, which its Windows bridge hands UI Automation as that
    /// window's element; made by its name, as Avalonia keeps it internal (held by UiaTextTests).</summary>
    internal static readonly ConstructorInfo? WindowPeer = typeof(ControlAutomationPeer).Assembly
        .GetType("Avalonia.Controls.Automation.Peers.InteropAutomationPeer")?.GetConstructor([typeof(IPlatformHandle)]);

    /// <summary>Avalonia's Windows bridge's element for a peer, made by name for the same reason (held by
    /// UiaTextTests): an element's neighbours in Avalonia's tree are its.</summary>
    internal static readonly MethodInfo? NodeOf = Type.GetType("Avalonia.Win32.Automation.AutomationNode, Avalonia.Win32.Automation")
        ?.GetMethod("GetOrCreate", BindingFlags.Public | BindingFlags.Static, [typeof(AutomationPeer)]);

    /// <summary>The wrappers Avalonia's elements are handed to UI Automation in from here.</summary>
    private static readonly StrategyBasedComWrappers AvaloniaElements = new();

    /// <summary>The window UI Automation finds this element by, null where the element is read without one.</summary>
    private UiaTextWindow? window;

    /// <summary>The control's peer in Avalonia's tree, which the control keeps; null where the element is read without
    /// one.</summary>
    private WeakReference<AutomationPeer>? peer;

    private readonly WeakReference<Control> owner;

    private readonly WeakReference<AccessibleText> text;

    /// <summary>The control's UI thread, where each answer is read.</summary>
    private readonly Dispatcher dispatcher;

    /// <summary>Whether the text changed since UI Automation was last told, so a key that changes it in pieces tells
    /// once.</summary>
    private bool changeTold = true;

    private UiaTextElement(Control owner, AccessibleText text)
    {
        this.owner = new WeakReference<Control>(owner);
        this.text = new WeakReference<AccessibleText>(text);
        dispatcher = owner.Dispatcher;
    }

    /// <summary>Follows the text and the control's name, to tell UI Automation as they change.</summary>
    private UiaTextElement Listening()
    {
        Text.CaretMoved += (_, _) => Tell(Uia.TextSelectionChangedEvent);
        Text.Changed += (_, _) => TellChanged();
        Text.CaretLineChanged += (_, _) => TellProperty(Uia.ValueValueProperty, () => Text.CaretLine);
        Owner.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationProperties.NameProperty)
            {
                TellProperty(Uia.NameProperty, () => AutomationProperties.GetName(Owner));
            }
        };
        return this;
    }

    /// <summary>The control, refused as UIA_E_ELEMENTNOTAVAILABLE once it is let go of.</summary>
    public Control Owner => owner.TryGetTarget(out Control? kept) ? kept : throw Gone();

    /// <summary>The control's text, refused as UIA_E_ELEMENTNOTAVAILABLE once the control is let go of.</summary>
    public AccessibleText Text => text.TryGetTarget(out AccessibleText? kept) ? kept : throw Gone();

    /// <summary>Whether the control is still kept, and it.</summary>
    internal bool Holds([NotNullWhen(true)] out Control? control) => owner.TryGetTarget(out control);

    /// <summary>Raised with each UI Automation event this element tells, whether or not anything listens.</summary>
    internal event Action<int>? Told;

    /// <summary>The peer for a control in a window of Windows' own: Avalonia's for a native window, over a window of
    /// Passenger's in which UI Automation finds this element; null where the control stands in none, Avalonia no longer
    /// has that peer, or Windows made no window (logged), so the control's own peer answers.</summary>
    [SupportedOSPlatform("windows")]
    public static AutomationPeer? PeerFor(Control owner, AccessibleText text)
    {
        if (WindowPeer is not { } made || UiaTextWindow.HandleOf(owner) == 0 || Hosted(owner, text) is not { window: { } window } element)
        {
            return null;
        }
        AutomationPeer peer = (AutomationPeer)made.Invoke([new PlatformHandle(window.Handle, "HWND")]);
        element.peer = new WeakReference<AutomationPeer>(peer);
        return peer;
    }

    /// <summary>An element with a window of its own for UI Automation to find it by, listening; null where Windows made no
    /// window, which is logged, the control's own peer answering a reader then.</summary>
    [SupportedOSPlatform("windows")]
    internal static UiaTextElement? Hosted(Control owner, AccessibleText text)
    {
        UiaTextElement element = new(owner, text);
        try
        {
            element.window = new UiaTextWindow(element);
        }
        catch (Win32Exception failed)
        {
            PassengerLog.Write($"Windows made no window for a reader to find the {owner.GetType().Name}'s text by, so its own peer answers: {failed.Message} ({failed.NativeErrorCode})");
            return null;
        }
        return element.Listening();
    }

    /// <summary>The window UI Automation finds this element by, null where it is read without one or Windows took it
    /// down.</summary>
    internal UiaTextWindow? Window => window;

    /// <summary>An element read without a window of its own, as a test reads it.</summary>
    internal static UiaTextElement Unhosted(Control owner, AccessibleText text) => new UiaTextElement(owner, text).Listening();

    /// <summary>A provider in Passenger's process that gives the keys itself, to the control, never to its window.</summary>
    public int GetProviderOptions() => Uia.ServerSideProvider | Uia.ProviderOwnsSetFocus;

    public nint GetPatternProvider(int patternId) => patternId switch
    {
        Uia.TextPattern or Uia.TextPattern2 => Uia.Of<ITextProvider2>(this),
        Uia.ValuePattern => Uia.Of<IValueProvider>(this),
        _ => 0,
    };

    public unsafe void GetPropertyValue(int propertyId, ComVariant* value) => *value = OnUi(() => Property(propertyId));

    public nint GetHostRawElementProvider()
    {
        if (!OperatingSystem.IsWindows() || window is not { Handle: not 0 and var handle })
        {
            return 0;
        }
        Marshal.ThrowExceptionForHR(Uia.UiaHostProviderFromHwnd(handle, out nint host));
        return host;
    }

    /// <summary>The element's parent and neighbours in Avalonia's tree, where its peer stands, so UI Automation finds its
    /// window there and nowhere else; none of its own, as it has no parts.</summary>
    public nint Toward(int direction) => OnUi(() => direction switch
    {
        Uia.ParentDirection => Neighbour(0),
        Uia.NextSiblingDirection => Neighbour(1),
        Uia.PreviousSiblingDirection => Neighbour(-1),
        _ => 0,
    });

    /// <summary>None of its own: UI Automation names the element by its window.</summary>
    public nint GetRuntimeId() => 0;

    public UiaRect GetBoundingRectangle() => OnUi(() => ScreenRect(new Rect(Owner.Bounds.Size)));

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() => OnUi(() => Owner.Focus());

    public nint GetFragmentRoot() => Uia.Of<IRawElementProviderFragmentRoot>(this);

    /// <summary>None: a point on the control is on the element itself, which has no parts.</summary>
    public nint ElementProviderFromPoint(double x, double y) => 0;

    /// <summary>None: the element itself holds the keys, having no parts.</summary>
    public nint GetFocus() => 0;

    public nint GetSelection() => Uia.Unknowns<ITextRangeProvider>(OnUi(Selection));

    public nint GetVisibleRanges() => Uia.Unknowns<ITextRangeProvider>(OnUi(() =>
    {
        TextRange shown = Text.Shown;
        return new[] { Range(shown.Start, shown.End) };
    }));

    public nint RangeFromChild(nint childElement) => throw new ArgumentException("The text holds no elements", nameof(childElement));

    public nint RangeFromPoint(UiaPoint point) => Uia.Of<ITextRangeProvider>(OnUi(() => RangeAt(point)));

    public nint GetDocumentRange() => Uia.Of<ITextRangeProvider>(OnUi(() => Range(0, Text.Length)));

    public int GetSupportedTextSelection() => Uia.SupportedTextSelectionMultiple;

    public nint RangeFromAnnotation(nint annotationElement) => throw new ArgumentException("The text holds no annotations", nameof(annotationElement));

    public nint GetCaretRange(out int isActive)
    {
        (UiaTextRange caret, bool active) = OnUi(CaretRange);
        isActive = active ? 1 : 0;
        return Uia.Of<ITextRangeProvider>(caret);
    }

    /// <summary>A range for each caret and what it selects, in the text's order; the caret's where nothing is.</summary>
    internal UiaTextRange[] Selection()
    {
        IReadOnlyList<TextRange> selected = Text.Selected;
        return selected.Count == 0 ? [Range(Text.Caret, Text.Caret)] : [.. selected.Select(s => Range(s.Start, s.End))];
    }

    /// <summary>The caret, and whether it is the one typing goes to: whether the control holds the keys.</summary>
    internal (UiaTextRange Caret, bool Active) CaretRange() => (Range(Text.Caret, Text.Caret), Owner.IsKeyboardFocusWithin);

    /// <summary>An empty range at the place nearest a point on the screen, in its pixels.</summary>
    internal UiaTextRange RangeAt(UiaPoint point)
    {
        int at = Text.OffsetAt(Owner.PointToClient(new PixelPoint((int)Math.Round(point.X), (int)Math.Round(point.Y))));
        return Range(at, at);
    }

    /// <summary>Puts a value in the place of the caret's line, refused where the text is read-only.</summary>
    public void SetValue(string value) => OnUi(() =>
    {
        if (Text.IsReadOnly)
        {
            throw new InvalidOperationException("The text is read-only");
        }
        Text.Replace(Text.LineAt(Text.Caret), value);
    });

    public string GetValue() => OnUi(() => Text.CaretLine);

    public int GetIsReadOnly() => OnUi(() => Text.IsReadOnly) ? 1 : 0;

    /// <summary>The element of the peer's parent (<paramref name="step"/> 0) or of a peer beside it in its parent's, as UI
    /// Automation calls it; zero for none.</summary>
    private nint Neighbour(int step)
    {
        if (Peer is not { } own || NodeOf is null || ParentPeer(own) is not { } parent)
        {
            return 0;
        }
        AutomationPeer? found = parent;
        if (step != 0)
        {
            IReadOnlyList<AutomationPeer> children = parent.GetChildren();
            int at = children.Select((child, i) => ReferenceEquals(child, own) ? i : -1).FirstOrDefault(i => i >= 0, -1);
            found = at >= 0 && at + step >= 0 && at + step < children.Count ? children[at + step] : null;
        }
        if (found is null || NodeOf.Invoke(null, [found]) is not { } node)
        {
            return 0;
        }
        nint unknown = AvaloniaElements.GetOrCreateComInterfaceForObject(node, CreateComInterfaceFlags.None);
        try
        {
            Guid fragment = typeof(IRawElementProviderFragment).GUID;
            return Marshal.QueryInterface(unknown, in fragment, out nint element) == 0 ? element : 0;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>The peer's parent: the peer of the nearest control holding the control, which takes it as its child as
    /// it lists its children.</summary>
    private AutomationPeer? ParentPeer(AutomationPeer own)
    {
        if (own.GetParent() is { } parent)
        {
            return parent;
        }
        if (Owner.FindAncestorOfType<Control>() is { } holder)
        {
            ControlAutomationPeer.CreatePeerForElement(holder).GetChildren();
        }
        return own.GetParent();
    }

    private AutomationPeer? Peer => peer is not null && peer.TryGetTarget(out AutomationPeer? kept) ? kept : null;

    /// <summary>What the element's questions answer once its control is let go of: UIA_E_ELEMENTNOTAVAILABLE.</summary>
    private static InvalidOperationException Gone() => new("The control was let go of") { HResult = Uia.ElementNotAvailable };

    /// <summary>A range of the text from <paramref name="start"/> to <paramref name="end"/>.</summary>
    internal UiaTextRange Range(int start, int end) => new(this, start, end);

    /// <summary>A rectangle in the control's coordinates as UI Automation's, on the screen in its pixels.</summary>
    internal UiaRect ScreenRect(Rect rect)
    {
        if (TopLevel.GetTopLevel(Owner) is null)
        {
            return default;
        }
        PixelPoint from = Owner.PointToScreen(rect.TopLeft);
        PixelPoint to = Owner.PointToScreen(rect.BottomRight);
        return new UiaRect(from.X, from.Y, to.X - from.X, to.Y - from.Y);
    }

    /// <summary>Reads an answer on the control's UI thread for UI Automation's thread, which waits for it.</summary>
    internal T OnUi<T>(Func<T> read)
    {
        if (dispatcher.CheckAccess())
        {
            return read();
        }
        Task<T> answer = dispatcher.InvokeAsync(read).GetTask();
        // UI Automation's own thread waits as its caller does; the UI thread never waits for it.
        Blocking.Until(answer);
        return answer.IsCompletedSuccessfully ? answer.Result
            : answer.Exception?.InnerException is { } failed ? throw failed
            : throw new COMException("The control's window no longer runs", Uia.ElementNotAvailable);
    }

    internal void OnUi(Action act) => OnUi(() =>
    {
        act();
        return true;
    });

    /// <summary>Lets go of the window UI Automation found this element by, which Windows took down with the window holding
    /// it: nothing more is told.</summary>
    internal void Unhost() => window = null;

    private ComVariant Property(int id) => id switch
    {
        Uia.ControlTypeProperty => ComVariant.Create(Uia.DocumentControlType),
        Uia.NameProperty => Words(AutomationProperties.GetName(Owner)),
        Uia.HelpTextProperty => Words(AutomationProperties.GetHelpText(Owner)),
        Uia.AutomationIdProperty => Words(AutomationProperties.GetAutomationId(Owner) ?? Owner.Name),
        Uia.ClassNameProperty => Words(Owner.GetType().Name),
        Uia.FrameworkIdProperty => Words("Avalonia"),
        Uia.HasKeyboardFocusProperty => ComVariant.Create(Owner.IsKeyboardFocusWithin),
        Uia.IsKeyboardFocusableProperty => ComVariant.Create(Owner.Focusable && Owner.IsEffectivelyEnabled),
        Uia.IsEnabledProperty => ComVariant.Create(Owner.IsEffectivelyEnabled),
        Uia.IsOffscreenProperty => ComVariant.Create(!Owner.IsEffectivelyVisible),
        Uia.IsControlElementProperty or Uia.IsContentElementProperty => ComVariant.Create(true),
        Uia.IsTextPatternAvailableProperty or Uia.IsTextPattern2AvailableProperty or Uia.IsValuePatternAvailableProperty => ComVariant.Create(true),
        Uia.ValueValueProperty => ComVariant.Create(Text.CaretLine),
        Uia.ValueIsReadOnlyProperty => ComVariant.Create(Text.IsReadOnly),
        Uia.ProcessIdProperty => ComVariant.Create(Environment.ProcessId),
        _ => default,
    };

    /// <summary>Words as a property's value, nothing where there are none.</summary>
    private static ComVariant Words(string? words) => string.IsNullOrEmpty(words) ? default : ComVariant.Create(words);

    /// <summary>Tells the text changed once for what one turn of the UI thread changed of it.</summary>
    private void TellChanged()
    {
        if (!changeTold)
        {
            return;
        }
        changeTold = false;
        dispatcher.Post(() =>
        {
            changeTold = true;
            Tell(Uia.TextChangedEvent);
        }, DispatcherPriority.Background);
    }

    private void Tell(int eventId)
    {
        Told?.Invoke(eventId);
        if (OperatingSystem.IsWindows() && window is { Provider: not 0 and var self } && Uia.UiaClientsAreListening())
        {
            Uia.UiaRaiseAutomationEvent(self, eventId);
        }
    }

    /// <summary>Tells a property's new value, read only where something listens.</summary>
    private void TellProperty(int propertyId, Func<string?> value)
    {
        Told?.Invoke(propertyId);
        if (OperatingSystem.IsWindows() && window is { Provider: not 0 and var self } && Uia.UiaClientsAreListening())
        {
            ComVariant now = Words(value());
            try
            {
                Uia.UiaRaiseAutomationPropertyChangedEvent(self, propertyId, default, Variant.Of(now));
            }
            finally
            {
                now.Dispose();
            }
        }
    }
}
