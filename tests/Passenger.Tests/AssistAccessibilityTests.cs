using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The popups the code's help places at a line, as a screen reader meets and hears them on a real project: each
/// is found in a walk of the window by its role and name, and what it says is heard through the window's announcing
/// region (<see cref="AccessibilityTests.Listen"/>), the editor keeping the keys: completion's choice as it opens, as Down
/// moves it, once as the list asked again for the name keeps it, and as it closes, and its documentation when asked, the
/// list answered again or not; Parameter Info's signature, then its parameter as a comma is typed and as the caret moves
/// back; a card a key shows, never one the pointer brings; the fixes' list as completion's.</summary>
public sealed class AssistAccessibilityTests : IClassFixture<AssistAccessibilityTests.Project>
{
    private const string Program = "class Program\n{\n    static void Main()\n    {\n        int result = Helper.Twice(2);\n        int area = Area.Of(3);\n        Missing gone = null;\n    }\n}\n";

    private readonly Project project;

    public AssistAccessibilityTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>Completion is a list a reader hears open, move and close, a narrowed or moved choice heard once and
    /// kept as the answer comes, documentation, Parameter Info, a card a key shows, Read as Text and the fixes list
    /// heard, the rename box named, as one flow, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void Completion_cards_and_fixes_are_heard_as_a_screen_reader_asks()
    {
        Flow.StepApart("completion is a list called completion its choice said as it opens and as down moves it and its closing said", () => new AssistAccessibilityTests(project), t => t.Completion_is_a_list_called_completion_its_choice_said_as_it_opens_and_as_down_moves_it_and_its_closing_said());
        Flow.StepApart("a narrowed choice is heard once however late the list asked again for the name comes", () => new AssistAccessibilityTests(project), t => t.A_narrowed_choice_is_heard_once_however_late_the_list_asked_again_for_the_name_comes());
        Flow.StepApart("a choice moved while the list is asked again stays on its item as the answer comes and enter puts it in", () => new AssistAccessibilityTests(project), t => t.A_choice_moved_while_the_list_is_asked_again_stays_on_its_item_as_the_answer_comes_and_enter_puts_it_in());
        Flow.StepApart("documentation asked as the list is answered again is heard for the item chosen", () => new AssistAccessibilityTests(project), t => t.Documentation_asked_as_the_list_is_answered_again_is_heard_for_the_item_chosen());
        Flow.StepApart("parameter info says its signature then the parameter as a comma is typed and as the caret moves back", () => new AssistAccessibilityTests(project), t => t.Parameter_info_says_its_signature_then_the_parameter_as_a_comma_is_typed_and_as_the_caret_moves_back());
        Flow.StepApart("a card a key shows is heard and one the pointer brings never", () => new AssistAccessibilityTests(project), t => t.A_card_a_key_shows_is_heard_and_one_the_pointer_brings_never());
        Flow.StepApart("read as text opens the carets problem and its card as text and Escape gives the editor its keys back at the caret", () => new AssistAccessibilityTests(project), t => t.Read_as_text_opens_the_carets_problem_and_its_card_as_text_and_Escape_gives_the_editor_its_keys_back_at_the_caret());
        Flow.StepApart("the fixes list is read as completion is and the rename box is named by its caption", () => new AssistAccessibilityTests(project), t => t.The_fixes_list_is_read_as_completion_is_and_the_rename_box_is_named_by_its_caption());
    }

    private void Completion_is_a_list_called_completion_its_choice_said_as_it_opens_and_as_down_moves_it_and_its_closing_said()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Helper");
        Ui.Settle();
        heard.Clear();
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen, "Helper's members after its dot");
        Ui.Settle();
        IReadOnlyList<string> labels = window.Assist.CompletionLabels;
        Assert.True(heard is [(var opened, AutomationLiveSetting.Polite)] && opened == $"Completion, {labels[0]}, method, item 1 of {labels.Count}",
            $"The list opening on {labels[0]} of {labels.Count} was heard as {AccessibilityTests.Said(heard)}");

        AutomationPeer[] met = [.. AccessibilityTests.Walk(ControlAutomationPeer.CreatePeerForElement(window))];
        AutomationPeer list = met.FirstOrDefault(peer => peer.GetAutomationControlType() == AutomationControlType.List && peer.GetName() == "Completion")
            ?? throw new Xunit.Sdk.XunitException($"A walk of the window meets no list called Completion, only: {AccessibilityTests.Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.List))}");
        string[] rows = [.. AccessibilityTests.Walk(list).Where(peer => peer.GetAutomationControlType() == AutomationControlType.ListItem).Select(peer => peer.GetName())];
        Assert.True(rows.Length == Math.Min(10, labels.Count) && rows.Zip(labels).All(row => row.First.StartsWith(row.Second, StringComparison.Ordinal)),
            $"The completion list's rows are met as {string.Join(", ", rows)}, not by the names it shows, {string.Join(", ", labels)}");

        heard.Clear();
        Ui.Press(window, "DOWN");
        Ui.Settle();
        Assert.True(heard is [(var moved, AutomationLiveSetting.Assertive)] && moved == $"{labels[1]}, method, item 2 of {labels.Count}",
            $"Down to {labels[1]} was heard as {AccessibilityTests.Said(heard)}, not its choice at once");

        heard.Clear();
        window.KeyTextInput("Tw");
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "Tw", "Helper's members narrowed by Tw");
        Ui.Settle();
        IReadOnlyList<string> narrowed = window.Assist.CompletionLabels;
        string chosen = window.Assist.CompletionChosen!;
        Assert.True(chosen == "Twice" && heard is [(var typed, AutomationLiveSetting.Polite)]
            && typed == $"Twice, method, item {narrowed.ToList().IndexOf(chosen) + 1} of {narrowed.Count}",
            $"Narrowed to {string.Join(", ", narrowed)}, {chosen} chosen, the list was heard as {AccessibilityTests.Said(heard)}, not its choice in turn");

        heard.Clear();
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.ItemDocumentation is { } shown && heard.Any(h => h.Words == shown), "the chosen item's documentation heard");
        Ui.Settle();
        string documentation = window.Assist.ItemDocumentation!;
        Assert.True(heard[^1] == (documentation, AutomationLiveSetting.Polite) && heard.Count(h => h.Words == documentation) == 1 && documentation.Contains("Twice", StringComparison.Ordinal),
            $"Quick Documentation in the list was heard as {AccessibilityTests.Said(heard)}, not the card beside it once, \"{documentation}\"");
        Assert.True(window.Assist.IsCompletionOpen && editor.IsKeyboardFocusWithin, "Asking for the documentation closed the list or took the editor's keys");

        heard.Clear();
        Ui.Press(window, "ESCAPE");
        Assert.True(heard is [("Completion closed", AutomationLiveSetting.Polite)], $"The list closed by Escape was heard as {AccessibilityTests.Said(heard)}");
        window.Close();
    }

    /// <summary>Helper's members after its dot are a list the server says left items out, so typing on asks for it again:
    /// the narrowed rows' choice is heard as the key narrows them, and the rows answered again, held until it was heard,
    /// keep that choice without its being heard a second time.</summary>
    private void A_narrowed_choice_is_heard_once_however_late_the_list_asked_again_for_the_name_comes()
    {
        MainWindow window = project.Open("Program.cs");
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        (Passenger.Lsp.LanguageServer server, string asking) = HelpersMembers(window);

        heard.Clear();
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.HoldNextAnswer(asking, AskedAgain, answered.Task);
        string said;
        try
        {
            window.KeyTextInput("Tw");
            Ui.AwaitPumped(held, "Helper's members asked again as Tw was typed into the list the server said left items out, answered and held");
            Ui.PumpUntil(() => window.Assist.CompletionNarrowedBy == "Tw" && heard.Count > 0, "the rows narrowed by Tw and their choice heard");
            said = $"Twice, method, item {window.Assist.CompletionLabels.ToList().IndexOf("Twice") + 1} of {window.Assist.CompletionLabels.Count}";
        }
        finally
        {
            answered.SetResult();
        }
        Ui.Settle();
        Assert.True(window.Assist.CompletionChosen == "Twice" && heard is [(var typed, AutomationLiveSetting.Polite)] && typed == said,
            $"Narrowed by Tw to {window.Assist.CompletionChosen} chosen, the list was heard as {AccessibilityTests.Said(heard)}, not \"{said}\" once as the rows asked again came");
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>The second of the rows T narrows Helper's members to, chosen by Down while the list asked again as T was
    /// typed is held, stays chosen as its answer comes, its items new, and is not heard again; Enter puts it in.</summary>
    private void A_choice_moved_while_the_list_is_asked_again_stays_on_its_item_as_the_answer_comes_and_enter_puts_it_in()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        (Passenger.Lsp.LanguageServer server, string asking) = HelpersMembers(window);

        heard.Clear();
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.HoldNextAnswer(asking, AskedAgain, answered.Task);
        string chosen;
        try
        {
            window.KeyTextInput("T");
            Ui.AwaitPumped(held, "Helper's members asked again as T was typed into the list the server said left items out, answered and held");
            Ui.PumpUntil(() => window.Assist.CompletionNarrowedBy == "T" && window.Assist.CompletionLabels.Count > 1 && heard.Count > 0,
                "the rows narrowed by T and their choice heard");
            chosen = window.Assist.CompletionLabels[1];
            heard.Clear();
            Ui.Press(window, "DOWN", settle: false);
            Ui.PumpUntil(() => window.Assist.CompletionChosen == chosen, $"{chosen}, the second row, chosen by Down");
        }
        finally
        {
            answered.SetResult();
        }
        Ui.Settle();
        Assert.True(window.Assist.CompletionChosen == chosen && heard is [(var moved, AutomationLiveSetting.Assertive)]
            && moved.StartsWith($"{chosen}, method, item 2 of ", StringComparison.Ordinal),
            $"Down chose {chosen}, and once the list asked again came {window.Assist.CompletionChosen} was chosen, the list heard as "
            + $"{AccessibilityTests.Said(heard)}: Enter would put in another item than the one chosen");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line).Trim();
        Assert.True(line.StartsWith($"Helper.{chosen}", StringComparison.Ordinal), $"Enter on {chosen}, chosen as the list asked again came, put in \"{line}\"");
        window.Close();
    }

    /// <summary>Quick Documentation asked for the item Down chose, its card still to come, is heard once the card comes,
    /// though the list asked again as T was typed is answered between the asking and the card: the answer's items are new,
    /// the item the same.</summary>
    private void Documentation_asked_as_the_list_is_answered_again_is_heard_for_the_item_chosen()
    {
        MainWindow window = project.Open("Program.cs");
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        (Passenger.Lsp.LanguageServer server, string asking) = HelpersMembers(window);

        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource made = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.HoldNextAnswer(asking, AskedAgain, answered.Task);
        string chosen;
        try
        {
            window.KeyTextInput("T");
            Ui.AwaitPumped(held, "Helper's members asked again as T was typed into the list the server said left items out, answered and held");
            Ui.PumpUntil(() => window.Assist.CompletionNarrowedBy == "T" && window.Assist.CompletionLabels.Count > 1 && window.Assist.ItemDocumentation is not null,
                "the rows narrowed by T, the first one's documentation beside them");
            chosen = window.Assist.CompletionLabels[1];
            Task describing = server.HoldNextAnswer(window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!.ResolveMethod,
                asked => asked is Passenger.Lsp.CompletionItem { Label: var label } && label == chosen, resolved.Task);
            Ui.Press(window, "DOWN", settle: false);
            heard.Clear();
            Ui.Run(window, AppActions.QuickDocumentation, settle: false);
            Ui.AwaitPumped(describing, $"{chosen}'s documentation, asked for by Quick Documentation, answered and held");
            Task rows = window.Assist.HoldNextCompletionRows(made.Task);
            answered.SetResult();
            Ui.AwaitPumped(rows, "the rows of Helper's members answered again, made and held");
            made.SetResult();
            // The rows answered again shown before the documentation's answer comes.
            Ui.Draw();
        }
        finally
        {
            answered.TrySetResult();
            made.TrySetResult();
            resolved.SetResult();
        }
        Ui.WaitFor(() => window.Assist.ItemDocumentation is not null, "the card beside the list");
        string documentation = window.Assist.ItemDocumentation!;
        Assert.True(window.Assist.CompletionChosen == chosen && documentation.Contains($"Helper.{chosen}(", StringComparison.Ordinal)
            && heard.Count(h => h.Words == documentation) == 1 && heard[^1] == (documentation, AutomationLiveSetting.Polite),
            $"Quick Documentation asked for {chosen}, the list answered again meanwhile, was heard as {AccessibilityTests.Said(heard)}, "
            + $"{window.Assist.CompletionChosen} chosen, not the card beside it once, \"{documentation}\"");
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>Helper's members listed after its dot on a line of their own: a list the server says left items out, so
    /// typing on asks for it again; with the server and the method that asks.</summary>
    private static (Passenger.Lsp.LanguageServer Server, string Asking) HelpersMembers(MainWindow window)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Helper");
        Ui.Settle();
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen, "Helper's members after its dot");
        Ui.Settle();
        return (window.CSharpService!.Session!.Server, window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!.CompletionMethod);
    }

    /// <summary>Whether a completion request asks again for a list the server said left items out.</summary>
    private static bool AskedAgain(object asked) =>
        asked is Passenger.Lsp.CompletionParams { Context.TriggerKind: Passenger.Lsp.CompletionTriggerKind.TriggerForIncompleteCompletions };

    private void Parameter_info_says_its_signature_then_the_parameter_as_a_comma_is_typed_and_as_the_caret_moves_back()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        Ui.PlaceAfter(editor, "Area.Of(3");
        editor.Focus();
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => window.Assist.SignatureText is not null, "Of's parameter info");
        Ui.Settle();
        Assert.True(heard is [(var signed, AutomationLiveSetting.Polite)] && signed.StartsWith("Parameter Info, ", StringComparison.Ordinal)
            && signed.Contains("Area.Of(int width, int height)", StringComparison.Ordinal) && signed.EndsWith(", parameter width", StringComparison.Ordinal),
            $"Of's parameter info was heard as {AccessibilityTests.Said(heard)}, not its signature with the parameter at the caret");
        AutomationPeer[] met = [.. AccessibilityTests.Walk(ControlAutomationPeer.CreatePeerForElement(window))];
        Assert.True(met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.ToolTip && peer.GetName() == "Parameter Info"),
            $"A walk of the window meets no tooltip called Parameter Info, only: {AccessibilityTests.Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.ToolTip))}");

        heard.Clear();
        window.KeyTextInput(",");
        Ui.WaitFor(() => heard.Count > 0, "the second parameter heard");
        Ui.Settle();
        Assert.True(heard is [("parameter height", AutomationLiveSetting.Polite)], $"A comma typed in Of's call was heard as {AccessibilityTests.Said(heard)}");

        heard.Clear();
        Ui.Press(window, "LEFT");
        Ui.WaitFor(() => heard.Count > 0, "the first parameter heard again");
        Ui.Settle();
        Assert.True(heard is [("parameter width", AutomationLiveSetting.Polite)], $"The caret moved back to the first argument was heard as {AccessibilityTests.Said(heard)}");

        heard.Clear();
        Ui.Press(window, "END");
        Ui.WaitFor(() => window.Assist.SignatureText is null, () => $"Parameter Info to go once the caret left the call, where it says {window.Assist.SignatureText}");
        Assert.True(heard.Count == 0, $"The caret leaving the call was heard as {AccessibilityTests.Said(heard)}");
        window.Close();
    }

    private void A_card_a_key_shows_is_heard_and_one_the_pointer_brings_never()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        int at = editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + 2;
        Ui.RestOn(editor, at);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the hover card");
        Ui.Settle();
        Assert.True(heard.Count == 0, $"A card the pointer brought was heard as {AccessibilityTests.Said(heard)}");
        window.MouseMove(new Point(1, 1), Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the hover card gone with the pointer");

        Ui.PlaceAfter(editor, "Helper.Tw");
        editor.Focus();
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "Quick Documentation's card");
        Ui.Settle();
        string card = window.Assist.HoverText!;
        Assert.True(heard.Count > 0 && heard[^1] == (card, AutomationLiveSetting.Polite) && heard.Count(h => h.Words == card) == 1 && card.Contains("Helper.Twice(int value)", StringComparison.Ordinal),
            $"Quick Documentation's card \"{card}\" was heard as {AccessibilityTests.Said(heard)}, not once at the end");
        AutomationPeer[] met = [.. AccessibilityTests.Walk(ControlAutomationPeer.CreatePeerForElement(window))];
        Assert.True(met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.ToolTip && peer.GetName() == "Quick Documentation"),
            $"A walk of the window meets no tooltip called Quick Documentation, only: {AccessibilityTests.Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.ToolTip))}");
        Assert.True(editor.IsKeyboardFocusWithin, "The card read out took the editor's keys");
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>Read as Text in the editor opens what is at the caret as text: on a line holding an error, the error by its
    /// kind and place; on a call, what its card says; each named by the file and the caret's place, and Escape gives the
    /// editor its keys back with the caret where it was. In Problems, the chosen problem by its kind, code and place.</summary>
    private void Read_as_text_opens_the_carets_problem_and_its_card_as_text_and_Escape_gives_the_editor_its_keys_back_at_the_caret()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => editor.Markers.Any(m => m.Kind == MarkerKind.Error && m.Message.Contains("'Missing'", StringComparison.Ordinal)), "the error under Missing");

        Ui.PlaceAfter(editor, "Missing go");
        editor.Focus();
        Ui.Settle();
        Passenger.Text.TextPosition caret = editor.CaretPosition;
        PlainTextView problem = ReadAsTextTests.Opened(window, window, "a line holding an error");
        Assert.Equal($"Program.cs, line {caret.Line + 1}, column {caret.Column + 1}", problem.Title);
        string first = problem.Text.Split('\n')[0];
        Assert.True(first.StartsWith("Error at line 7, column 9: ", StringComparison.Ordinal) && first.Contains("'Missing'", StringComparison.Ordinal),
            $"Read as Text on a line holding an error read it as:\n{problem.Text}");
        ReadAsTextTests.Escaped(window, window, () => editor.IsFocused && editor.CaretPosition == caret, $"the editor at {caret}");

        Ui.PlaceAfter(editor, "Helper.Tw");
        editor.Focus();
        Ui.Settle();
        caret = editor.CaretPosition;
        PlainTextView card = ReadAsTextTests.Opened(window, window, "a call");
        Assert.True(card.Text.Contains("Helper.Twice(int value)", StringComparison.Ordinal) && !card.Text.Contains("Error", StringComparison.Ordinal),
            $"Read as Text on a call read what its card says as:\n{card.Text}");
        ReadAsTextTests.Escaped(window, window, () => editor.IsFocused && editor.CaretPosition == caret, $"the editor at {caret}");

        window.ShowToolWindow(ToolWindowIds.Problems);
        Passenger.App.ToolWindows.ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.Rows.Any(r => r.Problem?.Message.Contains("'Missing'", StringComparison.Ordinal) == true), "the error under Missing in Problems");
        Avalonia.Controls.ListBox list = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.ProblemsWindow).OfType<Avalonia.Controls.ListBox>().First();
        list.SelectedItem = problems.Rows.First(r => r.Problem?.Message.Contains("'Missing'", StringComparison.Ordinal) == true);
        ListKeys.Hold(list);
        Ui.Settle();
        PlainTextView chosen = ReadAsTextTests.Opened(window, window, "a problem chosen in Problems");
        Assert.True(chosen.Title == "Problem: Program.cs:7:9" && chosen.Text.StartsWith("Error", StringComparison.Ordinal) && chosen.Text.Contains("CS0246", StringComparison.Ordinal)
            && chosen.Text.Contains("'Missing'", StringComparison.Ordinal) && chosen.Text.Contains("At line 7, column 9 of ", StringComparison.Ordinal),
            $"Read as Text on a problem chosen in Problems read \"{chosen.Title}\" as:\n{chosen.Text}");
        ReadAsTextTests.Escaped(window, window, () => list.IsKeyboardFocusWithin, "the problem's row");
        window.Close();
    }

    private void The_fixes_list_is_read_as_completion_is_and_the_rename_box_is_named_by_its_caption()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => window.CSharpService!.For(window.EditorArea.Active!)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Missing's error");
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        Ui.PlaceAfter(editor, "Missi");
        editor.Focus();
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 1, "the fixes for Missing");
        Ui.Settle();
        IReadOnlyList<string> offered = window.Assist.Offered;
        IReadOnlyList<string> details = window.Assist.OfferedDetails;
        string title = window.Assist.OfferedTitle!;
        Assert.True(heard is [(var opened, AutomationLiveSetting.Polite)] && opened == $"{title}, {Chosen(0)}",
            $"The fixes' list opening on {offered[0]} was heard as {AccessibilityTests.Said(heard)}");
        AutomationPeer[] met = [.. AccessibilityTests.Walk(ControlAutomationPeer.CreatePeerForElement(window))];
        AutomationPeer list = met.FirstOrDefault(peer => peer.GetAutomationControlType() == AutomationControlType.List && peer.GetName() == title)
            ?? throw new Xunit.Sdk.XunitException($"A walk of the window meets no list called {title}, only: {AccessibilityTests.Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.List))}");
        string[] rows = [.. AccessibilityTests.Walk(list).Where(peer => peer.GetAutomationControlType() == AutomationControlType.ListItem).Select(peer => peer.GetName())];
        Assert.True(rows.Length > 1 && rows.Zip(offered).All(row => row.First == row.Second), $"The fixes are met as {string.Join(", ", rows)}, not by their words, {string.Join(", ", offered)}");

        heard.Clear();
        Ui.Press(window, "DOWN");
        Ui.Settle();
        Assert.True(heard is [(var moved, AutomationLiveSetting.Assertive)] && moved == Chosen(1),
            $"Down in the fixes' list was heard as {AccessibilityTests.Said(heard)}, not \"{Chosen(1)}\" at once");
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Twice", "the rename box");
        Ui.Settle();
        met = [.. AccessibilityTests.Walk(ControlAutomationPeer.CreatePeerForElement(window))];
        AutomationPeer box = met.FirstOrDefault(peer => peer.GetAutomationControlType() == AutomationControlType.Group && peer.GetName() == "Rename")
            ?? throw new Xunit.Sdk.XunitException($"A walk of the window meets no group called Rename, only: {AccessibilityTests.Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.Group))}");
        AutomationPeer[] fields = [.. AccessibilityTests.Walk(box).Where(peer => peer.GetAutomationControlType() == AutomationControlType.Edit)];
        Assert.True(fields is [{ } name] && name.GetName() == "Rename" && name.HasKeyboardFocus(), $"The rename box's field is met as {AccessibilityTests.Heard(fields)}, not the focused field named Rename");
        Ui.Press(window, "ESCAPE");
        window.Close();

        string Chosen(int at) => string.IsNullOrEmpty(details[at]) ? $"{offered[at]}, item {at + 1} of {offered.Count}" : $"{offered[at]}, {details[at]}, item {at + 1} of {offered.Count}";
    }

    public sealed class Project : SharedProject
    {
        public Project()
            : base("assist-accessibility-")
        {
            File.WriteAllText(PathOf("Sample.csproj"), TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), Program);
            File.WriteAllText(PathOf("Helper.cs"), "static class Helper\n{\n    public static int Twice(int value) => value * 2;\n\n"
                + "    public static int Thrice(int value) => value * 3;\n}\n");
            File.WriteAllText(PathOf("Area.cs"), "static class Area\n{\n    public static int Of(int width, int height) => width * height;\n}\n");
        }
    }
}
