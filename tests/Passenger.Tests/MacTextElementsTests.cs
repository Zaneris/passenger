using System.Text;
using Avalonia.Automation;
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
        using MacBridge.Pool pool = new();
        nint element = MacBridge.ElementOf(rig.Editor);

        Assert.True(MacBridge.ClassName(element) == MacTextElements.ClassName && MacBridge.String(MacBridge.Send(element, "accessibilityRole")) == "AXTextArea",
            $"Avalonia's element for the editor is a {MacBridge.ClassName(element)} whose role is {MacBridge.String(MacBridge.Send(element, "accessibilityRole"))}");
        Assert.True((int)MacBridge.Send(element, "accessibilityNumberOfCharacters") == Text.Length && MacBridge.String(MacBridge.Send(element, "accessibilityValue")) == Text,
            $"The element reads \"{MacBridge.String(MacBridge.Send(element, "accessibilityValue"))}\" of {(int)MacBridge.Send(element, "accessibilityNumberOfCharacters")} characters");
        int count = Text.IndexOf("count", StringComparison.Ordinal);
        MacBridge.NSRange line = MacBridge.RangeFor(element, "accessibilityRangeForLine:", 2);
        Assert.True(MacBridge.Send(element, "accessibilityLineForIndex:", count) == 2 && line == new MacBridge.NSRange(13, (nuint)Third.Length)
            && MacBridge.String(MacBridge.Send(element, "accessibilityStringForRange:", line)) == Third,
            $"\"count\" is on line {MacBridge.Send(element, "accessibilityLineForIndex:", count)}, whose range is {line}, reading \"{MacBridge.String(MacBridge.Send(element, "accessibilityStringForRange:", line))}\"");
        int emoji = Text.IndexOf("\U0001F469", StringComparison.Ordinal);
        Assert.True(MacBridge.RangeFor(element, "accessibilityRangeForIndex:", emoji + 3) == new MacBridge.NSRange((nuint)emoji, 5),
            $"The character holding the emoji's joiner is {MacBridge.RangeFor(element, "accessibilityRangeForIndex:", emoji + 3)}, where it should be the whole emoji at {emoji}");
        Assert.True(MacBridge.String(MacBridge.Send(MacBridge.Send(element, "accessibilityAttributedStringForRange:", new MacBridge.NSRange((nuint)count, 5)), "string")) == "count",
            "The attributed string of \"count\"'s range reads otherwise");
        Assert.True(MacBridge.RangeOf(element, "accessibilityVisibleCharacterRange") == new MacBridge.NSRange(0, (nuint)Text.Length),
            $"The visible range of a short file is {MacBridge.RangeOf(element, "accessibilityVisibleCharacterRange")}");

        rig.PlaceCaret(count);
        Ui.Settle();
        rig.Press("alt shift RIGHT");
        Assert.True(MacBridge.Send(element, "accessibilityInsertionPointLineNumber") == 2 && MacBridge.String(MacBridge.Send(element, "accessibilitySelectedText")) is "count" or "count "
            && MacBridge.RangeOf(element, "accessibilitySelectedTextRange").Location == (nuint)count,
            $"After choosing \"count\" the caret is on line {MacBridge.Send(element, "accessibilityInsertionPointLineNumber")}, the selection {MacBridge.RangeOf(element, "accessibilitySelectedTextRange")} reading \"{MacBridge.String(MacBridge.Send(element, "accessibilitySelectedText"))}\"");
        MacBridge.Send(element, "setAccessibilitySelectedTextRange:", new MacBridge.NSRange(6, 4));
        Ui.Settle();
        Assert.True(rig.Editor.AccessibleText.Selected is [var chosen] && rig.Text[chosen.Start..chosen.End] == "Shop",
            $"VoiceOver choosing \"Shop\" chose {string.Join(", ", rig.Editor.AccessibleText.Selected)}");
        MacBridge.Send(element, "setAccessibilitySelectedTextRange:", new MacBridge.NSRange((nuint)Text.IndexOf('}', StringComparison.Ordinal), 0));
        Ui.Settle();
        Assert.True(rig.Caret == Text.IndexOf('}', StringComparison.Ordinal) && MacBridge.Send(element, "accessibilityInsertionPointLineNumber") == 3,
            $"VoiceOver putting the caret before the closing brace put it at {rig.Caret}");

        MacBridge.CGRect first = MacBridge.FrameFor(element, new MacBridge.NSRange(0, 5));
        MacBridge.CGRect wider = MacBridge.FrameFor(element, new MacBridge.NSRange(0, 10));
        MacBridge.CGRect below = MacBridge.FrameFor(element, new MacBridge.NSRange((nuint)count, 5));
        Assert.True(first.Height > 0 && Math.Abs(wider.Width - (2 * first.Width)) < 1 && below.Y < first.Y - first.Height + 1 && below.X > first.X,
            $"\"class\" is drawn at {first}, \"class Shop\" at {wider} and \"count\" two lines below at {below}, where the screen's y grows upwards");
        MacBridge.CGPoint middle = new(below.X + (below.Width / 2), below.Y + (below.Height / 2));
        MacBridge.NSRange atMiddle = MacBridge.RangeAt(element, middle);
        Assert.True(atMiddle.Location >= (nuint)count && atMiddle.Location < (nuint)count + 5 && atMiddle.Length == 1,
            $"The character at the middle of \"count\" on the screen is {atMiddle}");

        Assert.True(MacBridge.Allows(element, "setAccessibilityValue:") && MacBridge.Allows(element, "setAccessibilitySelectedTextRange:"),
            "VoiceOver may not change the editor's text or choose a range of it");
        rig.Editor.IsReadOnly = true;
        Assert.True(!MacBridge.Allows(element, "setAccessibilityValue:") && !MacBridge.Allows(element, "setAccessibilitySelectedText:"),
            "VoiceOver may change a read-only editor's text");
    }

    [AvaloniaFact]
    public void Typing_deleting_and_moving_the_caret_tell_VoiceOver_what_changed_and_where_the_caret_went()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "NSAccessibility is macOS's");
        EditorRig rig = new("int total;\nreturn total;\n");
        using MacBridge.Pool pool = new();
        nint element = MacBridge.ElementOf(rig.Editor);
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
        using MacBridge.Pool pool = new();
        List<MacTextElements.Told> told = [];
        EventHandler<MacTextElements.Told> heard = (_, said) => told.Add(said);
        MacTextElements.Posted += heard;
        try
        {
            Ui.Settle();
            nint element = MacBridge.ElementOf(view);
            session.Say("$ ls\r\nREADME.md  src\r\n$ ");
            Ui.Settle();
            AccessibleText text = view.AccessibleText;
            string all = text.Read(0, text.Length);
            Assert.True(MacBridge.String(MacBridge.Send(element, "accessibilityValue")) == all && all.StartsWith("$ ls\nREADME.md  src\n$", StringComparison.Ordinal),
                $"The terminal's element reads \"{MacBridge.String(MacBridge.Send(element, "accessibilityValue"))}\", its text \"{all}\"");
            MacBridge.NSRange second = MacBridge.RangeFor(element, "accessibilityRangeForLine:", 1);
            Assert.True(MacBridge.String(MacBridge.Send(element, "accessibilityStringForRange:", second)) == "README.md  src\n",
                $"The terminal's second line is {second}, reading \"{MacBridge.String(MacBridge.Send(element, "accessibilityStringForRange:", second))}\"");
            Assert.True(MacBridge.Send(element, "accessibilityInsertionPointLineNumber") == 2, $"The cursor reads on line {MacBridge.Send(element, "accessibilityInsertionPointLineNumber")}");
            Assert.True(!MacBridge.Allows(element, "setAccessibilityValue:") && MacBridge.Allows(element, "setAccessibilitySelectedTextRange:"),
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
            using MacBridge.Pool pool = new();
            nint element = MacBridge.ElementOf(rig.Editor);
            Assert.True(MacBridge.String(MacBridge.Send(element, "accessibilityValue"))?.Length == rig.Text.Length, "The element read none of the text");
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
            using MacBridge.Pool pool = new();
            nint element = MacBridge.ElementOf(rig.Editor);
            MacBridge.NSRange whole = new(0, (nuint)rig.Text.Length);
            MacBridge.Send(element, "setAccessibilitySelectedTextRange:", whole);
            Ui.Settle();
            long least = long.MaxValue;
            for (int round = 0; round < 4; round++)
            {
                GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                nint read = MacBridge.Send(element, "accessibilityStringForRange:", whole);
                nint attributed = MacBridge.Send(element, "accessibilityAttributedStringForRange:", whole);
                nint selected = MacBridge.Send(element, "accessibilitySelectedText");
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                string?[] answers = [MacBridge.String(read), MacBridge.String(MacBridge.Send(attributed, "string")), MacBridge.String(selected)];
                Assert.True(answers.All(a => a == rig.Text[..AccessibleText.MostRead]),
                    $"VoiceOver's questions of the whole text were answered with {string.Join(", ", answers.Select(a => a?.Length))} characters, not the text's first {AccessibleText.MostRead:N0}");
            }
            rig.Window.Close();
            return least;
        }
    }

    private static string Said(IEnumerable<MacTextElements.Told> told) =>
        string.Join("; ", told.Select(t => $"{t.Notification} {t.Change} {t.Direction} {t.Granularity} [{string.Join(", ", t.Edits.Select(e => $"{e.Kind} \"{e.Text}\""))}]"));
}
