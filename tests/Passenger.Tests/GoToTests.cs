using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Navigate actions past Declaration, on a real project with the language server: Type Declaration, Base
/// Symbols and Derived Symbols with their choosers, and the places the IDE keeps as a person works: Recent Locations
/// with each place's code, Last and Next Edit Location, Recently Changed Files. Each test opens its own window, on the
/// server the last one left running (<see cref="SharedProject"/>).</summary>
public sealed class GoToTests : IClassFixture<GoToTests.Project>
{
    /// <summary>Lines from zero, as the editor counts them.</summary>
    private const string Shapes =
        "interface IShape\n" +                                          // 0
        "{\n" +
        "    double Area();\n" +                                        // 2
        "}\n" +
        "\n" +
        "abstract class Shape : IShape\n" +                             // 5
        "{\n" +
        "    public abstract double Area();\n" +                        // 7
        "\n" +
        "    public virtual string Describe() => \"a shape\";\n" +      // 9
        "}\n" +
        "\n" +
        "class Square : Shape\n" +                                      // 12
        "{\n" +
        "    public double Side { get; init; }\n" +
        "\n" +
        "    public override double Area() => Side * Side;\n" +         // 16
        "}\n" +
        "\n" +
        "class Circle : Shape\n" +                                      // 19
        "{\n" +
        "    public double Radius { get; init; }\n" +
        "\n" +
        "    public override double Area() => 3 * Radius * Radius;\n" + // 23
        "\n" +
        "    public override string Describe() => \"a circle\";\n" +   // 25
        "}\n" +
        "\n" +
        "class Label : IShape\n" +                                      // 28
        "{\n" +
        "    public double Area() => 0;\n" +                            // 30
        "}\n";

    private const string Program =
        "class Program\n" +
        "{\n" +
        "    static void Main()\n" +
        "    {\n" +
        "        Square square = new() { Side = 2 };\n" +                // 4
        "        var shape = square;\n" +                               // 5
        "        double area = shape.Area();\n" +                       // 6
        "        System.Console.WriteLine(area);\n" +                   // 7
        "    }\n" +
        "}\n";

    private readonly Project project;

    public GoToTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [Gate]
    [AvaloniaFact]
    public void Type_declaration_from_a_variable_goes_to_the_class_it_holds_not_to_where_the_variable_is_declared()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "area = sha");

        Ui.Run(window, AppActions.GoToTypeDeclaration);
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "Shapes.cs", "Type Declaration to open Shapes.cs");
        Assert.True(editor.CaretPosition.Line == 12, $"Type Declaration of a var holding a Square went to line {editor.CaretPosition.Line + 1}, not to class Square on line 13");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Base_symbols_go_from_an_override_to_what_it_overrides_from_an_implementation_to_the_interface_and_offer_both_when_there_are_two()
    {
        MainWindow window = project.Open("Shapes.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;

        PlaceIn(editor, 25, "Describe");
        Ui.Run(window, AppActions.BaseSymbols);
        Ui.WaitFor(() => editor.CaretPosition.Line != 25, "Base Symbols to leave Circle's Describe");
        Assert.True(editor.CaretPosition.Line == 9, $"Base Symbols of Circle's Describe went to line {editor.CaretPosition.Line + 1}, not to Shape's virtual Describe on line 10");

        PlaceIn(editor, 30, "Area");
        Ui.Run(window, AppActions.BaseSymbols);
        Ui.WaitFor(() => editor.CaretPosition.Line != 30, "Base Symbols to leave Label's Area");
        Assert.True(editor.CaretPosition.Line == 2, $"Base Symbols of Label's Area went to line {editor.CaretPosition.Line + 1}, not to IShape's Area on line 3");

        // Square's Area overrides Shape's, which implements IShape's: both, the nearest first.
        PlaceIn(editor, 16, "Area");
        Ui.Run(window, AppActions.BaseSymbols);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the base symbols of Square's Area to choose from");
        Assert.Equal(["Shapes.cs:8", "Shapes.cs:3"], window.Assist.Offered);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Assert.True(editor.CaretPosition.Line == 2, $"Choosing IShape's Area went to line {editor.CaretPosition.Line + 1}");

        // A type: its base type, then what that implements.
        PlaceIn(editor, 12, "Square");
        Ui.Run(window, AppActions.BaseSymbols);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the base types of Square to choose from");
        Assert.Equal(["Shape", "IShape"], window.Assist.Offered);
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Derived_symbols_from_the_editors_go_to_menu_offer_what_overrides_or_implements_a_member_or_derives_from_a_type()
    {
        MainWindow window = project.Open("Shapes.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;

        int abstractArea = editor.Document.Snapshot.GetLineStart(7) + "    public abstract double Ar".Length;
        Ui.RightClick(editor, editor.PointOf(abstractArea) + new Vector(1, editor.LineHeight / 2));
        Avalonia.Controls.MenuItem goTo = window.OpenContextMenu!.Items.OfType<Avalonia.Controls.MenuItem>().First(i => (string?)i.Header == "Go To");
        Assert.Equal(["Go to Declaration", "Derived Symbols", "Go to Implementation", "Go to Type Declaration", "Base Symbols", "Call Hierarchy", "Type Hierarchy"],
            goTo.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header as string));
        Ui.Choose(window.OpenContextMenu, "Go To", "Derived Symbols");
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the overrides of Shape's abstract Area to choose from");
        Assert.Equal(["Shapes.cs:17", "Shapes.cs:24"], window.Assist.Offered);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Assert.True(editor.CaretPosition.Line == 23, $"Choosing Circle's Area went to line {editor.CaretPosition.Line + 1}");

        // An interface's member: every implementation, the abstract one's overrides among them.
        PlaceIn(editor, 2, "Area");
        Ui.Run(window, AppActions.DerivedSymbols);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the implementations of IShape's Area to choose from");
        Assert.Superset(new HashSet<string> { "Shapes.cs:17", "Shapes.cs:24", "Shapes.cs:31" }, window.Assist.Offered.ToHashSet());
        Assert.DoesNotContain("Shapes.cs:3", window.Assist.Offered);
        Ui.Press(window, "ESCAPE");

        // A type: the types deriving from it, by name.
        PlaceIn(editor, 5, "Shape");
        Ui.Run(window, AppActions.DerivedSymbols);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the types deriving from Shape to choose from");
        Assert.Equal(["Circle", "Square"], window.Assist.Offered);
        Ui.Press(window, "ESCAPE");

        // A virtual member only one type overrides: straight there.
        PlaceIn(editor, 9, "Describe");
        Ui.Run(window, AppActions.DerivedSymbols);
        Ui.WaitFor(() => editor.CaretPosition.Line != 9, "Derived Symbols to leave Shape's Describe");
        Assert.True(editor.CaretPosition.Line == 25, $"Derived Symbols of Shape's Describe went to line {editor.CaretPosition.Line + 1}, not to Circle's override on line 26");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Recent_locations_lists_the_places_left_with_their_lines_of_code_and_goes_to_the_one_chosen()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "shape.Ar");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "Shapes.cs", "Go to Declaration to open Square's Area");
        PlaceIn(editor, 12, "Shape");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => editor.CaretPosition.Line == 5, "Go to Declaration to reach Shape");

        RunAnyhow(window, AppActions.RecentLocations, "Recent Locations");
        RecentLocationsPopup popup = Assert.IsType<RecentLocationsPopup>(window.Popup);
        Assert.Equal("Recent Locations", popup.Title);
        Assert.Equal(["Shapes.cs:13", "Program.cs:7"], popup.Rows.Select(r => $"{r.Name}:{r.Place.Line + 1}"));
        // Each with its line and two either side, numbered as the file numbers them, and what it is in.
        RecentLocation call = popup.Rows[1];
        Assert.Equal(4, call.FirstLine);
        Assert.Equal(string.Join('\n', Program.Split('\n')[4..9]), call.Lines);
        Assert.Equal("Program › Main()", call.Member);
        Assert.Equal("Square", popup.Rows[0].Member);

        window.KeyTextInput("WriteLine");
        Ui.Settle();
        Assert.Equal(["Program.cs"], popup.Rows.Select(r => r.Name));
        Ui.Press(window, "ENTER");
        Assert.Null(window.Popup);
        Assert.Equal("Program.cs", window.EditorArea.Active?.Name);
        Assert.Equal(new TextPosition(6, "        double area = shape.Ar".Length), editor.CaretPosition);

        Ui.Press(window, Ui.Shortcut(AppActions.Back));
        Assert.Equal("Shapes.cs", window.EditorArea.Active?.Name);
        Assert.Equal(5, editor.CaretPosition.Line);
        window.Close();
    }

    /// <summary>Back from a declaration returns to the call Go to Declaration left, and Back again to the file shown
    /// before the call's file opened; Forward goes the same way again.</summary>
    [AvaloniaFact]
    public void Back_after_go_to_declaration_returns_to_the_call_then_to_the_file_shown_before_and_forward_retraces_them()
    {
        MainWindow window = project.Open("Shapes.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        PlaceIn(editor, 28, "Label");
        window.OpenFile(project.PathOf("Program.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "shape.Ar");
        TextPosition call = editor.CaretPosition;
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "Shapes.cs", "Go to Declaration to open Square's Area");
        TextPosition declaration = editor.CaretPosition;

        Step(window, AppActions.Back, "Program.cs", call, "Back from the declaration");
        Step(window, AppActions.Back, "Shapes.cs", new TextPosition(28, "class La".Length), "A second Back, to the file Program.cs was opened over,");
        Step(window, AppActions.Forward, "Program.cs", call, "Forward after two Backs");
        Step(window, AppActions.Forward, "Shapes.cs", declaration, "A second Forward");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Last_edit_location_steps_back_through_edits_in_two_files_and_recent_lists_show_only_what_was_edited()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(7, editor.Document.Snapshot.GetLineText(7).Length));
        window.KeyTextInput(" // first");
        window.OpenFile(project.PathOf("Shapes.cs"));
        Ui.Settle();
        editor.Focus();
        editor.GoTo(new TextPosition(25, editor.Document.Snapshot.GetLineText(25).Length));
        window.KeyTextInput(" // second");
        Ui.Settle();
        editor.GoTo(new TextPosition(0, 0));

        Ui.Run(window, AppActions.LastEditLocation);
        Assert.Equal("Shapes.cs", window.EditorArea.Active?.Name);
        Assert.True(editor.CaretPosition.Line == 25, $"Last Edit Location went to line {editor.CaretPosition.Line + 1}, not to the edit on line 26");
        Ui.Run(window, AppActions.LastEditLocation);
        Assert.True(window.EditorArea.Active?.Name == "Program.cs", "Last Edit Location from the latest edit did not go back to the one before it, in Program.cs");
        Assert.Equal(new TextPosition(7, "        System.Console.WriteLine(area); // first".Length), editor.CaretPosition);
        Ui.Run(window, AppActions.LastEditLocation);
        Assert.Equal("No earlier edit location", window.Status);
        Assert.Equal("Program.cs", window.EditorArea.Active?.Name);
        RunAnyhow(window, AppActions.NextEditLocation, "Next Edit Location");
        Assert.Equal("Shapes.cs", window.EditorArea.Active?.Name);
        Assert.Equal(25, editor.CaretPosition.Line);

        Ui.Run(window, AppActions.RecentChangedFiles);
        SymbolPopup files = Assert.IsType<SymbolPopup>(window.Popup);
        Ui.WaitFor(() => files.IsSettled, "the files edited");
        Assert.True(files.IsOptionChecked, "Recently Changed Files opened Recent Files without Show edited only");
        Assert.Equal(["Program.cs"], files.Rows.Select(r => r.Name));
        Ui.Press(window, "ESCAPE");

        // Recent Locations with Show edited only: the places edited, but the one the caret is at.
        editor.GoTo(new TextPosition(12, 0));
        RunAnyhow(window, AppActions.RecentLocations, "Recent Locations");
        RecentLocationsPopup places = Assert.IsType<RecentLocationsPopup>(window.Popup);
        places.EditedOnly = true;
        Ui.Settle();
        Assert.Equal("Recently Edited Locations", places.Title);
        Assert.Equal(["Shapes.cs:26", "Program.cs:8"], places.Rows.Select(r => $"{r.Name}:{r.Place.Line + 1}"));
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>Runs an action by its keys, or through Find Action where the platform's keymap gives it none (Recent
    /// Locations on macOS), as a person would.</summary>
    private static void RunAnyhow(MainWindow window, string action, string title)
    {
        if (AppActions.Keymap().ShortcutsFor(action).Count > 0)
        {
            Ui.Run(window, action);
            return;
        }
        Ui.Run(window, AppActions.FindAction);
        ActionPopup find = Assert.IsType<ActionPopup>(window.Popup);
        window.KeyTextInput(title);
        Ui.Settle();
        int row = find.Rows.ToList().FindIndex(a => a.Id == action);
        Assert.True(row >= 0, $"Find Action does not list {title}");
        for (int i = 0; i < row; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
    }

    /// <summary>Back or Forward by its keys, and the file and place it shows.</summary>
    private static void Step(MainWindow window, string action, string file, TextPosition at, string what)
    {
        Ui.Press(window, Ui.Shortcut(action));
        TextPosition caret = window.EditorArea.CodeEditor.CaretPosition;
        Assert.True(window.EditorArea.Active?.Name == file && caret == at,
            $"{what} showed {window.EditorArea.Active?.Name} at {caret.Line + 1}:{caret.Column + 1}, not {file} at {at.Line + 1}:{at.Column + 1}");
    }

    /// <summary>The caret inside a name on a line, past its first two letters.</summary>
    private static void PlaceIn(CodeEditor editor, int line, string name)
    {
        int column = editor.Document.Snapshot.GetLineText(line).IndexOf(name, StringComparison.Ordinal);
        Assert.True(column >= 0, $"'{name}' is not on line {line + 1}");
        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.GetLineStart(line) + column + 2);
    }

    /// <summary>The shapes and a program using them, restored once for every test, and the warm server they share on it.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("goto-")
        {
            File.WriteAllText(PathOf("Shapes.csproj"),
                TestProjects.Console());
            File.WriteAllText(PathOf("Shapes.cs"), Shapes);
            File.WriteAllText(PathOf("Program.cs"), Program);
        }

        /// <summary>A window on the project with a file open and its editor focused, once C# is ready.</summary>
        public MainWindow Open(string file) => Open(file, window => window.EditorArea.CodeEditor.Focus());
    }
}
