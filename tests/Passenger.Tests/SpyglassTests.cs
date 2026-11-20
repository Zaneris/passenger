using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Code Spyglass in a window on a C# project with a language server and a Git repository: with it on, the lines of
/// declarations shown are followed by how many places use each, what implements or overrides it and who changed it most;
/// off, nothing is asked of the server; a kind or Spyglass itself turned off takes its hints away, an edit above a
/// declaration carries its hints with it, and a press on a hint goes where it says.</summary>
public sealed class SpyglassTests : IClassFixture<SpyglassTests.Project>
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    private readonly Project project;

    public SpyglassTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    private static AppSettings Only(AppSettings settings, bool usages = false, bool inheritors = false, bool authors = false) =>
        settings with { CodeSpyglass = true, CodeSpyglassUsages = usages, CodeSpyglassInheritors = inheritors, CodeSpyglassAuthors = authors };

    private static List<LineAnnotation> Hints(CodeEditor editor) => [.. editor.LineAnnotations.Where(a => a.Look == LineAnnotationLook.Spyglass)];

    private static int LineOf(CodeEditor editor, string code)
    {
        TextSnapshot text = editor.Document.Snapshot;
        int at = text.ToString().IndexOf(code, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The file holds no \"{code}\"");
        return text.GetLineFromOffset(at);
    }

    private static string[] On(CodeEditor editor, string code)
    {
        int line = LineOf(editor, code);
        return [.. Hints(editor).Where(h => h.Line == line).Select(h => h.Text)];
    }

    private static string Shown(CodeEditor editor) =>
        string.Join("; ", Hints(editor).GroupBy(h => h.Line).Select(g => $"line {g.Key + 1}: {string.Join(", ", g.Select(h => h.Text))}"));

    /// <summary>Waits for each declaration to be followed by exactly these words, or, where <paramref name="exact"/> is false,
    /// by these among others.</summary>
    private static void WaitForHints(CodeEditor editor, (string Code, string[] Words)[] expected, bool exact = true) =>
        Ui.WaitFor(() => expected.All(e =>
            {
                string[] shown = On(editor, e.Code);
                return exact ? shown.SequenceEqual(e.Words) : e.Words.All(word => shown.Contains(word));
            }),
            () => $"the hints {string.Join("; ", expected.Select(e => $"{e.Code}: {string.Join(", ", e.Words)}"))} (shown: {Shown(editor)})", Limit);

    private static readonly (string Code, string[] Words)[] CountsHints =
    [
        ("static class Counts", ["3 usages"]), ("int Once()", ["1 usage"]), ("int Twice()", ["2 usages"]), ("int Never()", ["no usages"]),
        ("static class Callers", ["no usages"]), ("int Run()", ["no usages"]),
    ];

    private static readonly (string Code, string[] Words)[] ShapesHints =
    [
        ("interface IShape", ["2 usages", "1 implementation"]), ("int Sides { get; }", ["1 usage", "1 implementation"]), ("class Square : IShape", ["no usages"]),
        ("class Figure", ["1 usage"]), ("virtual double Perimeter()", ["no usages", "1 override"]), ("class Disc : Figure", ["no usages"]),
        ("abstract class Animal", ["1 usage", "1 inheritor"]), ("abstract string Name", ["no usages", "1 implementation"]), ("class Dog : Animal", ["no usages"]),
        ("class Reader", ["no usages"]), ("int Read(IShape shape)", ["no usages"]),
    ];

    /// <summary>With Spyglass on, each declaration of a file is followed by how many places use it, on its own line: a type
    /// by the places that name it, a method by its calls, none by "no usages".</summary>
    [AvaloniaFact]
    public void Turned_on_each_declaration_is_followed_by_how_many_places_use_it()
    {
        MainWindow window = project.Open("Counts.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            window.ApplySettings(Only(window.Settings, usages: true));
            WaitForHints(editor, CountsHints);
            Assert.True(Hints(editor).All(h => h.Data is not null), "A hint carries nothing to tell it apart by when it is pressed");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>With Spyglass off, as it is until turned on, no hint is drawn and the server is asked for no references,
    /// however the file is scrolled or typed in.</summary>
    [AvaloniaFact]
    public void Off_no_hint_is_drawn_and_the_server_is_asked_for_no_references()
    {
        MainWindow window = project.Open("Counts.cs");
        int asked = 0;
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            int server = window.CSharpService!.Session!.Server.ProcessId;
            Passenger.Lsp.LanguageServer.Trace = (from, message) =>
            {
                if (from == server && message.StartsWith("-->", StringComparison.Ordinal) && (message.Contains($"\"{Passenger.Lsp.LspMethods.References}\"", StringComparison.Ordinal)
                    || message.Contains($"\"{Passenger.Lsp.LspMethods.Implementation}\"", StringComparison.Ordinal)))
                {
                    Interlocked.Increment(ref asked);
                }
            };
            Assert.False(window.Settings.CodeSpyglass, "Code Spyglass is on in a window that was never told to show it");
            Ui.PlaceAfter(editor, "Never() => 3;");
            window.KeyTextInput(" ");
            Ui.Settle();
            Ui.Hold(TimeSpan.FromSeconds(3), "the pause a hint would wait for after typing and after the file was shown");
            Assert.True(Hints(editor).Count == 0, $"With Code Spyglass off the file shows {Shown(editor)}");
            Assert.True(Volatile.Read(ref asked) == 0, $"With Code Spyglass off the server was asked for references or implementations {asked} times");
        }
        finally
        {
            Passenger.Lsp.LanguageServer.Trace = null;
            window.Close();
        }
    }

    /// <summary>An interface's members and an abstract or virtual member's are followed by what implements or overrides them,
    /// an abstract class by what derives from it; a kind turned off takes its hints away alone, and Spyglass itself, or every
    /// kind, turned off takes the rest.</summary>
    [AvaloniaFact]
    public void Implementations_and_overrides_are_counted_and_each_kind_and_spyglass_itself_can_be_turned_off()
    {
        MainWindow window = project.Open("Shapes.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            window.ApplySettings(Only(window.Settings, usages: true, inheritors: true));
            WaitForHints(editor, ShapesHints);

            window.ApplySettings(window.Settings with { CodeSpyglassUsages = false });
            Ui.WaitFor(() => Hints(editor).Count > 0 && Hints(editor).All(h => !h.Text.Contains("usage", StringComparison.Ordinal)), () => $"the usages taken away ({Shown(editor)})");
            Assert.True(On(editor, "interface IShape").SequenceEqual(["1 implementation"]) && On(editor, "virtual double Perimeter()").SequenceEqual(["1 override"]),
                $"Turning usages off left {Shown(editor)}, not the implementations and overrides alone");

            window.ApplySettings(window.Settings with { CodeSpyglassUsages = true, CodeSpyglassInheritors = false });
            Ui.WaitFor(() => Hints(editor).Count > 0 && Hints(editor).All(h => h.Text.Contains("usage", StringComparison.Ordinal)), () => $"the implementations taken away ({Shown(editor)})");

            window.ApplySettings(window.Settings with { CodeSpyglass = false, CodeSpyglassInheritors = true });
            Ui.WaitFor(() => Hints(editor).Count == 0, () => $"every hint taken away by turning Code Spyglass off ({Shown(editor)})");

            window.ApplySettings(window.Settings with { CodeSpyglass = true, CodeSpyglassUsages = false, CodeSpyglassInheritors = false });
            Ui.Hold(TimeSpan.FromSeconds(2), "the pause a hint would wait for");
            Assert.True(Hints(editor).Count == 0, $"With no kind of hint on, Code Spyglass showed {Shown(editor)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A line added above the declarations leaves each hint after its own declaration, whether it is carried with the
    /// text or read again.</summary>
    [AvaloniaFact]
    public void An_edit_above_the_declarations_leaves_each_hint_after_its_own()
    {
        MainWindow window = project.Open("Counts.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            window.ApplySettings(Only(window.Settings, usages: true));
            WaitForHints(editor, CountsHints);
            editor.GoTo(new TextPosition(0, 0));
            Ui.Press(window, "ENTER");
            Assert.True(editor.Document.Snapshot.GetLineText(0).Length == 0, "The new line is not above the declarations");
            WaitForHints(editor, CountsHints);
            Assert.True(Hints(editor).Count == CountsHints.Length, $"After the edit the file shows {Hints(editor).Count} hints, not {CountsHints.Length}: {Shown(editor)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A usages hint of one place goes straight there; one of several lists them in Show Usages' popup, the caret
    /// put on the declaration.</summary>
    [AvaloniaFact]
    public void Pressing_one_usage_goes_there_and_pressing_several_lists_them_in_a_popup()
    {
        MainWindow window = project.Open("Counts.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            window.ApplySettings(Only(window.Settings, usages: true));
            WaitForHints(editor, CountsHints);

            int once = LineOf(editor, "int Once()");
            Ui.Click(editor, editor.AnnotationBox(once, "1 usage")!.Value.Center);
            int run = LineOf(editor, "int Run()");
            Ui.WaitFor(() => editor.CaretPosition.Line == run, () => $"the caret to go to the one use of Once, on line {run + 1} (it is on {editor.CaretPosition.Line + 1}; {window.Status})");

            int twice = LineOf(editor, "int Twice()");
            Ui.Click(editor, editor.AnnotationBox(twice, "2 usages")!.Value.Center);
            Ui.WaitFor(() => window.Popup is UsagesPopup { Rows.Count: 3 }, () => $"the usages of Twice in Show Usages' popup ({window.Status})", Limit);
            Assert.True(editor.CaretPosition.Line == twice, $"Pressing the usages put the caret on line {editor.CaretPosition.Line + 1}, not on Twice's declaration at {twice + 1}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Show Usages of an interface member lists its declaration and its calls, the declarations of what implements it
    /// left to Go to Implementation, as the hint counts them.</summary>
    [AvaloniaFact]
    public void Show_usages_of_an_interface_member_lists_its_calls_and_not_what_implements_it()
    {
        MainWindow window = project.Open("Shapes.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "    int Sid");
            Ui.Run(window, AppActions.ShowUsages);
            Ui.WaitFor(() => window.Popup is UsagesPopup, () => $"Show Usages' popup ({window.Status})", Limit);
            UsagesPopup popup = (UsagesPopup)window.Popup!;
            Ui.WaitFor(() => popup.Rows.Count > 0, "the usages");
            Assert.True(popup.Rows.Count == 2 && popup.Rows.Any(u => u.LineText.Contains("shape.Sides", StringComparison.Ordinal)) && popup.Rows.All(u => !u.LineText.Contains("public int Sides => 4", StringComparison.Ordinal)),
                $"The popup lists {string.Join(" | ", popup.Rows.Select(u => u.LineText.Trim()))}, not the member's declaration and the one call, with the implementation Square.Sides left out");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An implementations hint of one place goes straight to it, as Go to Implementation does.</summary>
    [AvaloniaFact]
    public void Pressing_one_implementation_goes_to_it()
    {
        MainWindow window = project.Open("Shapes.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            window.ApplySettings(Only(window.Settings, inheritors: true));
            WaitForHints(editor, [("interface IShape", ["1 implementation"]), ("virtual double Perimeter()", ["1 override"])]);

            Ui.Click(editor, editor.AnnotationBox(LineOf(editor, "interface IShape"), "1 implementation")!.Value.Center);
            int square = LineOf(editor, "class Square : IShape");
            Ui.WaitFor(() => editor.CaretPosition.Line == square, () => $"the caret to go to Square on line {square + 1} (it is on {editor.CaretPosition.Line + 1}; {window.Status})");

            Ui.Click(editor, editor.AnnotationBox(LineOf(editor, "virtual double Perimeter()"), "1 override")!.Value.Center);
            int disc = LineOf(editor, "override double Perimeter()");
            Ui.WaitFor(() => editor.CaretPosition.Line == disc, () => $"the caret to go to Disc's override on line {disc + 1} (it is on {editor.CaretPosition.Line + 1}; {window.Status})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>In a Git repository each declaration is followed by who changed it most, by Git blame; the pointer resting on
    /// it tells the commit, and pressing it chooses that commit in the Git window.</summary>
    [AvaloniaFact]
    public void Authors_are_named_from_git_blame_the_pointer_tells_the_commit_and_pressing_chooses_it()
    {
        MainWindow window = project.Open("Counts.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            window.ApplySettings(Only(window.Settings, authors: true));
            WaitForHints(editor, [("static class Counts", ["Passenger Tests"]), ("int Once()", ["Passenger Tests"]), ("int Run()", ["Passenger Tests"])]);

            int once = LineOf(editor, "int Once()");
            Rect box = editor.AnnotationBox(once, "Passenger Tests")!.Value;
            window.MouseMove(editor.TranslatePoint(box.Center, window)!.Value, RawInputModifiers.None);
            Ui.WaitFor(() => window.Assist.HoverText?.Contains("First", StringComparison.Ordinal) == true, () => $"the card of the authors, naming the commit ({window.Assist.HoverText})");

            Ui.Click(editor, editor.AnnotationBox(once, "Passenger Tests")!.Value.Center);
            Ui.WaitFor(() => window.Status?.Contains("First", StringComparison.Ordinal) == true, () => $"the commit First chosen in the Git window ({window.Status})", Limit);
        }
        finally
        {
            window.Close();
        }
    }

    public sealed class Project : SharedProject
    {
        public Project()
            : base("spyglass-")
        {
            File.WriteAllText(PathOf("Sample.csproj"), TestProjects.Console());
            File.WriteAllText(PathOf("Counts.cs"), "static class Counts\n{\n    public static int Once() => 1;\n    public static int Twice() => 2;\n    public static int Never() => 3;\n}\n\n"
                + "static class Callers\n{\n    public static int Run() => Counts.Once() + Counts.Twice() + Counts.Twice();\n}\n");
            File.WriteAllText(PathOf("Shapes.cs"), "interface IShape\n{\n    int Sides { get; }\n}\n\nclass Square : IShape\n{\n    public int Sides => 4;\n}\n\n"
                + "class Figure\n{\n    public virtual double Perimeter() => 0;\n}\n\nclass Disc : Figure\n{\n    public override double Perimeter() => 1;\n}\n\n"
                + "abstract class Animal\n{\n    public abstract string Name { get; }\n}\n\nclass Dog : Animal\n{\n    public override string Name => \"dog\";\n}\n\n"
                + "class Reader\n{\n    public static int Read(IShape shape) => shape.Sides;\n}\n");
            Directory.CreateDirectory(PathOf("bin"));
        }

        /// <summary>The files committed before they are restored, by an author of the test's own.</summary>
        public override async ValueTask InitializeAsync()
        {
            await TestGit.InitAsync(Folder);
            await base.InitializeAsync();
        }
    }
}
