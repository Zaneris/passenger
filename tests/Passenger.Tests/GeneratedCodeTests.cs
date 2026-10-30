using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A source generator's code, where Go to Declaration and Find Usages lead into it: Avalonia's InitializeComponent,
/// a System.Text.Json context's member and a GeneratedRegex method each open a read-only tab named by the generator's hint
/// name, at the member, its text the server's, and the tab takes the generator's code again as the code it is made from
/// changes.</summary>
public sealed class GeneratedCodeTests : IClassFixture<GeneratedCodeTests.GeneratorsApp>
{
    private const string Window = "<Window xmlns=\"https://github.com/avaloniaui\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
        + "        x:Class=\"Gallery.MainWindow\"\n        Title=\"Gallery\">\n    <TextBlock x:Name=\"Heading\" />\n</Window>\n";

    private const string WindowCode = "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class MainWindow : Window\n{\n    public MainWindow()\n    {\n"
        + "        InitializeComponent();\n        Heading.Text = \"Hello\";\n    }\n}\n";

    private const string Patterns = "using System.Text.Json.Serialization;\nusing System.Text.RegularExpressions;\n\nnamespace Gallery;\n\n"
        + "public static partial class Patterns\n{\n    [GeneratedRegex(\"a+b\")]\n    public static partial Regex Letters();\n\n"
        + "    public static bool Check(string text) => Letters().IsMatch(text);\n}\n\npublic sealed record Square(int Side);\n\n"
        + "[JsonSerializable(typeof(Square))]\npublic partial class ShapeContext : JsonSerializerContext\n{\n}\n\n"
        + "public static class UsesContext\n{\n    public static object Info => ShapeContext.Default.Square;\n}\n";

    private readonly GeneratorsApp app;

    public GeneratedCodeTests(GeneratorsApp app)
    {
        this.app = app;
        app.Reset();
    }

    /// <summary>Go to Declaration on InitializeComponent in an Avalonia window's code-behind opens the code Avalonia's
    /// generator made of its XAML, read-only and saying so, at the method, where it said "Could not find file
    /// '/Gallery.MainWindow.g.cs'".</summary>
    [AvaloniaFact]
    public void Go_to_declaration_on_InitializeComponent_opens_avalonias_generated_code_read_only_at_the_method()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml.cs"));
        try
        {
            Declared(window, "InitializeCompo", "Gallery.MainWindow.g.cs", "InitializeComponent(");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Go to Declaration on a System.Text.Json context's Default opens the code the JSON generator made of the
    /// context, read-only, at the property, where it said "Could not find file '/ShapeContext.g.cs'".</summary>
    [AvaloniaFact]
    public void Go_to_declaration_on_a_json_contexts_member_opens_the_json_generators_code_read_only_at_the_member()
    {
        MainWindow window = app.Open(app.PathOf("Patterns.cs"));
        try
        {
            Declared(window, "ShapeContext.Defa", "ShapeContext.g.cs", " Default ");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Find Usages of a GeneratedRegex method lists the regex generator's declaration under the project with its
    /// line of code, where it listed it under Miscellaneous Files with none; Enter on it opens the generator's code
    /// read-only at that line; and the open tab takes the generator's code again once the pattern is typed anew.</summary>
    [AvaloniaFact]
    public void Find_usages_of_a_generated_regex_lists_the_generators_line_opens_it_read_only_and_follows_the_pattern_typed()
    {
        MainWindow window = app.Open(app.PathOf("Patterns.cs"));
        try
        {
            FindView find = window.Find;
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "=> Lette");
            Ui.Run(window, AppActions.FindUsages);
            Ui.WaitFor(() => find.Rows.Any(r => r.Usage is not null), () => $"the usages of Letters in the Find window ({window.Status})");
            FindRow? generated = find.Rows.FirstOrDefault(r => r.Usage?.Path.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs", StringComparison.Ordinal) == true);
            Assert.True(generated?.Usage is { Project: "Gallery" } usage && usage.LineText.Contains("Regex Letters()", StringComparison.Ordinal),
                $"Find Usages of a GeneratedRegex method did not list the generator's declaration under the project with its code: "
                + string.Join("; ", find.Rows.Select(r => $"{new string(' ', r.Depth * 2)}{r.Label} [{r.Usage?.Project}]")));

            find.Selected = generated;
            find.FocusRows();
            Ui.Press(window, "ENTER");
            OpenDocument shown = Shown(window, "RegexGenerator.g.cs", "Enter on the generator's row in the Find window");
            Assert.Equal(new TextPosition(generated.Usage!.Line, generated.Usage.Column), window.EditorArea.CodeEditor.CaretPosition);
            Assert.Contains("<code>a+b</code>", shown.Text.Snapshot.ToString(), StringComparison.Ordinal);

            window.OpenFile(app.PathOf("Patterns.cs"));
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "GeneratedRegex(\"a+");
            window.KeyTextInput("c+");
            Ui.WaitFor(() => shown.Text.Snapshot.ToString().Contains("<code>a+c+b</code>", StringComparison.Ordinal),
                () => $"the open generator's code to follow the pattern typed: its remarks say {string.Join(" ", shown.Text.Snapshot.ToString().Split('\n').Where(l => l.Contains("<code>", StringComparison.Ordinal)))}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Go to Declaration from after a piece of the file shown, which lands in a generator's code: the tab of
    /// <paramref name="name"/>, read-only and saying it is a generator's, the caret on a line holding <paramref name="line"/>.</summary>
    private static void Declared(MainWindow window, string after, string name, string line)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.GoToDeclaration);
        OpenDocument shown = Shown(window, name, $"Go to Declaration after {after}");
        TextPosition caret = window.EditorArea.CodeEditor.CaretPosition;
        string at = shown.Text.Snapshot.GetLineText(caret.Line);
        Assert.True(at.Contains(line, StringComparison.Ordinal), $"Go to Declaration after {after} put the caret on line {caret.Line + 1} of {name}, '{at.Trim()}', not on its declaration");
    }

    /// <summary>The tab shown once it is a generator's code named <paramref name="name"/>, which nothing can type into and
    /// whose banner says what it is.</summary>
    private static OpenDocument Shown(MainWindow window, string name, string what)
    {
        Ui.WaitFor(() => window.EditorArea.Active?.Name == name, () => $"{what} to open {name} ({window.Status})");
        OpenDocument shown = window.EditorArea.Active!;
        Assert.True(GeneratedCode.IsCopy(shown.Path) && shown.IsReadOnly && shown.Text.IsReadOnly && window.EditorArea.CodeEditor.IsReadOnly,
            $"{what} opened {shown.Path}, read-only {shown.IsReadOnly}, which a person could type into");
        Assert.True(window.EditorArea.ActiveGroup.BannerText?.Contains("Code a source generator made", StringComparison.Ordinal) == true,
            $"The banner over {name} says '{window.EditorArea.ActiveGroup.BannerText}', not that a generator made it");
        string text = shown.Text.Snapshot.ToString();
        window.KeyTextInput("x");
        Ui.Settle();
        Assert.Equal(text, shown.Text.Snapshot.ToString());
        return shown;
    }

    /// <summary>An Avalonia app whose window's code-behind calls InitializeComponent, with a GeneratedRegex method and a
    /// System.Text.Json context beside it, restored once for the class's tests.</summary>
    public sealed class GeneratorsApp : SharedProject
    {
        public GeneratorsApp()
            : base("generated-code-")
        {
            TestFolders.Write(Folder, "Gallery.csproj", AvaloniaApp.HeadlessProject());
            TestFolders.Write(Folder, "Program.cs", "namespace Gallery;\n\npublic static class Program\n{\n    public static void Main()\n    {\n    }\n}\n");
            TestFolders.Write(Folder, "MainWindow.axaml", Window);
            TestFolders.Write(Folder, "MainWindow.axaml.cs", WindowCode);
            TestFolders.Write(Folder, "Patterns.cs", Patterns);
        }
    }
}
