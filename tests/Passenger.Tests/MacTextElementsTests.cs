using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Editor;
using Passenger.Terminal;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>VoiceOver's reading of the code editor and the terminal through Passenger's own text areas
/// (<see cref="MacTextElements"/>), held by sending NSAccessibility's selectors to the element Avalonia's macOS bridge
/// makes for each control's peer, as AppKit does for VoiceOver: an Avalonia update that renames its element class, its
/// making or its value property fails here, as does an answer that strays from the control's text or a key that costs
/// more with a reader's element than its change.</summary>
public sealed partial class MacTextElementsTests
{
    [AvaloniaFact]
    public void The_editors_element_is_a_text_area_reading_its_text_by_line_range_and_character_and_moving_its_caret()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "NSAccessibility is macOS's");
        const string Third = "    int count = 10; // \U0001F469\u200D\U0001F4BB\n";
        const string Text = "class Shop\n{\n" + Third + "}\n";
        EditorRig rig = new(Text);
        using Pool pool = new();
        nint element = Native.ElementOf(rig.Editor);

        Assert.True(Native.ClassName(element) == MacTextElements.ClassName && Native.String(Native.Send(element, "accessibilityRole")) == "AXTextArea",
            $"Avalonia's element for the editor is a {Native.ClassName(element)} whose role is {Native.String(Native.Send(element, "accessibilityRole"))}");
        Assert.True((int)Native.Send(element, "accessibilityNumberOfCharacters") == Text.Length && Native.String(Native.Send(element, "accessibilityValue")) == Text,
            $"The element reads \"{Native.String(Native.Send(element, "accessibilityValue"))}\" of {(int)Native.Send(element, "accessibilityNumberOfCharacters")} characters");
        int count = Text.IndexOf("count", StringComparison.Ordinal);
        Native.NSRange line = Native.RangeFor(element, "accessibilityRangeForLine:", 2);
        Assert.True(Native.Send(element, "accessibilityLineForIndex:", count) == 2 && line == new Native.NSRange(13, (nuint)Third.Length)
            && Native.String(Native.Send(element, "accessibilityStringForRange:", line)) == Third,
            $"\"count\" is on line {Native.Send(element, "accessibilityLineForIndex:", count)}, whose range is {line}, reading \"{Native.String(Native.Send(element, "accessibilityStringForRange:", line))}\"");
        int emoji = Text.IndexOf("\U0001F469", StringComparison.Ordinal);
        Assert.True(Native.RangeFor(element, "accessibilityRangeForIndex:", emoji + 3) == new Native.NSRange((nuint)emoji, 5),
            $"The character holding the emoji's joiner is {Native.RangeFor(element, "accessibilityRangeForIndex:", emoji + 3)}, where it should be the whole emoji at {emoji}");
        Assert.True(Native.String(Native.Send(Native.Send(element, "accessibilityAttributedStringForRange:", new Native.NSRange((nuint)count, 5)), "string")) == "count",
            "The attributed string of \"count\"'s range reads otherwise");
        Assert.True(Native.RangeOf(element, "accessibilityVisibleCharacterRange") == new Native.NSRange(0, (nuint)Text.Length),
            $"The visible range of a short file is {Native.RangeOf(element, "accessibilityVisibleCharacterRange")}");

        rig.PlaceCaret(count);
        Ui.Settle();
        rig.Press("alt shift RIGHT");
        Assert.True(Native.Send(element, "accessibilityInsertionPointLineNumber") == 2 && Native.String(Native.Send(element, "accessibilitySelectedText")) is "count" or "count "
            && Native.RangeOf(element, "accessibilitySelectedTextRange").Location == (nuint)count,
            $"After choosing \"count\" the caret is on line {Native.Send(element, "accessibilityInsertionPointLineNumber")}, the selection {Native.RangeOf(element, "accessibilitySelectedTextRange")} reading \"{Native.String(Native.Send(element, "accessibilitySelectedText"))}\"");
        Native.Send(element, "setAccessibilitySelectedTextRange:", new Native.NSRange(6, 4));
        Ui.Settle();
        Assert.True(rig.Editor.AccessibleText.Selected is [var chosen] && rig.Text[chosen.Start..chosen.End] == "Shop",
            $"VoiceOver choosing \"Shop\" chose {string.Join(", ", rig.Editor.AccessibleText.Selected)}");
        Native.Send(element, "setAccessibilitySelectedTextRange:", new Native.NSRange((nuint)Text.IndexOf('}', StringComparison.Ordinal), 0));
        Ui.Settle();
        Assert.True(rig.Caret == Text.IndexOf('}', StringComparison.Ordinal) && Native.Send(element, "accessibilityInsertionPointLineNumber") == 3,
            $"VoiceOver putting the caret before the closing brace put it at {rig.Caret}");

        Native.CGRect first = Native.FrameFor(element, new Native.NSRange(0, 5));
        Native.CGRect wider = Native.FrameFor(element, new Native.NSRange(0, 10));
        Native.CGRect below = Native.FrameFor(element, new Native.NSRange((nuint)count, 5));
        Assert.True(first.Height > 0 && Math.Abs(wider.Width - (2 * first.Width)) < 1 && below.Y < first.Y - first.Height + 1 && below.X > first.X,
            $"\"class\" is drawn at {first}, \"class Shop\" at {wider} and \"count\" two lines below at {below}, where the screen's y grows upwards");
        Native.CGPoint middle = new(below.X + (below.Width / 2), below.Y + (below.Height / 2));
        Native.NSRange atMiddle = Native.RangeAt(element, middle);
        Assert.True(atMiddle.Location >= (nuint)count && atMiddle.Location < (nuint)count + 5 && atMiddle.Length == 1,
            $"The character at the middle of \"count\" on the screen is {atMiddle}");

        Assert.True(Native.Allows(element, "setAccessibilityValue:") && Native.Allows(element, "setAccessibilitySelectedTextRange:"),
            "VoiceOver may not change the editor's text or choose a range of it");
        rig.Editor.IsReadOnly = true;
        Assert.True(!Native.Allows(element, "setAccessibilityValue:") && !Native.Allows(element, "setAccessibilitySelectedText:"),
            "VoiceOver may change a read-only editor's text");
    }

    [AvaloniaFact]
    public void Typing_deleting_and_moving_the_caret_tell_VoiceOver_what_changed_and_where_the_caret_went()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "NSAccessibility is macOS's");
        EditorRig rig = new("int total;\nreturn total;\n");
        using Pool pool = new();
        nint element = Native.ElementOf(rig.Editor);
        List<MacTextElements.Told> told = [];
        EventHandler<MacTextElements.Told> heard = (_, said) =>
        {
            if (said.Element == element)
            {
                told.Add(said);
            }
        };
        MacTextElements.Posted += heard;
        try
        {
            rig.PlaceCaret(4);
            Ui.Settle();
            told.Clear();
            rig.Type("x");
            Assert.True(told is [{ Notification: "AXValueChanged", Change: MacTextElements.Change.Edit, Edits: [{ Kind: MacTextElements.EditKind.Typing, Text: "x" }] },
                { Notification: "AXSelectedTextChanged", Change: MacTextElements.Change.Edit }],
                $"Typing \"x\" told {Said(told)}");

            told.Clear();
            rig.Press("BACK_SPACE");
            Assert.True(told is [{ Notification: "AXValueChanged", Edits: [{ Kind: MacTextElements.EditKind.Delete, Text: "x" }] }, { Notification: "AXSelectedTextChanged" }],
                $"Deleting \"x\" told {Said(told)}");

            told.Clear();
            rig.Press("RIGHT");
            Assert.True(told is [{ Notification: "AXSelectedTextChanged", Change: MacTextElements.Change.SelectionMove, Direction: MacTextElements.Direction.Next, Granularity: MacTextElements.Granularity.Character }],
                $"Moving right told {Said(told)}");
            told.Clear();
            rig.Press("DOWN");
            Assert.True(told is [{ Change: MacTextElements.Change.SelectionMove, Direction: MacTextElements.Direction.Next, Granularity: MacTextElements.Granularity.Line }],
                $"Moving down told {Said(told)}");
            told.Clear();
            rig.Press("shift LEFT");
            Assert.True(told is [{ Change: MacTextElements.Change.SelectionExtend, Direction: MacTextElements.Direction.Previous, Granularity: MacTextElements.Granularity.Character }],
                $"Choosing a character to the left told {Said(told)}");

            told.Clear();
            rig.PlaceCaret(rig.Text.IndexOf("total;", StringComparison.Ordinal));
            Ui.Settle();
            rig.Press("alt shift RIGHT");
            told.Clear();
            rig.Type("sum");
            Assert.True(told.Where(t => t.Notification == "AXValueChanged").SelectMany(t => t.Edits).ToList() is [{ Kind: MacTextElements.EditKind.Delete, Text: var gone }, { Kind: MacTextElements.EditKind.Typing, Text: "s" }, ..] && gone.StartsWith("total", StringComparison.Ordinal),
                $"Typing over \"total\" told {Said(told)}");
        }
        finally
        {
            MacTextElements.Posted -= heard;
        }
    }

    [AvaloniaFact]
    public void The_terminals_element_reads_its_lines_back_refuses_changes_and_tells_new_output_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "NSAccessibility is macOS's");
        TerminalSession session = new(40, 5);
        TerminalView view = new() { Session = session };
        Window window = new() { Width = 500, Height = 200, Content = view };
        AutomationProperties.SetName(view, "Local");
        window.Show();
        using Pool pool = new();
        List<MacTextElements.Told> told = [];
        EventHandler<MacTextElements.Told> heard = (_, said) => told.Add(said);
        MacTextElements.Posted += heard;
        try
        {
            Ui.Settle();
            nint element = Native.ElementOf(view);
            session.Say("$ ls\r\nREADME.md  src\r\n$ ");
            Ui.Settle();
            AccessibleText text = view.AccessibleText;
            string all = text.Read(0, text.Length);
            Assert.True(Native.String(Native.Send(element, "accessibilityValue")) == all && all.StartsWith("$ ls\nREADME.md  src\n$", StringComparison.Ordinal),
                $"The terminal's element reads \"{Native.String(Native.Send(element, "accessibilityValue"))}\", its text \"{all}\"");
            Native.NSRange second = Native.RangeFor(element, "accessibilityRangeForLine:", 1);
            Assert.True(Native.String(Native.Send(element, "accessibilityStringForRange:", second)) == "README.md  src\n",
                $"The terminal's second line is {second}, reading \"{Native.String(Native.Send(element, "accessibilityStringForRange:", second))}\"");
            Assert.True(Native.Send(element, "accessibilityInsertionPointLineNumber") == 2, $"The cursor reads on line {Native.Send(element, "accessibilityInsertionPointLineNumber")}");
            Assert.True(!Native.Allows(element, "setAccessibilityValue:") && Native.Allows(element, "setAccessibilitySelectedTextRange:"),
                "VoiceOver may change the terminal's text, or may not choose a range of it");

            told.Clear();
            session.Say("c");
            Ui.Settle();
            Assert.True(told.Where(t => t.Element == element && t.Notification == "AXValueChanged").SelectMany(t => t.Edits).ToList() is [{ Text: var echoed }] && echoed.Trim() == "c",
                $"A character the shell echoed after its prompt told {Said(told)}");
            told.Clear();
            session.Say("a");
            Ui.Settle();
            Assert.True(told.Where(t => t.Element == element && t.Notification == "AXValueChanged").SelectMany(t => t.Edits).ToList() is [{ Kind: MacTextElements.EditKind.Typing, Text: "a" }],
                $"A character the shell echoed told {Said(told)}");
            told.Clear();
            session.Say("t notes\r\nfirst note\r\n$ ");
            Ui.Settle();
            Assert.True(told.Where(t => t.Element == element && t.Notification == "AXValueChanged").SelectMany(t => t.Edits).ToList() is [{ Kind: MacTextElements.EditKind.Insert, Text: var output }]
                && output.Contains("first note", StringComparison.Ordinal) && !output.Contains("README", StringComparison.Ordinal),
                $"New output told {Said(told)}, where it should tell what came alone");
        }
        finally
        {
            MacTextElements.Posted -= heard;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_key_costs_no_more_with_VoiceOvers_element_on_a_long_file_than_on_a_short_one()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "NSAccessibility is macOS's");
        long small = KeyBytes(40);
        long large = KeyBytes(40_000);
        TestContext.Current.TestOutputHelper?.WriteLine($"With VoiceOver's element, a key and what it told allocated {small:N0} bytes on the UI thread in 40 lines, {large:N0} in 40,000");
        Assert.True(large - small < 8 * 1024, $"A key with VoiceOver's element allocated {large:N0} bytes on the UI thread in 40,000 lines against {small:N0} in 40: the text was read whole");

        static long KeyBytes(int lines)
        {
            StringBuilder text = new();
            for (int i = 0; i < lines; i++)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    int value{i} = {i};\n");
            }
            EditorRig rig = new(text.ToString());
            using Pool pool = new();
            nint element = Native.ElementOf(rig.Editor);
            Assert.True(Native.String(Native.Send(element, "accessibilityValue"))?.Length == rig.Text.Length, "The element read none of the text");
            rig.PlaceCaret(rig.Text.Length / 2);
            Ui.Settle();
            long least = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                rig.Type(i % 2 == 0 ? "x" : "y");
                rig.Press("LEFT");
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            rig.Window.Close();
            return least;
        }
    }

    /// <summary>VoiceOver's questions that may name the whole text (a range's string and attributed string, what a Select
    /// All selected) give its first <see cref="AccessibleText.MostRead"/> characters and allocate on the UI thread in
    /// 100,000 lines what they do in 4,000, where they read the whole text.</summary>
    [AvaloniaFact]
    public void Voiceovers_questions_of_the_whole_text_cost_in_100000_lines_what_they_do_in_4000()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "NSAccessibility is macOS's");
        long small = Asked(4_000);
        long large = Asked(100_000);
        TestContext.Current.TestOutputHelper?.WriteLine($"VoiceOver's questions of the whole text allocated {small:N0} bytes in 4,000 lines and {large:N0} in 100,000");
        Assert.True(large <= small + 1024,
            $"VoiceOver's questions of the whole text allocated {large:N0} bytes on the UI thread in 100,000 lines against {small:N0} in 4,000: the text was read whole");

        static long Asked(int lines)
        {
            EditorRig rig = new(string.Concat(Enumerable.Range(0, lines).Select(i => $"    public int Field{i} = {i} * 2; // a comment {i}\n")));
            using Pool pool = new();
            nint element = Native.ElementOf(rig.Editor);
            Native.NSRange whole = new(0, (nuint)rig.Text.Length);
            Native.Send(element, "setAccessibilitySelectedTextRange:", whole);
            Ui.Settle();
            long least = long.MaxValue;
            for (int round = 0; round < 4; round++)
            {
                GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                nint read = Native.Send(element, "accessibilityStringForRange:", whole);
                nint attributed = Native.Send(element, "accessibilityAttributedStringForRange:", whole);
                nint selected = Native.Send(element, "accessibilitySelectedText");
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                string?[] answers = [Native.String(read), Native.String(Native.Send(attributed, "string")), Native.String(selected)];
                Assert.True(answers.All(a => a == rig.Text[..AccessibleText.MostRead]),
                    $"VoiceOver's questions of the whole text were answered with {string.Join(", ", answers.Select(a => a?.Length))} characters, not the text's first {AccessibleText.MostRead:N0}");
            }
            rig.Window.Close();
            return least;
        }
    }

    private static string Said(IEnumerable<MacTextElements.Told> told) =>
        string.Join("; ", told.Select(t => $"{t.Notification} {t.Change} {t.Direction} {t.Granularity} [{string.Join(", ", t.Edits.Select(e => $"{e.Kind} \"{e.Text}\""))}]"));

    /// <summary>An autorelease pool around a test's messages, which answer objects autoreleased as AppKit's own calls do.</summary>
    private sealed class Pool : IDisposable
    {
        private readonly nint pool = Native.PoolPush();

        public void Dispose() => Native.PoolPop(pool);
    }

    /// <summary>The Objective-C runtime as the test sends its messages, and Avalonia's native bridge brought into the
    /// headless test process to make a control's element as it makes it for VoiceOver.</summary>
    private static partial class Native
    {
        private const string Runtime = "/usr/lib/libobjc.A.dylib";

        private static bool loaded;

        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct NSRange(nuint Location, nuint Length);

        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct CGPoint(double X, double Y);

        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct CGRect(double X, double Y, double Width, double Height);

        /// <summary>Avalonia's element for a control's peer, made by its own +acquire: as AppKit's questions make it, through
        /// the peer's native wrapper Avalonia hands its bridge.</summary>
        public static nint ElementOf(Control control)
        {
            Load();
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(control);
            Assembly avalonia = Assembly.Load("Avalonia.Native");
            Type wrapper = avalonia.GetType("Avalonia.Native.AvnAutomationPeer", throwOnError: true)!;
            Type face = avalonia.GetType("Avalonia.Native.Interop.IAvnAutomationPeer", throwOnError: true)!;
            object wrapped = wrapper.GetMethod("Wrap", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [peer])!;
            Type runtime = Type.GetType("MicroCom.Runtime.MicroComRuntime, MicroCom.Runtime", throwOnError: true)!;
            MethodInfo pointer = runtime.GetMethods().Single(m => m.Name == "GetNativeIntPtr" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2).MakeGenericMethod(face);
            nint native = (nint)pointer.Invoke(null, [wrapped, false])!;
            nint element = Send(Class(MacTextElements.AvaloniaClassName), Selector("acquire:"), native);
            Assert.True(element != 0, $"Avalonia's {MacTextElements.AvaloniaClassName} made no element for the {control.GetType().Name}'s peer");
            return element;
        }

        /// <summary>Loads Avalonia's native bridge, as the person's Passenger does, and takes its elements as text areas.</summary>
        private static void Load()
        {
            if (!loaded)
            {
                NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native", "libAvaloniaNative.dylib"));
                loaded = true;
            }
            Assert.True(MacTextElements.Install() is null, $"The text areas could not be put in Avalonia's bridge: {MacTextElements.Install()}");
            Type property = Assembly.Load("Avalonia.Native").GetType("Avalonia.Native.Interop.AvnAutomationProperty", throwOnError: true)!;
            Assert.True(Convert.ToInt32(Enum.Parse(property, "ValueProvider_Value"), System.Globalization.CultureInfo.InvariantCulture) == MacTextElements.AvaloniaValueProperty,
                "Avalonia's bridge numbers its value property otherwise than the text areas pass it over");
        }

        public static string ClassName(nint instance) => Marshal.PtrToStringUTF8(ClassNameOf(instance)) ?? string.Empty;

        public static nint Send(nint receiver, string selector) => Send(receiver, Selector(selector));

        public static nint Send(nint receiver, string selector, nint argument) => Send(receiver, Selector(selector), argument);

        public static nint Send(nint receiver, string selector, NSRange argument) => Send(receiver, Selector(selector), argument);

        public static NSRange RangeOf(nint receiver, string selector) => SendForRange(receiver, Selector(selector));

        public static NSRange RangeFor(nint receiver, string selector, nint argument) => SendForRange(receiver, Selector(selector), argument);

        public static NSRange RangeAt(nint receiver, CGPoint point) => SendForRange(receiver, Selector("accessibilityRangeForPosition:"), point);

        public static CGRect FrameFor(nint receiver, NSRange range) => RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? SendForRectInMemory(receiver, Selector("accessibilityFrameForRange:"), range)
            : SendForRect(receiver, Selector("accessibilityFrameForRange:"), range);

        public static bool Allows(nint receiver, string selector) => (byte)Send(receiver, Selector("isAccessibilitySelectorAllowed:"), Selector(selector)) != 0;

        /// <summary>A string of Foundation's as .NET's.</summary>
        public static string? String(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));

        [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint Class(string name);

        [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint Selector(string name);

        [LibraryImport(Runtime, EntryPoint = "object_getClassName")]
        private static partial nint ClassNameOf(nint instance);

        [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
        public static partial nint PoolPush();

        [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
        public static partial void PoolPop(nint pool);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector, nint argument);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector, NSRange argument);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial NSRange SendForRange(nint receiver, nint selector);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial NSRange SendForRange(nint receiver, nint selector, nint argument);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial NSRange SendForRange(nint receiver, nint selector, CGPoint argument);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial CGRect SendForRect(nint receiver, nint selector, NSRange argument);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend_stret")]
        private static partial CGRect SendForRectInMemory(nint receiver, nint selector, NSRange argument);
    }
}
