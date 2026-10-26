using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Problems without a build: the current file's as they are typed, and with solution-wide analysis on, every
/// file's, a file nobody has open included, grouped by project and file.</summary>
public sealed class ProblemsTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    /// <summary>Each test's own folder, restored a few at a time with the next tests' rather than each in its turn.</summary>
    public ProblemsTests(RestoredFolders folders) => folder = folders.Take("problems-", made =>
    {
        TestFolders.Write(made, "Sample.csproj", TestProjects.Console());
        TestFolders.Write(made, "Program.cs", "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
        TestFolders.Write(made, "Helper.cs", "static class Helper\n{\n    public static int Twice(int value) => value * 2;\n}\n");
        TestFolders.Write(made, "Caller.cs", "static class Caller\n{\n    public static int Four() => Helper.Twice(2);\n}\n");
        TestFolders.Write(made, "Bad.cs", "class Bad\n{\n    public int Count = \"none\";\n}\n");
        return made;
    });

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Solution_wide_analysis_lists_every_files_errors_and_follows_an_edit_that_breaks_a_file_nobody_has_open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Helper.cs");
        Ui.Settle();
        ProblemsModel problems = window.BottomPanel.Problems;
        Assert.Null(problems.SolutionProblems);

        window.Perform(AppActions.ToggleSolutionAnalysis);
        Assert.True(window.Settings.SolutionWideAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");
        Problem bad = problems.SolutionProblems!.First(p => p.Code == "CS0029");
        Assert.EndsWith("Bad.cs", bad.Path, StringComparison.Ordinal);
        Assert.Equal(new TextPosition(2, 23), bad.Position);
        problems.Shown = ProblemsTab.Solution;
        Assert.Equal(["Sample", "Bad.cs"], problems.Rows.Where(r => r.Problem is null).Select(r => r.Label).Take(2));

        string text = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        int at = text.IndexOf("Twice", StringComparison.Ordinal);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(new Selection(at, at + "Twice".Length));
        window.KeyTextInput("Thrice");
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Path?.EndsWith("Caller.cs", StringComparison.Ordinal) == true) == true,
            "the broken caller's error", TimeSpan.FromMinutes(1));
        Assert.Empty(problems.FileProblems);
        Assert.Equal(["Sample", "Bad.cs", "Caller.cs"], problems.Rows.Where(r => r.Problem is null).Select(r => r.Label));
        ListedAsAfresh(problems, "once the caller broke");

        window.KeyTextInput(" x");
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the open file's error");
        Assert.Empty(problems.Problems);
        string helper = window.EditorArea.Active!.Path;
        Ui.WaitFor(() => problems.Rows.Any(r => r.Problem?.Path == helper), "the open file's error in the Solution tab");
        ListedAsAfresh(problems, "once the open file had an error");

        window.ShowToolWindow(ToolWindowIds.Problems);
        problems.Shown = ProblemsTab.File;
        Avalonia.Controls.ListBox rows = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.ProblemsWindow).OfType<Avalonia.Controls.ListBox>().First();
        ProblemRow chosen = problems.Rows.First(r => r.Problem is { Severity: ProblemSeverity.Error });
        Ui.RightClick(Ui.Row(rows, chosen));
        Ui.Choose(rows.ContextMenu, "Copy Message");
        Ui.WaitFor(() => Ui.Clipboard(window) == chosen.Problem!.Message, "the message on the clipboard");

        problems.Shown = ProblemsTab.Solution;
        int typed = window.EditorArea.CodeEditor.Document.Snapshot.ToString().IndexOf(" x", StringComparison.Ordinal);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(new Selection(typed, typed + " x".Length));
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => !problems.Rows.Any(r => r.Problem?.Path == helper), "the open file's rows to leave the Solution tab");
        Assert.Equal(["Sample", "Bad.cs", "Caller.cs"], problems.Rows.Where(r => r.Problem is null).Select(r => r.Label));
        ListedAsAfresh(problems, "once the open file was mended");

        window.Perform(AppActions.ToggleSolutionAnalysis);
        Assert.Null(problems.SolutionProblems);
        Assert.Equal(ProblemsTab.File, problems.Shown);
        window.Close();
    }

    /// <summary>The Solution tab's rows, as the pulls since it was shown left them, are those it lists afresh.</summary>
    private static void ListedAsAfresh(ProblemsModel problems, string when)
    {
        string[] Listed() => [.. problems.Rows.Select(r => $"{new string(' ', r.Depth * 2)}{r.Label}  {r.Detail}")];
        string[] kept = Listed();
        problems.Shown = ProblemsTab.File;
        problems.Shown = ProblemsTab.Solution;
        string[] afresh = Listed();
        Assert.True(kept.SequenceEqual(afresh),
            $"The Solution tab's rows {when} are not those it lists afresh:\n{string.Join('\n', kept)}\n\nAfresh:\n{string.Join('\n', afresh)}");
    }

    /// <summary>A pull the server refuses or cuts short (Roslyn cancels one a change made stale) is asked again, so the
    /// solution's problems go on following edits.</summary>
    [AvaloniaFact]
    public void Solution_wide_analysis_goes_on_following_edits_after_the_server_refuses_a_pull()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Helper.cs");
        Ui.Settle();
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");

        // The pulls held open answer the edit, and the next pull of every source is refused.
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        Task refused = server.RefuseNext(Passenger.Lsp.LspMethods.WorkspaceDiagnostic, server.WorkspaceDiagnosticSources.Count);
        Rename(window, "Twice", "Thrice");
        bool CallerBroken() => problems.SolutionProblems?.Any(p => p.Path?.EndsWith("Caller.cs", StringComparison.Ordinal) == true) == true;
        Ui.WaitFor(CallerBroken, "the broken caller's error", TimeSpan.FromMinutes(1));
        Ui.WaitFor(() => refused.IsCompleted, "the server to refuse the next pull of every source", TimeSpan.FromMinutes(1));

        Rename(window, "Thrice", "Twice");
        Ui.WaitFor(() => problems.SolutionProblems is { } listed && !CallerBroken(), "the mended caller's error to go, after the server refused a pull",
            TimeSpan.FromMinutes(1));
        Assert.Equal(server, window.CSharpService.Session?.Server);
        window.Close();
    }

    /// <summary>Keys typed in a burst have solution-wide analysis ask the server again once typing pauses, not for every key:
    /// its pulls held open for a change are let go as the first key comes and asked again once the open file's own pull has
    /// answered, and the Solution tab then lists what the keys broke in a file nobody has open.</summary>
    [AvaloniaFact]
    public void Keys_typed_in_a_burst_have_solution_wide_analysis_ask_the_server_again_once_typing_pauses()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Helper.cs");
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + "Twice".Length);
        editor.Focus();
        bool CallerBroken() => problems.SolutionProblems?.Any(p => p.Path?.EndsWith("Caller.cs", StringComparison.Ordinal) == true) == true;
        // A key and its undoing first, which the server answers as it does a session's first change.
        Ui.Press(window, "SPACE");
        Ui.Press(window, "BACK_SPACE");
        Ui.Settle();
        Passenger.App.CSharp.CSharpService service = window.CSharpService!;
        IReadOnlyList<string?> sources = service.Session!.Server.WorkspaceDiagnosticSources;
        int[] before = [.. sources.Select(service.PullsAsked)];

        const int Keys = 10;
        for (int i = 0; i < Keys; i++)
        {
            window.KeyTextInput("x");
            Ui.Hold(TimeSpan.FromMilliseconds(150), "the time between a burst's keys, well inside the open file's pull's pause");
        }
        int[] during = [.. sources.Select((source, i) => service.PullsAsked(source) - before[i])];
        Ui.WaitFor(CallerBroken, "the error the keys made in the caller nobody has open", TimeSpan.FromMinutes(1));
        Ui.Settle();
        string Asked() => string.Join(", ", sources.Select((source, i) => $"{source ?? "every source"} {during[i]} then {service.PullsAsked(source) - before[i] - during[i]}"));
        Assert.True(during.All(n => n == 0),
            $"Solution-wide analysis asked the server again while {Keys} keys were typed in a burst, where it asks only once typing pauses (during the keys, then after: {Asked()})");
        window.Close();
    }

    /// <summary>The open file's word typed over, as a person selecting it and typing does.</summary>
    private static void Rename(MainWindow window, string from, string to)
    {
        string text = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        int at = text.IndexOf(from, StringComparison.Ordinal);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(new Selection(at, at + from.Length));
        window.KeyTextInput(to);
    }

    /// <summary>Lines typed above a file's problems move them, and the File tab says where they are now after every key,
    /// each row's place shown on its own line of the list as it was: the list's rows are not made again for every
    /// key.</summary>
    [AvaloniaFact]
    public void Lines_typed_above_a_files_problems_move_their_rows_without_making_the_list_again()
    {
        Write("Several.cs", "class Several\n{\n    int First = \"one\";\n    int Second = \"two\";\n    string Third = 3;\n}\n");
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Several.cs");
        window.Perform(AppActions.ShowProblems);
        Ui.Settle();
        ProblemsModel problems = window.BottomPanel.Problems;
        Assert.Equal(ProblemsTab.File, problems.Shown);
        Ui.WaitFor(() => problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Error) == 3, "the file's three errors");
        Avalonia.Controls.ListBox list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.ProblemsWindow).OfType<Avalonia.Controls.ListBox>().First();
        // The text on each row that says where its problem is, as the list shows it.
        Avalonia.Controls.TextBlock[] Shown() => [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Avalonia.Controls.ListBoxItem>()
            .Select(item => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(item).OfType<Avalonia.Controls.TextBlock>()
                .First(t => t.Text?.StartsWith("Several.cs:", StringComparison.Ordinal) == true))];
        string[] Places() => [.. Shown().Select(t => t.Text ?? string.Empty)];
        Avalonia.Controls.TextBlock[] before = Shown();
        Assert.Equal(3, before.Length);
        Assert.Equal(["Several.cs:3:17", "Several.cs:4:18", "Several.cs:5:20"], Places());

        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(1, 1));
        editor.Focus();
        for (int i = 1; i <= 3; i++)
        {
            Ui.Press(window, "ENTER");
            Assert.Equal([2 + i, 3 + i, 4 + i], problems.FileProblems.Select(p => p.Position.Line));
            Assert.True(Shown().SequenceEqual(before), $"After {i} lines went in above the problems the File tab's rows were made again");
            Assert.Equal([$"Several.cs:{3 + i}:17", $"Several.cs:{4 + i}:18", $"Several.cs:{5 + i}:20"], Places());
        }

        Ui.Settle();
        Assert.Equal(["Several.cs:6:17", "Several.cs:7:18", "Several.cs:8:20"], Places());
        window.Close();
    }

    /// <summary>A file with thousands of problems types as one with few does: every key moves the file's markers, and the
    /// File tab lists where they went once typing pauses, rather than placing and listing every one again for each
    /// key. Only keys that came within the pause of the one before, by the clock, are counted: a loaded machine's keys may
    /// come further apart, and the pause is then over between them.</summary>
    [AvaloniaFact]
    public void Keys_typed_above_thousands_of_problems_list_them_again_once_typing_pauses()
    {
        System.Text.StringBuilder warned = new("class Warned\n{\n    static void All()\n    {\n");
        for (int i = 0; i < 3000; i++)
        {
            warned.Append(System.Globalization.CultureInfo.InvariantCulture, $"        int unused{i};\n");
        }
        Write("Warned.cs", warned.Append("    }\n}\n").ToString());
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Warned.cs");
        window.Perform(AppActions.ShowProblems);
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.FileProblems.Count == 3000, "the file's 3,000 warnings", TimeSpan.FromMinutes(2));
        Assert.Equal(new TextPosition(4, 12), problems.FileProblems[0].Position);
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(1, 1));
        editor.Focus();
        Ui.Settle();

        // Keys in quick succession, each one's own work done but no pause between them, until eight came within the pause.
        int listed = 0;
        int quick = 0;
        int typed = 0;
        IReadOnlyList<Problem> shown = problems.FileProblems;
        const int Keys = 8;
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        while (quick < Keys && typed < Keys * 4)
        {
            long pressed = System.Diagnostics.Stopwatch.GetTimestamp();
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (typed == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(last) < MainWindow.ProblemsPause)
            {
                quick++;
                listed += ReferenceEquals(problems.FileProblems, shown) ? 0 : 1;
            }
            shown = problems.FileProblems;
            last = pressed;
            typed++;
        }
        Assert.True(quick == Keys, $"Of {typed} keys typed in quick succession only {quick} came within {MainWindow.ProblemsPause.TotalMilliseconds} ms of the one before");
        Assert.True(listed <= Keys / 4, $"The File tab listed the file's 3,000 problems again {listed} times for {Keys} keys typed in quick succession");

        Ui.Settle();
        Assert.Equal(3000, problems.FileProblems.Count);
        Assert.Equal(new TextPosition(4 + typed, 12), problems.FileProblems[0].Position);
        Assert.Equal(new TextPosition(4 + typed + 2999, 12), problems.FileProblems[^1].Position);
        window.Close();
    }

    /// <summary>A typing pause lists again only the problems it changed, the open file's as its pull lands, however many the
    /// solution has: with 4,000 warnings in twenty files nobody has open, the Solution tab shown, a pause allocates on the UI
    /// thread under 8 bytes a problem more than with one in each.</summary>
    [AvaloniaFact]
    public void A_typing_pause_lists_again_only_the_open_files_problems_however_many_the_solution_has()
    {
        (long few, int fewListed, string fewTaken) = UiBytesPerPause(warnings: 1);
        (long many, int manyListed, string manyTaken) = UiBytesPerPause(warnings: 200);
        int more = manyListed - fewListed;
        long each = (many - few) / more;
        string blinks = $"less what their time cost the window at rest: {manyTaken}, and {fewTaken}";
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A typing pause allocated {many:N0} bytes on the UI thread with {manyListed} problems in the solution, {few:N0} with {fewListed} ({blinks}): {each} bytes a problem.");
        Assert.True(each < 8,
            $"A typing pause allocated {many / 1024:N0} KB on the UI thread with {manyListed} problems in the solution, {few / 1024:N0} KB with {fewListed} ({blinks}): {each} bytes for each problem, which the window listed again");
    }

    /// <summary>The keys each case of a typing pause's cost types.</summary>
    private const int PauseKeys = 5;

    /// <summary>What a key typed in an open file and its pause allocate on the UI thread, less what the pause's time cost the
    /// window as it costs at rest, measured in each case: the caret's blinks it drew (<see cref="Ui.BlinkBytes"/>) and each
    /// millisecond of the wait (<see cref="Ui.RestBytesPerMs"/>), since the larger solution's server answers later. The least
    /// of a few, with solution-wide analysis on, the Solution tab shown and twenty files nobody has open each with
    /// <paramref name="warnings"/> warnings; how many problems the solution has, and what was taken out.</summary>
    private (long Bytes, int Listed, string Taken) UiBytesPerPause(int warnings)
    {
        (MainWindow window, string typed, int listed) = Warned(warnings);
        ProblemsModel problems = window.BottomPanel.Problems;
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        long blink = Ui.BlinkBytes(editor);
        double perMs = Ui.RestBytesPerMs(editor, blink, TimeSpan.FromSeconds(2));

        long least = long.MaxValue;
        int blinked = 0;
        double waited = 0;
        for (int i = 1; i <= PauseKeys; i++)
        {
            long blinks = editor.Blinks;
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            window.KeyTextInput("1");
            Ui.Settle();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            int drew = (int)(editor.Blinks - blinks);
            blinked += drew;
            waited += ms;
            least = Math.Min(least, allocated - (drew * blink) - (long)(ms * perMs));
            Problem moved = Assert.Single(problems.SolutionProblems!, p => p.Path == typed);
            Assert.True(moved.Position == new TextPosition(4, 27 + i), $"After {i} keys the open file's warning is listed at {moved.Where}, not Typed.cs:5:{28 + i}");
            Assert.Equal($"Typed.cs:5:{28 + i}", Assert.Single(problems.Rows, r => r.Problem?.Path == typed).Detail);
        }
        Assert.Equal(listed, problems.SolutionProblems!.Count);
        window.Close();
        return (least, listed, $"{blinked} blinks at {blink:N0} bytes and {waited / 1000:0.0} s at {perMs:0.0} bytes a ms in {PauseKeys} pauses with {listed}");
    }

    /// <summary>The file open in a typing pause's tests.</summary>
    private const string Typed = "static class Typed\n{\n    static int Count()\n    {\n        int value = 1; int unused;\n        return value;\n    }\n}\n";

    /// <summary>A window on twenty files nobody has open, each with <paramref name="warnings"/> warnings, and Typed.cs open with
    /// the caret after its 1, solution-wide analysis on and the Solution tab shown once it lists them all; the open file's
    /// path and how many problems the solution has.</summary>
    private (MainWindow Window, string Typed, int Listed) Warned(int warnings)
    {
        const int Files = 20;
        for (int i = 0; i < Files; i++)
        {
            System.Text.StringBuilder warned = new($"static class Warned{i}\n{{\n    static void All()\n    {{\n");
            for (int j = 0; j < warnings; j++)
            {
                warned.Append(System.Globalization.CultureInfo.InvariantCulture, $"        int unused{j};\n");
            }
            Write($"Warned{i}.cs", warned.Append("    }\n}\n").ToString());
        }
        Write("Typed.cs", Typed);
        string typed = Passenger.Core.PathNames.Canonical(Path.Combine(folder, "Typed.cs"));
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Typed.cs");
        window.Perform(AppActions.ShowProblems);
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.Settle();
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Assert.True(window.Settings.SolutionWideAnalysis);
        // Each file's warnings, Bad.cs's error and the open file's warning.
        int listed = (Files * warnings) + 2;
        Ui.WaitFor(() => problems.SolutionProblems?.Count == listed && problems.SolutionProblems.Any(p => p.Path == typed),
            $"the solution's {listed} problems", TimeSpan.FromMinutes(2));
        problems.Shown = ProblemsTab.Solution;
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(Typed.IndexOf("= 1", StringComparison.Ordinal) + 3);
        editor.Focus();
        return (window, typed, listed);
    }

    /// <summary>A key's problems never wait for what the whole project's analysis reports in the open file, which with
    /// solution-wide analysis on and 4,000 warnings in files nobody has open answers a change seconds after the file's own:
    /// with that held unanswered, the error a key makes is listed in the File and Solution tabs and marked in the editor.</summary>
    [AvaloniaFact]
    public void A_key_in_a_large_solution_lists_the_open_files_problems_without_waiting_for_the_whole_projects_analysis()
    {
        (MainWindow window, string typed, _) = Warned(warnings: 200);
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.Settle();
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldNext(Passenger.Lsp.LspMethods.DocumentDiagnostic,
            p => p is Passenger.Lsp.DocumentDiagnosticParams { Identifier: Passenger.Lsp.LspDocument.ProjectWideSource }, answered.Task);
        try
        {
            window.KeyTextInput(" + missing");
            Ui.PumpUntil(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error)
                && problems.SolutionProblems?.Any(p => p.Path == typed && p.Severity == ProblemSeverity.Error) == true
                && window.EditorArea.Active!.Markers.Any(m => m.Kind == Passenger.Editor.MarkerKind.Error),
                "the error the key made listed and marked, with the whole project's analysis of the open file held unanswered", TimeSpan.FromMinutes(1));
        }
        finally
        {
            answered.SetResult();
        }
        Ui.Settle();
        Problem error = Assert.Single(problems.FileProblems, p => p.Severity == ProblemSeverity.Error);
        Assert.Equal(new TextPosition(4, 24), error.Position);
        window.Close();
    }

    /// <summary>Roslyn's compiler ends a line at each break in Separated.cs's comment, as its language server does, where the
    /// editor's lines end only at '\n': the problem below them is listed and gone to at the editor's line 4, not the
    /// compiler's 8, from Problems' Build and Solution tabs, the Build window's link to the message, and an agent's look.</summary>
    [AvaloniaFact]
    public void A_problem_below_line_breaks_only_roslyn_counts_is_listed_and_gone_to_at_its_own_line()
    {
        Write("Separated.cs", $"class Separated\n{{\n    /* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\n    public int Below = \"none\";\n}}\n");
        string separated = Passenger.Core.PathNames.Canonical(Path.Combine(folder, "Separated.cs"));
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Program.cs");
        Ui.Settle();
        ProblemsModel problems = window.BottomPanel.Problems;
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Problem built = Assert.Single(problems.Problems, p => p.Path == separated);
        Assert.True(built.Position == new TextPosition(3, 23), $"The build's problem is listed at {built.Where}, not Separated.cs:4:24");

        // The compiler's message in the Build window names its line 8; its link goes to the editor's line 4.
        OutputView console = window.BottomPanel.Build;
        console.Flush();
        string message = console.Document.Snapshot.ToString().Split('\n').First(l => l.Contains("Separated.cs(8,24): error CS0029", StringComparison.Ordinal));
        Ui.ClickLink(console, message);
        Assert.Equal(separated, window.EditorArea.Active?.Path);
        Assert.True(editor.CaretPosition == new TextPosition(3, 23), $"The Build window's link went to {editor.CaretPosition.Line + 1}:{editor.CaretPosition.Column + 1}, not 4:24");

        window.OpenFile("Program.cs");
        window.Perform(AppActions.ShowProblems);
        problems.Shown = ProblemsTab.Build;
        Ui.Settle();
        Choose(window, problems, separated);
        Assert.Equal(separated, window.EditorArea.Active?.Path);
        Assert.Equal(new TextPosition(3, 23), editor.CaretPosition);

        window.OpenFile("Program.cs");
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Path == separated) == true, "Separated.cs's error in the solution's problems");
        Assert.Equal(new TextPosition(3, 23), problems.SolutionProblems!.First(p => p.Path == separated).Position);
        problems.Shown = ProblemsTab.Solution;
        Ui.Settle();
        Choose(window, problems, separated);
        Assert.Equal(new TextPosition(3, 23), editor.CaretPosition);
        Passenger.App.Mcp.ProblemsInfo told = (Passenger.App.Mcp.ProblemsInfo)Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Diagnostics(), "the problems an agent asks for");
        Assert.Equal((4, 24), told.Build.Where(f => f.Path == "Separated.cs").Select(f => (f.Line, f.Column)).Single());
        Assert.Equal((4, 24), told.Solution!.Where(f => f.Path == "Separated.cs").Select(f => (f.Line, f.Column)).Single());
        window.Close();
    }

    /// <summary>A problem's row chosen in the tab shown and gone to from its menu, as a person goes to it.</summary>
    private static void Choose(MainWindow window, ProblemsModel problems, string path)
    {
        Avalonia.Controls.ListBox rows = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.ProblemsWindow).OfType<Avalonia.Controls.ListBox>().First();
        rows.SelectedItem = problems.Rows.First(r => r.Problem?.Path == path);
        Ui.ChooseFromMenu(rows, "Go to");
    }

    private void Write(string name, string text) => TestFolders.Write(folder, name, text);
}
