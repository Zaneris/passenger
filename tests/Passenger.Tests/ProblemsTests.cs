using System.Collections.Specialized;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Problems without a build: the current file's as they are typed, and with solution-wide analysis on, every
/// file's, a file nobody has open included, grouped by project and file. The class's windows share one project, restored
/// once (<see cref="SharedProject"/>); a test's own files are written to it for its window and gone before the next.</summary>
public sealed class ProblemsTests : IClassFixture<ProblemsTests.Project>
{
    private readonly Project project;

    public ProblemsTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void Solution_wide_analysis_lists_every_files_errors_and_follows_an_edit_that_breaks_a_file_nobody_has_open()
    {
        MainWindow window = project.Open("Helper.cs");
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

    /// <summary>The status bar says solution-wide analysis is under way while its first round is, a turning ring in place
    /// of its icon: a tick there would say the solution has no errors before any file nobody opened was analysed. Once the
    /// round answers, it says what the round found (here Bad.cs's error).</summary>
    [AvaloniaFact]
    public void The_status_bar_says_solution_wide_analysis_is_under_way_until_its_first_round_answers()
    {
        MainWindow window = project.Open("Helper.cs");
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        Avalonia.Controls.Button analysis = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(window, "AnalysisButton")!;
        WorkingRing? ring = Avalonia.Controls.ControlExtensions.FindControl<WorkingRing>(window, "AnalysisWorking");
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // A first pull of the solution's diagnostics held unanswered, as by a server still analysing a large solution.
            Task holding = server.HoldNextAnswer(Passenger.Roslyn.Extension.ExtensionMethods.WorkspaceDiagnostic, _ => true, answered.Task);
            window.Perform(AppActions.ToggleSolutionAnalysis);
            // Pumped, not settled: the round is background work until it answers.
            Ui.PumpUntil(() => holding.IsCompleted, "the first pull of the solution's diagnostics to be answered, and held");
            Assert.True(window.CSharpService.IsAnalysing, "Solution-wide analysis ended its first round with a pull of it unanswered");
            string? said = Avalonia.Controls.ToolTip.GetTip(analysis) as string;
            Assert.True(said?.StartsWith("Solution-wide analysis is under way", StringComparison.Ordinal) == true && ring?.IsVisible == true,
                $"While solution-wide analysis had yet to answer its first round, the status bar said \"{said}\" with {(ring?.IsVisible == true ? "a turning ring" : "no ring")}");
        }
        finally
        {
            answered.TrySetResult();
        }
        Ui.WaitFor(() => window.CSharpService is { IsAnalysing: false }, "the first round to answer once its pull was let go");
        string? after = Avalonia.Controls.ToolTip.GetTip(analysis) as string;
        Assert.True(after?.StartsWith("Solution-wide analysis: ", StringComparison.Ordinal) == true && ring?.IsVisible == false,
            $"Once the first round answered, finding Bad.cs's error, the status bar said \"{after}\" with {(ring?.IsVisible == true ? "a ring still turning" : "no ring")}");
        window.Close();
    }

    /// <summary>Solution-wide analysis's first round is over once the server has analysed every source, not once it answers,
    /// which it may leave until the next change: a round's pull asked again as the solution moved, after the pull before it
    /// had taken that change, is held open with nothing coming to end it. With the first pull's answer held, the round ends
    /// and the Solution tab lists the closed file's error.</summary>
    [AvaloniaFact]
    public void The_first_round_ends_once_the_server_has_analysed_every_source_though_its_answer_waits()
    {
        MainWindow window = project.Open("Helper.cs");
        ProblemsModel problems = window.BottomPanel.Problems;
        Passenger.App.CSharp.CSharpService service = window.CSharpService!;
        Passenger.Lsp.LanguageServer server = service.Session!.Server;
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task holding = server.HoldNextAnswer(Passenger.Roslyn.Extension.ExtensionMethods.WorkspaceDiagnostic,
                p => p is Passenger.Lsp.WorkspaceDiagnosticParams { Identifier: "WorkspaceDocumentsAndProject" }, answered.Task);
            window.Perform(AppActions.ToggleSolutionAnalysis);
            // Pumped, not settled: a held answer is work in flight.
            bool Ended() => !service.IsAnalysing && problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true;
            Assert.True(Ui.Pumped(Ended, TimeSpan.FromSeconds(90)),
                $"Solution-wide analysis's first round {(service.IsAnalysing ? "was still under way" : "ended")} with its first pull "
                + $"{(holding.IsCompleted ? "answered and its answer held" : "let go or unanswered")}, the closed file's error "
                + $"{(problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true ? "listed" : "not listed")}{Ui.ServerLogTail()}");
        }
        finally
        {
            answered.TrySetResult();
        }
        window.Close();
    }

    /// <summary>A file nobody has open changed on disk while one of solution-wide analysis's pulls is under way has the pull
    /// let go and asked again of the solution the change made, rather than waited for: the server would finish it on the
    /// solution before the change, then analyse it whole again, as it did every session's first round, whose solution moves
    /// as its projects' generators run again once they have loaded. With the pull's answer held, as by a server analysing a
    /// large solution for minutes, the Solution tab lists the file's new error once the server says its solution moved.</summary>
    [AvaloniaFact]
    public void A_file_changed_on_disk_while_a_pull_is_under_way_has_it_asked_again_of_the_solution_the_change_made()
    {
        MainWindow window = project.Open("Helper.cs");
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");
        Passenger.App.CSharp.CSharpService service = window.CSharpService!;
        Passenger.Lsp.LanguageServer server = service.Session!.Server;
        const string Analysed = "WorkspaceDocumentsAndProject";
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task holding = server.HoldNextAnswer(Passenger.Roslyn.Extension.ExtensionMethods.WorkspaceDiagnostic,
                p => p is Passenger.Lsp.WorkspaceDiagnosticParams { Identifier: Analysed }, answered.Task);
            // A key lets go of the pull held open for a change; the one asked after the key's own pull is the one held.
            int before = service.PullsAsked(Analysed);
            Rename(window, "Twice", "Twice2");
            Ui.WaitFor(() => service.PullsAsked(Analysed) > before, "the analysis to ask again once the key's own pull answered");
            int asked = service.PullsAsked(Analysed);

            project.Rewrite("Bad.cs", "class Bad\n{\n    public int Count = Missing;\n}\n");
            bool Listed() => problems.SolutionProblems?.Any(p => p.Code == "CS0103" && p.Path?.EndsWith("Bad.cs", StringComparison.Ordinal) == true) == true;
            // Pumped, not settled: the held pull's answer is work in flight until it is let go.
            Assert.True(Ui.Pumped(Listed, TimeSpan.FromSeconds(90)),
                $"Bad.cs's new error was not listed while a pull asked before Bad.cs changed went unanswered ({(holding.IsCompleted ? "its answer came and is held" : "unanswered")}): "
                + $"the analysis asked {service.PullsAsked(Analysed) - asked} more pulls of every file's problems (listed: "
                + $"{string.Join("; ", problems.SolutionProblems?.Select(p => $"{Path.GetFileName(p.Path)} {p.Code}") ?? [])}){Ui.ServerLogTail()}");
            Assert.DoesNotContain(problems.SolutionProblems!, p => p.Code == "CS0029" && p.Path?.EndsWith("Bad.cs", StringComparison.Ordinal) == true);
        }
        finally
        {
            answered.TrySetResult();
        }
        window.Close();
    }

    /// <summary>A file's pull of what its project's whole analysis reports in it, which solution-wide analysis adds, is named
    /// by its source while it waits on the server, so a long wait (a large project's) says which pull it is.</summary>
    [AvaloniaFact]
    public void A_files_project_wide_pull_waiting_on_the_server_is_named_by_its_source()
    {
        MainWindow window = project.Open("Helper.cs");
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => server.DiagnosticSources.Contains(Passenger.Lsp.LspDocument.ProjectWideSource),
            "the server to give files a project-wide source of diagnostics, as it does analysing the whole solution");
        TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task holding = server.HoldNext(Passenger.Lsp.LspMethods.DocumentDiagnostic,
                p => p is Passenger.Lsp.DocumentDiagnosticParams { Identifier: Passenger.Lsp.LspDocument.ProjectWideSource }, asked.Task);
            window.KeyTextInput(" ");
            Ui.PumpUntil(() => holding.IsCompleted, "the file's project-wide pull to be asked, and held");
            string conversation = server.Conversation();
            Assert.True(conversation.Contains($"{Passenger.Lsp.LspMethods.DocumentDiagnostic} ({Passenger.Lsp.LspDocument.ProjectWideSource})", StringComparison.Ordinal),
                $"The file's project-wide pull waiting on the server was not named by its source: {conversation}");
        }
        finally
        {
            asked.TrySetResult();
        }
        window.Close();
    }

    /// <summary>Solution-wide analysis turned on as the folder opens, while its language server starts and before the
    /// window has a session of it, reaches the server once it has: the Solution tab lists the error of a file nobody
    /// opened, which a server left analysing the open files alone never reports.</summary>
    [AvaloniaFact]
    public void Solution_wide_analysis_turned_on_while_the_server_starts_lists_the_error_of_a_file_nobody_opened()
    {
        // A server of the window's own, started as the folder opens: one taken up warm is the window's at once.
        project.Drop();
        MainWindow window = project.Open(null, w =>
        {
            Ui.PumpUntil(() => w.CSharpService is { State: "Starting C#…" }, "the language server to start");
            w.Perform(AppActions.ToggleSolutionAnalysis);
        });
        Assert.True(window.Settings.SolutionWideAnalysis);
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true,
            $"the error of Bad.cs, a file nobody opened, listed by solution-wide analysis turned on as the server started (listed: {string.Join("; ", problems.SolutionProblems?.Select(p => p.Code) ?? [])})");
        problems.Shown = ProblemsTab.Solution;
        Assert.Contains("Bad.cs", problems.Rows.Where(r => r.Problem is null).Select(r => r.Label));
        window.Close();
    }

    /// <summary>Solution-wide analysis turned off narrows the running server's scope back to its own, so it stops analysing
    /// files nobody opened: asked again of Bad.cs, a file nobody opened, with the result it gave while the analysis was on,
    /// the server says that file's problems are gone, where one left analysing the whole solution has nothing new to say.</summary>
    [AvaloniaFact]
    public void Solution_wide_analysis_turned_off_narrows_the_running_servers_scope_to_its_own()
    {
        MainWindow window = project.Open("Helper.cs");
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        (string? source, Passenger.Lsp.WorkspaceDocumentDiagnosticReport analysed) = Reported(server, server.WorkspaceDiagnosticSources, [],
            r => r.Uri.EndsWith("/Bad.cs", StringComparison.Ordinal) && r.Items?.Any(d => d.Code == "CS0029") == true,
            () => "Bad.cs's error in a pull of the whole solution's problems");
        Assert.NotNull(analysed.ResultId);

        window.Perform(AppActions.ToggleSolutionAnalysis);
        Assert.False(window.Settings.SolutionWideAnalysis);
        (_, Passenger.Lsp.WorkspaceDocumentDiagnosticReport narrowed) = Reported(server, [source], [new Passenger.Lsp.PreviousResultId(analysed.Uri, analysed.ResultId, analysed.ProjectContext)],
            r => r.Uri == analysed.Uri,
            () => $"the server to say Bad.cs's problems are gone once solution-wide analysis was turned off: it says nothing new of a file it goes on analysing{Ui.ServerLogTail()}");
        Assert.True(narrowed is { Kind: "full", ResultId: null, Items: null or [] },
            $"Once solution-wide analysis was turned off the server reported Bad.cs as {narrowed.Kind} with {narrowed.Items?.Count ?? 0} problems and result {narrowed.ResultId ?? "none"}, where a server analysing the open files alone says its problems are gone");
        Assert.Equal(server, window.CSharpService.Session?.Server);
        window.Close();
    }

    /// <summary>The first report a pull of each source streams that <paramref name="wanted"/> takes, with its source, the
    /// pulls let go once it has come, since Roslyn holds a workspace pull open until the next change. Asked as solution-wide
    /// analysis asks, of Passenger's extension.</summary>
    private static (string? Source, Passenger.Lsp.WorkspaceDocumentDiagnosticReport Report) Reported(Passenger.Lsp.LanguageServer server,
        IEnumerable<string?> sources, IReadOnlyList<Passenger.Lsp.PreviousResultId> previous, Func<Passenger.Lsp.WorkspaceDocumentDiagnosticReport, bool> wanted,
        Func<string> what)
    {
        System.Collections.Concurrent.ConcurrentQueue<(string?, Passenger.Lsp.WorkspaceDocumentDiagnosticReport)> came = new();
        using CancellationTokenSource letGo = new();
        Task[] pulls = [.. sources.Select(source => server.WorkspaceDiagnosticsAsync(source, previous, reports =>
        {
            foreach (Passenger.Lsp.WorkspaceDocumentDiagnosticReport report in reports.Where(wanted))
            {
                came.Enqueue((source, report));
            }
        }, letGo.Token, new Dictionary<string, string>()))];
        try
        {
            Ui.WaitFor(() => !came.IsEmpty, what, TimeSpan.FromSeconds(30));
            return came.TryPeek(out (string?, Passenger.Lsp.WorkspaceDocumentDiagnosticReport) first) ? first : throw new InvalidOperationException("No report came");
        }
        finally
        {
            letGo.Cancel();
            Ui.WaitFor(() => pulls.All(p => p.IsCompleted), "the pulls let go to end");
            if (pulls.FirstOrDefault(p => p.IsFaulted) is { Exception: { } failed })
            {
                throw failed;
            }
        }
    }

    /// <summary>A pull the server refuses or cuts short (Roslyn cancels one a change made stale) is asked again, so the
    /// solution's problems go on following edits.</summary>
    [AvaloniaFact]
    public void Solution_wide_analysis_goes_on_following_edits_after_the_server_refuses_a_pull()
    {
        MainWindow window = project.Open("Helper.cs");
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");

        // The pulls held open answer the edit, and the next pull of every source is refused.
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        Task refused = server.RefuseNext(Passenger.Roslyn.Extension.ExtensionMethods.WorkspaceDiagnostic, server.WorkspaceDiagnosticSources.Count);
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
    /// answered, and the Solution tab then lists what the keys broke in a file nobody has open. The keys come one straight
    /// after another, each with the work it posted run, so no pause comes between them however slow the machine.</summary>
    [AvaloniaFact]
    public void Keys_typed_in_a_burst_have_solution_wide_analysis_ask_the_server_again_once_typing_pauses()
    {
        MainWindow window = project.Open("Helper.cs");
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0029") == true, "the closed file's error");
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + "Twice".Length);
        editor.Focus();
        bool CallerBroken() => problems.SolutionProblems?.Any(p => p.Path?.EndsWith("Caller.cs", StringComparison.Ordinal) == true) == true;
        // A key and its undoing first, which the server answers as it does a session's first change.
        string written = editor.Document.Snapshot.ToString();
        Ui.Type(window, "SPACE", " ");
        Ui.Press(window, "BACK_SPACE");
        Assert.True(editor.Document.Snapshot.ToString() == written, "A space typed after Twice and taken back left the file changed");
        Passenger.App.CSharp.CSharpService service = window.CSharpService!;
        IReadOnlyList<string?> sources = service.Session!.Server.WorkspaceDiagnosticSources;
        int[] Asked() => [.. sources.Select(service.PullsAsked)];
        int[] before = Asked();

        const int Keys = 10;
        int[] during = new int[sources.Count];
        for (int typed = 0; typed < Keys; typed++)
        {
            int[] asked = Asked();
            window.KeyTextInput("x");
            during = [.. Asked().Select((now, i) => during[i] + now - asked[i])];
        }
        Ui.WaitFor(CallerBroken, "the error the keys made in the caller nobody has open", TimeSpan.FromMinutes(1));
        Ui.Settle();
        int[] after = Asked();
        string Said() => string.Join(", ", sources.Select((source, i) => $"{source ?? "every source"} {during[i]} then {after[i] - before[i] - during[i]}"));
        Assert.True(during.All(n => n == 0),
            $"Solution-wide analysis asked the server again while {Keys} keys were typed in a burst, where it asks only once typing pauses (during the keys, then after: {Said()})");
        window.Close();
    }

    /// <summary>A file's markers are put in order once, as its diagnostics are taken, however its server's sources gave them
    /// (a syntax error each side of a type error, reported by two): the keys typed in it, and the pulls after each, never
    /// have them sorted again.</summary>
    [AvaloniaFact]
    public void A_files_markers_are_put_in_order_as_they_come_and_never_sorted_again_for_a_key()
    {
        Write("Two.cs", "class Two\n{\n    void A() { int a = 1 }\n    int Count = \"none\";\n    void B() { int b = 2 }\n}\n");
        MainWindow window = project.Open("Two.cs");
        Passenger.App.Workspace.OpenDocument open = window.EditorArea.Active!;
        Ui.WaitFor(() => open.Markers.Count(m => m.Kind == Passenger.Editor.MarkerKind.Error) == 3, "the file's three errors");
        Ui.Settle();
        int sorted = open.Decorations.MarkersSorted;
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(0, 0));
        editor.Focus();
        const int Keys = 4;
        for (int i = 0; i < Keys; i++)
        {
            Ui.Press(window, "ENTER");
        }
        Assert.True(open.Decorations.MarkersSorted == sorted,
            $"The file's markers were sorted again {open.Decorations.MarkersSorted - sorted} times for {Keys} keys and the pulls after them");
        Assert.Equal([2 + Keys, 3 + Keys, 4 + Keys], open.Markers.Where(m => m.Kind == Passenger.Editor.MarkerKind.Error)
            .Select(m => open.Text.Snapshot.GetLineFromOffset(m.Range.Start)));
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
        MainWindow window = project.Open("Several.cs", w => w.Perform(AppActions.ShowProblems));
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
            Assert.Equal([2 + i, 3 + i, 4 + i], problems.FileProblems.Where(p => p.Severity == ProblemSeverity.Error).Select(p => p.Position.Line));
            Assert.True(Shown().SequenceEqual(before), $"After {i} lines went in above the problems the File tab's rows were made again");
            Assert.Equal([$"Several.cs:{3 + i}:17", $"Several.cs:{4 + i}:18", $"Several.cs:{5 + i}:20"], Places());
        }

        Ui.Settle();
        Assert.Equal(["Several.cs:6:17", "Several.cs:7:18", "Several.cs:8:20"], Places());
        window.Close();
    }

    /// <summary>Problems filtered as a person does it: Warnings turned off leaves the file's errors, the counts on its tab and
    /// toggles following; Find's keys give the filter the keys and its text leaves the error that holds it, the warning that
    /// holds it too staying hidden; Escape empties it and Warnings turned on lists all four again.</summary>
    [AvaloniaFact]
    public void Warnings_turned_off_and_a_filters_text_leave_only_the_matching_errors_and_the_counts_follow()
    {
        Write("Mixed.cs", "public class Mixed\n{\n    public int First = \"one\";\n    public int Second = missing;\n    public void A() { int unused; }\n    public void B() { int missingToo; }\n}\n");
        MainWindow window = project.Open("Mixed.cs", w => w.Perform(AppActions.ShowProblems));
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Error) == 2 && problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Warning) == 2,
            "the file's two errors and two warnings");
        Avalonia.Controls.Control view = window.ProblemsWindow;
        Avalonia.Controls.Primitives.ToggleButton Toggle(string name) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
            .OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => Avalonia.Controls.ToolTip.GetTip(b) as string == $"Show {name}");
        string Count(string name) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Toggle(name)).OfType<Avalonia.Controls.TextBlock>().Single().Text ?? string.Empty;
        string FileTab() => (string)Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(b => b.Content is string title && title.StartsWith("File", StringComparison.Ordinal)).Content!;
        string[] Listed() => [.. problems.Rows.Where(r => r.Problem is not null).Select(r => r.Problem!.Code ?? r.Problem!.Message)];
        // A message the server adds (a suggestion) is counted on its toggle, which a window starts with off.
        int messages = problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Message);
        Assert.Equal(["2", "2", $"{messages}"], [Count("Errors"), Count("Warnings"), Count("Messages")]);
        Assert.True(Toggle("Messages").IsChecked == false && FileTab() == "File: Mixed.cs  4" && problems.Rows.All(r => r.Problem?.Severity != ProblemSeverity.Message),
            $"A window starts listing [{string.Join("; ", problems.Rows.Select(r => r.Label))}] under \"{FileTab()}\", where it lists the errors and warnings alone");

        Ui.Click(Toggle("Warnings"));
        Assert.True(problems.Rows.All(r => r.Problem?.Severity == ProblemSeverity.Error) && problems.Rows.Count == 2,
            $"Warnings turned off left [{string.Join("; ", problems.Rows.Select(r => r.Label))}] listed, where only the two errors are");
        Assert.True(Toggle("Warnings").IsChecked == false && FileTab() == "File: Mixed.cs  2", $"With Warnings off the File tab says \"{FileTab()}\", where it counts the two errors it lists");

        Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.ListBoxItem>().First());
        Ui.Run(window, AppActions.Find);
        Avalonia.Controls.TextBox filter = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.TextBox>()
            .Single(t => t.PlaceholderText?.EndsWith("to filter", StringComparison.Ordinal) == true);
        Assert.True(filter.IsKeyboardFocusWithin, $"Find in Problems gave the keys to {window.FocusManager?.GetFocusedElement()?.GetType().Name}, not its filter");
        window.KeyTextInput("missing");
        Ui.Settle();
        Assert.True(problems.Rows.Count == 1 && problems.Rows[0].Problem is { Severity: ProblemSeverity.Error } named && named.Message.Contains("'missing'", StringComparison.Ordinal),
            $"Warnings off and the text \"missing\" left [{string.Join("; ", problems.Rows.Select(r => r.Label))}] listed, where only the error naming missing is");
        Assert.Equal(["1", "1"], [Count("Errors"), Count("Warnings")]);
        Assert.Equal("File: Mixed.cs  1", FileTab());

        Ui.Press(window, "ESCAPE");
        Ui.Click(Toggle("Warnings"));
        Assert.True(Listed().Length == 4 && FileTab() == "File: Mixed.cs  4",
            $"The filter emptied and Warnings back on list {Listed().Length} problems and the tab says \"{FileTab()}\"");
        if (messages > 0)
        {
            Ui.Click(Toggle("Messages"));
            Assert.True(Listed().Length == 4 + messages && FileTab() == $"File: Mixed.cs  {4 + messages}",
                $"Messages turned on list {Listed().Length} problems and the tab says \"{FileTab()}\", where the file has {4 + messages}");
        }

        // A link to a warning (the status bar's) takes away the filter hiding it.
        Ui.Click(Toggle("Warnings"));
        problems.Reveal(ProblemsTab.File, p => p.Message.Contains("'unused'", StringComparison.Ordinal));
        Ui.Settle();
        Assert.True(Toggle("Warnings").IsChecked == true && problems.Selected?.Message.Contains("'unused'", StringComparison.Ordinal) == true,
            $"A link to a warning Warnings hid chose {problems.Selected?.Message ?? "nothing"} with Warnings {(Toggle("Warnings").IsChecked == true ? "on" : "off")}");
        window.Close();
    }

    /// <summary>A file with thousands of problems types as one with few does: every key moves the file's markers, and the
    /// File tab lists where they went once typing pauses, rather than placing and listing every one again for each
    /// key. Sixteen keys come one straight after another, so however slow the machine some come within the pause.</summary>
    [AvaloniaFact]
    public void Keys_typed_above_thousands_of_problems_list_them_again_once_typing_pauses()
    {
        System.Text.StringBuilder warned = new("class Warned\n{\n    static void All()\n    {\n");
        for (int i = 0; i < 3000; i++)
        {
            warned.Append(System.Globalization.CultureInfo.InvariantCulture, $"        int unused{i};\n");
        }
        Write("Warned.cs", warned.Append("    }\n}\n").ToString());
        MainWindow window = project.Open("Warned.cs", w => w.Perform(AppActions.ShowProblems));
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Warning) == 3000, "the file's 3,000 warnings", TimeSpan.FromMinutes(2));
        Assert.Equal(new TextPosition(4, 12), problems.FileProblems[0].Position);
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(1, 1));
        editor.Focus();
        Ui.Settle();

        // Keys in quick succession, each one's own work done but no pause between them.
        int listed = 0;
        IReadOnlyList<Problem> shown = problems.FileProblems;
        const int Keys = 16;
        for (int typed = 0; typed < Keys; typed++)
        {
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            listed += ReferenceEquals(problems.FileProblems, shown) ? 0 : 1;
            shown = problems.FileProblems;
        }
        Assert.True(listed < Keys, $"The File tab listed the file's 3,000 problems again for each of {Keys} keys typed one straight after another");

        Ui.Settle();
        Assert.Equal(3000, problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Warning));
        Assert.Equal(new TextPosition(4 + Keys, 12), problems.FileProblems[0].Position);
        Assert.Equal(new TextPosition(4 + Keys + 2999, 12), problems.FileProblems[^1].Position);
        window.Close();
    }

    /// <summary>A typing pause lists again only the open file's problems, however many the solution has: with 4,000 warnings
    /// in twenty files nobody has open and the Solution tab shown, keys that move the open file's warning move its row where
    /// it is, and one that gives the file an error takes out and puts in that file's rows alone, with its project's count.
    /// What the tab's list is told counts, not what the pause costs: the tab is never listed whole again, nor another file's
    /// rows.</summary>
    [AvaloniaFact]
    public void A_typing_pause_lists_again_only_the_open_files_problems_however_many_the_solution_has()
    {
        (MainWindow window, string typed, int listed) = Warned(warnings: 200);
        ProblemsModel problems = window.BottomPanel.Problems;
        List<string> others = [];
        int typedRows = 0;
        void Heard(object? sender, NotifyCollectionChangedEventArgs change)
        {
            if (change.Action == NotifyCollectionChangedAction.Reset)
            {
                others.Add($"every row of the tab ({problems.Rows.Count})");
                return;
            }
            foreach (ProblemRow row in (change.NewItems ?? Array.Empty<ProblemRow>()).OfType<ProblemRow>().Concat((change.OldItems ?? Array.Empty<ProblemRow>()).OfType<ProblemRow>()))
            {
                if (row.Problem?.Path == typed || row is { Depth: 1, Label: "Typed.cs" })
                {
                    typedRows++;
                }
                else if (row.Depth > 0)
                {
                    others.Add($"{row.Label} {row.Detail}");
                }
            }
        }
        INotifyCollectionChanged told = (INotifyCollectionChanged)problems.Rows;
        told.CollectionChanged += Heard;
        try
        {
            const int Keys = 5;
            for (int i = 1; i <= Keys; i++)
            {
                window.KeyTextInput("1");
                Ui.Settle();
                Problem moved = Assert.Single(problems.SolutionProblems!, p => p.Path == typed);
                Assert.True(moved.Position == new TextPosition(4, 27 + i), $"After {i} keys the open file's warning is listed at {moved.Where}, not Typed.cs:5:{28 + i}");
                Assert.Equal($"Typed.cs:5:{28 + i}", Assert.Single(problems.Rows, r => r.Problem?.Path == typed).Detail);
            }
            Assert.True(typedRows == 0 && others.Count == 0, $"Keys that only moved the open file's warning listed rows again: {typedRows} of Typed.cs, and {string.Join(", ", others.Take(5))}");

            window.KeyTextInput(" + missing");
            Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Path == typed && p.Severity == ProblemSeverity.Error) == true, "the error the key made, in the solution's problems");
            Assert.Contains(problems.Rows, r => r.Problem is { Severity: ProblemSeverity.Error } error && error.Path == typed);
        }
        finally
        {
            told.CollectionChanged -= Heard;
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"With {listed + 1} problems in the solution, the typing pauses listed again {typedRows} rows of Typed.cs and {others.Count} others");
        Assert.True(others.Count == 0,
            $"A typing pause in Typed.cs listed again {others.Count} rows the keys never changed, of {listed + 1} problems in the solution: {string.Join(", ", others.Take(10))}");
        Assert.True(typedRows > 0, "The error the key made in Typed.cs was listed in the Solution tab with no row of Typed.cs put in");
        Assert.Equal(listed + 1, problems.SolutionProblems!.Count);
        window.Close();
    }

    /// <summary>The file open in a typing pause's tests.</summary>
    private const string Typed = "static class Typed\n{\n    static int Count()\n    {\n        int value = 1; int unused;\n        return value;\n    }\n}\n";

    /// <summary>A window on twenty files nobody has open, each with <paramref name="warnings"/> warnings, and Typed.cs open with
    /// the caret after its 1, solution-wide analysis on and the Solution tab shown once it lists them all; the open file's
    /// path and how many problems the solution has.</summary>
    private (MainWindow Window, string Typed, int Listed) Warned(int warnings)
    {
        const int Files = 20;
        List<(string, string)> files = [("Typed.cs", Typed)];
        for (int i = 0; i < Files; i++)
        {
            System.Text.StringBuilder warned = new($"static class Warned{i}\n{{\n    static void All()\n    {{\n");
            for (int j = 0; j < warnings; j++)
            {
                warned.Append(System.Globalization.CultureInfo.InvariantCulture, $"        int unused{j};\n");
            }
            files.Add(($"Warned{i}.cs", warned.Append("    }\n}\n").ToString()));
        }
        project.Write(files);
        string typed = Passenger.Core.PathNames.Canonical(Path.Combine(project.Folder, "Typed.cs"));
        MainWindow window = project.Open("Typed.cs", w => w.Perform(AppActions.ShowProblems));
        ProblemsModel problems = window.BottomPanel.Problems;
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
        string separated = Passenger.Core.PathNames.Canonical(Path.Combine(project.Folder, "Separated.cs"));
        MainWindow window = project.Open("Program.cs");
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

    /// <summary>A thousand files rewritten on disk as a checkout writes them, one of them open with an edit: the language
    /// server, which registered its files for the IDE to watch and so watches none itself, hears of them from the folder's
    /// watcher, in batches rather than file by file, and solution-wide analysis lists the problems of the closed files'
    /// new text and the open file's edit, until the open file takes the disk's text.</summary>
    [AvaloniaFact]
    public void A_thousand_files_rewritten_on_disk_reach_the_server_from_the_folders_watcher_and_their_new_problems_are_listed()
    {
        const int Files = 1000;
        static string Checked(int i, string variable) => $"static class Checked{i}\n{{\n    static void All()\n    {{\n        int {variable};\n    }}\n}}\n";
        project.Write([.. Enumerable.Range(0, Files).Select(i => ($"Checked{i}.cs", Checked(i, "before")))]);
        MainWindow window = project.Open("Checked0.cs", w => w.Perform(AppActions.ShowProblems));
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        string open = window.EditorArea.Active!.Path;
        int Saying(string variable, bool closed = true) => problems.SolutionProblems?.Count(p => p.Path!.Contains("Checked", StringComparison.Ordinal)
            && (p.Path == open) != closed && p.Message.Contains($"'{variable}'", StringComparison.Ordinal)) ?? 0;
        Ui.WaitFor(() => Saying("before") == Files - 1 && Saying("before", closed: false) == 1, $"the {Files} files' warnings", TimeSpan.FromMinutes(3));
        Passenger.Lsp.WatchedFiles watched = window.CSharpService!.Session!.Server.WatchedFiles;
        Assert.True(watched.Watches.Count > 0, "The language server registered no file for the IDE to watch, so it watches the folder itself");

        string text = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        int at = text.IndexOf("before", StringComparison.Ordinal);
        window.Perform(AppActions.FocusEditor);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(new Selection(at, at + "before".Length));
        window.KeyTextInput("typed");
        Ui.WaitFor(() => Saying("typed", closed: false) == 1, "the open file's edit's warning");
        TaskCompletionSource<bool?> fromDisk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.AskConflict = _ => fromDisk.Task;

        Passenger.Lsp.LanguageServer server = window.CSharpService.Session.Server;
        bool Replaced() => window.CSharpService.Session?.Server != server;
        int batches = 0;
        int named = 0;
        int lost = 0;
        window.FolderWatcher!.Changed += (_, changes) =>
        {
            batches++;
            lost += changes.Lost ? 1 : 0;
            named += changes.Paths.Count(p => Path.GetFileName(p).StartsWith("Checked", StringComparison.Ordinal));
        };
        HashSet<string> told = new(StringComparer.Ordinal);
        int notifications = 0;
        EventHandler<IReadOnlyList<Passenger.Lsp.FileEvent>> sent = (_, changes) =>
        {
            lock (told)
            {
                notifications++;
                told.UnionWith(changes.Select(c => Path.GetFileName(c.Path)).Where(name => name.StartsWith("Checked", StringComparison.Ordinal)));
            }
        };
        int Told()
        {
            lock (told)
            {
                return told.Count;
            }
        }
        watched.Sent += sent;
        long rewritten = System.Diagnostics.Stopwatch.GetTimestamp();
        Parallel.For(0, Files, i => File.WriteAllText(project.PathOf($"Checked{i}.cs"), Checked(i, "after")));
        // Pumped, not settled, so each time is when it happened, not when the work after it ended.
        bool heard = Ui.Pumped(() => Told() == Files || Replaced(), TimeSpan.FromSeconds(90));
        double toldAfter = System.Diagnostics.Stopwatch.GetElapsedTime(rewritten).TotalSeconds;
        Assert.False(Replaced(), $"The language server was replaced as the files changed on disk (it ended or stopped answering), so it never heard of them: {WatchLog(server)}");
        Assert.True(heard, $"The language server was told of {Told()} of the {Files} files rewritten on disk, the folder's watcher handing over {batches} batches naming {named} of them: {WatchLog(server)}");
        Assert.True(Ui.Pumped(() => Saying("after") == Files - 1 && Saying("before") == 0, TimeSpan.FromMinutes(2)),
            $"The closed files' warnings were not those of their new text after 2 minutes ({Saying("after")} of the new, {Saying("before")} of the old)");
        double listedAfter = System.Diagnostics.Stopwatch.GetElapsedTime(rewritten).TotalSeconds;
        watched.Sent -= sent;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Files} files rewritten on disk: the language server was told of them {toldAfter:0.00} s after, in {notifications} notifications (the folder's watcher handed over {batches} batches), "
            + $"and solution-wide analysis listed the closed files' new problems {listedAfter:0.00} s after; it registered {watched.Watches.Count} watchers, {window.CSharpService.Watching?.Watching.Count} folders watched beside the folder's watcher; {Recovered()}");
        Assert.True(notifications < Files / 10, $"The language server was told of the {Files} files changed on disk in {notifications} notifications, as if file by file");
        Assert.True(lost == 0, $"The folder's watcher read the whole folder again for {Passenger.Core.Plural.Of(lost, "batch", "batches")} of the {batches} rather than the folders the files changed in");
        Assert.Equal(Files, window.FileIndex.Files.Count(f => f.StartsWith("Checked", StringComparison.Ordinal)));
        Assert.True(Saying("typed", closed: false) == 1 && Saying("after", closed: false) == 0,
            "The open file's problems were those of its text on disk once the server heard it changed there, not those of its edit");

        fromDisk.SetResult(true);
        Ui.WaitFor(() => Saying("after", closed: false) == 1, "the open file's warning of the disk's text once it took it");
        window.Close();
    }

    /// <summary>A thousand files deleted side by side, as a script or a clean deletes them, more at once than macOS's watcher
    /// names: the folder's watcher lists again the folder they went from, rather than the whole folder, and every one
    /// leaves Go to File and is told the language server as gone.</summary>
    [AvaloniaFact]
    public void A_thousand_files_deleted_side_by_side_each_leave_go_to_file_and_reach_the_server_as_gone()
    {
        const int Files = 1000;
        project.Write([.. Enumerable.Range(0, Files).Select(i => ($"Side{i}.cs", $"static class Side{i}\n{{\n}}\n"))]);
        MainWindow window = project.Open("Program.cs");
        int Listed() => window.FileIndex.Files.Count(f => f.StartsWith("Side", StringComparison.Ordinal));
        Ui.WaitFor(() => Listed() == Files, $"the {Files} files in Go to File");
        Passenger.Lsp.WatchedFiles watched = window.CSharpService!.Session!.Server.WatchedFiles;
        Assert.True(watched.Watches.Count > 0, "The language server registered no file for the IDE to watch, so it watches the folder itself");
        Passenger.Lsp.LanguageServer server = window.CSharpService.Session.Server;
        HashSet<string> told = new(StringComparer.Ordinal);
        EventHandler<IReadOnlyList<Passenger.Lsp.FileEvent>> sent = (_, changes) =>
        {
            lock (told)
            {
                told.UnionWith(changes.Where(c => c.Change == Passenger.Lsp.FileChangeType.Deleted).Select(c => Path.GetFileName(c.Path)).Where(name => name.StartsWith("Side", StringComparison.Ordinal)));
            }
        };
        int Told()
        {
            lock (told)
            {
                return told.Count;
            }
        }
        int batches = 0;
        int named = 0;
        int lost = 0;
        window.FolderWatcher!.Changed += (_, changes) =>
        {
            batches++;
            lost += changes.Lost ? 1 : 0;
            named += changes.Paths.Count(p => Path.GetFileName(p).StartsWith("Side", StringComparison.Ordinal));
        };
        watched.Sent += sent;
        long deleted = System.Diagnostics.Stopwatch.GetTimestamp();
        Parallel.For(0, Files, i => File.Delete(project.PathOf($"Side{i}.cs")));
        // Pumped, not settled, so each time is when it happened, not when the work after it ended.
        bool heard = Ui.Pumped(() => Told() == Files && Listed() == 0, TimeSpan.FromSeconds(90));
        double after = System.Diagnostics.Stopwatch.GetElapsedTime(deleted).TotalSeconds;
        watched.Sent -= sent;
        Assert.True(heard, $"Of the {Files} files deleted side by side, the language server was told {Told()} went and Go to File still lists {Listed()}, "
            + $"the folder's watcher handing over {batches} batches naming {named} of them: {WatchLog(server)}");
        Assert.True(lost == 0, $"The folder's watcher read the whole folder again for {Passenger.Core.Plural.Of(lost, "batch", "batches")} of the {batches} rather than the folder the files went from");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Files} files deleted side by side: Go to File let go of them and the language server was told of them {after:0.00} s after, the folder's watcher handing over {batches} batches naming {named} of them; {Recovered()}");
        window.Close();
    }

    /// <summary>A thousand warned files nobody has open deleted at once, as a checkout deletes them, with solution-wide
    /// analysis on and the project's design-time build held, as a large project's takes a while: the open file's completion
    /// and the error a key makes there are answered while the removal is under way, and once the build lands the Solution
    /// tab lists none of their warnings.</summary>
    [AvaloniaFact]
    public void A_thousand_warned_files_deleted_at_once_leave_the_open_files_answers_unstalled()
    {
        const int Files = 1000;
        static string Gone(int i) => $"static class Gone{i}\n{{\n    static void All()\n    {{\n        int unused;\n    }}\n}}\n";
        project.Write([("Typed.cs", Typed), .. Enumerable.Range(0, Files).Select(i => ($"Gone{i}.cs", Gone(i)))]);
        MainWindow window = project.Open("Typed.cs", w => w.Perform(AppActions.ShowProblems));
        ProblemsModel problems = window.BottomPanel.Problems;
        window.Perform(AppActions.ToggleSolutionAnalysis);
        int Warned() => problems.SolutionProblems?.Count(p => Path.GetFileName(p.Path)!.StartsWith("Gone", StringComparison.Ordinal)) ?? 0;
        Ui.WaitFor(() => Warned() == Files, $"the {Files} files' warnings", TimeSpan.FromMinutes(3));
        Passenger.Lsp.WatchedFiles watched = window.CSharpService!.Session!.Server.WatchedFiles;
        int told = 0;
        EventHandler<IReadOnlyList<Passenger.Lsp.FileEvent>> sent = (_, changes) => Interlocked.Add(ref told, changes.Count(c => c.Change == Passenger.Lsp.FileChangeType.Deleted));
        watched.Sent += sent;
        bool heard = false;
        window.FolderWatcher!.Changed += (_, changes) => heard |= changes.Lost || changes.Paths.Any(p => Path.GetFileName(p).StartsWith("Gone", StringComparison.Ordinal));
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        long deleted = System.Diagnostics.Stopwatch.GetTimestamp();
        double completed;
        double erred;
        (IDisposable held, _) = window.Session.DesignTimeBuilds.HoldOff([project.Folder]);
        try
        {
            for (int i = 0; i < Files; i++)
            {
                File.Delete(project.PathOf($"Gone{i}.cs"));
            }
            // Pumped, not settled: the removal waits on the design-time build held.
            Ui.PumpUntil(() => heard, "the folder's watcher to hear of the files deleted");
            Ui.PlaceAfter(editor, "return value");
            editor.Focus();
            window.KeyTextInput(".");
            Ui.PumpUntil(() => window.Assist.IsCompletionOpen && window.Assist.CompletionLabels.Contains("CompareTo"),
                "int's members offered in the open file as the deleted files go", TimeSpan.FromSeconds(30));
            completed = System.Diagnostics.Stopwatch.GetElapsedTime(deleted).TotalSeconds;
            Ui.Press(window, "ESCAPE", settle: false);
            Ui.Press(window, "BACK_SPACE", settle: false);
            Ui.PlaceAfter(editor, "int value = 1");
            window.KeyTextInput(" + missing");
            Ui.PumpUntil(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error),
                "the error the key made listed in the open file as the deleted files go", TimeSpan.FromSeconds(30));
            erred = System.Diagnostics.Stopwatch.GetElapsedTime(deleted).TotalSeconds;
        }
        finally
        {
            held.Dispose();
        }
        Ui.WaitFor(() => Warned() == 0, "the deleted files' warnings gone from the Solution tab", TimeSpan.FromMinutes(1));
        double listed = System.Diagnostics.Stopwatch.GetElapsedTime(deleted).TotalSeconds;
        watched.Sent -= sent;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Files} warned files deleted: the open file's completion opened {completed:0.00} s after, the error a key made was listed {erred:0.00} s after, "
            + $"and the Solution tab listed none of their warnings {listed:0.00} s after, the design-time build held until then; the language server was told of {told} files gone");
        window.Close();
    }

    /// <summary>What the IDE's log says the folder's watcher found by listing folders again after a burst of changes.</summary>
    private static string Recovered() => string.Join(" | ", Ui.IdeLog().Split('\n').Where(l => l.Contains("The folder's watcher listed", StringComparison.Ordinal)).TakeLast(4));

    /// <summary>What the server's log says of its files watched and told, and the IDE's of the folders watched for it, last.</summary>
    private static string WatchLog(Passenger.Lsp.LanguageServer server)
    {
        IEnumerable<string> lines = Ui.Shared(server.LogPath).Split('\n').Where(l => l.Contains("watched for the server", StringComparison.Ordinal) || l.Contains("Told the server", StringComparison.Ordinal))
            .Concat(Ui.Shared(Passenger.App.IdeLog.PathOf(Passenger.App.IdeLog.Ide)).Split('\n').Where(l => l.Contains($"(process {server.ProcessId})", StringComparison.Ordinal) && l.Contains("watch", StringComparison.Ordinal)));
        return string.Join(" | ", lines.TakeLast(12));
    }

    /// <summary>A problem's row chosen in the tab shown and gone to from its menu, as a person goes to it.</summary>
    private static void Choose(MainWindow window, ProblemsModel problems, string path)
    {
        Avalonia.Controls.ListBox rows = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.ProblemsWindow).OfType<Avalonia.Controls.ListBox>().First();
        rows.SelectedItem = problems.Rows.First(r => r.Problem?.Path == path);
        Ui.ChooseFromMenu(rows, "Go to");
    }

    /// <summary>The class's server, kept with no window, hears its project file change (its time alone, as a checkout
    /// leaves it) and loads the project again, asking for builds no window is there to give: the next window that takes
    /// it up gives them, and is ready with a file written meanwhile loaded and its error listed, rather than starting for
    /// ever behind that question.</summary>
    [AvaloniaFact]
    public void A_kept_server_loading_its_project_again_between_windows_has_its_builds_from_the_next_window_which_lists_a_new_files_error()
    {
        project.Open("Helper.cs").Close();
        File.SetLastWriteTimeUtc(project.PathOf("Sample.csproj"), DateTime.UtcNow);
        Ui.WaitFor(() => project.Server.Unanswered > 0, "the kept server to ask for the builds of the project whose file it heard change");

        Write("Several.cs", "class Several\n{\n    int First = \"one\";\n}\n");
        MainWindow window = project.Open("Several.cs", w => w.Perform(AppActions.ShowProblems));
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Error) == 1,
            "the error of the file written while the server loaded its project again with no window");
        window.Close();
    }

    /// <summary>C# 15's collection expression arguments (<c>[with(…)]</c>) in a net11.0 project at LangVersion latest are
    /// no preview feature to the language server, as the .NET 11 SDK's compiler builds them: the file lists its one warning
    /// and no error (#1352).</summary>
    [AvaloniaFact]
    public void A_net11_projects_collection_expression_arguments_at_lang_version_latest_list_no_error()
    {
        string folder = Passenger.Core.PathNames.Canonical(TestFolders.Create("csharp15-"));
        TestFolders.Write(folder, "Demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n"
            + "    <LangVersion>latest</LangVersion>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Class1.cs", "namespace Demo;\n\npublic class Class1\n{\n"
            + "    private readonly Dictionary<string, string> _dict = [with(StringComparer.OrdinalIgnoreCase)];\n\n"
            + "    public int Count => _dict.Count;\n\n    public void Unused()\n    {\n        int unused;\n    }\n}\n");
        Restore.Run(folder);
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Class1.cs");
        window.Perform(AppActions.ShowProblems);
        ProblemsModel problems = window.BottomPanel.Problems;
        // The unused local's warning says the server has answered for the file; the File tab's problems begin with their codes.
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Message.StartsWith("CS0168: ", StringComparison.Ordinal)),
            () => $"the unused local's warning from the language server (Problems lists [{string.Join("; ", problems.FileProblems.Select(p => p.Message))}], C# {window.CSharpService?.State})");
        Problem[] errors = [.. problems.FileProblems.Where(p => p.Severity == ProblemSeverity.Error || p.Message.StartsWith("CS8652: ", StringComparison.Ordinal))];
        Assert.True(errors.Length == 0,
            $"Problems lists C# 15 that the .NET 11 SDK builds as an error: {string.Join("; ", errors.Select(p => p.Message))}");
        window.Close();
    }

    private void Write(string name, string text) => project.Write(name, text);

    /// <summary>A console project of a program, a helper, its caller and a file with an error, restored once for every test,
    /// whose server the windows share through the files the tests write.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("problems-", loadsAgain: true)
        {
            TestFolders.Write(Folder, "Sample.csproj", TestProjects.Console());
            TestFolders.Write(Folder, "Program.cs", "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
            TestFolders.Write(Folder, "Helper.cs", "static class Helper\n{\n    public static int Twice(int value) => value * 2;\n}\n");
            TestFolders.Write(Folder, "Caller.cs", "static class Caller\n{\n    public static int Four() => Helper.Twice(2);\n}\n");
            TestFolders.Write(Folder, "Bad.cs", "class Bad\n{\n    public int Count = \"none\";\n}\n");
            // The server's first load makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }
    }
}

/// <summary>What a pull of solution-wide analysis costs the IDE to read, counted across the process, so the class runs
/// alone: on the class's own copy of <see cref="ProblemsTests"/>' project.</summary>
[Collection(Alone.Name)]
public sealed class SolutionAnalysisBytesTests : IClassFixture<ProblemsTests.Project>
{
    private readonly ProblemsTests.Project project;

    public SolutionAnalysisBytesTests(ProblemsTests.Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>Solution-wide analysis places each file's warnings without a copy of the file's text, and decodes each
    /// warning at a small cost: over a thousand files nobody has open with no break only Roslyn counts, a pull of their
    /// warnings allocates (on every thread, the least of three) under 8 KB a file more when each is 17 KB long than when it
    /// is a few lines, where a copy of its text costs three times its size, and twenty warnings a file under 4 KB for each
    /// warning more than two.</summary>
    [AvaloniaFact]
    public void A_pull_of_thousands_of_warnings_decodes_each_at_a_small_cost_and_never_copies_a_files_text()
    {
        (long dense, int denseListed) = PullBytes(files: 100, warnings: 20, lines: 2);
        (long few, int fewListed) = PullBytes(files: 100, warnings: 2, lines: 2);
        (long shortFiles, int listed) = PullBytes(files: 1000, warnings: 2, lines: 2);
        (long longFiles, _) = PullBytes(files: 1000, warnings: 2, lines: 210);
        long perText = (longFiles - shortFiles) / 1000;
        long perWarning = (dense - few) / (denseListed - fewListed);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A pull of {listed} problems allocated {longFiles / 1024:N0} KB over 1,000 files of 17 KB, {shortFiles / 1024:N0} KB over 1,000 of a few lines: {perText:N0} bytes a file more; "
            + $"{dense / 1024:N0} KB for {denseListed} problems in 100 files, {few / 1024:N0} KB for {fewListed}: {perWarning:N0} bytes a warning more");
        Assert.True(perText < 8 * 1024,
            $"A pull of solution-wide analysis allocated {longFiles / 1024:N0} KB over 1,000 files of 17 KB, {shortFiles / 1024:N0} KB over 1,000 of a few lines: {perText:N0} bytes for each file's 17 KB, as if its text were read to place its warnings");
        Assert.True(perWarning < 4 * 1024,
            $"A pull of solution-wide analysis allocated {dense / 1024:N0} KB for {denseListed} warnings, {few / 1024:N0} KB for {fewListed} in the same files: {perWarning:N0} bytes for each warning more");
    }

    /// <summary>What turning solution-wide analysis on allocates on every thread until Problems counts every warning, the
    /// least of three, over <paramref name="files"/> files of a comment of <paramref name="lines"/> lines and
    /// <paramref name="warnings"/> unused variables each; and how many problems it listed.</summary>
    private (long Bytes, int Listed) PullBytes(int files, int warnings, int lines)
    {
        string comment = string.Concat(Enumerable.Repeat("// A line of a long comment the compiler passes over, as long as a line of code.\n", lines));
        List<(string, string)> written = [];
        for (int i = 0; i < files; i++)
        {
            System.Text.StringBuilder text = new(comment);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"static class Long{i}\n{{\n    static void All()\n    {{\n");
            for (int j = 0; j < warnings; j++)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"        int unused{j};\n");
            }
            written.Add(($"Long{i}.cs", text.Append("    }\n}\n").ToString()));
        }
        project.Write(written);
        MainWindow window = project.Open("Program.cs");
        ProblemsModel problems = window.BottomPanel.Problems;
        // Each file's warnings and Bad.cs's error.
        int listed = (files * warnings) + 1;
        long least = long.MaxValue;
        for (int time = 0; time < 3; time++)
        {
            Ui.Settle();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            window.Perform(AppActions.ToggleSolutionAnalysis);
            // Counted rather than quiet: a source's first pull may stay open, and the whole list sorts again at each look.
            Ui.PumpUntil(() => problems.SolutionCount == listed, $"the solution's {listed} problems", TimeSpan.FromMinutes(3));
            least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
            Assert.Equal(new TextPosition(lines + 4, 12), problems.SolutionProblems!.First(p => p.Path!.EndsWith("Long0.cs", StringComparison.Ordinal)).Position);
            window.Perform(AppActions.ToggleSolutionAnalysis);
            Assert.Null(problems.SolutionProblems);
        }
        window.Close();
        return (least, listed);
    }
}
