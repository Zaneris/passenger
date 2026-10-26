using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The language server's help in a file of 20,000 lines costs a key what the key changed, never the file: its
/// colours, squiggles, hints and completion as you type, held to what the UI thread allocates and what reaches the server;
/// and an answer as large as the solution (usages by the thousand) is read off the UI thread. The files are the project's
/// from the start, so the class's windows share one warm server, which compiles them once (<see cref="SharedProject"/>);
/// each test still opens its own window on them as written. Allocations counted, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class LongFileAssistTests : IClassFixture<LongFileAssistTests.Project>
{
    private readonly Project project;

    public LongFileAssistTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>Completion after a dot reads the file's syntax (the live templates that fit there, the postfix templates
    /// for what is before the dot), which in a file of 20,000 lines is the highlighter's tree carried over the edit, never
    /// the file parsed again on the UI thread. What the window allocates is the count, not a time a slow machine would
    /// stretch: the frame that opens the list allocates less than one parse of the file does.</summary>
    [AvaloniaFact]
    public void Typing_a_dot_in_a_file_of_twenty_thousand_lines_opens_completion_without_parsing_the_file_again()
    {
        MainWindow window = project.Open("Dotted.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "        Console");
        editor.Focus();
        Ui.Settle();

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(Project.Dotted).GetRoot();
        long parse = GC.GetAllocatedBytesForCurrentThread() - before;

        // The list opens in the frame that has the server's answer, where the syntax at the dot is read.
        window.KeyTextInput(".");
        long opening = 0;
        DateTime limit = DateTime.UtcNow + TimeSpan.FromSeconds(60).Scaled();
        while (!window.Assist.IsCompletionOpen && DateTime.UtcNow < limit)
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            Ui.Hold(TimeSpan.FromMilliseconds(10), "one frame, whose allocations are measured");
            opening = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.True(window.Assist.IsCompletionOpen, "Completion never opened after the dot");
        Assert.Contains("WriteLine", window.Assist.CompletionLabels);
        Assert.True(opening < parse,
            $"The frame that opened completion allocated {opening / 1024} KB on the UI thread, more than the {parse / 1024} KB of one parse of the file: it parsed the file again");
        window.Close();
    }

    /// <summary>Squiggles in a file of 20,000 lines stay on their words as lines go in above them, and so do the
    /// diagnostics a fix asked for at the caret is given, a key reading a few characters rather than the whole file.
    /// The server's colours, hundreds of thousands of tokens, are decoded off the UI thread: no frame while they land
    /// allocates a quarter of what one decoded copy takes, nor a key half of it; through the keys typed before they
    /// land, and after, a local keeps its colour on its word, on screen and off it.</summary>
    [AvaloniaFact]
    public void Squiggles_and_colours_in_a_file_of_twenty_thousand_lines_stay_on_their_words_and_a_key_reads_and_decodes_only_what_it_changed()
    {
        string source = Project.Long;
        MainWindow window = project.Open("Long.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Passenger.App.CSharp.CSharpDocument csharp = window.CSharpService!.For(document)!;
        string[] literals = ["25", "75", "125", "175"];
        Ui.WaitFor(() => document.Markers.Count(m => m.Kind == MarkerKind.Error) == literals.Length, "the four errors' squiggles");
        void OnTheirWords(string when)
        {
            TextSnapshot text = document.Text.Snapshot;
            Marker[] errors = [.. document.Markers.Where(m => m.Kind == MarkerKind.Error).OrderBy(m => m.Range.Start)];
            Assert.True(errors.Select(m => text.GetText(m.Range)).SequenceEqual(literals),
                $"{when} the squiggles are under {string.Join(", ", errors.Select(m => $"'{text.GetText(m.Range)}'"))}");
            Passenger.Lsp.Diagnostic[] diagnosed = [.. csharp.Diagnostics.Where(d => d.Severity == Passenger.Lsp.DiagnosticSeverity.Error).OrderBy(d => d.Range.Start.Line)];
            Assert.Equal(literals.Length, diagnosed.Length);
            for (int i = 0; i < literals.Length; i++)
            {
                int line = text.GetLineFromOffset(errors[i].Range.Start);
                Assert.Equal($"        string wrong{literals[i]} = {literals[i]};", text.GetLineText(line));
                TextPosition start = new(diagnosed[i].Range.Start.Line, diagnosed[i].Range.Start.Character);
                TextPosition end = new(diagnosed[i].Range.End.Line, diagnosed[i].Range.End.Character);
                Assert.True(start.Line == line && text.GetText(TextRange.FromBounds(text.GetOffset(start), text.GetOffset(end))) == literals[i],
                    $"{when} the diagnostic under '{literals[i]}' on line {line + 1} is at {start.Line + 1}:{start.Column + 1}");
            }
        }
        OnTheirWords("Before any key");

        int top = source.IndexOf("seed + 0;", StringComparison.Ordinal) + "seed + 0;".Length;
        editor.Selections = SelectionSet.Single(top);
        editor.Focus();
        Ui.Settle();
        const int Keys = 5;
        long read = 0;
        for (int i = 0; i < Keys; i++)
        {
            long before = Passenger.Lsp.ServerLines.CharactersRead;
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Ui.Hold(TimeSpan.FromMilliseconds(10), "a frame after the key");
            read += Passenger.Lsp.ServerLines.CharactersRead - before;
            OnTheirWords($"After {i + 1} lines went in above them");
        }
        Assert.Equal(source.Count(c => c == '\n') + Keys, document.Text.Snapshot.LineCount - 1);
        Assert.True(read / Keys < source.Length / 100,
            $"A key read {read / Keys} characters to find the server's lines, of the file's {source.Length}: it read the whole file again");

        Ui.Settle();
        OnTheirWords("Once the server's diagnostics came again");

        // The server's colours: a local's use on screen below the keys, and one at the file's end.
        TokenKind? KindAt(int offset) => document.Highlighter?.GetSpans(document.Text.Snapshot, offset, 1)
            .Where(s => s.Start == offset).Select(s => (TokenKind?)s.Kind).FirstOrDefault();
        int near = document.Text.Snapshot.ToString().IndexOf("value0 * 3", StringComparison.Ordinal);
        int far = document.Text.Snapshot.ToString().LastIndexOf("value98 * 3", StringComparison.Ordinal);
        Ui.WaitFor(() => KindAt(near) == TokenKind.Local && KindAt(far) == TokenKind.Local, "the server's colours on the locals");
        (Passenger.Lsp.SemanticTokens? Tokens, int Version) asked = Ui.Await(csharp.Lsp!.SemanticTokensAsync(), "the whole file's tokens");
        long decoded = asked.Tokens!.Data.Length / 5 * System.Runtime.CompilerServices.Unsafe.SizeOf<SyntaxSpan>();
        Assert.True(decoded > 1_000_000, $"The file's tokens decode to only {decoded / 1024} KB");

        // Keys that open no completion, before the first statement's end: everything after them moves.
        int typing = document.Text.Snapshot.ToString().IndexOf("seed + 0;", StringComparison.Ordinal) + "seed + ".Length;
        editor.Selections = SelectionSet.Single(typing);
        editor.Focus();
        Ui.Settle();
        long keys = 0;
        const string Digits = "123";
        for (int i = 0; i < Digits.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput(Digits[i].ToString());
            Ui.Hold(TimeSpan.FromMilliseconds(10), "a frame after the key, whose allocations are measured");
            keys = Math.Max(keys, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(KindAt(near + i + 1) == TokenKind.Local, $"After {i + 1} keys the local's use below them lost its colour: {KindAt(near + i + 1)}");
        }

        // A statement put in at once, whose use of the local only the server colours as one.
        editor.Selections = SelectionSet.Single(document.Text.Snapshot.ToString().IndexOf("0;", typing, StringComparison.Ordinal) + 2);
        const string Statement = " int fresh = value0;";
        window.KeyTextInput(Statement);
        int fresh = editor.Selections.Primary.Caret - "value0;".Length;
        int moved = Digits.Length + Statement.Length;
        long landing = 0;
        DateTime limit = DateTime.UtcNow + TimeSpan.FromSeconds(90).Scaled();
        while ((KindAt(fresh) != TokenKind.Local || BackgroundWork.Pending > 0) && DateTime.UtcNow < limit)
        {
            Assert.True(KindAt(near + moved) == TokenKind.Local, $"While the server's colours were out the local's use below lost its colour: {KindAt(near + moved)}");
            long before = GC.GetAllocatedBytesForCurrentThread();
            Ui.Hold(TimeSpan.FromMilliseconds(10), "one frame, whose allocations are measured");
            landing = Math.Max(landing, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.True(KindAt(fresh) == TokenKind.Local, "The local used in the statement put in never took the server's colour");
        Assert.True(KindAt(near + moved) == TokenKind.Local, "The local's use below the keys lost its colour once the server's landed");
        Assert.True(landing < decoded / 4,
            $"A frame while the server's colours landed allocated {landing / 1024} KB on the UI thread, a quarter or more of the {decoded / 1024} KB the file's tokens decode to: they were decoded there");
        Assert.True(keys < decoded / 2, $"A key allocated {keys / 1024} KB on the UI thread, half or more of the {decoded / 1024} KB the file's tokens decode to");

        for (int i = 0; i < Digits.Length; i++)
        {
            window.KeyTextInput(Digits[i].ToString());
            Ui.Hold(TimeSpan.FromMilliseconds(10), "a frame after the key");
            Assert.True(KindAt(far + moved + i + 1) == TokenKind.Local, $"After {i + 1} more keys the local's use at the end of the file lost its colour: {KindAt(far + moved + i + 1)}");
        }
        Ui.Settle();
        Assert.Equal(TokenKind.Local, KindAt(far + moved + Digits.Length));
        Assert.Equal(TokenKind.Local, KindAt(fresh));
        window.Close();
    }

    /// <summary>Keys typed in one place while the server answers no colours (as while its projects load) keep the
    /// colours it gave last on their words, below the keys and at the file's end, each key costing no more than the one
    /// before. Each key waits for its colours' refusal, not for the file's diagnostics (seconds a key in 20,000 lines),
    /// which a key's colours never wait on.</summary>
    [AvaloniaFact]
    public void Keys_typed_while_the_server_gives_no_colours_carry_its_last_through_one_change_for_the_run()
    {
        string source = Project.Long;
        MainWindow window = project.Open("Long.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        TokenKind? KindAt(int offset) => document.Highlighter?.GetSpans(document.Text.Snapshot, offset, 1)
            .Where(s => s.Start == offset).Select(s => (TokenKind?)s.Kind).FirstOrDefault();
        int near = source.IndexOf("value0 * 3", StringComparison.Ordinal);
        int far = source.LastIndexOf("value98 * 3", StringComparison.Ordinal);
        Ui.WaitFor(() => KindAt(near) == TokenKind.Local && KindAt(far) == TokenKind.Local, "the server's colours on the locals");
        Ui.Settle();
        Passenger.Languages.CSharp.SemanticHighlighter colours = Assert.IsType<Passenger.Languages.CSharp.SemanticHighlighter>(document.Highlighter);
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        _ = server.RefuseNext(Passenger.Lsp.LspMethods.SemanticTokensRange, 1000);
        _ = server.RefuseNext(Passenger.Lsp.LspMethods.SemanticTokensFull, 1000);
        try
        {
            editor.Selections = SelectionSet.Single(source.IndexOf("seed + 0;", StringComparison.Ordinal) + "seed + 0".Length);
            editor.Focus();
            const int Keys = 12;
            for (int i = 0; i < Keys; i++)
            {
                window.KeyTextInput((i % 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(colours.Asking, $"Key {i + 1} asked the server for no colours");
                Ui.PumpUntil(() => !colours.Asking, $"the server's refusal of the colours after key {i + 1}");
                Assert.True(KindAt(near + i + 1) == TokenKind.Local, $"After {i + 1} keys the local's use below them lost its colour: {KindAt(near + i + 1)}");
            }
            Assert.True(colours.Carrying <= 1, $"The server's last colours were carried through {colours.Carrying} changes, one for each of the {Keys} keys typed in one place");
            Assert.Equal(TokenKind.Local, KindAt(far + Keys));
        }
        finally
        {
            _ = server.RefuseNext(Passenger.Lsp.LspMethods.SemanticTokensRange, 0);
            _ = server.RefuseNext(Passenger.Lsp.LspMethods.SemanticTokensFull, 0);
        }
        window.Close();
    }

    /// <summary>Until the parse catches up with a key (it falls behind while a solution loads), the lines on screen
    /// keep the colours read for them from the tree before the key, carried through it: a key classifies again the line
    /// it changed and no other, and they are the colours the tree gives once the parse has landed. Counted in the
    /// characters classified on the UI thread, not a time.</summary>
    [AvaloniaFact]
    public void Until_the_parse_catches_up_a_key_classifies_again_only_the_line_it_changed()
    {
        string source = Project.Long;
        MainWindow window = project.Open("Long.cs");
        OpenDocument document = window.EditorArea.Active!;
        Passenger.Languages.CSharp.CSharpHighlighter syntax = document.Syntax!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(source.IndexOf("seed + 0;", StringComparison.Ordinal) + "seed + 0".Length);
        editor.Focus();
        Ui.Settle();
        TextSnapshot text = document.Text.Snapshot;
        int start = text.GetLineStart(0);
        int end = text.GetLine(80).EndIncludingBreak;
        _ = syntax.GetSpans(text, start, end - start);

        // Through the editor but not its input, whose end runs the window's loop, where a quick parse lands.
        long before = syntax.CharactersClassified;
        int caret = editor.Selections.Primary.Caret;
        editor.ApplyEdits([new TextEdit(caret, 0, "1")], SelectionSet.Single(caret + 1));
        TextSnapshot typed = document.Text.Snapshot;
        Assert.True(syntax.Newest is null, "The key's parse landed before the screen's colours were read");
        Assert.Equal(text.Length + 1, typed.Length);
        SyntaxSpan[] carried = [.. syntax.GetSpans(typed, start, end + 1 - start)];
        long classified = syntax.CharactersClassified - before;
        Assert.True(classified < (end - start) / 10,
            $"A key classified {classified} characters again of the {end - start} on screen: it classified every line drawn, not the one it changed");

        Ui.Settle();
        SyntaxSpan[] landed = [.. syntax.GetSpans(typed, start, end + 1 - start)];
        Assert.True(carried.SequenceEqual(landed),
            $"The colours carried through the key differ from the tree's once its parse landed, first at {carried.Zip(landed).FirstOrDefault(pair => pair.First != pair.Second)}");
        window.Close();
    }

    /// <summary>A file of 20,000 lines is a million numbers of tokens, which the server takes a second to make and the
    /// IDE megabytes to read: while typing goes on with pauses, only the lines on screen are asked for again, and what
    /// only the server colours on screen is coloured from them; once typing stops, the whole file is asked for once, a word
    /// from the server that its colours moved read before that request crossed asking nothing more, however late it is
    /// parsed. Counted in the protocol's own trace, once the colours have nothing more to ask.</summary>
    [AvaloniaFact]
    public void Typing_with_pauses_in_a_file_of_twenty_thousand_lines_asks_for_the_screens_colours_and_the_whole_files_once_it_stops()
    {
        string source = Project.Long;
        MainWindow? window = null;
        int whole = 0;
        int screens = 0;
        int refreshedSince = 0;
        try
        {
            window = project.Open("Long.cs");
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            TokenKind? KindAt(int offset) => document.Highlighter?.GetSpans(document.Text.Snapshot, offset, 1)
                .Where(s => s.Start == offset).Select(s => (TokenKind?)s.Kind).FirstOrDefault();
            int far = source.LastIndexOf("value98 * 3", StringComparison.Ordinal);
            Ui.WaitFor(() => KindAt(far) == TokenKind.Local, "the server's colours on the file's last local");
            Ui.Settle();
            Passenger.Languages.CSharp.SemanticHighlighter colours = Assert.IsType<Passenger.Languages.CSharp.SemanticHighlighter>(document.Highlighter);
            // Every colour request and refresh as the wire had them, for a failure to say.
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            System.Collections.Concurrent.ConcurrentQueue<string> wire = new();
            Passenger.Lsp.LanguageServer.Trace = (server, message) =>
            {
                if (System.Text.RegularExpressions.Regex.Match(message, "\"method\":\"([^\"]*semanticTokens[^\"]*)\"") is { Success: true } method)
                {
                    string file = System.Text.RegularExpressions.Regex.Match(message, "\"uri\":\"[^\"]*/([^\"/]+)\"") is { Success: true } uri ? $" {uri.Groups[1].Value}" : "";
                    wire.Enqueue($"{clock.ElapsedMilliseconds} ms, server {server}: {message[..3]} {method.Groups[1].Value}{file}");
                }
                if (message.StartsWith("-->", StringComparison.Ordinal) && message.Contains($"\"{Passenger.Lsp.LspMethods.SemanticTokensFull}\"", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref whole);
                }
                else if (message.StartsWith("-->", StringComparison.Ordinal) && message.Contains($"\"{Passenger.Lsp.LspMethods.SemanticTokensRange}\"", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref screens);
                }
                else if (message.StartsWith("<--", StringComparison.Ordinal) && message.Contains($"\"{Passenger.Lsp.LspMethods.SemanticTokensRefresh}\"", StringComparison.Ordinal)
                    && Volatile.Read(ref whole) > 0)
                {
                    // The server's word that its colours moved, after the file's were asked for: they are rightly asked again.
                    Interlocked.Increment(ref refreshedSince);
                }
            };

            // Statements typed one by one, a pause after each long enough for the screen's colours and short of the idle.
            int at = source.IndexOf("seed + 0;", StringComparison.Ordinal) + "seed + 0;".Length;
            editor.Selections = SelectionSet.Single(at);
            editor.Focus();
            for (int i = 0; i < 4; i++)
            {
                window.KeyTextInput($" int fresh{i} = value0;");
                int used = editor.Selections.Primary.Caret - "value0;".Length;
                Ui.PumpUntil(() => KindAt(used) == TokenKind.Local, $"the screen's colours on the local used in statement {i + 1}");
                Ui.Hold(TimeSpan.FromMilliseconds(300), "a pause in typing, shorter than the idle after which the whole file's colours are asked for");
            }
            Assert.True(screens > 0, "The screen's colours were never asked for as the statements were typed");
            Assert.True(whole == 0, $"The whole file's colours were asked for {whole} times while typing went on with pauses:\n{string.Join('\n', wire)}");

            Ui.PumpUntil(() => Volatile.Read(ref whole) > 0, "the whole file's colours once typing stopped");
            // A needless second ask waits out the idle after the screen's colours, which no settle waits for, and is still asking.
            Ui.PumpUntil(() => !colours.Asking, "the colours' last ask answered, a needless second one included");
            Ui.Settle();
            int refreshes = Volatile.Read(ref refreshedSince);
            Assert.True(whole <= 1 + refreshes,
                $"Once typing stopped the whole file's colours were asked for {whole} times, with {refreshes} word from the server since that they moved:\n{string.Join('\n', wire)}");
            Assert.Equal(TokenKind.Local, KindAt(far + 4 * " int freshN = value0;".Length));
        }
        finally
        {
            Passenger.Lsp.LanguageServer.Trace = null;
            window?.Close();
        }
    }

    /// <summary>Find Usages' answer, like every answer that grows with the solution, is read into places off the UI thread,
    /// before what asked resumes there: a method called four thousand times answers as many places, and no frame while its
    /// answer lands allocates on the UI thread a quarter of what reading that answer takes, nor much past the most a frame
    /// does while a local's two usages land or while nothing is asked (the caret's blink draws the editor again). What the
    /// window allocates is the count, not a time a busy machine stretches.</summary>
    [AvaloniaFact]
    public void The_usages_of_a_method_called_thousands_of_times_are_read_off_the_UI_thread()
    {
        MainWindow window = project.Open("Calls.cs");
        (int few, long fewLanding, _) = Usages(window, "int total =");
        (int many, long manyLanding, long read) = Usages(window, "int Of(");
        long idle = Frames(() => false, 120);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{few} usages landed in frames of at most {fewLanding / 1024} KB on the UI thread; {many} in {manyLanding / 1024} KB, reading them taking {read / 1024} KB; "
            + $"a frame with nothing asked allocates up to {idle / 1024} KB");
        Assert.True(many > 4000, $"The method called four thousand times had {many} usages");
        Assert.True(manyLanding < read / 4,
            $"A frame while {many} usages landed allocated {manyLanding / 1024} KB on the UI thread, a quarter or more of the {read / 1024} KB reading them takes: they were read there");
        Assert.True(manyLanding <= Math.Max(fewLanding, idle) + (64 * 1024),
            $"A frame while {many} usages landed allocated {manyLanding / 1024} KB on the UI thread, against {fewLanding / 1024} KB for {few} and {idle / 1024} KB with nothing asked");
        window.Close();
    }

    /// <summary>The usages of what the shown file declares in <paramref name="declaration"/> (its own among them): how
    /// many, the most a frame on the UI thread allocated while they were asked for there and landed, and what reading the
    /// answer allocates (the same asked for from the thread pool, the window's thread waiting).</summary>
    private static (int Usages, long Landing, long Read) Usages(MainWindow window, string declaration)
    {
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(document)!.Lsp!;
        int declared = document.Text.Snapshot.ToString().IndexOf(declaration, StringComparison.Ordinal) + "int ".Length;
        long read = GC.GetTotalAllocatedBytes(precise: true);
        int usages = Ui.Idle(Waiting.Apart(() => lsp.ReferencesAsync(declared)), $"the usages in {document.Name} asked from the thread pool").Count;
        read = GC.GetTotalAllocatedBytes(precise: true) - read;
        // What the wait above put off, done before the frames are counted.
        Ui.Settle();
        Task<IReadOnlyList<Passenger.Lsp.LspLocation>> asking = lsp.ReferencesAsync(declared);
        long landing = Frames(() => asking.IsCompleted, int.MaxValue);
        if (!asking.IsCompletedSuccessfully)
        {
            throw new Xunit.Sdk.XunitException($"The usages in {document.Name} never came");
        }
        Assert.Equal(usages, asking.Result.Count);
        return (usages, landing, read);
    }

    /// <summary>The most one frame allocated on the UI thread, of those run until <paramref name="done"/> or
    /// <paramref name="most"/> frames have run (a minute at most).</summary>
    private static long Frames(Func<bool> done, int most)
    {
        long largest = 0;
        DateTime limit = DateTime.UtcNow + TimeSpan.FromSeconds(60).Scaled();
        for (int frame = 0; frame < most && !done() && DateTime.UtcNow < limit; frame++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Ui.Hold(TimeSpan.FromMilliseconds(10), "one frame, whose allocations are measured");
            largest = Math.Max(largest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return largest;
    }

    /// <summary>A key typed above four thousand calls, a parameter's name hint before each argument, costs the UI
    /// thread about what it does above sixty (a screen of them, the rest naming their parameters), soft-wrapped or not,
    /// and each hint still stands before its argument.</summary>
    [AvaloniaFact]
    public void A_key_above_thousands_of_hinted_calls_costs_what_it_does_above_a_few_and_wraps_only_its_line()
    {
        MainWindow window = project.Open("Few.cs");
        foreach (bool wrapping in new[] { false, true })
        {
            (int few, long fewBytes, long fewWrapped) = HintedKeys(window, "Few.cs", wrapping);
            (int many, long manyBytes, long manyWrapped) = HintedKeys(window, "Calls.cs", wrapping);
            string how = wrapping ? "soft-wrapped" : "not wrapped";
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"A key {how} above {few} hints allocated {fewBytes / 1024} KB on the UI thread and wrapped {fewWrapped} characters; above {many}, {manyBytes / 1024} KB and {manyWrapped}");
            Assert.True(manyBytes <= fewBytes + (32 * 1024),
                $"A key {how} above {many} hints allocated {manyBytes / 1024} KB on the UI thread, {(manyBytes - fewBytes) / 1024} KB more than above {few}: it copied or sorted every hint");
            Assert.True(manyWrapped <= fewWrapped,
                $"A key {how} above {many} hints wrapped {manyWrapped} characters, more than the {fewWrapped} it wraps above {few}: it wrapped the hinted lines again");
        }
        window.Close();
    }

    /// <summary>Keys typed above a file's calls once its hints have come, with soft wrap on or off: how many hints, and the
    /// bytes each key and the frame after it allocate on the UI thread and the characters each wraps, on average; every hint
    /// then still stands before its argument.</summary>
    private (int Hints, long Bytes, long Wrapped) HintedKeys(MainWindow window, string file, bool wrapping)
    {
        window.OpenFile(project.PathOf(file));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        int calls = (document.Text.Snapshot.ToString().Length - document.Text.Snapshot.ToString().Replace("Of(3, 4)", string.Empty, StringComparison.Ordinal).Length) / "Of(3, 4)".Length;
        Ui.WaitFor(() => editor.InlayHints.Count == 2 * calls, $"the hints of {file}'s {calls} calls");
        if (document.SoftWrap != wrapping)
        {
            Ui.Run(window, AppActions.SoftWrap);
        }
        Ui.PlaceAfter(editor, "int total = 0");
        editor.Focus();
        Ui.Settle();
        const int Keys = 5;
        long wrapped = editor.CharactersWrapped;
        long bytes = 0;
        for (int i = 0; i < Keys; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput("1");
            Ui.Hold(TimeSpan.FromMilliseconds(10), "a frame after the key, whose allocations are measured");
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        wrapped = editor.CharactersWrapped - wrapped;
        TextSnapshot text = document.Text.Snapshot;
        foreach (InlayHint hint in editor.InlayHints)
        {
            string argument = hint.Label == "width:" ? "3" : "4";
            Assert.True(text.GetText(hint.Offset, 1) == argument,
                $"After {Keys} keys above them, {file}'s hint '{hint.Label}' stands before '{text.GetText(hint.Offset, Math.Min(8, text.Length - hint.Offset))}' on line {text.GetLineFromOffset(hint.Offset) + 1}");
        }
        Assert.Equal(2 * calls, editor.InlayHints.Count);
        return (2 * calls, bytes / Keys, wrapped / Keys);
    }

    /// <summary>The Structure window over a file of four thousand members is scrolled with the wheel as a person scrolls
    /// it: its rows are handed other members, none made again, and a step costs the UI thread what one down a file of two
    /// hundred does.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_the_structure_of_four_thousand_members_hands_the_rows_other_members_never_making_one_again()
    {
        (long fewStep, int fewMade) = StructureScrolled("Long.cs", 200);
        (long manyStep, int manyMade) = StructureScrolled("Calls.cs", 4000);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Structure: a wheel step allocated {fewStep / 1024} KB on the UI thread over Long.cs's members, {manyStep / 1024} KB over Calls.cs's; a scroll past the rows shown made {fewMade} and {manyMade} again");
        Assert.True(fewMade == 0 && manyMade == 0,
            $"Scrolling the Structure window made {fewMade} rows of Long.cs's members and {manyMade} of Calls.cs's again, where each row is kept and handed another: every member scrolled into view was built and styled anew");
        Assert.True(manyStep <= fewStep + (64 * 1024),
            $"A wheel step down the Structure window allocated {manyStep / 1024} KB on the UI thread over Calls.cs's members, against {fewStep / 1024} KB over Long.cs's: a step costs the list's length");
    }

    /// <summary>Typing through completion over a type of four thousand members (the best 200 listed), Down a row at a time
    /// past its first screen and on into the list a letter narrows, hands the rows on screen the members they come to, none
    /// made again.</summary>
    [AvaloniaFact]
    public void Typing_through_completion_over_four_thousand_members_hands_its_rows_other_members_never_making_one_again()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "    static void Main()\n    {");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Calls");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen && window.Assist.CompletionLabels.Count(l => l.StartsWith("Call", StringComparison.Ordinal)) > 100,
            () => $"Calls's members, where the list shows [{string.Join(", ", window.Assist.CompletionLabels.Take(5))}] of {window.Assist.CompletionLabels.Count}");
        HashSet<Avalonia.Controls.Control> containers = [];
        HashSet<Avalonia.Controls.Control> rows = [];
        void Seen()
        {
            foreach (Avalonia.Controls.Control row in window.Assist.CompletionRowControls)
            {
                rows.Add(row);
                containers.Add(row.FindAncestorOfType<Avalonia.Controls.ListBoxItem>() ?? throw new Xunit.Sdk.XunitException("A completion row is in no container of the list"));
            }
        }
        Seen();
        for (int i = 0; i < 30; i++)
        {
            Ui.Press(window, "DOWN");
            Seen();
        }
        foreach (char letter in "Call1")
        {
            window.KeyTextInput(letter.ToString());
            Ui.Settle();
            Seen();
        }
        Assert.True(window.Assist.IsCompletionOpen && window.Assist.CompletionLabels.All(l => l.StartsWith("Call1", StringComparison.Ordinal)),
            $"Narrowed by Call1 the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(5))}");
        for (int i = 0; i < 30; i++)
        {
            Ui.Press(window, "DOWN");
            Seen();
        }
        Assert.False(window.Assist.CompletionRowControls.Count == 0, "No row of the list is on screen after the keys");
        Assert.True(rows.Count == containers.Count,
            $"Typing through the completion list made {rows.Count - containers.Count} rows again for its {containers.Count} containers, where each row is kept and handed another: every member scrolled into view was built and styled anew");
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>The least the UI thread allocated for a wheel step down the Structure window over a file's members, and
    /// how many rows a scroll past those first shown made again.</summary>
    private (long Least, int Made) StructureScrolled(string file, int members)
    {
        MainWindow window = project.Open(file);
        window.Perform(AppActions.ShowStructure);
        Passenger.App.ToolWindows.StructureView structure = window.StructureView;
        Ui.WaitFor(() => structure.Rows.Count > members, () => $"{file}'s {members} members in Structure, where it lists {structure.Rows.Count}");
        Ui.Settle();
        (long, int) scrolled = LongListsTests.Scrolled(structure.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single());
        window.Close();
        return scrolled;
    }

    /// <summary>A small console project on disk with two files of 20,000 lines and two of calls, restored once for every
    /// test, and the warm server they share on it, which loads and compiles the files once.</summary>
    public sealed class Project : SharedProject
    {
        /// <summary>Calls.cs: a method of two parameters called by four thousand members with literal arguments, each
        /// argument with its parameter's name hint before it; Few.cs the same, but for the first sixty, a screen of them,
        /// the rest naming their parameters, which shows none.</summary>
        public static readonly string Calls = Called("Calls", 4000, 4000);

        public static readonly string Few = Called("Few", 4000, 60);

        /// <summary>Long.cs: two hundred methods of a hundred lines and more, every fiftieth with an error, a string given a number.</summary>
        public static readonly string Long = TwentyThousandLines(errors: true, "Long", "}\n");

        /// <summary>Dotted.cs: two hundred methods of a hundred lines with no error, and a last one where a dot goes after Console.</summary>
        public static readonly string Dotted = TwentyThousandLines(errors: false, "Dotted", "    public static void Last()\n    {\n        Console\n    }\n}\n");

        public Project()
            : base("long-assist-")
        {
            File.WriteAllText(PathOf("Sample.csproj"),
                TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
            File.WriteAllText(PathOf("Long.cs"), Long);
            File.WriteAllText(PathOf("Dotted.cs"), Dotted);
            File.WriteAllText(PathOf("Calls.cs"), Calls);
            File.WriteAllText(PathOf("Few.cs"), Few);
        }

        /// <summary>A method of two parameters and members calling it, each in one of its own, the first
        /// <paramref name="hinted"/> with literal arguments, which show their parameters' names, the rest naming them.</summary>
        private static string Called(string name, int calls, int hinted)
        {
            StringBuilder text = new($"static class {name}\n{{\n    public static int Of(int width, int height) => width * height;\n\n    public static int Typed()\n    {{\n        int total = 0;\n        return total;\n    }}\n\n");
            for (int i = 0; i < calls; i++)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    public static int Call{i}() => {(i < hinted ? "Of(3, 4)" : "Of(width: 3, height: 4)")};\n");
            }
            return text.Append("}\n").ToString();
        }

        /// <summary>Two hundred methods of a hundred lines and more: 20,000 lines, as many as Roslyn parses fresh every time.</summary>
        private static string TwentyThousandLines(bool errors, string name, string end)
        {
            StringBuilder text = new($"using System;\n\nstatic class {name}\n{{\n");
            for (int i = 0; i < 200; i++)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    public static int Method{i}(int seed)\n    {{\n        int value0 = seed + {i};\n");
                if (errors && i % 50 == 25)
                {
                    text.Append(System.Globalization.CultureInfo.InvariantCulture, $"        string wrong{i} = {i};\n");
                }
                for (int j = 1; j < 100; j++)
                {
                    text.Append(System.Globalization.CultureInfo.InvariantCulture, $"        int value{j} = value{j - 1} * 3 + \"{i}:{j}\".Length;\n");
                }
                text.Append("        return value99;\n    }\n\n");
            }
            string source = text.Append(end).ToString();
            Assert.True(source.Count(c => c == '\n') > 20_000);
            return source;
        }
    }
}
