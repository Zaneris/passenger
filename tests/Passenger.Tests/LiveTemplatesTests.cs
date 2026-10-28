using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Live Templates and Postfix Completion pages in Settings, and what Tab and completion then do in the
/// editor on a real project with its language server: a template turned off stays the word typed, one whose text is
/// edited writes its new text, one added fills in its variables as their macros say, and all of it is there again after
/// a restart; a postfix template that could wrap more than one expression asks which, and one turned off is offered no
/// more.</summary>
public sealed class LiveTemplatesTests : IClassFixture<LiveTemplatesTests.Project>, IDisposable
{
    private readonly Project project;
    private readonly string settings = TestFolders.Create("settings-");

    public LiveTemplatesTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    public void Dispose() => TestFolders.Delete(settings);

    /// <summary>A template turned off stays the word typed and one whose text is edited expands to its new text; a template
    /// added fills in its variables as their macros say; and every change is there after a restart.</summary>
    [AvaloniaFact]
    public void Templates_turned_off_edited_and_added_in_Settings_type_as_changed_and_every_change_is_there_after_a_restart()
    {
        MainWindow window = Open("Sum.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        SettingsDialog dialog = LiveTemplates(window);
        LiveTemplatesPane page = dialog.Templates;
        Assert.True(page.Choose("cw"), "The Live Templates page does not list cw");
        Ui.Settle();
        Assert.Equal("Console.WriteLine($END$);", page.TextBox.Text);
        Ui.Click(page.EnabledBox("cw")!);
        Assert.False(page.Chosen?.Enabled, "cw's check box did not turn it off");
        Assert.True(page.Choose("thr"), "The Live Templates page does not list thr");
        Type(page.TextBox, "throw new $EXCEPTION$(\"$MESSAGE$\");");
        dialog.Ok();
        Ui.Settle();

        NewLine(window, "int total = 0;");
        window.KeyTextInput("c");
        Ui.Settle();
        window.KeyTextInput("w");
        Ui.Settle();
        Assert.DoesNotContain("cw", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.False(editor.IsInSnippet, "cw, turned off, still expanded");
        Assert.DoesNotContain("Console.WriteLine", Text(editor), StringComparison.Ordinal);

        NewLine(window, "int total = 0;");
        window.KeyTextInput("thr");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.Contains("        throw new InvalidOperationException(\"\");\n", Text(editor), StringComparison.Ordinal);
        Assert.Equal("InvalidOperationException", Selected(editor));
        Ui.Press(window, "TAB");
        window.KeyTextInput("Not yet");
        Ui.Press(window, "TAB");
        Assert.False(editor.IsInSnippet);
        Assert.Contains("        throw new InvalidOperationException(\"Not yet\");\n", Text(editor), StringComparison.Ordinal);

        dialog = LiveTemplates(window);
        page = dialog.Templates;
        Ui.Click(page.NewButton);
        Assert.True(page.ShortcutBox.IsFocused, "A template just added does not have its shortcut ready to type");
        Type(page.ShortcutBox, "hello");
        Type(page.DescriptionBox, "Says hello");
        Type(page.TextBox, "Console.WriteLine(\"Hello from $TYPE$, $WHO$!\");$END$");
        Assert.Equal("C# statements", page.AvailabilityText.Text);
        Assert.True(page.GenerationButton.IsChecked == true, "A template added is not typed by its shortcut");

        // Edit variables: TYPE is the containing type's name, not to be typed over; WHO is the constant "world".
        Ui.Click(page.EditVariablesButton);
        TemplateVariablesDialog variables = DialogButtonsTests.Check(Assert.Single(dialog.OwnedWindows.OfType<TemplateVariablesDialog>()));
        Assert.Equal(["TYPE", "WHO"], variables.Variables.Select(v => v.Name));
        Assert.Equal("No macro selected", variables.MacroText);
        Choose(variables, "Containing type name");
        Ui.Click(variables.EditableBox);
        Assert.True(variables.Choose("WHO"));
        Choose(variables, "Constant value");
        Type(variables.ParameterBox, "world");
        Ui.Click(variables.DoneButton);
        Assert.Equal([new TemplateVariable("TYPE", "typeName", string.Empty, Editable: false), new TemplateVariable("WHO", "constant", "world")],
            page.Chosen?.Variables ?? []);

        Assert.True(page.Choose("while"));
        Ui.Settle();
        Ui.Click(page.EnabledBox("while")!);
        dialog.Ok();
        Ui.Settle();

        NewLine(window, "int total = 0;");
        window.KeyTextInput("h");
        Ui.Settle();
        window.KeyTextInput("ello");
        Ui.Settle();
        Assert.Contains("hello", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.Contains("        Console.WriteLine(\"Hello from Sum, world!\");\n", Text(editor), StringComparison.Ordinal);
        Assert.True(Selected(editor) == "world", $"Tab went first to '{Selected(editor)}', not to WHO with TYPE passed over");
        window.KeyTextInput("you");
        Ui.Press(window, "TAB");
        Assert.False(editor.IsInSnippet);
        Assert.Contains("        Console.WriteLine(\"Hello from Sum, you!\");\n", Text(editor), StringComparison.Ordinal);
        window.Close();

        // Kept with the settings, a built-in one as what differs from it: after a restart all of it is as it was left.
        string kept = File.ReadAllText(Path.Combine(settings, "settings.json"));
        Assert.Contains("\"enabled\": false", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("while ($CONDITION$)", kept, StringComparison.Ordinal);
        window = Open("Sum.cs");
        editor = window.EditorArea.CodeEditor;
        NewLine(window, "int total = 0;");
        window.KeyTextInput("hello");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.Contains("        Console.WriteLine(\"Hello from Sum, world!\");\n", Text(editor), StringComparison.Ordinal);
        Ui.Press(window, "TAB");
        NewLine(window, "int total = 0;");
        window.KeyTextInput("while");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.DoesNotContain("while (true)", Text(editor), StringComparison.Ordinal);
        dialog = LiveTemplates(window);
        Assert.True(dialog.Templates.Choose("hello"), "The template added is not listed after a restart");
        Assert.Equal("Says hello", dialog.Templates.DescriptionBox.Text);
        Assert.True(dialog.Templates.Choose("while"));
        Assert.False(dialog.Templates.Chosen?.Enabled, "while, turned off, is on again after a restart");
        dialog.Close();
        window.Close();
    }

    /// <summary>A postfix template that could wrap more than one expression asks which and wraps the one chosen, one that
    /// fits only one wraps it at once; one turned off in Settings is offered no more, postfix templates kept out of the
    /// list still expand on Tab, and postfix completion turned off leaves Tab a tab.</summary>
    [AvaloniaFact]
    public void A_postfix_template_asks_which_expression_to_wrap_when_more_than_one_fits_and_Settings_turn_one_or_all_off()
    {
        MainWindow window = Open("Pair.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        NewLine(window, "int count = 2;");
        window.KeyTextInput("int both = other.First + count.par");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");

        // The innermost first, the one chosen highlighted in the editor.
        Assert.Equal("Select expression to parenthesize", window.Assist.OfferedTitle);
        Assert.Equal(["count", "other.First + count"], window.Assist.Offered);
        string text = Text(editor);
        int start = text.IndexOf("other.First + count.par", StringComparison.Ordinal);
        Assert.Equal([new TextRange(start + "other.First + ".Length, "count".Length)], editor.Highlights);
        Ui.Press(window, "DOWN");
        Assert.Equal([new TextRange(start, "other.First + count".Length)], editor.Highlights);
        Ui.Press(window, "ENTER");
        Assert.Contains("        int both = (other.First + count)\n", Text(editor), StringComparison.Ordinal);
        Assert.Empty(editor.Highlights);

        NewLine(window, "int count = 2;");
        window.KeyTextInput("count.not");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.Empty(window.Assist.Offered);
        Assert.Contains("        !count\n", Text(editor), StringComparison.Ordinal);

        NewLine(window, "int count = 2;");
        window.KeyTextInput("count.");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("par"), "the postfix templates after the dot");
        Ui.Press(window, "ESCAPE");

        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "postfix";
        Ui.Settle();
        Assert.Equal([SettingsDialog.PostfixPage], dialog.Pages);
        Assert.True(dialog.Postfix.Choose("par"), "The Postfix Completion page does not list par");
        Ui.Settle();
        Ui.Click(dialog.Postfix.EnabledBox("par")!);
        dialog.Ok();
        Ui.Settle();

        NewLine(window, "int count = 2;");
        window.KeyTextInput("count.");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("not"), "the postfix templates after the dot");
        Assert.DoesNotContain("par", window.Assist.CompletionLabels);
        window.KeyTextInput("par");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.DoesNotContain("(count)", Text(editor), StringComparison.Ordinal);

        dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Page = SettingsDialog.PostfixPage;
        Ui.Settle();
        Ui.Click(dialog.Postfix.AutopopupBox);
        dialog.Ok();
        Ui.Settle();
        int negated = Text(editor).Split("        !count\n").Length;
        NewLine(window, "int count = 2;");
        window.KeyTextInput("count.");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("ToString"), "count's members after the dot");
        Assert.DoesNotContain("not", window.Assist.CompletionLabels);
        window.KeyTextInput("not");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.True(Text(editor).Split("        !count\n").Length == negated + 1, $"Tab after not, with postfix templates kept out of the list, did not expand it:\n{Text(editor)}");

        dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Page = SettingsDialog.PostfixPage;
        Ui.Settle();
        Ui.Click(dialog.Postfix.EnableBox);
        dialog.Ok();
        Ui.Settle();
        NewLine(window, "int count = 2;");
        window.KeyTextInput("count.notnull");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.DoesNotContain("if (count != null)", Text(editor), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Settings opened on its Live Templates page, found by searching for it.</summary>
    private static SettingsDialog LiveTemplates(MainWindow window)
    {
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "live templates";
        Ui.Settle();
        Assert.Equal(SettingsDialog.LiveTemplatesPage, dialog.Page);
        return dialog;
    }

    /// <summary>Edit variables' Change macro, choosing a macro by what Choose macro calls it.</summary>
    private static void Choose(TemplateVariablesDialog variables, string macro)
    {
        Ui.Click(variables.ChangeMacroButton);
        ChooseMacroDialog macros = DialogButtonsTests.Check(Assert.Single(variables.OwnedWindows.OfType<ChooseMacroDialog>()));
        Assert.True(macros.Choose(macro), $"Choose macro does not offer {macro}");
        Ui.Click(macros.OkButton);
        Assert.Equal(macro, variables.MacroText);
    }

    /// <summary>A new line after the first occurrence of some code, the editor focused.</summary>
    private static void NewLine(MainWindow window, string after)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        int at = Text(editor).IndexOf(after, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{after}' is not in the file");
        editor.Selections = SelectionSet.Single(at + after.Length);
        editor.Focus();
        Ui.Press(window, "ENTER");
    }

    private static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();

    private static string Selected(CodeEditor editor) => editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length);

    /// <summary>A field's text replaced, as typing over it does, and the dialog having heard it.</summary>
    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        Ui.Settle();
    }

    /// <summary>A window that keeps its settings in the test's own folder, on the project with a file open once C# is ready,
    /// on the server the class's last window left running.</summary>
    private MainWindow Open(string file) => project.Open(Path.Combine(project.Folder, file), state: settings);

    /// <summary>A small console project on disk, restored once for every test, and the warm server they share on it; a
    /// window never saves what its test typed.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("templates-")
        {
            File.WriteAllText(PathOf("Templates.csproj"),
                TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), "class Program\n{\n    static void Main()\n    {\n        int sum = Sum.Of();\n    }\n}\n");
            File.WriteAllText(PathOf("Sum.cs"), "using System;\n\nstatic class Sum\n{\n    public static int Of()\n    {\n        int total = 0;\n        return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Pair.cs"), "class Pair\n{\n    public int First;\n\n    public int Both(Pair other)\n    {\n        int count = 2;\n        return count;\n    }\n}\n");
        }
    }
}
