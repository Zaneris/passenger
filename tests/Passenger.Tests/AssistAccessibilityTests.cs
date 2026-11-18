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
/// moves it, once as the list asked again for the name keeps it, and as it closes, and its documentation when asked;
/// Parameter Info's signature, then its parameter as a comma is typed and as the caret moves back; a card a key shows,
/// never one the pointer brings; the fixes' list as completion's.</summary>
public sealed class AssistAccessibilityTests : IClassFixture<AssistAccessibilityTests.Project>
{
    private const string Program = "class Program\n{\n    static void Main()\n    {\n        int result = Helper.Twice(2);\n        int area = Area.Of(3);\n        Missing gone = null;\n    }\n}\n";

    private readonly Project project;

    public AssistAccessibilityTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void Completion_is_a_list_called_completion_its_choice_said_as_it_opens_and_as_down_moves_it_and_its_closing_said()
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
    [AvaloniaFact]
    public void A_narrowed_choice_is_heard_once_however_late_the_list_asked_again_for_the_name_comes()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Helper");
        Ui.Settle();
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen, "Helper's members after its dot");
        Ui.Settle();
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;

        heard.Clear();
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.HoldNextAnswer(lsp.CompletionMethod,
            asked => asked is Passenger.Lsp.CompletionParams { Context.TriggerKind: Passenger.Lsp.CompletionTriggerKind.TriggerForIncompleteCompletions }, answered.Task);
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

    [AvaloniaFact]
    public void Parameter_info_says_its_signature_then_the_parameter_as_a_comma_is_typed_and_as_the_caret_moves_back()
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

    [AvaloniaFact]
    public void A_card_a_key_shows_is_heard_and_one_the_pointer_brings_never()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
        int at = editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + 2;
        window.MouseMove(editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value, Avalonia.Input.RawInputModifiers.None);
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

    [AvaloniaFact]
    public void The_fixes_list_is_read_as_completion_is_and_the_rename_box_is_named_by_its_caption()
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
