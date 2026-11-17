using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Editor;
using Passenger.Editor.UiAutomation;
using Passenger.Terminal;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The code editor's and the terminal's text as UI Automation's text pattern reads it on Windows
/// (<see cref="UiaTextElement"/>, <see cref="UiaTextRange"/>), as a screen reader steps it: by character, word, line and
/// the whole text, the caret and what is selected following the keys and told as they move, a range chosen or found, where
/// a range is drawn and the range at a point, the caret's line as the value; a key's cost with a reader listening and
/// asking after each; and the Avalonia internals the bridge leans on, which an update of Avalonia may take away. Read
/// through the providers' own methods, on every system, as UI Automation calls them on Windows.</summary>
public sealed partial class UiaTextTests
{
    private const string Notes = "first line\r\n    int count = 10;\r\nlast line\r\n";

    [AvaloniaFact]
    public void The_editors_text_pattern_steps_by_character_word_and_line_and_follows_the_caret_as_keys_move_it()
    {
        EditorRig rig = new(Notes);
        UiaTextElement element = UiaTextElement.Unhosted(rig.Editor, rig.Editor.AccessibleText);
        List<int> told = [];
        element.Told += told.Add;

        UiaTextRange document = element.Range(0, Notes.Length);
        Assert.True(document.GetText(-1) == Notes && document.GetText(5) == "first", $"The document range reads \"{document.GetText(-1)}\"");
        Assert.True(Steps(element, Uia.WordUnit) is ["first ", "line", "\r\n", "    ", "int ", "count ", "= ", "10", ";", "\r\n", "last ", "line", "\r\n"],
            $"Stepping by words reads {string.Join(" ", Steps(element, Uia.WordUnit).Select(w => $"\"{w}\""))}");
        Assert.True(Steps(element, Uia.LineUnit) is ["first line\r\n", "    int count = 10;\r\n", "last line\r\n"],
            $"Stepping by lines reads {string.Join(" ", Steps(element, Uia.LineUnit).Select(w => $"\"{w}\""))}");
        Assert.True(Steps(element, Uia.CharacterUnit).Count(c => c == "\r\n") == 3, "A line break is not read as one character");

        int count = Notes.IndexOf("count", StringComparison.Ordinal);
        rig.PlaceCaret(count);
        Ui.Settle();
        Assert.True(told.Contains(Uia.TextSelectionChangedEvent), "Moving the caret told no reader the selection changed");
        Assert.True(element.Selection() is [var caret] && caret.Ends == (count, count) && element.CaretRange() is { Active: true } held && held.Caret.Ends == (count, count),
            $"The selection reads {string.Join("; ", element.Selection().Select(r => r.Ends))} with the caret at {count}");
        Assert.True(Unit(element.Selection()[0], Uia.CharacterUnit) == "c" && Unit(element.Selection()[0], Uia.WordUnit) == "count "
            && Unit(element.Selection()[0], Uia.LineUnit) == "    int count = 10;\r\n" && Unit(element.Selection()[0], Uia.ParagraphUnit) == "    int count = 10;\r\n",
            "The caret's character, word or line reads otherwise than a reader hears it");
        UiaTextRange back = element.Selection()[0];
        Assert.True(back.Move(Uia.CharacterUnit, -3) == -3 && back.Ends == (count - 3, count - 3), $"Three characters back the caret is at {back.Ends}");
        UiaTextRange growing = element.Selection()[0];
        Assert.True(growing.MoveEndpointByUnit(1, Uia.WordUnit, 1) == 1 && growing.GetText(-1) == "count ", $"A range grown by a word reads \"{growing.GetText(-1)}\"");

        told.Clear();
        for (int i = 0; i < "count".Length; i++)
        {
            rig.Press("shift RIGHT");
        }
        Assert.True(element.Selection() is [var chosen] && chosen.GetText(-1) == "count" && told.Count(e => e == Uia.TextSelectionChangedEvent) >= 5,
            $"A selection grown by five keys reads \"{string.Join(", ", element.Selection().Select(r => r.GetText(-1)))}\", told {told.Count(e => e == Uia.TextSelectionChangedEvent)} times");

        told.Clear();
        rig.Type("total");
        Ui.Settle();
        Assert.True(told.Count(e => e == Uia.TextChangedEvent) == "total".Length && told.Contains(Uia.ValueValueProperty) && element.GetValue() == "    int total = 10;",
            $"Typing five letters over the selection told {string.Join(", ", told)}, the value reading \"{element.GetValue()}\"");
    }

    [AvaloniaFact]
    public void A_reader_finds_chooses_and_points_at_the_editors_text_and_reads_where_it_is_drawn()
    {
        EditorRig rig = new(Notes);
        UiaTextElement element = UiaTextElement.Unhosted(rig.Editor, rig.Editor.AccessibleText);
        UiaTextRange document = element.Range(0, Notes.Length);
        UiaTextRange count = document.Find("COUNT", backward: false, ignoreCase: true) ?? throw new Xunit.Sdk.XunitException("Finding \"COUNT\" found nothing");
        Assert.True(count.GetText(-1) == "count" && document.Find("COUNT", backward: false, ignoreCase: false) is null, $"Found \"{count.GetText(-1)}\"");

        count.Select();
        Ui.Settle();
        Assert.True(rig.Editor.Selections.Primary is { Start: var start, End: var end } && Notes[start..end] == "count",
            $"Choosing the range found selected {rig.Editor.Selections.Primary}");
        Assert.True(count.Compare(element.Selection()[0]) && count.CompareEndpoints(0, document, 0) == Notes.IndexOf("count", StringComparison.Ordinal),
            "The range chosen reads otherwise than the selection it made");

        double[] drawn = count.BoundingRectangles();
        Assert.True(drawn.Length == 4 && drawn[2] > 0 && drawn[3] > 0, $"\"count\" is drawn at {string.Join(", ", drawn)}");
        UiaTextRange pointed = element.RangeAt(new UiaPoint(drawn[0] + (drawn[2] / 2), drawn[1] + (drawn[3] / 2)));
        Assert.True(Unit(pointed, Uia.WordUnit) == "count ", $"The word at the middle of where \"count\" is drawn reads \"{Unit(pointed, Uia.WordUnit)}\"");

        UiaTextRange last = element.Range(Notes.IndexOf("line\r\n", 30, StringComparison.Ordinal), Notes.Length - 2);
        last.ExpandToEnclosingUnit(Uia.WordUnit);
        Assert.True(last.Move(Uia.WordUnit, 1) == 1 && last.GetText(-1) == "\r\n" && last.Move(Uia.WordUnit, 1) == 0,
            $"The last word moves on to \"{last.GetText(-1)}\", then onto the text's end");

        element.SetValue("    int total = 11;");
        Assert.True(rig.Text == "first line\r\n    int total = 11;\r\nlast line\r\n" && element.GetIsReadOnly() == 0,
            $"Setting the value made \"{rig.Text}\", where it should put the caret's line in its place");
    }

    [AvaloniaFact]
    public void The_terminals_text_pattern_reads_its_lines_read_only_and_selects_as_a_drag_does()
    {
        TerminalSession session = new(40, 5);
        TerminalView view = new() { Session = session };
        Window window = new() { Width = 500, Height = 200, Content = view };
        AutomationProperties.SetName(view, "Local");
        window.Show();
        try
        {
            Ui.Settle();
            session.Say("one\r\ntwo words\r\n");
            UiaTextElement element = UiaTextElement.Unhosted(view, view.AccessibleText);
            Assert.True(Steps(element, Uia.LineUnit) is ["one\n", "two words\n", ..], $"The terminal's lines read {string.Join(" ", Steps(element, Uia.LineUnit).Select(l => $"\"{l}\""))}");
            Assert.True(element.GetIsReadOnly() == 1, "The terminal's text reads as one a reader may set");
            Assert.Throws<InvalidOperationException>(() => element.SetValue("typed"));
            Assert.True(element.Selection() is [var cursor] && cursor.Ends.Start == cursor.Ends.End && Unit(cursor, Uia.LineUnit) == "\n",
                $"The terminal's cursor reads at {element.Selection()[0].Ends} on \"{Unit(element.Selection()[0], Uia.LineUnit)}\"");

            UiaTextRange two = element.Range(0, view.AccessibleText.Length).Find("words", backward: false, ignoreCase: false)!;
            two.Select();
            Ui.Settle();
            Assert.True(view.SelectedText == "words" && element.Selection() is [var chosen] && chosen.GetText(-1) == "words",
                $"Choosing \"words\" selected \"{view.SelectedText}\"");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Avalonias_bridge_hands_UI_Automation_its_peer_for_a_native_window_as_that_windows_element()
    {
        ConstructorInfo made = UiaTextElement.WindowPeer
            ?? throw new Xunit.Sdk.XunitException("Avalonia has no InteropAutomationPeer taking a platform handle: the Windows bridge's text pattern can't be reached");
        AutomationPeer peer = (AutomationPeer)made.Invoke([new PlatformHandle(1234, "HWND")]);
        Assert.True(peer.GetType().GetProperty("NativeControlHandle")?.GetValue(peer) is IPlatformHandle { Handle: 1234 },
            "Avalonia's peer for a native window keeps no handle the bridge gave it");
        MethodInfo nodeOf = UiaTextElement.NodeOf
            ?? throw new Xunit.Sdk.XunitException("Avalonia's Windows bridge has no AutomationNode.GetOrCreate: the element's neighbours can't be named");
        object node = nodeOf.Invoke(null, [peer])!;
        Assert.True(node.GetType().Name == "InteropAutomationNode", $"Avalonia's Windows bridge hands UI Automation a {node.GetType().Name} for a native window's peer");
        MethodInfo options = node.GetType().GetMethod("GetProviderOptions")!;
        Assert.True(Convert.ToInt32(options.Invoke(node, []), System.Globalization.CultureInfo.InvariantCulture) == (Uia.ServerSideProvider | Uia.OverrideProvider)
            && node.GetType().GetMethod("GetPropertyValue")!.Invoke(node, [Uia.NameProperty]) is null
            && node.GetType().GetMethod("GetPatternProvider")!.Invoke(node, [Uia.TextPattern]) is null,
            "Avalonia's element for a native window answers for it, where it should leave every property and pattern to the window's own");

        Window window = new() { Content = new Border() };
        window.Show();
        try
        {
            object plain = nodeOf.Invoke(null, [ControlAutomationPeer.CreatePeerForElement((Control)window.Content!)])!;
            Assert.True(plain.GetType().GetInterfaces().Any(i => i.GUID == typeof(IRawElementProviderFragment).GUID),
                $"Avalonia's element {plain.GetType().Name} is no fragment of UI Automation's as the bridge names one");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_reader_listening_and_asking_after_each_key_costs_in_a_line_of_a_million_characters_what_it_does_in_one_of_ten_thousand()
    {
        KeysAsked small = new(10_000, asking: true);
        KeysAsked large = new(1_000_000, asking: true);
        KeysAsked smallAlone = new(10_000, asking: false);
        KeysAsked largeAlone = new(1_000_000, asking: false);
        // Taken in turns, so what the process takes on meanwhile weighs on each alike.
        for (int round = 0; round < 4; round++)
        {
            small.Round();
            large.Round();
            smallAlone.Round();
            largeAlone.Round();
        }
        long smallReader = small.Close() - smallAlone.Close();
        long largeReader = large.Close() - largeAlone.Close();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A reader listening and asking after each key added {smallReader:N0} bytes to the keys in a line of 10,000 and {largeReader:N0} in one of a million");
        Assert.True(largeReader <= smallReader + 2048,
            $"A reader listening and asking after each key added {largeReader:N0} bytes on the UI thread to the keys in a line of a million characters, against {smallReader:N0} in a line of 10,000: what it asked was read from the whole line");
    }

    [AvaloniaFact]
    public void On_Windows_the_elements_window_answers_UI_Automation_with_the_element_and_passes_every_click_through()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows has UI Automation's windows to ask");
            return;
        }
        EditorRig rig = new(Notes);
        UiaTextWindow window = new(UiaTextElement.Unhosted(rig.Editor, rig.Editor.AccessibleText));
        nint handle = window.Handle;
        try
        {
            Assert.True(handle != 0 && window.Provider != 0, "No window was made for UI Automation to find the element by");
            nint answered = SendMessage(handle, 0x003D, 0, Uia.RootObjectId);
            Assert.True(answered != 0, "The window answered UI Automation's WM_GETOBJECT with no element");
            Assert.True(SendMessage(handle, 0x0084, 0, 0) == -1 && SendMessage(handle, 0x0021, 0, 0) == 3,
                "The window takes a click or the keys, where it should pass every click to the window beneath and never be activated");
            Assert.True(UiaTextElement.Unhosted(rig.Editor, rig.Editor.AccessibleText).GetProviderOptions() == (Uia.ServerSideProvider | Uia.ProviderOwnsSetFocus),
                "UI Automation would give the element's window the keys itself, taking them from the editor");
        }
        finally
        {
            DestroyWindow(handle);
        }
        Assert.True(window.Handle == 0 && window.Provider == 0, "A window taken down still holds the element for UI Automation");
    }

    /// <summary>The window UI Automation finds the editor's element by covers the editor whatever the interface's scale: at
    /// 1.5 and at 0.8 its place in the window holding it reaches every corner of the editor and nothing beside it, as the
    /// window's hit testing finds them.</summary>
    [AvaloniaFact]
    public void On_Windows_the_elements_window_covers_its_editor_at_every_interface_scale()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows has UI Automation's windows to lay over a control");
            return;
        }
        string folder = TestFolders.Create("uia-scale-");
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, Notes);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(notes);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            foreach (double scale in new[] { 1.5, 0.8 })
            {
                window.ApplySettings(window.Settings with { DisplayScale = scale });
                Ui.Settle();
                PixelRect over = UiaTextWindow.Over(editor, window) ?? throw new Xunit.Sdk.XunitException($"At {scale} the editor stands nowhere in its window");
                double pixels = window.RenderScaling;
                Rect placed = new(over.X / pixels, over.Y / pixels, over.Width / pixels, over.Height / pixels);
                // Only the editor's own visuals and those holding it are hit, so a bar laid over it is passed by.
                bool OnEditor(Point point) => window.InputHitTest(point, v => v == editor || editor.IsVisualAncestorOf(v) || v.IsVisualAncestorOf(editor)) is Visual hit
                    && (hit == editor || editor.IsVisualAncestorOf(hit));
                Point[] inside = [placed.TopLeft + new Point(2, 2), placed.TopRight + new Point(-2, 2), placed.BottomLeft + new Point(2, -2), placed.BottomRight + new Point(-2, -2)];
                Point[] outside = [placed.TopLeft + new Point(-2, -2), placed.BottomRight + new Point(2, 2)];
                Assert.True(inside.All(OnEditor) && !outside.Any(OnEditor),
                    $"At {scale} the element's window stands at {placed} over an editor of {editor.Bounds.Size}: a reader's window covers {string.Join(", ", inside.Select(p => OnEditor(p) ? "the editor" : $"not the editor at {p}"))} and {string.Join(", ", outside.Select(p => OnEditor(p) ? $"the editor at {p}" : "not the editor"))} beside it");
            }
        }
        finally
        {
            window.ApplySettings(window.Settings with { DisplayScale = 1 });
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Editors and a terminal a reader touched (asked their window for the element, kept a range of the text) are let
    /// go of once closed, the range kept holding nothing of them and answering that its element is gone; on Windows
    /// their windows are taken down too.</summary>
    [AvaloniaFact]
    public void Editors_and_a_terminal_a_reader_touched_are_let_go_of_once_closed_and_on_Windows_their_windows_with_them()
    {
        List<nint> kept = [];
        try
        {
            List<(WeakReference<Control> Control, nint Window)> closed = [.. Enumerable.Range(0, 3).Select(_ => EditorTouchedThenClosed(kept)), TerminalTouchedThenClosed(kept)];
            foreach ((WeakReference<Control> control, _) in closed)
            {
                Ui.Collected(control, "A control a reader touched, closed, is still alive: the reader's element keeps it, with its text, for as long as the process runs");
            }
            if (OperatingSystem.IsWindows())
            {
                Ui.WaitFor(() => closed.All(c => !IsWindow(c.Window)),
                    () => $"{closed.Count(c => IsWindow(c.Window))} of the {closed.Count} closed controls' windows still stand, each holding its element for UI Automation");
            }
            foreach (nint pointer in kept)
            {
                UiaTextRange range = Uia.From<UiaTextRange>(pointer) ?? throw new Xunit.Sdk.XunitException("A range handed out is none of Passenger's");
                (long held, string most) = HeldBytes.Of(range, o => o is Avalonia.Threading.Dispatcher);
                Assert.True(held < 4096, $"A range a reader keeps of a closed control holds {held:N0} bytes, the most in {most}");
                InvalidOperationException gone = Assert.Throws<InvalidOperationException>(() => range.GetText(-1));
                Assert.True(gone.HResult == Uia.ElementNotAvailable, $"Reading a range of a closed control answered {gone.HResult:X8}, not that its element is gone");
            }
        }
        finally
        {
            kept.ForEach(pointer => Marshal.Release(pointer));
        }
    }

    /// <summary>An editor of a long text a reader touched, then closed: held here only weakly, with its window's handle.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<Control>, nint) EditorTouchedThenClosed(List<nint> kept)
    {
        EditorRig rig = new(string.Concat(Enumerable.Repeat(Notes, 5_000)));
        nint window = Touched(rig.Editor, rig.Editor.AccessibleText, kept);
        rig.Window.Close();
        Ui.Settle();
        return (new WeakReference<Control>(rig.Editor), window);
    }

    /// <summary>A terminal a reader touched, then closed: held here only weakly, with its window's handle.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<Control>, nint) TerminalTouchedThenClosed(List<nint> kept)
    {
        TerminalSession session = new(40, 5);
        TerminalView view = new() { Session = session };
        Window shown = new() { Width = 500, Height = 200, Content = view };
        shown.Show();
        Ui.Settle();
        session.Say("one\r\ntwo words\r\n");
        nint window = Touched(view, view.AccessibleText, kept);
        shown.Close();
        Ui.Settle();
        return (new WeakReference<Control>(view), window);
    }

    /// <summary>Touches a control as a reader does: asks its window for the element (on Windows) and keeps a range of its
    /// text; answers the window's handle, zero elsewhere.</summary>
    private static nint Touched(Control control, AccessibleText text, List<nint> kept)
    {
        UiaTextElement element;
        nint window = 0;
        if (OperatingSystem.IsWindows())
        {
            element = UiaTextElement.Hosted(control, text) ?? throw new Xunit.Sdk.XunitException("Windows made no window for the element");
            window = element.Window?.Handle ?? 0;
            Assert.True(window != 0 && SendMessage(window, 0x003D, 0, Uia.RootObjectId) != 0, "The element's window answered UI Automation with no element");
        }
        else
        {
            element = UiaTextElement.Unhosted(control, text);
        }
        kept.Add(Uia.Of<ITextRangeProvider>(element.Range(0, 10)));
        return window;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    /// <summary>The text of each unit from the text's start to its end, as a reader steps a range through it.</summary>
    private static List<string> Steps(UiaTextElement element, int unit)
    {
        UiaTextRange range = element.Range(0, 0);
        range.ExpandToEnclosingUnit(unit);
        List<string> read = [range.GetText(-1)];
        while (range.Move(unit, 1) == 1)
        {
            read.Add(range.GetText(-1));
        }
        return read;
    }

    /// <summary>The unit holding a range's start, read.</summary>
    private static string Unit(UiaTextRange range, int unit)
    {
        UiaTextRange enclosing = range.Copy();
        enclosing.ExpandToEnclosingUnit(unit);
        return enclosing.GetText(-1);
    }

    /// <summary>Keys in the middle of a long line, with or without a reader listening to the editor's text pattern and
    /// asking after each key for the caret, its character, its word and its line, as one following the caret does.</summary>
    private sealed class KeysAsked
    {
        private readonly EditorRig rig;
        private readonly UiaTextElement? element;
        private readonly Action[] keys;
        private readonly long[] bytes;

        /// <summary>How many characters the reader heard, so its questions are answered in full.</summary>
        private long heard;

        public KeysAsked(int length, bool asking)
        {
            StringBuilder line = new("above\n");
            line.Insert(line.Length, "word café 中文 ok, x ", length / 20);
            rig = new EditorRig(line.Append("\nbelow\n").ToString());
            if (asking)
            {
                element = UiaTextElement.Unhosted(rig.Editor, rig.Editor.AccessibleText);
                element.Told += _ => { };
            }
            rig.PlaceCaret("above\n".Length + (length / 2 / 20 * 20) + 5);
            Ui.Settle();
            keys = [() => rig.Type("x"), () => rig.Press("BACK_SPACE"), () => rig.Press("RIGHT"), () => rig.Press("LEFT")];
            bytes = [.. keys.Select(_ => long.MaxValue)];
        }

        public void Round()
        {
            rig.Window.Activate();
            rig.Editor.Focus();
            Ui.Settle();
            for (int k = 0; k < keys.Length; k++)
            {
                GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                keys[k]();
                Ui.Settle();
                if (element is not null)
                {
                    UiaTextRange caret = element.CaretRange().Caret;
                    heard += Unit(caret, Uia.WordUnit).Length + Unit(caret, Uia.CharacterUnit).Length + element.GetValue().Length;
                }
                bytes[k] = Math.Min(bytes[k], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        public long Close()
        {
            Assert.True(element is null || heard > 0, "The reader heard nothing of the caret's word, character or line");
            rig.Window.Close();
            return bytes.Sum();
        }
    }
}
