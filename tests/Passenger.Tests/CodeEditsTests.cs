using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Edits beyond completion and Refactor This, each through its own keys in the window on a real project with
/// its language server: Surround With's templates, Complete Current Statement, Optimize Imports, Safe Delete, Introduce
/// Variable, Field and Parameter, Encapsulate Field, and Inline.</summary>
public sealed class CodeEditsTests : IClassFixture<CodeEditsTests.Project>
{
    private readonly Project project;

    public CodeEditsTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    private const string TwoLines = "total += 1;\n        total += 2;";

    /// <summary>The statement templates around two statements, in the order Surround With offers them: what each
    /// writes, what it leaves chosen to type over first, and where its caret ends once its variables are filled
    /// in.</summary>
    private static readonly (string Template, string Written, string First)[] StatementTemplates =
    [
        ("if", "        if (true)\n        {\n            total += 1;\n            total += 2;\n        }\n", "true"),
        ("while", "        while (true)\n        {\n            total += 1;\n            total += 2;\n        }\n", "true"),
        ("for", "        for (int i = 0; i < length; i++)\n        {\n            total += 1;\n            total += 2;\n        }\n", "i"),
        ("do", "        do\n        {\n            total += 1;\n            total += 2;\n        } while (true);\n", "true"),
        ("lock", "        lock (this)\n        {\n            total += 1;\n            total += 2;\n        }\n", "this"),
        ("{}", "        {\n            total += 1;\n            total += 2;\n        }\n", ""),
        ("try", "        try\n        {\n            total += 1;\n            total += 2;\n        }\n        catch (Exception e)\n        {\n            Console.WriteLine(e);\n            throw;\n        }\n", "Exception"),
        ("tryf", "        try\n        {\n            total += 1;\n            total += 2;\n        }\n        finally\n        {\n            \n        }\n", ""),
        ("#if", "        #if true\n        total += 1;\n        total += 2;\n        #endif\n", "true"),
        ("#region", "        #region MyRegion\n        total += 1;\n        total += 2;\n        #endregion\n", "MyRegion"),
        ("foreach", "        foreach (var item in collection)\n        {\n            total += 1;\n            total += 2;\n        }\n", "item"),
        ("using", "        using (resource)\n        {\n            total += 1;\n            total += 2;\n        }\n", "resource"),
    ];

    /// <summary>Surround With wraps the statements chosen in each of its templates and leaves the caret at the
    /// template's end, each taken back by one Undo; Complete Current Statement closes what is typed (an if, a call, a
    /// declaration, a method) and puts the caret where the next code goes; and around an expression Surround With
    /// offers a cast and parentheses, around types a namespace.</summary>
    [Gate]
    [AvaloniaFact]
    public void Surround_with_and_complete_current_statement_write_rider_s_code_around_and_after_what_is_chosen_or_typed()
    {
        MainWindow window = project.Open("Sum.cs", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        string before = editor.Document.Snapshot.ToString();
        foreach ((string template, string written, string first) in StatementTemplates)
        {
            Choose(editor, TwoLines);
            editor.Focus();
            Ui.Run(window, AppActions.SurroundWith);
            Assert.Equal(["if", "while", "for", "do", "lock", "()", "{}", "try", "tryf", "#if", "#region", "foreach", "using"], window.Assist.Offered);
            Pick(window, template);
            string text = editor.Document.Snapshot.ToString();
            Assert.True(text.Contains("        int total = 0;\n" + written + "        return total;\n", StringComparison.Ordinal),
                $"Surround With {template} did not wrap the two statements as Rider writes it:\n{text}");
            Assert.True(first == Selected(editor), $"Surround With {template} chose '{Selected(editor)}' to type over first, not '{first}'");

            for (int field = 0; field < 3 && editor.IsInSnippet; field++)
            {
                Ui.Press(window, "TAB");
            }
            Assert.False(editor.IsInSnippet, $"{template} was still being filled in after three Tabs");
            Assert.Equal(template == "try" ? "Console.WriteLine(e);\n            throw;" : string.Empty, Selected(editor));
            int expected = template switch
            {
                "try" => text.IndexOf("throw;", StringComparison.Ordinal) + "throw;".Length,
                "tryf" => text.IndexOf("finally\n        {\n            ", StringComparison.Ordinal) + "finally\n        {\n            ".Length,
                _ => text.IndexOf("total += 2;", StringComparison.Ordinal) + "total += 2;".Length,
            };
            Assert.True(expected == editor.Selections.Primary.Caret,
                $"After {template} the caret is at {editor.CaretPosition}, not where Rider leaves it:\n{text.Insert(editor.Selections.Primary.Caret, "|")}");

            Ui.Run(window, EditorActions.Undo);
            Assert.True(before == editor.Document.Snapshot.ToString(), $"Undo after {template} left:\n{editor.Document.Snapshot}");
        }

        string Text() => editor.Document.Snapshot.ToString();
        int After(string code) => Text().IndexOf(code, StringComparison.Ordinal) + code.Length;
        Ui.PlaceAfter(editor, "int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("if (total > 0");
        Ui.Run(window, AppActions.CompleteStatement);
        Assert.Contains("        int total = 0;\n        if (total > 0)\n        {\n            \n        }\n        total += 1;\n", Text(), StringComparison.Ordinal);
        Assert.Equal(After("if (total > 0)\n        {\n            "), editor.Selections.Primary.Caret);

        window.KeyTextInput("Report(total");
        Ui.Run(window, AppActions.CompleteStatement);
        Assert.Contains("        {\n            Report(total);\n            \n        }\n", Text(), StringComparison.Ordinal);
        Assert.Equal(After("Report(total);\n            "), editor.Selections.Primary.Caret);

        window.KeyTextInput("int doubled = total * 2");
        Ui.Run(window, AppActions.CompleteStatement);
        Assert.Contains("            Report(total);\n            int doubled = total * 2;\n            \n        }\n", Text(), StringComparison.Ordinal);
        Assert.Equal(After("int doubled = total * 2;\n            "), editor.Selections.Primary.Caret);

        Ui.PlaceAfter(editor, "        return total;\n    }");
        Ui.Press(window, "ENTER");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("static void Report(int value");
        Ui.Run(window, AppActions.CompleteStatement);
        Assert.True(Text().EndsWith("\n    static void Report(int value)\n    {\n        \n    }\n}\n", StringComparison.Ordinal),
            $"The method's signature was not completed as Rider completes it:\n{Text().Insert(editor.Selections.Primary.Caret, "|")}");
        Assert.Equal(After("static void Report(int value)\n    {\n        "), editor.Selections.Primary.Caret);

        Choose(editor, "return total", skip: "return ".Length);
        editor.Focus();
        Ui.Run(window, AppActions.SurroundWith);
        Assert.Equal(["()", "{}", "cast"], window.Assist.Offered);
        // Its digit chooses it: 0 is the cast, its type to be typed first.
        Ui.Press(window, "0");
        Assert.Contains("        return (()total);\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.KeyTextInput("long");
        Ui.Press(window, "TAB");
        string wrapped = editor.Document.Snapshot.ToString();
        Assert.Contains("        return ((long)total);\n", wrapped, StringComparison.Ordinal);
        Assert.Equal(wrapped.IndexOf("((long)total)", StringComparison.Ordinal) + "((long)total)".Length, editor.Selections.Primary.Caret);

        // 6 is parentheses, the caret after what they wrap.
        Choose(editor, "total += 1", skip: 0);
        Ui.Run(window, AppActions.SurroundWith);
        Ui.Press(window, "6");
        wrapped = editor.Document.Snapshot.ToString();
        Assert.Contains("        (total += 1);\n", wrapped, StringComparison.Ordinal);
        Assert.Equal(wrapped.IndexOf("(total += 1)", StringComparison.Ordinal) + "(total += 1".Length, editor.Selections.Primary.Caret);

        window.OpenFile(project.PathOf("Two.cs"));
        Ui.Settle();
        editor.Selections = SelectionSet.Single(new Selection(0, editor.Document.Snapshot.Length));
        editor.Focus();
        Ui.Run(window, AppActions.SurroundWith);
        Assert.Contains("namespace", window.Assist.Offered);
        Pick(window, "namespace");
        Assert.Equal("namespace MyNamespace\n{\n    class First\n    {\n    }\n\n    class Second\n    {\n    }\n}\n", editor.Document.Snapshot.ToString());
        Assert.Equal("MyNamespace", Selected(editor));
        window.Close();
    }

    /// <summary>The language server's edits, in one window: Optimize Imports removes the usings nothing needs and sorts
    /// the rest; Introduce Field makes a constant a field of the type and says why not for anything else, one Undo
    /// taking it back; Introduce Parameter makes an expression a parameter and passes it at every call; and Encapsulate
    /// Field offers the choice of using the property and makes it.</summary>
    [Gate]
    [AvaloniaFact]
    public void Optimize_imports_introduce_field_and_parameter_and_encapsulate_field_make_their_edits_as_rider_names_them()
    {
        MainWindow window = project.Open("Usings.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Text() => editor.Document.Snapshot.ToString();
        editor.Focus();
        Ui.Run(window, AppActions.OptimizeUsings);
        Ui.WaitFor(() => window.Status?.StartsWith("Optimize Imports", StringComparison.Ordinal) == true || window.Status == "The usings are already optimized",
            "Optimize Imports to finish");
        Assert.Equal("Optimize Imports removed unused usings and sorted the rest", window.Status);
        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n\nstatic class Usings\n", Text(), StringComparison.Ordinal);

        window.OpenFile(project.PathOf("Scale.cs"));
        Ui.Settle();
        string scale = Text();
        editor.Focus();
        Choose(editor, "value * 1024", skip: 0, length: "value".Length);
        Ui.Run(window, AppActions.IntroduceField);
        Ui.WaitFor(() => window.Status?.StartsWith("Introduce Field", StringComparison.Ordinal) == true, "Introduce Field to answer");
        Assert.Equal("Introduce Field is not available here: the C# language server makes a field only of a constant expression", window.Status);

        Choose(editor, "1024", skip: 0);
        Ui.Run(window, AppActions.IntroduceField);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Introduce Field's choices");
        Assert.All(window.Assist.Offered, offered => Assert.DoesNotContain("local", offered, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Introduce constant for '1024'", window.Assist.Offered);
        PickTitled(window, "Introduce constant for '1024'");
        Ui.WaitFor(() => Text().Contains("const int", StringComparison.Ordinal), "the constant field to be made");
        Assert.Matches(@"\n    private const int \w+ = 1024;\n", Text());
        Assert.DoesNotContain("value * 1024", Text(), StringComparison.Ordinal);
        Ui.Run(window, EditorActions.Undo);
        Assert.True(Text() == scale, $"One Undo did not take the field back:\n{Text()}");

        Choose(editor, "1024", skip: 0);
        Ui.Run(window, AppActions.IntroduceParameter);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Introduce Parameter's choices");
        Assert.All(window.Assist.Offered, offered => Assert.StartsWith("Introduce parameter for", offered, StringComparison.Ordinal));
        PickTitled(window, "Introduce parameter for '1024'");
        Ui.WaitFor(() => window.Assist.Offered.Any(o => o.Contains("call sites", StringComparison.Ordinal)), "how the parameter is to be passed");
        PickTitled(window, window.Assist.Offered.First(o => o.Contains("call sites", StringComparison.Ordinal)));
        Ui.WaitFor(() => Text().Contains("Kilo(2, 1024)", StringComparison.Ordinal), "the call to pass the new parameter");
        Assert.Matches(@"public static int Kilo\(int value, int \w+\)", Text());
        Assert.DoesNotContain("value * 1024", Text(), StringComparison.Ordinal);

        window.OpenFile(project.PathOf("Counter.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "public int cou");
        editor.Focus();
        Ui.Run(window, AppActions.EncapsulateField);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Encapsulate Field's choices");
        Assert.Equal(["Encapsulate field: 'count' (and use property)", "Encapsulate field: 'count' (but still use field)"], window.Assist.Offered);
        PickTitled(window, "Encapsulate field: 'count' (and use property)");
        Ui.WaitFor(() => Text().Contains("public int Count", StringComparison.Ordinal), "the property to be made");
        Assert.Contains("private int count;", Text(), StringComparison.Ordinal);
        Assert.Contains("public void Add() => Count++;", Text(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Introduce Variable over an expression the method uses twice offers the server's two choices, and the one
    /// for every occurrence makes a local of it in the place of each, one Undo taking it back.</summary>
    [Gate]
    [AvaloniaFact]
    public void Introduce_variable_offers_one_occurrence_or_all_and_makes_a_local_in_the_place_of_each()
    {
        MainWindow window = project.Open("Prices.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Text() => editor.Document.Snapshot.ToString();
        string prices = Text();
        editor.Focus();
        Choose(editor, "price * count");
        Ui.Run(window, AppActions.IntroduceVariable);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Introduce Variable's choices");
        Assert.Equal(["Introduce local for 'price * count'", "Introduce local for all occurrences of 'price * count'"], window.Assist.Offered);
        PickTitled(window, "Introduce local for all occurrences of 'price * count'");
        Ui.WaitFor(() => !Text().Contains("price * count + price * count", StringComparison.Ordinal), "the local to be introduced");
        Assert.Matches(@"\n        int (\w+) = price \* count;\n        return \1 \+ \1 / 10;\n", Text());
        Assert.Equal("Introduce Variable done", window.Status);
        Ui.Run(window, EditorActions.Undo);
        Assert.True(Text() == prices, $"One Undo did not take the local back:\n{Text()}");
        window.Close();
    }

    /// <summary>Inline on a local puts its value in its one use at once; on the call of a method called once it offers
    /// to inline the method or to keep it too, and inlining puts its body in the call's place and deletes it.</summary>
    [Gate]
    [AvaloniaFact]
    public void Inline_puts_a_locals_value_in_its_use_and_a_methods_body_in_its_call()
    {
        MainWindow window = project.Open("Inlined.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Text() => editor.Document.Snapshot.ToString();
        Ui.PlaceAfter(editor, "int are");
        editor.Focus();
        Ui.Run(window, AppActions.Inline);
        Ui.WaitFor(() => Text().Contains("return width * height + 1;", StringComparison.Ordinal), () => $"the local to be inlined: {window.Status}");
        Assert.DoesNotContain("int area", Text(), StringComparison.Ordinal);
        Assert.Equal("Inline done", window.Status);

        Ui.PlaceAfter(editor, "=> Ha");
        Ui.Run(window, AppActions.Inline);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, () => $"Inline's choices for the method: {window.Status}");
        Assert.Equal(["Inline 'Half(int value)'", "Inline and keep 'Half(int value)'"], window.Assist.Offered);
        PickTitled(window, "Inline 'Half(int value)'");
        Ui.WaitFor(() => Text().Contains("Quarter(int value) => value / 2 + 1;", StringComparison.Ordinal), () => $"the method's body to take the call's place:\n{Text()}");
        Assert.DoesNotContain("Half", Text(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A method nothing uses goes at once, its doc comment and the blank line before it with it; one that is
    /// called has its calls listed and is deleted only when told to, from a usage too, the call left to fix; and Undo
    /// in the Solution view puts the declaration back.</summary>
    [Gate]
    [AvaloniaFact]
    public void Safe_delete_deletes_an_unused_method_at_once_and_lists_a_used_ones_usages_before_deleting_it_when_told_to()
    {
        MainWindow window = project.Open("Calc.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Text() => editor.Document.Snapshot.ToString();

        Ui.PlaceAfter(editor, "int Unu");
        editor.Focus();
        Ui.Run(window, AppActions.SafeDelete);
        Ui.WaitFor(() => !Text().Contains("Unused", StringComparison.Ordinal), "the unused method to be deleted");
        Assert.Equal("static class Calc\n{\n    public static int Twice(int value) => value * 2;\n\n    public static int Four() => Twice(2);\n}\n", Text());
        Assert.Equal("Deleted the method 'Unused'", window.Status);
        Assert.Empty(window.OwnedWindows.OfType<ConfirmDialog>());

        Ui.PlaceAfter(editor, "int Twi");
        Ui.Run(window, AppActions.SafeDelete);
        ConfirmDialog asked = Asked(window);
        string[] shown = [.. asked.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
        Assert.Contains("The method 'Twice' has 2 usages that would no longer compile once it is deleted:", shown);
        Assert.Contains("Calc.cs:5", shown);
        Assert.Contains("public static int Four() => Twice(2);", shown);
        Assert.Contains("Program.cs:5", shown);
        Ui.ClickButton(asked, "Cancel");
        Assert.Contains("Twice(int value)", Text(), StringComparison.Ordinal);
        Assert.Equal("Safe Delete left the method 'Twice' where it is", window.Status);

        Ui.PlaceAfter(editor, "=> Twi");
        Ui.Run(window, AppActions.SafeDelete);
        Ui.ClickButton(Asked(window), "Delete Anyway");
        Ui.WaitFor(() => !Text().Contains("Twice(int", StringComparison.Ordinal), "the used method to be deleted anyway");
        Assert.Equal("static class Calc\n{\n    public static int Four() => Twice(2);\n}\n", Text());
        Assert.Equal("Deleted the method 'Twice', leaving 2 usages to fix", window.Status);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Safe_delete_from_a_usage_of_a_method_declared_in_a_file_not_open_is_undone_from_the_solution_view()
    {
        string calc = project.PathOf("Calc.cs");
        string before = File.ReadAllText(calc);
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        List<string> asked = [];
        Ui.PlaceAfter(editor, "Calc.Twi");
        editor.Focus();
        Ui.Run(window, AppActions.SafeDelete);
        Ui.ClickButton(Asked(window), "Delete Anyway");
        Ui.WaitFor(() => !File.ReadAllText(calc).Contains("Twice(int", StringComparison.Ordinal), "Twice deleted from Calc.cs, which is not open");
        Assert.DoesNotContain(window.EditorArea.Documents, d => d.Name == "Calc.cs");

        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        window.ExplorerView.Reveal(Passenger.Core.PathNames.Canonical(calc));
        Ui.Settle();
        Passenger.App.Explorer.ExplorerNode row = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Name == "Calc.cs");
        Ui.ClickRow(window, row);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], asked);
        string after = File.ReadAllText(calc);
        Assert.True(after == before, $"Undo did not put back what Safe Delete took out of Calc.cs, which was not open, so only Local History has it:\n{after}");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void A_rename_reaching_a_file_not_open_that_changed_on_disk_a_moment_before_is_refused_and_once_the_server_has_it_lands_right()
    {
        string calc = project.PathOf("Calc.cs");
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string program = editor.Document.Snapshot.ToString();

        // An agent, or a checkout, writes Calc.cs, its lines two further down, as the rename is asked for.
        string written = "// Checked out again\n// by another program\n" + File.ReadAllText(calc);
        Ui.PlaceAfter(editor, "Calc.Twi");
        Ui.Run(window, AppActions.Rename);
        Ui.Settle();
        window.KeyTextInput("Double");
        File.WriteAllText(calc, written);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Status?.StartsWith("Rename was not applied", StringComparison.Ordinal) == true, "the rename to be refused");
        Assert.Equal("Rename was not applied: Calc.cs changed on disk as the language server worked this out, so the places it gave there may be where they were before: nothing was changed; try again in a moment", window.Status);
        Assert.Equal(written, File.ReadAllText(calc));
        Assert.Equal(program, editor.Document.Snapshot.ToString());

        // Asked again once the server holds the file as written and a write's moment has passed, it lands where the name is now.
        string held = Passenger.Roslyn.Extension.TextHash.Of(written);
        string served = Passenger.Core.PathNames.Canonical(calc);
        Task<IReadOnlyList<Passenger.Roslyn.Extension.TextHeld>>? asking = null;
        Ui.WaitFor(() =>
        {
            if (asking is { IsCompletedSuccessfully: true } done && done.Result is [{ Hash: { } hash }] && hash == held)
            {
                return true;
            }
            if (asking is null || asking.IsCompleted)
            {
                asking = window.CSharpService!.Session!.HeldTextsAsync([served]);
            }
            return false;
        }, "the language server to hold Calc.cs as it was written");
        TimeSpan unsettled = File.GetLastWriteTimeUtc(calc) + Passenger.App.CSharp.WorkspaceEdits.Catching - DateTime.UtcNow;
        if (unsettled > TimeSpan.Zero)
        {
            Ui.Hold(unsettled + TimeSpan.FromMilliseconds(100), "the moment after Calc.cs was written, in which a rename is refused whatever the server holds, to pass");
        }
        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.ToString().IndexOf("Calc.Twi", StringComparison.Ordinal) + "Calc.Twi".Length);
        Ui.Run(window, AppActions.Rename);
        Ui.Settle();
        window.KeyTextInput("Double");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.True(File.ReadAllText(calc) != written, $"Asked again once the server held Calc.cs, the rename was not applied: {window.Status}");
        Assert.Equal(written.Replace("Twice", "Double", StringComparison.Ordinal), File.ReadAllText(calc));
        Assert.Contains("Calc.Double(2)", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Safe_delete_in_a_file_not_open_whose_lines_end_in_carriage_returns_keeps_them()
    {
        string halve = project.Write("Halve.cs", "static class Halve\r{\r    public static int Of(int value) => value / 2;\r\r    public static int Two() => 1;\r}\r");
        project.Write("Halves.cs", "static class Halves\n{\n    public static int Four() => Halve.Of(8);\n}\n");
        MainWindow window = project.Open("Halves.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Halve.O");
        editor.Focus();
        Ui.Run(window, AppActions.SafeDelete);
        Ui.ClickButton(Asked(window), "Delete Anyway");
        Ui.WaitFor(() => !File.ReadAllText(halve).Contains("Of(int", StringComparison.Ordinal), "Of deleted from Halve.cs, which is not open");
        string after = File.ReadAllText(halve);
        Assert.True(!after.Contains('\n', StringComparison.Ordinal) && after.StartsWith("static class Halve\r{\r", StringComparison.Ordinal) && after.EndsWith("    public static int Two() => 1;\r}\r", StringComparison.Ordinal),
            $"Safe Delete wrote Halve.cs, whose lines end in carriage returns, with other line breaks: {after.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}");
        window.Close();
    }

    /// <summary>Chooses code in the editor: <paramref name="length"/> characters (all of <paramref name="code"/> when not
    /// given) from <paramref name="skip"/> into its first occurrence.</summary>
    private static void Choose(CodeEditor editor, string code, int skip = 0, int? length = null)
    {
        int at = editor.Document.Snapshot.ToString().IndexOf(code, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{code}' is not in the file");
        editor.Selections = SelectionSet.Single(new Selection(at + skip, at + skip + (length ?? code.Length - skip)));
    }

    private static string Selected(CodeEditor editor) =>
        editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length);

    /// <summary>Chooses a surround template by its digit when it has one, else by the arrow keys.</summary>
    private static void Pick(MainWindow window, string template)
    {
        string digit = template switch
        {
            "if" => "1",
            "while" => "2",
            "for" => "3",
            "do" => "4",
            "lock" => "5",
            "()" => "6",
            "{}" => "7",
            "try" => "8",
            "tryf" => "9",
            "cast" => "0",
            _ => string.Empty,
        };
        if (digit.Length > 0)
        {
            Ui.Press(window, digit);
            return;
        }
        PickTitled(window, template);
    }

    /// <summary>Chooses an item of the open list by its words, with the keys a person would press.</summary>
    private static void PickTitled(MainWindow window, string item)
    {
        int at = window.Assist.Offered.ToList().IndexOf(item);
        Assert.True(at >= 0, $"{item} is not offered: {string.Join("; ", window.Assist.Offered)}");
        Ui.Settle();
        for (int i = 0; i < at; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
    }

    private static ConfirmDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "Safe Delete to list the usages");
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single());
    }

    /// <summary>A small console project on disk, restored once for every test, and the warm server they share on it; a
    /// window never saves what its test typed.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("edits-")
        {
            File.WriteAllText(PathOf("Edits.csproj"),
                TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), "class Program\n{\n    static void Main()\n    {\n        int four = Calc.Twice(2) + Calc.Four() + Sum.Of() + Scale.Two();\n    }\n}\n");
            File.WriteAllText(PathOf("Sum.cs"), "static class Sum\n{\n    public static int Of()\n    {\n        int total = 0;\n        total += 1;\n        total += 2;\n        return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Two.cs"), "class First\n{\n}\n\nclass Second\n{\n}\n");
            File.WriteAllText(PathOf("Usings.cs"), "using System.Text;\nusing System.Collections.Generic;\nusing System;\n\nstatic class Usings\n{\n    public static int Count()\n    {\n"
                + "        List<string> names = [];\n        Console.WriteLine(names.Count);\n        return names.Count;\n    }\n}\n");
            File.WriteAllText(PathOf("Calc.cs"), "static class Calc\n{\n    public static int Twice(int value) => value * 2;\n\n    /// <summary>Never called.</summary>\n"
                + "    public static int Unused(int value) => value + 1;\n\n    public static int Four() => Twice(2);\n}\n");
            File.WriteAllText(PathOf("Scale.cs"), "static class Scale\n{\n    public static int Kilo(int value)\n    {\n        return value * 1024;\n    }\n\n    public static int Two() => Kilo(2);\n}\n");
            File.WriteAllText(PathOf("Counter.cs"), "class Counter\n{\n    public int count;\n\n    public void Add() => count++;\n}\n");
            File.WriteAllText(PathOf("Prices.cs"), "static class Prices\n{\n    public static int Total(int price, int count)\n    {\n        return price * count + price * count / 10;\n    }\n}\n");
            File.WriteAllText(PathOf("Inlined.cs"), "static class Inlined\n{\n    public static int Area(int width, int height)\n    {\n        int area = width * height;\n"
                + "        return area + 1;\n    }\n\n    private static int Half(int value) => value / 2;\n\n    public static int Quarter(int value) => Half(value) + 1;\n}\n");
        }
    }
}
