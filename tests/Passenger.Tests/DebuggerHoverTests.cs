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
/// leaves it or the program goes on. The tests share one program as written (<see cref="People"/>). Debugged, so run alone.</summary>
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
    internal static string Shown(VariablesList values) => string.Join("; ", values.Tree.Rows.Select(r => $"{r.Name} = {r.Value}{(r.IsExpanded ? " (open)" : "")}"));

    private static VariableNode Chosen(VariablesList values) =>
        values.SelectedItem as VariableNode ?? throw new InvalidOperationException("No row of the card is chosen");

    /// <summary>A row's chevron clicked, as a person opens or closes it.</summary>
    internal static void Toggle(MainWindow window, VariablesList values, VariableNode row)
    {
        values.ScrollIntoView(row);
        Ui.Settle();
        Control shown = values.ContainerFromItem(row) ?? throw new InvalidOperationException($"The card does not show {row.Name}");
        PathIcon chevron = shown.GetVisualDescendants().OfType<PathIcon>().First(i => i.Classes.Contains("chevron"));
        Click(window, (Control)chevron.Parent!);
    }

    /// <summary>A click in a control (its middle unless said), the pointer moved there first as a person's is, which the
    /// card follows.</summary>
    internal static void Click(MainWindow window, Control control, Point? within = null)
    {
        window.MouseMove(within is { } point ? control.TranslatePoint(point, window)!.Value : Ui.Centre(control, window), RawInputModifiers.None);
        Ui.Click(control, within);
    }

    /// <summary>A program that makes an object holding another and a list, and writes them: the stop is on that line.</summary>
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
                + "sealed class Person(string name, Address home)\n{\n    public string Name { get; set; } = name;\n\n    public Address Home { get; } = home;\n}\n");
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }
    }
}
