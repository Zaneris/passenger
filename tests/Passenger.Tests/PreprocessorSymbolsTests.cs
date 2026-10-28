using Avalonia.Headless.XUnit;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A C# file's own parse (its colours before any server's, its brackets' pairs, its folds, what Safe Delete reads)
/// takes the preprocessor symbols and C# version its project compiles it with: the SDK's for its framework
/// (NET8_0_OR_GREATER and C# 14 in a net10.0 project), the project's own DefineConstants and LangVersion, a change of them
/// taken up in one parse.</summary>
public sealed class PreprocessorSymbolsTests : IClassFixture<PreprocessorSymbolsTests.Project>
{
    private const string Shapes =
        "static class Shapes\n{\n#if NET8_0_OR_GREATER\n    public static double Area(double r)\n    {\n        return r * r;\n    }\n#endif\n\n"
        + "#if !NET8_0_OR_GREATER\n    public static double Square(double r)\n    {\n        return r * 4;\n    }\n#endif\n\n"
        + "#if CIRCLES\n    public static double Round(double r)\n    {\n        return r * 3;\n    }\n#endif\n}\n";

    private readonly Project project;

    public PreprocessorSymbolsTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>With no language server to colour it, a method under #if NET8_0_OR_GREATER is code: drawn so, its braces
    /// paired, its body folding; one under #if !NET8_0_OR_GREATER is drawn disabled, as is one under a symbol the project
    /// does not define until its project file defines it, which parses the file again once.</summary>
    [AvaloniaFact]
    public void Code_under_its_frameworks_symbols_is_code_before_any_server_answers_and_a_symbol_the_project_comes_to_define_is_taken_up_in_one_parse()
    {
        MainWindow window = project.Open("Shapes.cs", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => !Disabled(document, "return r * r;"),
            $"the method under #if NET8_0_OR_GREATER drawn as code, as its {TestProjects.Framework} project compiles it, not disabled");

        int open = Shapes.IndexOf("    {\n        return r * r;", StringComparison.Ordinal) + 4;
        int close = Shapes.IndexOf("    }\n#endif", StringComparison.Ordinal) + 4;
        editor.Selections = SelectionSet.Single(open + 1);
        Ui.Settle();
        Assert.True(editor.MatchedBraces is { } pair && pair.Touched.Start == open && pair.Matching.Start == close,
            $"The caret after Area's opening brace shows {editor.MatchedBraces?.ToString() ?? "no pair"}, where its closing brace at {close} is its pair");
        int signature = Shapes.IndexOf("Area(double r)", StringComparison.Ordinal) + "Area(double r)".Length;
        Ui.WaitFor(() => document.FoldsFound && document.FoldRegions.Any(r => r.Start == signature),
            () => $"Area's body to fold, among the folds [{string.Join(", ", document.FoldRegions)}]");
        Assert.True(Disabled(document, "return r * 4;"), "The method under #if !NET8_0_OR_GREATER is drawn as code, which its project never compiles");
        Assert.True(Disabled(document, "return r * 3;"), "The method under #if CIRCLES is drawn as code before its project defines CIRCLES");

        int parses = document.Syntax!.Worked.Parses;
        File.WriteAllText(project.PathOf("Symbols.csproj"), TestProjects.Console("<DefineConstants>$(DefineConstants);CIRCLES</DefineConstants>"));
        Ui.WaitFor(() => !Disabled(document, "return r * 3;"), "the method under #if CIRCLES drawn as code once the project file defines CIRCLES");
        Assert.True(document.Syntax.Worked.Parses == parses + 1,
            $"The project's new symbol parsed Shapes.cs {document.Syntax.Worked.Parses - parses} times, where one parse takes it up");
        Assert.False(Disabled(document, "return r * r;"), "The method under #if NET8_0_OR_GREATER is drawn disabled once the project defines CIRCLES too");
        Assert.True(Disabled(document, "return r * 4;"), "The method under #if !NET8_0_OR_GREATER is drawn as code once the project defines CIRCLES");
        window.Close();
    }

    /// <summary>Safe Delete reads a declaration under #if NET8_0_OR_GREATER as the project compiles it: from a usage, in a
    /// file not open, a used method goes once told to; in the file open, an unused one goes at once.</summary>
    [AvaloniaFact]
    public void Safe_delete_takes_a_method_under_its_frameworks_symbols_from_a_file_not_open_and_from_the_file_open()
    {
        string calc = project.PathOf("Calc.cs");
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Calc.Twi");
        editor.Focus();
        Ui.Run(window, AppActions.SafeDelete);
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), () => $"Safe Delete to list Twice's usages: {window.Status}");
        Ui.ClickButton(DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single()), "Delete Anyway");
        Ui.WaitFor(() => !File.ReadAllText(calc).Contains("Twice(int", StringComparison.Ordinal), () => $"Twice deleted from Calc.cs, which is not open: {window.Status}");
        Assert.Contains("Unused(int value)", File.ReadAllText(calc), StringComparison.Ordinal);

        window.OpenFile(calc);
        Ui.Settle();
        OpenDocument opened = window.EditorArea.Active!;
        editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => !Disabled(opened, "value + 1"), "Unused, under #if NET8_0_OR_GREATER, drawn as code");
        Ui.PlaceAfter(editor, "int Unu");
        Ui.Run(window, AppActions.SafeDelete);
        Ui.WaitFor(() => !opened.Text.Snapshot.ToString().Contains("Unused", StringComparison.Ordinal), () => $"the unused method to be deleted: {window.Status}");
        Assert.Equal("Deleted the method 'Unused'", window.Status);
        Assert.Contains("#if NET8_0_OR_GREATER\n", opened.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>With no language server to colour it, <c>field</c> in a property's accessor is C# 14's keyword in a net10.0
    /// project, and once its project file sets LangVersion 12 the name of the class's own field, as its compiler reads it
    /// there, taken up in one parse.</summary>
    [AvaloniaFact]
    public void Field_in_an_accessor_is_a_keyword_at_its_frameworks_csharp_and_a_name_once_the_project_sets_langversion_12()
    {
        MainWindow window = project.Open("Counter.cs", languageServer: false);
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Syntax?.Current?.Tree.Options is CSharpParseOptions { LanguageVersion: LanguageVersion.CSharp14 },
            $"Counter.cs parsed as C# 14, the SDK's for its {TestProjects.Framework} project");
        Assert.True(Keyword(document, "field; }"), $"field in Value's getter is not drawn as C# 14's keyword in a {TestProjects.Framework} project");
        Assert.False(Keyword(document, "field;\n"), "The field's own name, declared as one, is drawn as a keyword");

        int parses = document.Syntax!.Worked.Parses;
        File.WriteAllText(project.PathOf("Symbols.csproj"), TestProjects.Console("<LangVersion>12</LangVersion>"));
        Ui.WaitFor(() => !Keyword(document, "field; }"), "field in Value's getter drawn as the name it is in C# 12 once the project file sets LangVersion 12");
        Assert.True(document.Syntax.Worked.Parses == parses + 1,
            $"The project's LangVersion parsed Counter.cs {document.Syntax.Worked.Parses - parses} times, where one parse takes it up");
        Assert.False(Keyword(document, "field = value;"), "field in Value's setter is drawn as a keyword in a C# 12 project, where it names the class's field");
        window.Close();
    }

    /// <summary>Whether the editor's parse colours the word that starts <paramref name="code"/> as a keyword.</summary>
    private static bool Keyword(OpenDocument document, string code)
    {
        TextSnapshot text = document.Text.Snapshot;
        int at = text.ToString().IndexOf(code, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{code}' is not in {document.Name}");
        LineSpan line = text.GetLine(text.GetLineFromOffset(at));
        return document.Highlighter!.GetSpans(text, line.Start, line.EndIncludingBreak - line.Start).Any(s => s.Kind == TokenKind.Keyword && s.Start <= at && s.End > at);
    }

    /// <summary>Whether the editor draws the code that starts with <paramref name="code"/> as disabled, as its parse colours
    /// the line.</summary>
    private static bool Disabled(OpenDocument document, string code)
    {
        TextSnapshot text = document.Text.Snapshot;
        int at = text.ToString().IndexOf(code, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{code}' is not in {document.Name}");
        LineSpan line = text.GetLine(text.GetLineFromOffset(at));
        return document.Highlighter!.GetSpans(text, line.Start, line.EndIncludingBreak - line.Start).Any(s => s.Kind == TokenKind.Excluded && s.Start <= at && s.End > at);
    }

    /// <summary>A console project of the tests' framework whose code declares methods under preprocessor symbols, restored
    /// once, and the warm server its windows share.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("symbols-")
        {
            File.WriteAllText(PathOf("Symbols.csproj"), TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), "class Program\n{\n    static void Main()\n    {\n        int two = Calc.Twice(1);\n    }\n}\n");
            File.WriteAllText(PathOf("Calc.cs"), "static class Calc\n{\n#if NET8_0_OR_GREATER\n    public static int Twice(int value) => value * 2;\n\n"
                + "    public static int Unused(int value) => value + 1;\n#endif\n}\n");
            File.WriteAllText(PathOf("Shapes.cs"), Shapes);
            File.WriteAllText(PathOf("Counter.cs"), "class Counter\n{\n    private int field;\n\n    public int Value\n    {\n        get { return field; }\n"
                + "        set { field = value; }\n    }\n}\n");
        }
    }
}
