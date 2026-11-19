using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A C# file whose names come from namespaces the project's other files and its build bring in: a <c>global
/// using</c> another file declares, a <c>&lt;Using&gt;</c> item and the SDK's implicit usings. Its types and methods
/// are coloured as such, a type's uses lit up together, and a change to the other file's using is followed in this
/// one's colours, uses and problems.</summary>
public sealed class GlobalUsingsTests : IClassFixture<GlobalUsingsTests.Project>
{
    private const string Usings = "global using System.Diagnostics;\n";

    private const string Tools = """
        namespace Probe;

        public static class Tools
        {
            public static void CloseRegisterProcess()
            {
                try
                {
                    Process[] myProcesses = Process.GetProcesses();
                    List<Process> processes = [];
                    foreach (Process myProcess in myProcesses)
                    {
                        if (Regex.IsMatch(myProcess.ProcessName, "^device_register$"))
                            processes.Add(myProcess);
                    }
                    foreach (Process proce in processes)
                    {
                        proce.Kill();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        """;

    private readonly Project project;

    public GlobalUsingsTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void Names_from_a_global_using_in_another_file_a_using_item_and_implicit_usings_are_coloured_and_a_types_uses_lit_together()
    {
        MainWindow window = project.Open("Tools.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => Kinds(editor) == Resolved, () => $"the server's colours for the names the other file, the item and the SDK bring in: {Kinds(editor)}");

        int type = editor.Document.Snapshot.ToString().IndexOf("Process[]", StringComparison.Ordinal);
        Ui.DoubleClick(editor, editor.PointOf(type + 3) + new Point(1, editor.LineHeight / 2));
        Ui.WaitFor(() => editor.UsageHighlights.Count == 5, () => $"Process's five uses lit up, not {editor.UsageHighlights.Count}");
        Assert.All(editor.UsageHighlights, u => Assert.Equal("Process", editor.Document.Snapshot.GetText(u.Range)));
        Assert.Equal(editor.UsageHighlights.Select(u => RowOf(editor, u.Range.Start)),
            editor.StripeMarks.Where(m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage).Select(m => m.Row));
        window.Close();
    }

    /// <summary>The global using taken out of the other file, open beside, and put back: the file relying on it follows each
    /// change, its problems too, of which the server says nothing.</summary>
    [AvaloniaFact]
    public void Taking_a_global_using_out_of_another_open_file_marks_the_names_it_brought_and_putting_it_back_clears_them()
    {
        MainWindow window = project.Open("Tools.cs");
        OpenDocument tools = window.EditorArea.Active!;
        Ui.WaitFor(() => Kinds(window.EditorArea.CodeEditor) == Resolved, () => $"the server's colours: {Kinds(window.EditorArea.CodeEditor)}");
        Assert.DoesNotContain(tools.Markers, m => m.Kind == MarkerKind.Error);

        window.OpenFile(project.PathOf("GlobalUsings.cs"));
        CodeEditor other = window.EditorArea.CodeEditor;
        other.Selections = SelectionSet.Single(0);
        window.KeyTextInput("// ");
        Ui.Settle();
        window.OpenFile(project.PathOf("Tools.cs"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => tools.Markers.Count(m => m.Kind == MarkerKind.Error && m.Message.Contains("'Process'", StringComparison.Ordinal)) >= 5,
            () => $"Process marked where the global using was taken out of the other file, not [{string.Join("; ", tools.Markers.Select(m => m.Message))}]");
        Ui.WaitFor(() => Kinds(editor).StartsWith("Process[]=Local", StringComparison.Ordinal), () => $"Process no longer a type's colour: {Kinds(editor)}");

        window.OpenFile(project.PathOf("GlobalUsings.cs"));
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Usings, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        window.OpenFile(project.PathOf("Tools.cs"));
        Ui.WaitFor(() => !tools.Markers.Any(m => m.Kind == MarkerKind.Error), () => $"the marks gone once the global using is back: [{string.Join("; ", tools.Markers.Select(m => m.Message))}]");
        Ui.WaitFor(() => Kinds(editor) == Resolved, () => $"the type's colours back with the global using: {Kinds(editor)}");
        window.Close();
    }

    /// <summary>What each name the file takes from elsewhere is coloured as, in order.</summary>
    private const string Resolved = "Process[]=Class Process.GetProcesses=Class GetProcesses=Method List<Process>=Class Regex=Class IsMatch=Method Kill=Method Exception=Class";

    private static string Kinds(CodeEditor editor)
    {
        TextSnapshot text = editor.Document.Snapshot;
        string all = text.ToString();
        List<string> said = [];
        foreach (string stretch in (string[])["Process[]", "Process.GetProcesses", "GetProcesses", "List<Process>", "Regex", "IsMatch", "Kill", "Exception"])
        {
            int at = all.IndexOf(stretch, StringComparison.Ordinal);
            int length = stretch.IndexOfAny(['[', '.', '<']) is int cut and > 0 ? cut : stretch.Length;
            List<TokenKind> kinds = [.. editor.Highlighter!.GetSpans(text, at, length).Where(s => s.Start <= at && s.End >= at + length).Select(s => s.Kind)];
            said.Add($"{stretch}={(kinds.Count == 0 ? "Text" : string.Join("/", kinds))}");
        }
        return string.Join(" ", said);
    }

    /// <summary>The row an offset is drawn on, counted from the file's first.</summary>
    private static int RowOf(CodeEditor editor, int offset) =>
        (int)Math.Round((editor.PointOf(offset).Y + editor.ScrollOffset.Y) / editor.LineHeight);

    /// <summary>A library taking System.Diagnostics from another file's global using, Regex's namespace from a Using item
    /// and List's and Exception's from the SDK's implicit usings.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("global-usings-")
        {
            File.WriteAllText(PathOf("Probe.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Library</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                + "    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  <ItemGroup>\n    <Using Include=\"System.Text.RegularExpressions\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(PathOf("GlobalUsings.cs"), Usings);
            File.WriteAllText(PathOf("Tools.cs"), Tools);
        }
    }
}
