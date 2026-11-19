using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>C# formatted as it is typed, on a real project and its language server: the ';' that ends a statement lays out
/// the statement and the '}' that closes a block lays out the block, as Reformat Code would, one Undo taking each back
/// with its key; a for loop's header, a string and a comment stay as typed, so does everything with the setting off, and an
/// agent's edit is written as given. In Vim mode the layout joins the insert session's one step, and . types again what
/// the session typed.</summary>
public sealed class FormatOnTypeTests : IClassFixture<FormatOnTypeTests.Project>
{
    /// <summary>A method with an empty line in its body, where each test types at the left margin.</summary>
    private const string Empty = "static class Loop\n{\n    public static void Run()\n    {\n\n    }\n}\n";

    private readonly Project project;

    public FormatOnTypeTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>The issue's loop typed at the left margin as the keys type it here: its header's ';'s change nothing, the
    /// body's ';' lays out the statement it ends, and the '}' typed over the one '{' wrote lays out the loop whole; one Undo
    /// takes each layout back with its key, the caret after what was typed throughout.</summary>
    [AvaloniaFact]
    public void The_issues_loop_typed_at_the_left_margin_is_laid_out_at_its_semicolon_and_its_brace_and_one_undo_takes_each_back_with_its_key()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Ui.PlaceAfter(editor, "Run()\n    {\n");
        editor.Focus();

        foreach (string typed in (string[])["for (var i=0", ";", "i<10", ";", "i+=1", ")"])
        {
            window.KeyTextInput(typed);
            Ui.Settle();
        }
        Assert.Equal("for (var i=0;i<10;i+=1)", Line());

        Ui.Press(window, "ENTER");
        window.KeyTextInput("{");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("var x=i+1");
        Ui.Settle();
        string beforeSemicolon = editor.Document.Snapshot.ToString();
        window.KeyTextInput(";");
        Ui.Settle();
        // Indented where the statement stands in the method, as Reformat Code would: the loop around it is laid out at its '}'.
        Assert.True(Line() == "            var x = i + 1;" && editor.CaretPosition.Column == Line().Length,
            $"The ';' did not lay out the statement it ends, the caret after it: '{Line()}', the caret at {editor.CaretPosition.Column}\n{editor.Document.Snapshot}");
        string statement = editor.Document.Snapshot.ToString();

        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == beforeSemicolon,
            $"One Undo did not take the ';' and its layout back together, to the statement as typed:\n{editor.Document.Snapshot}");
        Ui.Run(window, EditorActions.Redo);
        Assert.True(editor.Document.Snapshot.ToString() == statement, $"Redo did not bring the ';' back laid out:\n{editor.Document.Snapshot}");

        // The '}' that '{' wrote, typed over.
        Ui.Press(window, "DOWN");
        Ui.Press(window, "HOME");
        Assert.Equal("}", Line());
        window.KeyTextInput("}");
        Ui.Settle();
        const string Laid = "static class Loop\n{\n    public static void Run()\n    {\n        for (var i = 0; i < 10; i += 1)\n        {\n            var x = i + 1;\n        }\n    }\n}\n";
        Assert.True(editor.Document.Snapshot.ToString() == Laid, $"The '}}' closing the loop's block did not lay the loop out:\n{editor.Document.Snapshot}");
        Assert.True(Line() == "        }" && editor.CaretPosition.Column == Line().Length,
            $"The caret did not stay after the '}}' typed: '{Line()}', the caret at {editor.CaretPosition.Column}");

        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == statement, $"One Undo did not take the '}}' and the loop's layout back together:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>A ';' typed in a string or a comment changes nothing; turned off in Settings, a statement's ';' leaves it as
    /// typed; and a key typed before the server's layout came leaves the text as the keys made it. A ';' that chooses what
    /// completion offers before it still lays out the statement it ends.</summary>
    [AvaloniaFact]
    public void A_semicolon_in_a_string_or_a_comment_with_the_setting_off_or_typed_on_before_the_layout_came_leaves_the_text_as_typed()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Ui.PlaceAfter(editor, "Run()\n    {\n");
        editor.Focus();

        void TypeLine(string text, string end)
        {
            window.KeyTextInput(text);
            Ui.Settle();
            window.KeyTextInput(end);
            Ui.Settle();
        }
        TypeLine("var s=\"a=b", ";");
        Assert.Equal("var s=\"a=b;", Line());
        Ui.Press(window, "ENTER");
        TypeLine("// x=1", ";");
        Assert.Equal("// x=1;", Line());
        Ui.Press(window, "ENTER");

        window.ApplySettings(window.Settings with { FormatOnType = false });
        Ui.Settle();
        TypeLine("int y=1", ";");
        Assert.Equal("int y=1;", Line());
        window.ApplySettings(window.Settings with { FormatOnType = true });
        Ui.Settle();

        // The layout's answer held until a key is typed after the ';'.
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int z=2");
        Ui.Settle();
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = window.CSharpService!.Session!.Server.HoldNextAnswer(Passenger.Lsp.LspMethods.OnTypeFormatting, _ => true, released.Task);
        try
        {
            window.KeyTextInput(";");
            Ui.PumpUntil(() => held.IsCompleted, "the server's layout of the statement, held");
            window.KeyTextInput(" ");
        }
        finally
        {
            released.SetResult();
        }
        Ui.Settle();
        Assert.Equal("int z=2; ", Line());

        // Typed on its own once more, the same statement is laid out.
        Ui.Press(window, "BACK_SPACE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput(";");
        Ui.Settle();
        Assert.True(Line().TrimStart() == "int z = 2;", $"A statement's ';' was not laid out once its answer came in time: '{Line()}'");

        // A ';' that chooses the name completion offers lays out the statement it ends all the same.
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int w=z");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen && window.Assist.CompletionLabels.FirstOrDefault() == "z", "the local z offered after the '='");
        window.KeyTextInput(";");
        Ui.Settle();
        Assert.True(Line().TrimStart() == "int w = z;", $"A ';' choosing the completion did not lay out the statement it ends: '{Line()}'");
        window.Close();
    }

    /// <summary>An agent's edit writing a statement and a block is written as given: only typing is laid out.</summary>
    [AvaloniaFact]
    public void An_agents_edit_is_written_as_given_and_never_laid_out()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        PassengerTools agent = new(window);
        const string Written = "    {\nfor (var i=0;i<10;i+=1)\n{\n    var x=i+1;\n}\n";
        Ui.Await(agent.Edit("Loop.cs", "    {\n\n", Written), "the agent's edit");
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString() == Empty.Replace("    {\n\n", Written, StringComparison.Ordinal),
            $"The agent's edit was not written as given:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>In Vim mode a statement typed in an insert session is laid out at its ';' within the session's one step of
    /// Undo, and . on another line types again what the session's keys typed, laid out as they were, its indent the
    /// layout's alone.</summary>
    [AvaloniaFact]
    public void In_vim_mode_the_layout_joins_the_insert_sessions_one_step_and_dot_types_again_what_the_keys_typed()
    {
        MainWindow window = project.Open("Twice.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        window.ApplySettings(window.Settings with { VimMode = true });
        Ui.Settle();
        Ui.PlaceAfter(editor, "Run()\n    {\n");
        editor.Focus();
        string before = editor.Document.Snapshot.ToString();

        Ui.Type(window, "I", "i");
        window.KeyTextInput("int x=1");
        Ui.Settle();
        window.KeyTextInput(";");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        string laid = editor.Document.Snapshot.ToString();
        Assert.True(laid == before.Replace("{\n\n", "{\n        int x = 1;\n", StringComparison.Ordinal), $"The ';' typed in the insert session did not lay out its statement:\n{laid}");

        Ui.Type(window, "U", "u");
        Assert.True(editor.Document.Snapshot.ToString() == before, $"u did not take the insert session back whole, its layout with it:\n{editor.Document.Snapshot}");
        Ui.Press(window, "control R");
        Assert.True(editor.Document.Snapshot.ToString() == laid, $"Ctrl+R did not bring the session back laid out:\n{editor.Document.Snapshot}");

        Ui.Type(window, "J", "j");
        Ui.Type(window, "J", "j");
        Ui.Type(window, "PERIOD", ".");
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line) == "int x = 1;",
            $". did not type again what the session's keys typed, laid out as they were:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>A console project with methods to type in, restored once for every test.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("ontype-")
        {
            File.WriteAllText(PathOf("Typed.csproj"), TestProjects.Console());
            File.WriteAllText(PathOf("Loop.cs"), Empty);
            File.WriteAllText(PathOf("Twice.cs"), "static class Twice\n{\n    public static void Run()\n    {\n\n        int kept = 0;\n\n    }\n}\n");
        }
    }
}
