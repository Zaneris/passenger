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
/// one's colours, uses and problems. A name no using brings in any more keeps the colour its syntax gives it, never a
/// local's, until the server binds it again.</summary>
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

    /// <summary>A file bringing in a struct's namespace with a using of its own.</summary>
    private const string Words = """
        using System.Text;

        namespace Probe;

        public static class Words
        {
            public static int Letters(string said)
            {
                int count = 0;
                foreach (Rune rune in said.EnumerateRunes())
                {
                    if (Rune.IsLetter(rune))
                    {
                        count++;
                    }
                }
                return count;
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
        Ui.WaitFor(() => Kinds(editor, ToolsNames) == Resolved, () => $"the server's colours for the names the other file, the item and the SDK bring in: {Kinds(editor, ToolsNames)}");

        int type = editor.Document.Snapshot.ToString().IndexOf("Process[]", StringComparison.Ordinal);
        Ui.DoubleClick(editor, editor.PointOf(type + 3) + new Point(1, editor.LineHeight / 2));
        Ui.WaitFor(() => editor.UsageHighlights.Count == 5, () => $"Process's five uses lit up, not {editor.UsageHighlights.Count}");
        Assert.All(editor.UsageHighlights, u => Assert.Equal("Process", editor.Document.Snapshot.GetText(u.Range)));
        Assert.Equal(editor.UsageHighlights.Select(u => RowOf(editor, u.Range.Start)),
            editor.StripeMarks.Where(m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage).Select(m => m.Row));
        window.Close();
    }

    /// <summary>The global using taken out of the other file, open beside, and put back: the file relying on it follows each
    /// change, its problems too, of which the server says nothing, the names it brought keeping their syntax's colours.</summary>
    [AvaloniaFact]
    public void Taking_a_global_using_out_of_another_open_file_marks_the_names_it_brought_and_putting_it_back_clears_them()
    {
        MainWindow window = project.Open("Tools.cs");
        OpenDocument tools = window.EditorArea.Active!;
        Ui.WaitFor(() => Kinds(window.EditorArea.CodeEditor, ToolsNames) == Resolved, () => $"the server's colours: {Kinds(window.EditorArea.CodeEditor, ToolsNames)}");
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
        Ui.WaitFor(() => Kinds(editor, ToolsNames) == Unbound, () => $"Process keeping the colours its syntax gives it, the locals theirs: {Kinds(editor, ToolsNames)}");

        window.OpenFile(project.PathOf("GlobalUsings.cs"));
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Usings, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        window.OpenFile(project.PathOf("Tools.cs"));
        Ui.WaitFor(() => !tools.Markers.Any(m => m.Kind == MarkerKind.Error), () => $"the marks gone once the global using is back: [{string.Join("; ", tools.Markers.Select(m => m.Message))}]");
        Ui.WaitFor(() => Kinds(editor, ToolsNames) == Resolved, () => $"the type's colours back with the global using: {Kinds(editor, ToolsNames)}");
        window.Close();
    }

    /// <summary>A file's own using of a struct's namespace taken out and put back: the struct's name keeps its syntax's
    /// colour, a type's where it declares a local and none where it stands before a dot, while a local's use keeps the
    /// server's local colour; once the using is back the server's struct colour is laid over the syntax's guess.</summary>
    [AvaloniaFact]
    public void A_type_whose_using_is_taken_out_keeps_its_syntaxs_colour_a_local_its_own_and_takes_the_servers_once_bound_again()
    {
        MainWindow window = project.Open("Words.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        const string Bound = "Rune rune=Struct Rune.IsLetter=Struct IsLetter(=Method IsLetter(rune)=Local count++=Local";
        Ui.WaitFor(() => Kinds(editor, WordsNames) == Bound, () => $"the server's colours with the using: {Kinds(editor, WordsNames)}");

        editor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("// ");
        const string Unbound = "Rune rune=Class Rune.IsLetter=Text IsLetter(=Method IsLetter(rune)=Local count++=Local";
        Ui.WaitFor(() => Kinds(editor, WordsNames) == Unbound,
            () => $"Rune keeping its syntax's colours with its using taken out, not a local's, and the local its own: {Kinds(editor, WordsNames)}");

        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Words, editor.Document.Snapshot.ToString());
        Ui.WaitFor(() => Kinds(editor, WordsNames) == Bound, () => $"the server's struct colour over the syntax's once the using is back: {Kinds(editor, WordsNames)}");
        window.Close();
    }

    /// <summary>The names of Tools.cs whose colours are read: each in the first stretch of text holding it.</summary>
    private static readonly (string Stretch, string Name)[] ToolsNames =
    [
        ("Process[]", "Process"), ("Process.GetProcesses", "Process"), ("GetProcesses", "GetProcesses"), ("List<Process>", "List"),
        ("<Process>", "Process"), ("Regex", "Regex"), ("IsMatch", "IsMatch"), ("Kill", "Kill"), ("Exception", "Exception"),
        ("in myProcesses", "myProcesses"), ("processes.Add", "processes"),
    ];

    /// <summary>What each name the file takes from elsewhere is coloured as, in order, and its locals.</summary>
    private const string Resolved = "Process[]=Class Process.GetProcesses=Class GetProcesses=Method List<Process>=Class <Process>=Class Regex=Class IsMatch=Method "
        + "Kill=Method Exception=Class in myProcesses=Local processes.Add=Local";

    /// <summary>The same with System.Diagnostics no longer brought in: Process as its syntax colours it, a type's where the
    /// syntax says a type and none before a dot, and a method called on it a method's.</summary>
    private const string Unbound = "Process[]=Class Process.GetProcesses=Text GetProcesses=Method List<Process>=Class <Process>=Class Regex=Class IsMatch=Method "
        + "Kill=Method Exception=Class in myProcesses=Local processes.Add=Local";

    /// <summary>The names of Words.cs whose colours are read.</summary>
    private static readonly (string Stretch, string Name)[] WordsNames =
        [("Rune rune", "Rune"), ("Rune.IsLetter", "Rune"), ("IsLetter(", "IsLetter"), ("IsLetter(rune)", "rune"), ("count++", "count")];

    /// <summary>What each name is coloured as, by the stretch holding it, in order.</summary>
    private static string Kinds(CodeEditor editor, (string Stretch, string Name)[] names)
    {
        TextSnapshot text = editor.Document.Snapshot;
        string all = text.ToString();
        List<string> said = [];
        foreach ((string stretch, string name) in names)
        {
            int at = all.IndexOf(stretch, StringComparison.Ordinal) + stretch.IndexOf(name, StringComparison.Ordinal);
            List<TokenKind> kinds = [.. editor.Highlighter!.GetSpans(text, at, name.Length).Where(s => s.Start <= at && s.End >= at + name.Length).Select(s => s.Kind)];
            said.Add($"{stretch}={(kinds.Count == 0 ? "Text" : string.Join("/", kinds))}");
        }
        return string.Join(" ", said);
    }

    /// <summary>The row an offset is drawn on, counted from the file's first.</summary>
    private static int RowOf(CodeEditor editor, int offset) =>
        (int)Math.Round((editor.PointOf(offset).Y + editor.ScrollOffset.Y) / editor.LineHeight);

    /// <summary>A library taking System.Diagnostics from another file's global using, Regex's namespace from a Using item
    /// and List's and Exception's from the SDK's implicit usings, and a file taking System.Text with its own using.</summary>
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
            File.WriteAllText(PathOf("Words.cs"), Words);
        }
    }
}
