using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>C# formatted as it is typed, on a real project and its language server: the ';' that ends a statement lays out
/// the statement, and the '}' that closes a block, or leaving a block whose '}' Enter wrote, lays out the block, as
/// Reformat Code would, one Undo taking each back with its key or alone; a for loop's header, a string and a comment stay
/// as typed, so does everything with the setting off, and an agent's edit is written as given. In Vim mode the layout
/// joins the insert session's one step, and . types again what the session typed.</summary>
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

    /// <summary>The issue's loop as laid out once typed, the method around it as written.</summary>
    private const string Laid = "static class Loop\n{\n    public static void Run()\n    {\n        for (var i = 0; i < 10; i += 1)\n        {\n            var x = i + 1;\n        }\n    }\n}\n";

    /// <summary>The issue's loop typed at the left margin as a person types it here, '{' and Enter writing its '}': its
    /// header's ';'s change nothing, the body's ';' lays out the statement it ends, the caret after it, and Down, leaving
    /// the block, lays out the loop whole. Each layout is a step of Undo of its own key or move: Undo takes the loop's
    /// back alone, and again the ';' with its statement's.</summary>
    [AvaloniaFact]
    public void The_issues_loop_typed_as_a_person_types_it_is_laid_out_at_its_semicolon_and_as_the_caret_leaves_its_block_each_undone_alone()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        (string beforeSemicolon, string statement) = TypeTheLoop(window, editor);

        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == beforeSemicolon,
            $"One Undo did not take the ';' and its layout back together, to the statement as typed:\n{editor.Document.Snapshot}");
        Ui.Run(window, EditorActions.Redo);
        Assert.True(editor.Document.Snapshot.ToString() == statement, $"Redo did not bring the ';' back laid out:\n{editor.Document.Snapshot}");

        // Down goes past the '}' that '{' and Enter wrote: the block is left.
        Ui.Press(window, "DOWN");
        Assert.True(editor.Document.Snapshot.ToString() == Laid, $"Leaving the loop's block did not lay the loop out:\n{editor.Document.Snapshot}");
        Assert.True(Line() == "        }" && editor.CaretPosition.Column == Line().Length,
            $"The caret did not stay after the block's '}}' it went to: '{Line()}', the caret at {editor.CaretPosition.Column}");

        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == statement, $"One Undo did not take the loop's layout back alone:\n{editor.Document.Snapshot}");
        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == beforeSemicolon, $"A second Undo did not take the ';' and its layout back:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>A '}' typed on a line of its own above the '}' that '{' and Enter wrote types over that one, the line it was
    /// typed on taken away, so the loop has one brace, and lays the loop out, the caret after it; one Undo takes the brace
    /// and the layout back together, the line typed on with them.</summary>
    [AvaloniaFact]
    public void A_brace_typed_above_the_one_enter_wrote_closes_the_block_with_one_brace_laid_out_and_one_undo_takes_both_back()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        TypeTheLoop(window, editor);
        Ui.Press(window, "ENTER");
        string above = editor.Document.Snapshot.ToString();
        Assert.True(Line().Trim().Length == 0, $"Enter after the statement did not start a line of its own: '{Line()}'");

        window.KeyTextInput("}");
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString() == Laid, $"The '}}' typed above the written one did not close the loop with one brace, laid out:\n{editor.Document.Snapshot}");
        Assert.True(Line() == "        }" && editor.CaretPosition.Column == Line().Length,
            $"The caret did not stay after the '}}' typed: '{Line()}', the caret at {editor.CaretPosition.Column}");

        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == above, $"One Undo did not take the '}}' and the loop's layout back together:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>Types the issue's loop at the left margin of Run()'s empty line as a person types it: the header in runs
    /// with each ';' alone, Enter, '{' (which writes its '}'), Enter (which pushes it down), the statement and its ';'.
    /// Returns the text before that ';' and once it laid its statement out.</summary>
    private static (string BeforeSemicolon, string Statement) TypeTheLoop(MainWindow window, CodeEditor editor)
    {
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
        Assert.True(editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line + 1) == "}", $"'{{' and Enter did not write the block's '}}' below:\n{editor.Document.Snapshot}");
        window.KeyTextInput("var x=i+1");
        Ui.Settle();
        string beforeSemicolon = editor.Document.Snapshot.ToString();
        window.KeyTextInput(";");
        Ui.Settle();
        // Indented where the statement stands in the method, as Reformat Code would: the loop around it is laid out as its block closes.
        Assert.True(Line() == "            var x = i + 1;" && editor.CaretPosition.Column == Line().Length,
            $"The ';' did not lay out the statement it ends, the caret after it: '{Line()}', the caret at {editor.CaretPosition.Column}\n{editor.Document.Snapshot}");
        return (beforeSemicolon, editor.Document.Snapshot.ToString());
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

    /// <summary>In Vim mode a block typed in an insert session and left by o on its '}' line is laid out within o's session,
    /// whose one step u takes back with the layout and Ctrl+R brings back, and . types again what that session's keys
    /// typed.</summary>
    [AvaloniaFact]
    public void In_vim_mode_a_block_left_by_o_is_laid_out_within_os_one_step_and_dot_types_again_what_its_keys_typed()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        window.ApplySettings(window.Settings with { VimMode = true });
        Ui.Settle();
        Ui.PlaceAfter(editor, "Run()\n    {\n");
        editor.Focus();
        Ui.Type(window, "I", "i");
        window.KeyTextInput("for (var i=0;i<10;i+=1)");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("{");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("var x=i+1");
        Ui.Settle();
        window.KeyTextInput(";");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        string typed = editor.Document.Snapshot.ToString();

        Ui.Type(window, "J", "j");
        Assert.Equal("}", Line());
        Ui.Type(window, "O", "o");
        window.KeyTextInput("int y=2");
        Ui.Settle();
        window.KeyTextInput(";");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        string laid = Laid.Replace("        }\n    }", "        }\n        int y = 2;\n    }", StringComparison.Ordinal);
        Assert.True(editor.Document.Snapshot.ToString() == laid, $"o leaving the loop's block did not lay the loop out:\n{editor.Document.Snapshot}");

        Ui.Type(window, "U", "u");
        Assert.True(editor.Document.Snapshot.ToString() == typed, $"u did not take o's session back whole, the loop's layout with it:\n{editor.Document.Snapshot}");
        Ui.Press(window, "control R");
        Assert.True(editor.Document.Snapshot.ToString() == laid, $"Ctrl+R did not bring o's session back, the loop laid out:\n{editor.Document.Snapshot}");

        Ui.Type(window, "PERIOD", ".");
        Ui.Settle();
        Assert.True(Line().TrimStart() == "int y = 2;", $". did not type again what o's session typed: '{Line()}'\n{editor.Document.Snapshot}");
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
