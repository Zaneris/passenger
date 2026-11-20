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
    /// with each ';' alone, Enter, '{' (which writes its '}'), Enter (which pushes it down, the braces indented where the
    /// block's layout has them), the statement and its ';'. Returns the text before that ';' and once it laid its
    /// statement out.</summary>
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
        Assert.True(editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line + 1) == "        }", $"'{{' and Enter did not write the block's '}}' below, laid out:\n{editor.Document.Snapshot}");
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
        Assert.Equal("        }", Line());
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

    /// <summary>A class with an indented empty line in its body and one in a method's, where the block tests type.</summary>
    private const string Blocks = "using System;\n\nclass Blocks\n{\n    \n    public static void Run(string[] args)\n    {\n        \n    }\n}\n";

    /// <summary><see cref="Blocks"/> under an .editorconfig that keeps a block's '{' on the line before it.</summary>
    private const string KeptBlocks = "using System;\n\nclass KeptBlocks\n{\n    \n    public static void Run(string[] args)\n    {\n        \n    }\n}\n";

    /// <summary>The issue's initializer typed key by key in a method as a person types it: '{' writes its '}', and Enter
    /// between them lays the '{' out on a line of its own at the statement's indentation, as the file's formatting (C#'s
    /// default, no .editorconfig saying otherwise) puts it, the caret on the empty line between at the block's
    /// indentation. One Undo takes Enter and its layout back together, to the braces as typed.</summary>
    [AvaloniaFact]
    public void The_issues_initializer_has_its_brace_laid_out_on_a_line_of_its_own_at_enter_and_one_undo_takes_both_back()
    {
        MainWindow window = project.Open("Blocks.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "    {\n        ");
        editor.Focus();
        string typed = TypeBlock(window, editor, "var item = new", "(", ")", " ");
        Assert.True(typed == Blocks.Replace("    {\n        \n", "    {\n        var item = new() {}\n", StringComparison.Ordinal),
            $"The keys did not type the initializer's braces as a pair:\n{typed}");

        Ui.Press(window, "ENTER");
        AssertOpened(editor, Blocks.Replace("    {\n        \n", "    {\n        var item = new()\n        {\n            \n        }\n", StringComparison.Ordinal),
            "the initializer's");

        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == typed && editor.Document.Snapshot[editor.Selections.Primary.Caret - 1] == '{',
            $"One Undo did not take Enter and its layout back together, to the braces as typed, the caret between:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>Under an .editorconfig that keeps a block's '{' on its line, Enter between the initializer's braces leaves
    /// the '{' where it was typed, the caret on the empty line between at the block's indentation.</summary>
    [AvaloniaFact]
    public void Under_an_editorconfig_keeping_braces_on_their_line_enter_leaves_the_initializers_brace_where_it_was_typed()
    {
        MainWindow window = project.Open("Kept/Blocks.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "    {\n        ");
        editor.Focus();
        TypeBlock(window, editor, "var item = new", "(", ")", " ");
        Ui.Press(window, "ENTER");
        AssertOpened(editor, KeptBlocks.Replace("    {\n        \n", "    {\n        var item = new() {\n            \n        }\n", StringComparison.Ordinal),
            "the initializer's, kept on its line,");
        window.Close();
    }

    /// <summary>Each of C#'s blocks typed as a person types it, in a method's body or a class's, its '{' writing its '}' and
    /// Enter laying the '{' out on a line of its own at its statement's or member's indentation, as the file's formatting
    /// puts each kind's (initializers, control blocks, a switch, a lambda, a local function, a method, a property, a type),
    /// the caret on the empty line between; one Undo takes each Enter back with its layout.</summary>
    [AvaloniaFact]
    public void Each_kind_of_block_has_its_brace_laid_out_as_the_file_puts_it_at_enter()
    {
        MainWindow window = project.Open("Blocks.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        (string Place, string Header)[] blocks =
        [
            ("    {\n        ", "var list = new System.Collections.Generic.List<int>"),
            ("    {\n        ", "int[] numbers ="),
            ("    {\n        ", "if (args.Length > 0)"),
            ("    {\n        ", "for (var i = 0; i < 3; i++)"),
            ("    {\n        ", "foreach (string arg in args)"),
            ("    {\n        ", "while (args.Length > 3)"),
            ("    {\n        ", "switch (args.Length)"),
            ("    {\n        ", "try"),
            ("    {\n        ", "Action run = () =>"),
            ("    {\n        ", "void Local()"),
            ("Blocks\n{\n    ", "void Other()"),
            ("Blocks\n{\n    ", "int Count"),
            ("Blocks\n{\n    ", "class Inner"),
        ];
        foreach ((string place, string header) in blocks)
        {
            Ui.PlaceAfter(editor, place);
            editor.Focus();
            string indent = place[(place.LastIndexOf('\n') + 1)..];
            string typed = TypeBlock(window, editor, header + " ");
            Ui.Press(window, "ENTER");
            AssertOpened(editor, Blocks.Replace(place + "\n", $"{place}{header}\n{indent}{{\n{indent}    \n{indent}}}\n", StringComparison.Ordinal),
                $"'{header}''s");
            Ui.Run(window, EditorActions.Undo);
            Assert.True(editor.Document.Snapshot.ToString() == typed, $"One Undo did not take Enter after '{header}' back with its layout:\n{editor.Document.Snapshot}");
            for (int undone = 0; undone < 20 && editor.Document.Snapshot.ToString() != Blocks; undone++)
            {
                Ui.Run(window, EditorActions.Undo);
            }
            Assert.True(editor.Document.Snapshot.ToString() == Blocks, $"Undo did not take '{header}' and its block back:\n{editor.Document.Snapshot}");
        }
        window.Close();
    }

    /// <summary>In Vim mode the initializer's layout at Enter joins the insert session's one step, which u takes back whole
    /// and Ctrl+R brings back, and . on another line types again what the session typed, laid out as it was.</summary>
    [AvaloniaFact]
    public void In_vim_mode_enters_layout_joins_the_insert_sessions_one_step_and_dot_types_it_again_laid_out()
    {
        MainWindow window = project.Open("Blocks.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line(int below = 0) => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line + below);
        window.ApplySettings(window.Settings with { VimMode = true });
        Ui.Settle();
        Ui.PlaceAfter(editor, "    {\n    ");
        editor.Focus();
        Ui.Type(window, "shift A", "A");
        TypeBlock(window, editor, "var item = new", "(", ")", " ");
        Ui.Press(window, "ENTER");
        Assert.True(Line(-1) == "        {" && Line(-2) == "        var item = new()",
            $"Enter in the insert session did not lay the initializer's '{{' out on a line of its own:\n{editor.Document.Snapshot}");
        Ui.Press(window, "ESCAPE");
        string laid = editor.Document.Snapshot.ToString();

        Ui.Type(window, "U", "u");
        Assert.True(editor.Document.Snapshot.ToString() == Blocks, $"u did not take the insert session back whole, the layout with it:\n{editor.Document.Snapshot}");
        Ui.Press(window, "control R");
        Assert.True(editor.Document.Snapshot.ToString() == laid, $"Ctrl+R did not bring the session back laid out:\n{editor.Document.Snapshot}");

        Ui.PlaceAfter(editor, "Blocks\n{\n");
        Ui.Type(window, "PERIOD", ".");
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString().StartsWith("using System;\n\nclass Blocks\n{\n    var item = new()\n        {\n", StringComparison.Ordinal),
            $". did not type again what the session typed, laid out as it was:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>Types each of <paramref name="keys"/> alone, as a person types them, then '{', which writes its '}'; returns
    /// the text then.</summary>
    private static string TypeBlock(MainWindow window, CodeEditor editor, params string[] keys)
    {
        foreach (string key in (string[])[.. keys, "{"])
        {
            window.KeyTextInput(key);
            Ui.Settle();
        }
        Assert.True(editor.Document.Snapshot[editor.Selections.Primary.Caret] == '}', $"'{{' did not write its '}}' after the caret:\n{editor.Document.Snapshot}");
        return editor.Document.Snapshot.ToString();
    }

    /// <summary>Holds the text to <paramref name="laid"/>, Enter's block laid out, the caret at the end of the empty line
    /// inside it.</summary>
    private static void AssertOpened(CodeEditor editor, string laid, string what)
    {
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        string below = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line + 1);
        Assert.True(editor.Document.Snapshot.ToString() == laid, $"Enter did not lay out {what} block as the file's formatting does:\n{editor.Document.Snapshot}");
        Assert.True(line.Trim().Length == 0 && line.Length == below.Length - below.TrimStart().Length + 4 && editor.CaretPosition.Column == line.Length && below.Trim() == "}",
            $"The caret did not stand on the empty line inside {what} block at its indentation: '{line}', the caret at {editor.CaretPosition.Column}");
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
            File.WriteAllText(PathOf("Blocks.cs"), Blocks);
            Directory.CreateDirectory(PathOf("Kept"));
            File.WriteAllText(PathOf("Kept/.editorconfig"), "[*.cs]\ncsharp_new_line_before_open_brace = none\n");
            File.WriteAllText(PathOf("Kept/Blocks.cs"), KeptBlocks);
        }
    }
}
