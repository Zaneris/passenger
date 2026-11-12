using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The debugger's hover card, as a person uses it at a stop: the pointer rested on an object shows its row, which
/// opens into its members as the Variables view's rows do (each asked of the debugger only as it opens), a collection into
/// its items, by the pointer or the keys; its rows' menu adds a watch and takes a value set; and it goes as the pointer
/// leaves it or the program goes on; Quick Evaluate Expression shows its value there. The tests share one program as
/// written (<see cref="People"/>). Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class DebuggerHoverTests : IClassFixture<DebuggerHoverTests.People>
{
    private readonly People people;

    public DebuggerHoverTests(People people)
    {
        this.people = people;
        people.Reset();
    }

    [AvaloniaFact]
    public void An_object_rested_on_while_paused_opens_in_its_card_into_its_members_and_a_members_into_its_own()
    {
        MainWindow window = Stopped();
        try
        {
            Passenger.App.ToolWindows.DebugModel view = window.BottomPanel.Debug;
            VariablesList values = Hover(window, "person.Name");
            VariableNode person = Assert.Single(values.Tree.Rows);
            Assert.Equal("person", person.Name);
            Assert.True(person.HasChildren && !person.IsExpanded, "The hovered object's row cannot be opened");
            Assert.True(person.Children is null, "The hovered object's members were asked for before its row was opened");
            UiRulesTests.HoldsToPopupRules("The debugger's hover card", window.Assist.HoverCard!);

            Toggle(window, values, person);
            Ui.WaitFor(() => Member(values, person, "Name")?.Value == "\"Ada\"", "person's members in the card");
            VariableNode home = Member(values, person, "Home")!;
            Assert.True(home.HasChildren, "A member that is an object cannot be opened in the card");
            Toggle(window, values, home);
            Ui.WaitFor(() => Member(values, home, "City")?.Value == "\"London\"" && Member(values, home, "Number")?.Value == "12", "Home's members in the card");
            Assert.Same(values, window.Assist.HoverValues);
            Assert.Contains("\n    City = ", window.Assist.HoverText, StringComparison.Ordinal);
            UiRulesTests.HoldsToPopupRules("The debugger's hover card, opened", window.Assist.HoverCard!);

            Click(window, values.ContainerFromItem(Member(values, home, "City")!)!);
            Ui.ChooseFromMenu(values, "Add to Watches");
            Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Role == VariableRole.Watch && r.Name == "person.Home.City" && r.Value == "\"London\""),
                "the member's watch in the Debug window");
            Assert.Same(values, window.Assist.HoverValues);

            CodeEditor editor = window.EditorArea.CodeEditor;
            Click(window, editor, editor.PointOf(0) + new Point(4, editor.LineHeight / 2));
            Ui.WaitFor(() => window.Assist.HoverValues is null, "the card to go once the pointer and the keys left it");
            Assert.True(editor.IsFocused, "The editor clicked did not take the keys");

            values = Hover(window, "person.Name");
            Assert.False(Assert.Single(values.Tree.Rows).IsExpanded);
            window.MouseMove(Ui.Centre(values.ContainerFromIndex(0)!, window), RawInputModifiers.None);
            Ui.Settle();
            Assert.Same(values, window.Assist.HoverValues);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => window.Assist.HoverValues is null, "the card to go with the stop");
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to end");
            Assert.Contains("Ada London 3 10", view.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_cards_rows_open_with_the_keys_list_a_collections_items_and_take_a_value_set_in_them()
    {
        MainWindow window = Stopped();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Selections = SelectionSet.Single(editor.Document.Snapshot.ToString().IndexOf("numbers.Count", StringComparison.Ordinal) + 2);
            Ui.Run(window, AppActions.QuickDocumentation);
            Ui.WaitFor(() => window.Assist.HoverValues is not null, "the card for the value at the caret");
            VariablesList values = window.Assist.HoverValues!;
            Assert.True(editor.IsFocused, "The card took the keys as it showed, before it was asked to");
            Ui.Run(window, AppActions.QuickDocumentation);
            Assert.True(values.IsKeyboardFocusWithin, "Quick Documentation pressed again did not give the card the keys");

            Ui.Press(window, "Right");
            VariableNode numbers = values.Tree.Rows[0];
            Ui.WaitFor(() => Member(values, numbers, "[2]")?.Value == "30", $"the list's items in the card, where it showed {Shown(values)}");
            Assert.Equal("10", Member(values, numbers, "[0]")!.Value);
            Ui.Press(window, "Down");
            Assert.Equal("[0]", Chosen(values).Name);

            Ui.Press(window, "F2");
            Assert.True(Chosen(values).IsEditing, "F2 in the card did not open the item's value for editing");
            window.KeyTextInput("42");
            Ui.Press(window, "Enter");
            Ui.WaitFor(() => values.Tree.Rows.Count > 1 && values.Tree.Rows[1] is { Name: "[0]", Value: "42" }, "the value set, in the card");
            Assert.True(values.Tree.Rows[0].IsExpanded, "The list's row closed as a value was set in it");
            Assert.Equal("[0]", Chosen(values).Name);
            Assert.True(values.IsKeyboardFocusWithin, "The card lost the keys as a value was set in it");

            Ui.Press(window, "Left");
            Assert.Equal("numbers", Chosen(values).Name);
            Ui.Press(window, "Left");
            Assert.False(values.Tree.Rows[0].IsExpanded, "Left did not close the list's row");
            Assert.Single(values.Tree.Rows);
            Ui.Press(window, "Escape");
            Assert.Null(window.Assist.HoverValues);
            Assert.True(editor.IsFocused, "Escape left the keys with the card it took away");

            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to end");
            Assert.Contains("Ada London 3 42", window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Quick Evaluate Expression shows its value in the hover card's list with the keys, with no Evaluate dialog:
    /// from its keys, the expression the hover reads at the caret, opening into its members; the selection where there is
    /// one; and from Alt-click, the expression clicked, while an Alt-drag there still selects a column.</summary>
    [AvaloniaFact]
    public void Quick_evaluate_shows_the_caret_or_selections_or_an_alt_clicked_expression_in_the_cards_list_with_the_keys()
    {
        MainWindow window = Stopped();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            string text = editor.Document.Snapshot.ToString();
            editor.Selections = SelectionSet.Single(text.IndexOf("person.Home.City", StringComparison.Ordinal) + "person.Ho".Length);
            Ui.Press(window, window.Actions.Keymap.ShortcutsFor(AppActions.QuickEvaluate)[0].Notation());
            Ui.WaitFor(() => window.Assist.HoverValues is { } shown && shown.IsKeyboardFocusWithin, "Quick Evaluate's card, with the keys");
            VariablesList values = window.Assist.HoverValues!;
            VariableNode home = Assert.Single(values.Tree.Rows);
            Assert.Equal("person.Home", home.Name);
            Assert.False(window.OwnedWindows.Any(w => w.IsVisible), "Quick Evaluate opened a dialog");
            UiRulesTests.HoldsToPopupRules("Quick Evaluate's card", window.Assist.HoverCard!);
            Ui.Press(window, "Right");
            Ui.WaitFor(() => Member(values, home, "City")?.Value == "\"London\"", $"Home's members in the card, where it showed {Shown(values)}");
            Ui.Press(window, "Escape");
            Assert.Null(window.Assist.HoverValues);

            int indexed = text.IndexOf("numbers[0]", StringComparison.Ordinal);
            editor.Selections = SelectionSet.Single(new Selection(indexed, indexed + "numbers[0]".Length));
            Ui.Run(window, AppActions.QuickEvaluate);
            Ui.WaitFor(() => window.Assist.HoverValues?.Tree.Rows is [{ Name: "numbers[0]", Value: "10" }], () => $"the selection's value in the card, not {Shown(window.Assist.HoverValues)}");
            Ui.Press(window, "Escape");

            int name = text.IndexOf("person.Name", StringComparison.Ordinal) + "person.Na".Length;
            Point at = editor.PointOf(name) + new Point(1, editor.LineHeight / 2);
            Ui.Click(editor, at, RawInputModifiers.Alt);
            Ui.WaitFor(() => window.Assist.HoverValues?.Tree.Rows is [{ Name: "person.Name", Value: "\"Ada\"" }], () => $"Alt-click's value in the card, not {Shown(window.Assist.HoverValues)}");
            Ui.Press(window, "Escape");
            Point from = editor.TranslatePoint(editor.PointOf(0) + new Point(1, editor.LineHeight / 2), window)!.Value;
            Point to = editor.TranslatePoint(editor.PointOf(text.IndexOf('\n') + 4) + new Point(1, editor.LineHeight / 2), window)!.Value;
            Ui.Drag(window, from, to, MouseButton.Left, RawInputModifiers.Alt);
            Assert.True(editor.Selections.Count == 2 && window.Assist.HoverValues is null,
                $"Alt-drag over two lines made {editor.Selections.Count} selections with {Shown(window.Assist.HoverValues)} in a card, where it selects their column alone");

            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to end");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A hover reads only text the stopped frame can see: a name in another method of the stopped file, or in another
    /// file, shows no value and asks nothing of the program, even a name the frame has a local of; a local of the method it
    /// stopped in still shows its value.</summary>
    [AvaloniaFact]
    public void A_name_rested_on_outside_the_stopped_method_asks_nothing_of_the_program_and_a_local_inside_shows_its_value()
    {
        MainWindow window = Stopped();
        try
        {
            NotEvaluated(window, "numbers.Sum()", "another method of the stopped file");
            Assert.Null(window.OpenFile(people.PathOf("Other.cs")));
            Ui.Settle();
            NotEvaluated(window, "numbers.Count * 2", "another file");

            Assert.Null(window.OpenFile(people.PathOf("Program.cs")));
            Ui.Settle();
            VariableNode numbers = Assert.Single(Hover(window, "numbers.Count").Tree.Rows);
            Assert.True(numbers.Name == "numbers" && numbers.HasChildren, $"The stopped method's local showed as {numbers.Name} = {numbers.Value}");

            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the program to end");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The pointer rested on the start of some text of the editor's, a name the stopped frame has a local of, and the
    /// hover it started read: the card shows no value of it.</summary>
    private static void NotEvaluated(MainWindow window, string text, string where)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        bool rested = false;
        EventHandler<HoverEventArgs> heard = (_, _) => rested = true;
        editor.Hovered += heard;
        try
        {
            window.MouseMove(editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value, RawInputModifiers.None);
            Ui.WaitFor(() => rested, $"the pointer to rest on {text} in {where}");
            Ui.Settle();
        }
        finally
        {
            editor.Hovered -= heard;
        }
        string word = text[..text.IndexOf('.', StringComparison.Ordinal)];
        Assert.True(window.Assist.HoverValues is null && window.Assist.HoverText?.Contains($"{word} =", StringComparison.Ordinal) != true,
            $"{word} rested on in {where} was evaluated in the stopped frame, which ran it in the program and showed the frame's value under another's name: "
            + $"{Shown(window.Assist.HoverValues)}, \"{window.Assist.HoverText}\"");
    }

    /// <summary>A window on the program paused at its breakpoint, on the line that writes what it made, its locals shown; with
    /// no C# language server, since a paused program's card is the debugger's values alone.</summary>
    private MainWindow Stopped()
    {
        MainWindow window = people.Open(people.PathOf("Program.cs"), languageServer: false);
        window.EditorArea.CodeEditor.GoTo(new TextPosition(People.Stop, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.Debug);
        Assert.True(window.Debugger.Session is not null, $"Debug did not start the program: {Ui.Said(window)}");
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Tree.Rows.Any(r => r.Name == "numbers"),
            $"the stop at the breakpoint and its locals; the status bar said \"{window.Status}\"");
        return window;
    }

    /// <summary>The card shown with the pointer resting on the start of some text of the editor's, which shows a value's rows.</summary>
    internal static VariablesList Hover(MainWindow window, string text)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        window.MouseMove(editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverValues is not null, $"the card of the value of {text}, not \"{window.Assist.HoverText}\"");
        return window.Assist.HoverValues!;
    }

    /// <summary>A row's member in the card, by name, once the row has opened.</summary>
    internal static VariableNode? Member(VariablesList values, VariableNode row, string name) =>
        values.Tree.Rows.FirstOrDefault(r => r.Parent == row && r.Name == name);

    /// <summary>The card's rows as they are, for a failure to say.</summary>
    internal static string Shown(VariablesList? values) => values is null ? "no card" : string.Join("; ", values.Tree.Rows.Select(r => $"{r.Name} = {r.Value}{(r.IsExpanded ? " (open)" : "")}"));

    private static VariableNode Chosen(VariablesList values) =>
        values.SelectedItem as VariableNode ?? throw new InvalidOperationException("No row of the card is chosen");

    /// <summary>A row's chevron clicked, as a person opens or closes it.</summary>
    internal static void Toggle(MainWindow window, VariablesList values, VariableNode row)
    {
        values.ScrollIntoView(row);
        Ui.Settle();
        Control shown = values.ContainerFromItem(row) ?? throw new InvalidOperationException($"The card does not show {row.Name}");
        RowIcon chevron = shown.GetVisualDescendants().OfType<RowIcon>().First(i => i.DataKey == IconKeys.ChevronRight);
        Click(window, (Control)chevron.Parent!);
    }

    /// <summary>A click in a control (its middle unless said), the pointer moved there first as a person's is, which the
    /// card follows.</summary>
    internal static void Click(MainWindow window, Control control, Point? within = null)
    {
        window.MouseMove(within is { } point ? control.TranslatePoint(point, window)!.Value : Ui.Centre(control, window), RawInputModifiers.None);
        Ui.Click(control, within);
    }

    /// <summary>A program that makes an object holding another and a list, and writes them: the stop is on that line. Below
    /// its classes, and in a file of its own, a method of a parameter named as its list.</summary>
    public sealed class People : SharedProject
    {
        /// <summary>The line (from zero) that writes what the program made, where the breakpoint stops it.</summary>
        public const int Stop = 2;

        public People()
            : base("hover-")
        {
            File.WriteAllText(PathOf("People.csproj"),
                TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
            File.WriteAllText(PathOf("Program.cs"),
                "Person person = new(\"Ada\", new Address(\"London\", 12));\nList<int> numbers = [10, 20, 30];\n"
                + "Console.WriteLine($\"{person.Name} {person.Home.City} {numbers.Count} {numbers[0]}\");\n\n"
                + "sealed record Address(string City, int Number);\n\n"
                + "sealed class Person(string name, Address home)\n{\n    public string Name { get; set; } = name;\n\n    public Address Home { get; } = home;\n}\n"
                + "\nstatic class Sums\n{\n    public static int Of(List<int> numbers) => numbers.Sum();\n}\n");
            File.WriteAllText(PathOf("Other.cs"), "static class Other\n{\n    public static int Twice(List<int> numbers) => numbers.Count * 2;\n}\n");
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }
    }
}
