using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Passenger.Editor;
using Passenger.Text;
using static Passenger.App.MacObjC;

namespace Passenger.App;

/// <summary>VoiceOver's reading of a control drawing its own text (the code editor, the terminal) by character, word, line
/// and selection, which Avalonia's macOS bridge has no text attributes for: Avalonia's element for such a control
/// (AvnAccessibilityElement, told apart as Avalonia makes it by the peer's <see cref="AccessibleTextPeer.IdStart"/> id) is
/// made a text area of a class of Passenger's own, answering NSAccessibility's text attributes from
/// <see cref="AccessibleText"/> as asked, and telling VoiceOver what a key typed or deleted and where the caret went.
/// AppKit asks on the main thread, Avalonia's UI thread; a question from another is answered as about no text.</summary>
public static unsafe partial class MacTextElements
{
    /// <summary>The Objective-C class of the text areas, a subclass of Avalonia's element.</summary>
    public const string ClassName = "PassengerTextElement";

    /// <summary>Avalonia's element class, which this one derives from and whose made elements it takes.</summary>
    public const string AvaloniaClassName = "AvnAccessibilityElement";

    /// <summary>Avalonia's AvnAutomationProperty for a value provider's value, whose change this element tells its own way
    /// (<see cref="Element.Tell"/>), its value being the whole text.</summary>
    public const int AvaloniaValueProperty = 5;

    /// <summary>The most of a change told for VoiceOver to echo, from its start, so output of a megabyte costs no more to
    /// tell than a screenful.</summary>
    public const int MostEcho = 4096;

    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    /// <summary>Each element made a text area, by its address, until it is let go of; held under its own lock, as an element's
    /// last release may come from the finalizer's thread.</summary>
    private static readonly Dictionary<nint, Element> elements = [];

    /// <summary>Held while installing, which each test session's App may ask for on a thread of its own.</summary>
    private static readonly Lock Installed = new();

    /// <summary>Null until Avalonia's native bridge is there to install into, then empty once installed, or why it could
    /// not be.</summary>
    private static string? installed;

    private static nint avaloniaClass;
    private static nint textClass;

    /// <summary>The role every text area answers, kept for the process.</summary>
    private static nint textAreaRole;

    /// <summary>Avalonia's own initWithPeer:, which the element's making goes through first.</summary>
    private static delegate* unmanaged<nint, nint, nint, nint> madeWithPeer;

    /// <summary>How a change of the text is told, as VoiceOver reads AXTextStateChangeType.</summary>
    public enum Change
    {
        Unknown,
        Edit,
        SelectionMove,
        SelectionExtend,
    }

    /// <summary>What an edit did, as VoiceOver reads AXTextEditType.</summary>
    public enum EditKind
    {
        Unknown,
        Delete,
        Insert,
        Typing,
    }

    /// <summary>Which way the caret went, as VoiceOver reads AXTextSelectionDirection.</summary>
    public enum Direction
    {
        Unknown,
        Beginning,
        End,
        Previous,
        Next,
        Discontiguous,
    }

    /// <summary>How far the caret went, as VoiceOver reads AXTextSelectionGranularity; unknown leaves it to the key
    /// VoiceOver saw.</summary>
    public enum Granularity
    {
        Unknown,
        Character,
        Word,
        Line,
    }

    /// <summary>Raised with each notification a text area posts, as it posts it.</summary>
    public static event EventHandler<Told>? Posted;

    /// <summary>Makes Avalonia's elements for the editor and the terminal text areas of Passenger's own from now on, where
    /// Avalonia's native platform runs on macOS: answers null once done (again, or where there is no such platform to
    /// do it in), else why it could not be.</summary>
    public static string? Install()
    {
        lock (Installed)
        {
            if (installed is null && OperatingSystem.IsMacOS() && Class(AvaloniaClassName) is not 0 and var avalonia)
            {
                installed = Installing(avalonia);
            }
            return installed is { Length: > 0 } ? installed : null;
        }
    }

    private static string Installing(nint avalonia)
    {
        nint init = InstanceMethod(avalonia, Selector("initWithPeer:"));
        if (init == 0)
        {
            return $"Avalonia's {AvaloniaClassName} has no initWithPeer: to make its elements by";
        }
        nint type = Class(ClassName);
        if (type == 0)
        {
            List<(nint Selector, nint Implementation, nint Types)> methods = [];
            nint protocol = Protocol("NSAccessibility");
            foreach ((string name, nint implementation) in Methods())
            {
                nint selector = Selector(name);
                nint method = InstanceMethod(avalonia, selector);
                nint types = method != 0 ? TypeEncoding(method) : ProtocolMethod(protocol, selector, required: false, instance: true).Types;
                if (types == 0)
                {
                    return $"Neither Avalonia's {AvaloniaClassName} nor NSAccessibility declares {name}";
                }
                methods.Add((selector, implementation, types));
            }
            type = AllocateClassPair(avalonia, ClassName, 0);
            foreach ((nint selector, nint implementation, nint types) in methods)
            {
                AddMethod(type, selector, implementation, types);
            }
            RegisterClassPair(type);
        }
        textAreaRole = Send(StringOf("AXTextArea"), Objc.Retain);
        avaloniaClass = avalonia;
        textClass = type;
        madeWithPeer = (delegate* unmanaged<nint, nint, nint, nint>)SetImplementation(init, (nint)(delegate* unmanaged<nint, nint, nint, nint>)&MadeWithPeer);
        return string.Empty;
    }

    /// <summary>The methods a text area answers in its own way, by selector.</summary>
    private static (string Selector, nint Implementation)[] Methods() =>
    [
        ("dealloc", (nint)(delegate* unmanaged<nint, nint, void>)&Dealloc),
        ("raisePropertyChanged:", (nint)(delegate* unmanaged<nint, nint, int, void>)&RaisePropertyChanged),
        ("isAccessibilitySelectorAllowed:", (nint)(delegate* unmanaged<nint, nint, nint, byte>)&IsSelectorAllowed),
        ("accessibilityRole", (nint)(delegate* unmanaged<nint, nint, nint>)&Role),
        ("accessibilityValue", (nint)(delegate* unmanaged<nint, nint, nint>)&Value),
        ("setAccessibilityValue:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&SetValue),
        ("accessibilityNumberOfCharacters", (nint)(delegate* unmanaged<nint, nint, nint>)&NumberOfCharacters),
        ("accessibilitySelectedText", (nint)(delegate* unmanaged<nint, nint, nint>)&SelectedText),
        ("setAccessibilitySelectedText:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&SetSelectedText),
        ("accessibilitySelectedTextRange", (nint)(delegate* unmanaged<nint, nint, NSRange>)&SelectedTextRange),
        ("setAccessibilitySelectedTextRange:", (nint)(delegate* unmanaged<nint, nint, NSRange, void>)&SetSelectedTextRange),
        ("accessibilitySelectedTextRanges", (nint)(delegate* unmanaged<nint, nint, nint>)&SelectedTextRanges),
        ("setAccessibilitySelectedTextRanges:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&SetSelectedTextRanges),
        ("accessibilityVisibleCharacterRange", (nint)(delegate* unmanaged<nint, nint, NSRange>)&VisibleCharacterRange),
        ("accessibilityInsertionPointLineNumber", (nint)(delegate* unmanaged<nint, nint, nint>)&InsertionPointLineNumber),
        ("accessibilityLineForIndex:", (nint)(delegate* unmanaged<nint, nint, nint, nint>)&LineForIndex),
        ("accessibilityRangeForLine:", (nint)(delegate* unmanaged<nint, nint, nint, NSRange>)&RangeForLine),
        ("accessibilityStringForRange:", (nint)(delegate* unmanaged<nint, nint, NSRange, nint>)&StringForRange),
        ("accessibilityAttributedStringForRange:", (nint)(delegate* unmanaged<nint, nint, NSRange, nint>)&AttributedStringForRange),
        ("accessibilityFrameForRange:", (nint)(delegate* unmanaged<nint, nint, NSRange, CGRect>)&FrameForRange),
        ("accessibilityRangeForPosition:", (nint)(delegate* unmanaged<nint, nint, CGPoint, NSRange>)&RangeForPosition),
        ("accessibilityRangeForIndex:", (nint)(delegate* unmanaged<nint, nint, nint, NSRange>)&RangeForIndex),
        ("accessibilityStyleRangeForIndex:", (nint)(delegate* unmanaged<nint, nint, nint, NSRange>)&StyleRangeForIndex),
    ];

    /// <summary>Avalonia's making of an element, which takes one for a peer of a control's own text as a text area.</summary>
    [UnmanagedCallersOnly]
    private static nint MadeWithPeer(nint self, nint selector, nint peer)
    {
        nint made = madeWithPeer(self, selector, peer);
        try
        {
            if (made != 0 && Dispatcher.UIThread.CheckAccess() && AccessibleTextPeer.Find(Managed(Send(made, Objc.Identifier))) is { } found)
            {
                SetClass(made, textClass);
                Element element = new(made, found);
                lock (elements)
                {
                    elements[made] = element;
                }
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
        return made;
    }

    [UnmanagedCallersOnly]
    private static void Dealloc(nint self, nint selector)
    {
        try
        {
            Element? element;
            lock (elements)
            {
                elements.Remove(self, out element);
            }
            element?.LetGo();
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
        Super above = new(self, avaloniaClass);
        SendSuper(in above, selector);
    }

    /// <summary>Passes Avalonia's tellings on but its value's, which is the caret's line where this element's is the whole
    /// text, and whose change the element tells with what changed.</summary>
    [UnmanagedCallersOnly]
    private static void RaisePropertyChanged(nint self, nint selector, int property)
    {
        if (property == AvaloniaValueProperty)
        {
            return;
        }
        Super above = new(self, avaloniaClass);
        SendSuper(in above, selector, property);
    }

    /// <summary>Lets a reader choose a range of any text, and change the text only where it is not read-only.</summary>
    [UnmanagedCallersOnly]
    private static byte IsSelectorAllowed(nint self, nint selector, nint asked)
    {
        try
        {
            if (asked == Objc.SetValueSelector || asked == Objc.SetSelectedTextSelector)
            {
                return TextOf(self) is { IsReadOnly: false } ? (byte)1 : (byte)0;
            }
            if (asked == Objc.SetSelectedTextRangeSelector || asked == Objc.SetSelectedTextRangesSelector)
            {
                return TextOf(self) is null ? (byte)0 : (byte)1;
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
        Super above = new(self, avaloniaClass);
        return (byte)SendSuper(in above, selector, asked);
    }

    [UnmanagedCallersOnly]
    private static nint Role(nint self, nint selector) => textAreaRole;

    /// <summary>The whole text, kept until it changes, as a reader may ask it again and again.</summary>
    [UnmanagedCallersOnly]
    private static nint Value(nint self, nint selector)
    {
        try
        {
            return ElementOf(self)?.Value() is { } value and not 0 ? Send(Send(value, Objc.Retain), Objc.Autorelease) : StringOf(string.Empty);
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    /// <summary>Puts a string in the whole text's place, as one change the person can undo.</summary>
    [UnmanagedCallersOnly]
    private static void SetValue(nint self, nint selector, nint value)
    {
        try
        {
            if (TextOf(self) is { IsReadOnly: false } text && IsString(value))
            {
                text.Replace(new TextRange(0, text.Length), Managed(value) ?? string.Empty);
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    [UnmanagedCallersOnly]
    private static nint NumberOfCharacters(nint self, nint selector)
    {
        try
        {
            return TextOf(self)?.Length ?? 0;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    /// <summary>What is selected, its first <see cref="AccessibleText.MostRead"/> characters.</summary>
    [UnmanagedCallersOnly]
    private static nint SelectedText(nint self, nint selector)
    {
        try
        {
            return TextOf(self) is { } text ? StringOf(text.ReadCut(Chosen(text))) : 0;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    /// <summary>Puts a string in place of what is selected, as typing it does.</summary>
    [UnmanagedCallersOnly]
    private static void SetSelectedText(nint self, nint selector, nint value)
    {
        try
        {
            if (TextOf(self) is { IsReadOnly: false } text && IsString(value))
            {
                text.Replace(Chosen(text), Managed(value) ?? string.Empty);
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    [UnmanagedCallersOnly]
    private static NSRange SelectedTextRange(nint self, nint selector)
    {
        try
        {
            return TextOf(self) is { } text ? RangeOf(Chosen(text)) : default;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return default;
        }
    }

    [UnmanagedCallersOnly]
    private static void SetSelectedTextRange(nint self, nint selector, NSRange range)
    {
        try
        {
            if (TextOf(self) is { } text)
            {
                text.Select(Within(text, range));
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    [UnmanagedCallersOnly]
    private static nint SelectedTextRanges(nint self, nint selector)
    {
        try
        {
            if (TextOf(self) is not { } text)
            {
                return 0;
            }
            IReadOnlyList<TextRange> selected = text.Selected;
            if (selected.Count == 0)
            {
                return ArrayOf([Send(Objc.NSValue, Objc.WithRange, RangeOf(new TextRange(text.Caret, 0)))]);
            }
            nint[] ranges = new nint[selected.Count];
            for (int i = 0; i < ranges.Length; i++)
            {
                ranges[i] = Send(Objc.NSValue, Objc.WithRange, RangeOf(selected[i]));
            }
            return ArrayOf(ranges);
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    /// <summary>Takes the first of the ranges a reader chooses, as the text has one choice a reader can make.</summary>
    [UnmanagedCallersOnly]
    private static void SetSelectedTextRanges(nint self, nint selector, nint ranges)
    {
        try
        {
            if (TextOf(self) is { } text && ranges != 0 && Send(ranges, Objc.Count) > 0)
            {
                text.Select(Within(text, SendForRange(Send(ranges, Objc.FirstObject), Objc.RangeValue)));
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    [UnmanagedCallersOnly]
    private static NSRange VisibleCharacterRange(nint self, nint selector)
    {
        try
        {
            return TextOf(self) is { } text ? RangeOf(text.Shown) : default;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return default;
        }
    }

    [UnmanagedCallersOnly]
    private static nint InsertionPointLineNumber(nint self, nint selector)
    {
        try
        {
            return TextOf(self) is { } text ? text.LineOf(text.Caret) : 0;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint LineForIndex(nint self, nint selector, nint index)
    {
        try
        {
            return TextOf(self) is { } text ? text.LineOf((int)Math.Clamp(index, 0, text.Length)) : 0;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    /// <summary>A line's range with its line break, as a reader reads a line.</summary>
    [UnmanagedCallersOnly]
    private static NSRange RangeForLine(nint self, nint selector, nint line)
    {
        try
        {
            if (TextOf(self) is not { } text)
            {
                return default;
            }
            LineSpan span = text.Line((int)Math.Clamp(line, 0, text.LineCount - 1));
            return RangeOf(TextRange.FromBounds(span.Start, span.EndIncludingBreak));
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return default;
        }
    }

    /// <summary>A range's characters, its first <see cref="AccessibleText.MostRead"/>.</summary>
    [UnmanagedCallersOnly]
    private static nint StringForRange(nint self, nint selector, NSRange range)
    {
        try
        {
            return TextOf(self) is { } text ? StringOf(text.ReadCut(Within(text, range))) : 0;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    /// <summary>A range's characters with no attributes, as the text gives a reader none, its first
    /// <see cref="AccessibleText.MostRead"/>.</summary>
    [UnmanagedCallersOnly]
    private static nint AttributedStringForRange(nint self, nint selector, NSRange range)
    {
        try
        {
            if (TextOf(self) is not { } text)
            {
                return 0;
            }
            nint made = Send(Send(Objc.NSAttributedString, Objc.Alloc), Objc.InitWithString, StringOf(text.ReadCut(Within(text, range))));
            return Send(made, Objc.Autorelease);
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return 0;
        }
    }

    /// <summary>Where a range is drawn on the screen, every row it is shown on in one rectangle, none where none is.</summary>
    [UnmanagedCallersOnly]
    private static CGRect FrameForRange(nint self, nint selector, NSRange range)
    {
        try
        {
            if (ElementOf(self) is not { } element || element.Text is not { } text || element.Owner is not { } owner)
            {
                return default;
            }
            IReadOnlyList<Rect> rows = text.BoundsOf(Within(text, range));
            if (rows.Count == 0)
            {
                return default;
            }
            Rect bounds = rows[0];
            for (int i = 1; i < rows.Count; i++)
            {
                bounds = bounds.Union(rows[i]);
            }
            return OnScreen(self, owner, bounds);
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return default;
        }
    }

    /// <summary>The character drawn nearest a point on the screen.</summary>
    [UnmanagedCallersOnly]
    private static NSRange RangeForPosition(nint self, nint selector, CGPoint point)
    {
        try
        {
            if (ElementOf(self) is not { } element || element.Text is not { } text || element.Owner is not { } owner
                || InControl(self, owner, point) is not { } inControl)
            {
                return default;
            }
            return RangeOf(text.CharacterAt(text.OffsetAt(inControl)));
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return default;
        }
    }

    /// <summary>The character holding an index, as a person sees one.</summary>
    [UnmanagedCallersOnly]
    private static NSRange RangeForIndex(nint self, nint selector, nint index)
    {
        try
        {
            return TextOf(self) is { } text ? RangeOf(text.CharacterAt((int)Math.Clamp(index, 0, text.Length))) : default;
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return default;
        }
    }

    /// <summary>The line holding an index, as the text gives a reader no styles to run between and a line is what a reader
    /// may read at once.</summary>
    [UnmanagedCallersOnly]
    private static NSRange StyleRangeForIndex(nint self, nint selector, nint index)
    {
        try
        {
            if (TextOf(self) is not { } text)
            {
                return default;
            }
            LineSpan span = text.Line(text.LineOf((int)Math.Clamp(index, 0, text.Length)));
            return RangeOf(TextRange.FromBounds(span.Start, span.EndIncludingBreak));
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
            return default;
        }
    }

    private static Element? ElementOf(nint self)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return null;
        }
        lock (elements)
        {
            return elements.GetValueOrDefault(self);
        }
    }

    private static AccessibleText? TextOf(nint self) => ElementOf(self)?.Text;

    /// <summary>What is selected as one range: the selection holding the caret, else the first, else the caret's place.</summary>
    private static TextRange Chosen(AccessibleText text)
    {
        int caret = text.Caret;
        IReadOnlyList<TextRange> selected = text.Selected;
        foreach (TextRange range in selected)
        {
            if (range.Start <= caret && caret <= range.End)
            {
                return range;
            }
        }
        return selected.Count > 0 ? selected[0] : new TextRange(caret, 0);
    }

    /// <summary>A reader's range held to the text, which it may ask past the end of.</summary>
    private static TextRange Within(AccessibleText text, NSRange range)
    {
        int length = text.Length;
        int start = (int)Math.Min(range.Location, (nuint)length);
        int end = start + (int)Math.Min(range.Length, (nuint)(length - start));
        return TextRange.FromBounds(start, end);
    }

    private static NSRange RangeOf(TextRange range) => new((nuint)range.Start, (nuint)range.Length);

    /// <summary>A rectangle in the control's coordinates on the screen, as AppKit's are: from the bottom left of the main
    /// screen, placed by the element's own frame, which Avalonia works out for the control's bounds.</summary>
    private static CGRect OnScreen(nint self, Control owner, Rect rectangle)
    {
        if (TopLevel.GetTopLevel(owner) is not Visual root || owner.TransformToVisual(root) is not { } toRoot)
        {
            return default;
        }
        CGRect frame = SendForRect(self, Objc.Frame);
        Rect control = new Rect(owner.Bounds.Size).TransformToAABB(toRoot);
        Rect shown = rectangle.TransformToAABB(toRoot);
        return new CGRect(frame.X + shown.X - control.X, frame.Y + frame.Height - (shown.Y - control.Y) - shown.Height, shown.Width, shown.Height);
    }

    /// <summary>A point on the screen, as AppKit's are, in the control's coordinates.</summary>
    private static Point? InControl(nint self, Control owner, CGPoint point)
    {
        if (TopLevel.GetTopLevel(owner) is not Visual root || owner.TransformToVisual(root) is not { } toRoot)
        {
            return null;
        }
        CGRect frame = SendForRect(self, Objc.Frame);
        Rect control = new Rect(owner.Bounds.Size).TransformToAABB(toRoot);
        return root.TranslatePoint(new Point(control.X + point.X - frame.X, control.Y + frame.Y + frame.Height - point.Y), owner);
    }

    /// <summary>A string of Foundation's, let go of with the pool it is made in.</summary>
    private static nint StringOf(ReadOnlySpan<char> text)
    {
        fixed (char* characters = text)
        {
            return Send(Objc.NSString, Objc.WithCharacters, (nint)characters, text.Length);
        }
    }

    /// <summary>A string of Foundation's as .NET's, null for none.</summary>
    private static string? Managed(nint value)
    {
        if (value == 0)
        {
            return null;
        }
        int length = (int)Send(value, Objc.Length);
        return string.Create(length, value, static (characters, from) =>
        {
            fixed (char* into = characters)
            {
                Send(from, Objc.GetCharacters, (nint)into, new NSRange(0, (nuint)characters.Length));
            }
        });
    }

    private static bool IsString(nint value) => value != 0 && (byte)Send(value, Objc.IsKindOfClass, Objc.NSString) != 0;

    private static nint ArrayOf(ReadOnlySpan<nint> items)
    {
        fixed (nint* objects = items)
        {
            return Send(Objc.NSArray, Objc.WithObjects, (nint)objects, items.Length);
        }
    }

    private static nint DictionaryOf(ReadOnlySpan<nint> keys, ReadOnlySpan<nint> values)
    {
        fixed (nint* named = keys)
        fixed (nint* objects = values)
        {
            return Send(Objc.NSDictionary, Objc.WithObjectsForKeys, (nint)objects, (nint)named, keys.Length);
        }
    }

    private static nint NumberOf(int value) => Send(Objc.NSNumber, Objc.WithInteger, value);

    [LibraryImport(AppKit, EntryPoint = "NSAccessibilityPostNotificationWithUserInfo")]
    private static partial void PostNotification(nint element, nint notification, nint userInfo);

    /// <summary>An edit as told: what it removed or put in.</summary>
    public readonly record struct Edit(EditKind Kind, string Text);

    /// <summary>A notification a text area posted (AXValueChanged, AXSelectedTextChanged) with what its userInfo said.</summary>
    public sealed record Told(nint Element, string Notification, Change Change, IReadOnlyList<Edit> Edits, Direction Direction, Granularity Granularity);

    /// <summary>A text area's tie to its peer, held weakly so an element AppKit keeps keeps no control, and what it has yet
    /// to tell VoiceOver: what changed and whether the caret moved since it last told, told once the work of a key is
    /// done.</summary>
    private sealed class Element
    {
        private readonly nint self;

        private readonly WeakReference<AccessibleTextPeer> peer;

        private readonly EventHandler<AccessibleTextChange> changed;

        private readonly EventHandler moved;

        /// <summary>Each change not yet told: what it removed (null where the text before is not kept) and how much, and where
        /// what it put in stands in the text now.</summary>
        private readonly List<(string? Removed, int RemovedCount, int Start, int Inserted)> edits = [];

        /// <summary>The whole text as last asked, retained until it changes.</summary>
        private nint value;

        private bool caretMoved;

        private bool telling;

        /// <summary>The caret and what was selected as last told.</summary>
        private (int Caret, TextRange Chosen) told;

        public Element(nint self, AccessibleTextPeer peer)
        {
            this.self = self;
            this.peer = new WeakReference<AccessibleTextPeer>(peer);
            changed = (sender, change) => Heard(change);
            moved = (_, _) => Moved();
            told = (peer.Text.Caret, Chosen(peer.Text));
            peer.Text.Changed += changed;
            peer.Text.CaretMoved += moved;
        }

        public AccessibleText? Text => peer.TryGetTarget(out AccessibleTextPeer? kept) ? kept.Text : null;

        public Control? Owner => peer.TryGetTarget(out AccessibleTextPeer? kept) ? kept.Owner : null;

        /// <summary>The whole text, read once until it changes.</summary>
        public nint Value()
        {
            if (value == 0 && Text is { } text)
            {
                value = Send(StringOf(text.Read(0, text.Length)), Objc.Retain);
            }
            return value;
        }

        /// <summary>Lets go of the text and what was kept of it, as AppKit lets go of the element, perhaps on another thread.</summary>
        public void LetGo()
        {
            if (Text is { } text)
            {
                text.Changed -= changed;
                text.CaretMoved -= moved;
            }
            nint kept = Interlocked.Exchange(ref value, 0);
            if (kept != 0)
            {
                Send(kept, Objc.Release);
            }
        }

        private void Heard(AccessibleTextChange change)
        {
            nint kept = value;
            value = 0;
            if (kept != 0)
            {
                Send(kept, Objc.Release);
            }
            if (Text is not { } text)
            {
                return;
            }
            int shift = change.Inserted - change.Removed;
            for (int i = 0; i < edits.Count; i++)
            {
                (string? removed, int count, int start, int inserted) = edits[i];
                if (start >= change.Start + change.Removed)
                {
                    edits[i] = (removed, count, start + shift, inserted);
                }
                else if (start + inserted > change.Start)
                {
                    edits[i] = (null, count, Math.Min(start, change.Start), 0);
                }
            }
            edits.Add((text.ReadRemoved(change, MostEcho), change.Removed, change.Start, change.Inserted));
            Later();
        }

        private void Moved()
        {
            caretMoved = true;
            Later();
        }

        private void Later()
        {
            if (!telling)
            {
                telling = true;
                UiThread.Post(Tell);
            }
        }

        /// <summary>Tells what changed since last told, then where the caret went, each as VoiceOver reads it.</summary>
        public void Tell()
        {
            telling = false;
            if (Text is not { } text)
            {
                edits.Clear();
                return;
            }
            List<Edit> said = [];
            int room = MostEcho;
            foreach ((string? removed, int count, int start, int inserted) in edits)
            {
                if (removed is null || room <= 0)
                {
                    continue;
                }
                string put = text.Read(start, Math.Min(inserted, room));
                room -= put.Length;
                (int same, int sameAtEnd) = Shared(removed, put, whole: removed.Length == count && put.Length == inserted);
                string gone = removed[same..^sameAtEnd];
                string typed = put[same..^sameAtEnd];
                // Blanks written over (a terminal's empty rows taking output) are not heard as deleted.
                if (gone.Length > 0 && (typed.Length == 0 || !string.IsNullOrWhiteSpace(gone)))
                {
                    said.Add(new Edit(EditKind.Delete, gone));
                }
                if (typed.Length > 0)
                {
                    said.Add(new Edit(StringInfo.GetNextTextElementLength(typed) == typed.Length ? EditKind.Typing : EditKind.Insert, typed));
                }
            }
            edits.Clear();
            if (said.Count > 0)
            {
                Post(new Told(self, "AXValueChanged", Change.Edit, said, Direction.Unknown, Granularity.Unknown));
            }
            if (!caretMoved && said.Count == 0)
            {
                return;
            }
            caretMoved = false;
            (int Caret, TextRange Chosen) was = told;
            (int Caret, TextRange Chosen) now = (text.Caret, Chosen(text));
            told = now;
            if (now == was && said.Count == 0)
            {
                return;
            }
            Change how = said.Count > 0 ? Change.Edit : now.Chosen.Length > 0 || was.Chosen.Length > 0 ? Change.SelectionExtend : Change.SelectionMove;
            Direction way = now.Caret > was.Caret ? Direction.Next : now.Caret < was.Caret ? Direction.Previous : Direction.Discontiguous;
            Post(new Told(self, "AXSelectedTextChanged", how, [], way, how == Change.Edit ? Granularity.Unknown : HowFar(text, was.Caret, now.Caret)));
        }

        /// <summary>How far the caret went: to another line, or over one character; anything else is left to the key
        /// VoiceOver saw.</summary>
        private static Granularity HowFar(AccessibleText text, int was, int now)
        {
            if (text.LineOf(was) != text.LineOf(now))
            {
                return Granularity.Line;
            }
            int from = Math.Min(was, now);
            return Math.Max(was, now) == text.CharacterAt(from).End ? Granularity.Character : Granularity.Unknown;
        }

        /// <summary>How many characters what a change removed and what it put in share at their start, and then at their end
        /// where both are read whole, so a change of a whole line or row tells only what differs.</summary>
        private static (int Start, int End) Shared(string removed, string put, bool whole)
        {
            int start = removed.AsSpan().CommonPrefixLength(put);
            int most = whole ? Math.Min(removed.Length, put.Length) - start : 0;
            int end = 0;
            while (end < most && removed[^(end + 1)] == put[^(end + 1)])
            {
                end++;
            }
            return (start, end);
        }

        /// <summary>Posts a notification while the element is still made a text area, held so AppKit's letting go of it
        /// waits until it is posted.</summary>
        private void Post(Told told)
        {
            lock (elements)
            {
                if (elements.ContainsKey(self))
                {
                    Posted?.Invoke(null, told);
                    PostNow(told);
                }
            }
        }

        private void PostNow(Told told)
        {
            nint pool = PoolPush();
            try
            {
                List<nint> keys = [StringOf("AXTextStateChangeType"), StringOf("AXTextChangeElement")];
                List<nint> values = [NumberOf((int)told.Change), self];
                if (told.Edits.Count > 0)
                {
                    nint[] changes = new nint[told.Edits.Count];
                    for (int i = 0; i < changes.Length; i++)
                    {
                        Edit edit = told.Edits[i];
                        changes[i] = DictionaryOf(
                            [StringOf("AXTextEditType"), StringOf("AXTextChangeValue"), StringOf("AXTextChangeValueLength")],
                            [NumberOf((int)edit.Kind), StringOf(edit.Text), NumberOf(edit.Text.Length)]);
                    }
                    keys.Add(StringOf("AXTextChangeValues"));
                    values.Add(ArrayOf(changes));
                }
                else
                {
                    keys.AddRange([StringOf("AXTextStateSync"), StringOf("AXTextSelectionDirection"), StringOf("AXTextSelectionGranularity")]);
                    values.AddRange([Send(Objc.NSNumber, Objc.WithBool, true), NumberOf((int)told.Direction), NumberOf((int)told.Granularity)]);
                }
                PostNotification(self, StringOf(told.Notification), DictionaryOf(keys.ToArray(), values.ToArray()));
            }
            finally
            {
                PoolPop(pool);
            }
        }
    }

    /// <summary>The Objective-C classes and selectors the text areas send, found on macOS alone, the first time one is.</summary>
    private static class Objc
    {
        public static readonly nint NSString = Class("NSString");
        public static readonly nint NSNumber = Class("NSNumber");
        public static readonly nint NSValue = Class("NSValue");
        public static readonly nint NSArray = Class("NSArray");
        public static readonly nint NSDictionary = Class("NSDictionary");
        public static readonly nint NSAttributedString = Class("NSAttributedString");

        public static readonly nint Alloc = Selector("alloc");
        public static readonly nint Retain = Selector("retain");
        public static readonly nint Release = Selector("release");
        public static readonly nint Autorelease = Selector("autorelease");
        public static readonly nint Length = Selector("length");
        public static readonly nint GetCharacters = Selector("getCharacters:range:");
        public static readonly nint WithCharacters = Selector("stringWithCharacters:length:");
        public static readonly nint InitWithString = Selector("initWithString:");
        public static readonly nint WithInteger = Selector("numberWithInteger:");
        public static readonly nint WithBool = Selector("numberWithBool:");
        public static readonly nint WithRange = Selector("valueWithRange:");
        public static readonly nint RangeValue = Selector("rangeValue");
        public static readonly nint WithObjects = Selector("arrayWithObjects:count:");
        public static readonly nint WithObjectsForKeys = Selector("dictionaryWithObjects:forKeys:count:");
        public static readonly nint Count = Selector("count");
        public static readonly nint FirstObject = Selector("firstObject");
        public static readonly nint IsKindOfClass = Selector("isKindOfClass:");
        public static readonly nint Identifier = Selector("accessibilityIdentifier");
        public static readonly nint Frame = Selector("accessibilityFrame");
        public static readonly nint SetValueSelector = Selector("setAccessibilityValue:");
        public static readonly nint SetSelectedTextSelector = Selector("setAccessibilitySelectedText:");
        public static readonly nint SetSelectedTextRangeSelector = Selector("setAccessibilitySelectedTextRange:");
        public static readonly nint SetSelectedTextRangesSelector = Selector("setAccessibilitySelectedTextRanges:");
    }
}
