using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The language server's help through the whole window, on a real project: completion as you type, go to
/// definition, rename. Each test opens its own window, on the server the last one left running, warm, and on the
/// project as written (<see cref="SharedProject"/>); what it counts (the UI thread's allocations, the server's
/// requests) a busy machine leaves as it is.</summary>
public sealed class CodeAssistTests : IClassFixture<CodeAssistTests.Project>
{
    private readonly Project project;

    public CodeAssistTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>What the window says of the code around the caret, in one window on Program.cs: the status bar's path names
    /// the file and the member the caret is in; resting the pointer on a method shows a card with its signature; File
    /// Structure lists the file's members, opens on the caret's and filters keeping their type; and Go to Symbol finds a
    /// member in another file by name and goes to it.</summary>
    [AvaloniaFact]
    public void The_path_a_hover_card_file_structure_and_go_to_symbol_name_the_code_and_go_to_it()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.WaitFor(() => window.Breadcrumbs.Count == 3, "the member the caret is in");
        Assert.Equal(["Program.cs", "Program", "Main()"], window.Breadcrumbs);
        editor.GoTo(new TextPosition(1, 0));
        Ui.Settle();
        Assert.Equal(["Program.cs", "Program"], window.Breadcrumbs);

        int at = editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + 2;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the hover card");
        Assert.Contains("int Helper.Twice(int value)", window.Assist.HoverText, StringComparison.Ordinal);
        window.MouseMove(new Point(1, 1), Avalonia.Input.RawInputModifiers.None);
        Ui.Settle();

        editor.GoTo(new TextPosition(4, 8));
        Ui.Run(window, AppActions.FileStructure);
        SymbolPopup structure = Assert.IsType<SymbolPopup>(window.Popup);
        Ui.WaitFor(() => structure.IsSettled && structure.Rows.Count > 0, "the file's outline");
        Assert.Equal([("Program", 0), ("Main()", 1)], structure.Rows.Select(r => (r.Name, r.Depth)));
        Assert.Equal(": void", structure.Rows[1].Detail);
        Assert.Equal("Main()", structure.Selected?.Name);
        window.KeyTextInput("Ma");
        Ui.WaitFor(() => structure.IsSettled && structure.Rows.Count == 2, "the filtered outline");
        Ui.Press(window, "Enter");
        Assert.Equal(2, editor.CaretPosition.Line);

        Ui.Run(window, AppActions.GoToSymbol);
        SymbolPopup popup = Assert.IsType<SymbolPopup>(window.Popup);
        window.KeyTextInput("Twic");
        Ui.WaitFor(() => popup.Rows.Any(r => r.Name.StartsWith("Twice", StringComparison.Ordinal)), "the server's symbols for what was typed");
        SymbolRow twice = popup.Rows.First(r => r.Name.StartsWith("Twice", StringComparison.Ordinal));
        Assert.Equal("Helper", twice.Detail);
        Assert.Equal("Helper.cs:3", twice.Where);

        Ui.Press(window, "Enter");
        Assert.Null(window.Popup);
        Assert.Equal(PathNames.Canonical(project.PathOf("Helper.cs")), window.EditorArea.Active?.Path);
        Assert.Equal(2, window.EditorArea.CodeEditor.CaretPosition.Line);
        window.Close();
    }

    /// <summary>Go to Symbol lists the solution's own declarations, never what a source generator wrote for them: a
    /// [GeneratedRegex] method is found in its own file, and nothing at a generated file's name.</summary>
    [AvaloniaFact]
    public void Go_to_Symbol_lists_a_generated_methods_own_declaration_and_nothing_a_generator_wrote()
    {
        project.Write("Words.cs", "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
            + "    [GeneratedRegex(@\"\\w+\")]\n    public static partial Regex Word();\n}\n");
        MainWindow window = project.Open("Words.cs");
        try
        {
            Ui.Run(window, AppActions.GoToSymbol);
            SymbolPopup popup = Assert.IsType<SymbolPopup>(window.Popup);
            window.KeyTextInput("Word");
            Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Where?.StartsWith("Words.cs:", StringComparison.Ordinal) == true),
                () => $"Word's own declaration among the server's symbols: [{string.Join(", ", popup.Rows.Select(r => $"{r.Name} {r.Where}"))}]");
            Assert.DoesNotContain(popup.Rows, r => r.Where is { } where && (where.Contains(".g.cs", StringComparison.Ordinal) || where.StartsWith('/')));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_structure_window_shows_the_shown_files_outline_whenever_it_is_shown_and_once_the_server_has_the_file_and_follows_the_editor()
    {
        // A server started after the file opened, as a folder reopened with its files starts one.
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        ClickStripe(window, ToolWindowIds.Structure);
        window.OpenFolder(project.Folder);
        window.OpenFile("Program.cs");
        Ui.WaitFor(() => window.CSharpService?.IsReady == true, "the language server to load the project", TimeSpan.FromMinutes(2));
        Ui.Settle();
        Assert.True(window.StructureView.Rows.Select(r => r.Name).SequenceEqual(["Program", "Main()"]),
            $"Structure, open as the folder opened, still showed [{string.Join(", ", window.StructureView.Rows.Select(r => r.Name))}] once the server had the file, not its members");
        window.Close();

        // Opened from its stripe once the server has the file: the class's warm server is as good as any for that.
        window = project.Open("Program.cs");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(4, 8));
        ClickStripe(window, ToolWindowIds.Structure);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Structure));
        Passenger.App.ToolWindows.StructureView structure = window.StructureView;
        Assert.True(structure.Rows.Select(r => r.Name).SequenceEqual(["Program", "Main()"]),
            $"Structure opened from its stripe showed [{string.Join(", ", structure.Rows.Select(r => r.Name))}], not the file's members");
        Assert.Equal("Main()", structure.Selected?.Name);

        window.OpenFile("Helper.cs");
        Ui.WaitFor(() => structure.Rows.FirstOrDefault()?.Name == "Helper", "the other file's outline");
        Assert.Contains(structure.Rows, r => r.Name == "Twice(int)");

        ClickStripe(window, ToolWindowIds.Structure);
        window.OpenFile("Program.cs");
        ClickStripe(window, ToolWindowIds.Structure);
        Assert.True(structure.Rows.FirstOrDefault()?.Name == "Program",
            $"Structure opened again showed [{string.Join(", ", structure.Rows.Select(r => r.Name))}], not Program.cs's members");
        window.Close();
    }

    /// <summary>Clicks a tool window's stripe button, as a person opens or closes it.</summary>
    private static void ClickStripe(MainWindow window, string id)
    {
        Avalonia.Controls.Primitives.ToggleButton button = window.DockHost.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Tag, id));
        Ui.Click(button);
    }

    [AvaloniaFact]
    public void CamelHumps_typed_offer_the_names_they_start_after_the_prefix_matches_and_enter_writes_the_one_chosen()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static void Main()\n    {");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("var built = new System.Text.");
        Ui.Settle();
        Type("SB");
        Assert.True(window.Assist.CompletionLabels.FirstOrDefault() == "StringBuilder",
            $"Typing SB offered [{string.Join(", ", window.Assist.CompletionLabels.Take(5))}], not StringBuilder first");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("new System.Text.StringBuilder", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        // A lower-case hump then capitals, as gTOD names GetTypeOrDefault.
        window.KeyTextInput("();");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int at = \"text\".");
        Ui.Settle();
        Type("lIO");
        Assert.True(window.Assist.CompletionLabels.Take(2).SequenceEqual(["LastIndexOf", "LastIndexOfAny"]),
            $"Typing lIO offered [{string.Join(", ", window.Assist.CompletionLabels.Take(5))}], not LastIndexOf then LastIndexOfAny");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("int at = \"text\".LastIndexOf", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        // A name the humps start comes after every name the letters begin.
        window.KeyTextInput("('x');");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("bool plain = \"text\".");
        Ui.Settle();
        Type("IN");
        IReadOnlyList<string> labels = window.Assist.CompletionLabels;
        int humps = labels.ToList().IndexOf("IsNormalized");
        Assert.True(humps > 0 && labels.Take(humps).All(l => l.StartsWith("IN", StringComparison.OrdinalIgnoreCase))
            && !labels.Skip(humps).Any(l => l.StartsWith("IN", StringComparison.OrdinalIgnoreCase)),
            $"Typing IN offered [{string.Join(", ", labels.Take(8))}], not IsNormalized after every name beginning with In");
        Ui.Press(window, "ESCAPE");

        // An acronym's last capital before lower case starts a word, as IPAddress is IP + Address.
        window.KeyTextInput(";");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("var address = System.Net.");
        Ui.Settle();
        Type("IA");
        Assert.True(window.Assist.CompletionLabels.Contains("IPAddress"),
            $"Typing IA offered [{string.Join(", ", window.Assist.CompletionLabels.Take(5))}], not IPAddress");
        Ui.Press(window, "ESCAPE");
        window.Close();

        void Type(string letters)
        {
            foreach (char letter in letters)
            {
                window.KeyTextInput(letter.ToString());
                Ui.Settle();
            }
        }
    }

    [AvaloniaFact]
    public void A_commit_character_types_the_chosen_item_before_it_and_the_items_documentation_shows_beside_the_list()
    {
        Assert.SkipWhen(project.ReferencePackDownloaded,
            $"No installed .NET SDK has the {TestProjects.Framework} reference pack the newest SDK targets, so restore took it from NuGet, whose "
            + "package carries no documentation for completion to show (install the .NET 10 SDK of that version)");
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static void Main()\n    {\n");
        window.KeyTextInput("        System.");
        Ui.Settle();
        window.KeyTextInput("C");
        Ui.Settle();
        window.KeyTextInput("onso");
        Ui.Settle();
        Assert.Equal("Console", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.WaitFor(() => window.Assist.ItemDocumentation?.Contains("standard input", StringComparison.Ordinal) == true, "the chosen item's documentation");

        window.KeyTextInput(".");
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString().Contains("System.Console.", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
        Ui.WaitFor(() => window.Assist.IsCompletionOpen, "Console's members");
        window.KeyTextInput("WriteL");
        Ui.Settle();
        window.KeyTextInput("(");
        Ui.Settle();
        Assert.Contains("System.Console.WriteLine(", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.False(window.Assist.IsCompletionOpen);
        Assert.Null(window.Assist.ItemDocumentation);
        window.Close();
    }

    [AvaloniaFact]
    public void A_member_chosen_from_the_list_a_dot_opened_with_nothing_typed_leaves_the_caret_after_it()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("System.Console");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "BackgroundColor", "Console's members after its dot, BackgroundColor first");

        Ui.Press(window, "ENTER");
        Ui.Settle();
        window.KeyTextInput(" = System.ConsoleColor.DarkBlue;");
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Assert.True(line == "        System.Console.BackgroundColor = System.ConsoleColor.DarkBlue;",
            $"What was typed after choosing BackgroundColor with nothing typed after the dot did not follow it: '{line}'");
        Assert.Equal(line.Length, editor.CaretPosition.Column);
        window.Close();
    }

    [AvaloniaFact]
    public void An_extension_method_chosen_after_a_dot_adds_its_using_above_and_leaves_the_caret_after_the_method()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int[] numbers = [3, 1, 2];");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int largest = numbers");
        // Roslyn lists the methods of namespaces not imported once it has indexed them, so the dot is typed again until it has.
        Ui.WaitFor(() =>
        {
            if (window.Assist.CompletionLabels.Contains("Max"))
            {
                return true;
            }
            Ui.Press(window, "ESCAPE");
            if (editor.Document.Snapshot.ToString().Contains("numbers.", StringComparison.Ordinal))
            {
                Ui.Press(window, "BACK_SPACE");
            }
            Ui.Hold(TimeSpan.FromMilliseconds(250), "a pause before the dot is typed again, as a person retries");
            window.KeyTextInput(".");
            Ui.Settle();
            return false;
        }, "LINQ's Max, of a namespace Program.cs does not import, among the array's members");
        for (int down = window.Assist.CompletionLabels.ToList().IndexOf("Max"); down > 0; down--)
        {
            Ui.Press(window, "DOWN");
        }

        Ui.Press(window, "ENTER");
        Ui.Settle();
        window.KeyTextInput("();");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.StartsWith("using System.Linq;\n", StringComparison.Ordinal), $"Choosing Max did not add the using it needs above:\n{text}");
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Assert.True(line == "        int largest = numbers.Max();",
            $"What was typed after choosing Max, its using added above, did not follow it: '{line}'\n{text}");
        Assert.Equal(line.Length, editor.CaretPosition.Column);
        window.Close();
    }

    /// <summary>What Roslyn's server package carries under Visual Studio's terms rather than an open-source licence and
    /// Passenger.Lsp.csproj leaves out of roslyn/ (RoslynServerLeftOut), since nothing Passenger asks of the server loads them.</summary>
    private static readonly string[] LeftOut =
    [
        "Microsoft.ServiceHub.Client", "Microsoft.VisualStudio.Debugger.Contracts", "Microsoft.VisualStudio.RemoteControl",
        "Microsoft.VisualStudio.RpcContracts",
    ];

    /// <summary>Typing after a dot opens completion, filtered as one types, and Enter commits the item chosen, from a
    /// language server that has none of the Visual Studio assemblies Passenger leaves out.</summary>
    [AvaloniaFact]
    public void Typing_after_a_dot_completes_filtered_as_you_type_and_enter_commits_it_without_the_visual_studio_assemblies_passenger_leaves_out()
    {
        string roslyn = Path.Combine(AppContext.BaseDirectory, "roslyn");
        string[] carried = [.. Directory.EnumerateFiles(roslyn, "*.dll", SearchOption.AllDirectories)
            .Where(file => LeftOut.Any(name => Path.GetFileName(file).StartsWith(name + ".", StringComparison.Ordinal)))];
        Assert.True(carried.Length == 0, $"Passenger carries Visual Studio's own assemblies beside the language server, whose terms are not open source:\n{string.Join('\n', carried)}");

        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static void Main()\n    {\n");
        window.KeyTextInput("        System.Console.");
        Ui.Settle();
        window.KeyTextInput("W");
        Ui.Settle();
        window.KeyTextInput("riteL");
        Ui.Settle();
        Assert.True(window.Assist.IsCompletionOpen);
        Assert.Equal("WriteLine", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.WaitFor(() => window.Assist.ItemDocumentation is { Length: > 0 }, "WriteLine's documentation");

        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.False(window.Assist.IsCompletionOpen);
        Assert.Contains("System.Console.WriteLine", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        // Razor's start is where the server first loads Visual Studio's assemblies; a failed load logs the name.
        int server = window.CSharpService!.Session!.Server.ProcessId;
        string log = string.Empty;
        Ui.WaitFor(() => (log = ServerLog(server)).Contains("Razor extension startup finished.", StringComparison.Ordinal), "the language server's Razor support to start");
        string[] looked = [.. log.Split('\n').Where(line => LeftOut.Any(name => line.Contains(name, StringComparison.Ordinal)))];
        Assert.True(looked.Length == 0, $"The language server looked for an assembly Passenger leaves out, so something it does needs it:\n{string.Join('\n', looked)}");
        window.Close();
    }

    /// <summary>The log of the language server with this process, as a person opening the logs folder would find it.</summary>
    private static string ServerLog(int process) =>
        Directory.EnumerateFiles(Passenger.App.CSharp.CSharpService.LogFolder, $"server-*-{process}.log").FirstOrDefault() is { } path
            ? Ui.Shared(path) : string.Empty;

    /// <summary>Find Usages says so with no search yet and shows nothing else; it groups usages by what they do,
    /// project and file, previews the chosen one beside a divider that drags, keeps each search, goes to a usage on
    /// Enter, lists a usage in a closed UTF-16 file at its line with its code, and lists a declaration once where a
    /// primary constructor or a record's property is declared with it.</summary>
    [AvaloniaFact]
    public void Find_usages_groups_them_previews_the_chosen_one_keeps_each_search_and_lists_each_place_once_at_its_line()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        FindView find = window.Find;
        Ui.Run(window, AppActions.ShowFind);
        Avalonia.Controls.Border[] Rules() => [.. find.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Where(b => b.IsEffectivelyVisible && b.BorderThickness.Bottom > 0 && b.TemplatedParent is null)];
        Assert.True(Rules().Length == 0, "The empty Find window shows a row with a rule under it, before any search has a tab there");
        Ui.PlaceAfter(editor, "int tot");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => find.Rows.Count > 0, "the usages in the Find window");
        Assert.True(Rules().Length > 0, "A search's tab has no row of its own over the usages");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Find));
        Assert.Equal(["Declaration", "Write access", "Read access"], find.Rows.Where(r => r.Depth == 0).Select(r => r.Label));
        Assert.All(find.Rows.Where(r => r.Depth == 1), r => Assert.Equal("Sample", r.Label));
        Assert.All(find.Rows.Where(r => r.Depth == 2), r => Assert.Equal("Loop.cs", r.Label));
        Assert.Equal(["int total = 0;", "total += i * 2;", "return total;"], find.Rows.Where(r => r.Usage is not null).Select(r => r.Label));

        Assert.Equal("int total = 0;", find.Selected?.Label);
        Assert.Contains("for (int i = 0", find.Preview.Document.Snapshot.ToString(), StringComparison.Ordinal);
        TextRange marked = Assert.Single(find.Preview.Highlights);
        Assert.Equal("total", find.Preview.Document.Snapshot.GetText(marked.Start, marked.Length));

        Avalonia.Controls.ListBox usageList = find.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single();
        (double usagesWidth, double previewWidth) = (usageList.Bounds.Width, find.Preview.Bounds.Width);
        Avalonia.Controls.GridSplitter divider = find.GetVisualDescendants().OfType<Avalonia.Controls.GridSplitter>().Single();
        Point grip = divider.TranslatePoint(new Point(divider.Bounds.Width / 2, divider.Bounds.Height / 2), window)!.Value;
        window.MouseDown(grip, Avalonia.Input.MouseButton.Left);
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(grip - new Point(15 * step, 0), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        }
        window.MouseUp(grip - new Point(150, 0), Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.True(Math.Abs(usageList.Bounds.Width - (usagesWidth - 150)) < 2 && Math.Abs(find.Preview.Bounds.Width - (previewWidth + 150)) < 2,
            $"The usages and the preview were {usagesWidth} and {previewWidth} wide, and {usageList.Bounds.Width} and {find.Preview.Bounds.Width} once the divider between them was dragged 150 px left");

        window.OpenFile(project.PathOf("Program.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Helper.Twi");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => find.Shown == "Usages of Twice", "the second search");
        Assert.Equal(["Usages of total", "Usages of Twice"], find.Searches);
        Assert.Equal(["Declaration", "Invocation"], find.Rows.Where(r => r.Depth == 0).Select(r => r.Label));
        find.ShowSearch("Usages of total");
        Assert.Equal(3, find.Rows.Count(r => r.Usage is not null));

        find.Selected = find.Rows.Last(r => r.Usage is not null);
        find.FocusRows();
        Ui.Press(window, "ENTER");
        Assert.Equal("Loop.cs", window.EditorArea.Active?.Name);
        Assert.Equal(new TextPosition(9, 15), window.EditorArea.CodeEditor.CaretPosition);

        string wide = project.PathOf("Wide.cs");
        Assert.True(File.ReadAllBytes(wide) is [0xFF, 0xFE, ..], "Wide.cs is not written in UTF-16");
        window.OpenFile(project.PathOf("Area.cs"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "static int O");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => find.Shown == "Usages of Of" && find.Rows.Any(r => r.Usage?.Path == PathNames.Canonical(wide)), "the usage in the UTF-16 file");
        Usage usage = find.Rows.Select(r => r.Usage).OfType<Usage>().Single(u => u.Path == PathNames.Canonical(wide));
        Assert.True((usage.Line, usage.Column, usage.LineText) == (4, 20, "        return Area.Of(2, 2);"),
            $"Find Usages listed the UTF-16 file's usage at {usage.Line + 1}:{usage.Column + 1} as '{usage.LineText}', not where it is with its line of code");

        window.OpenFile(project.PathOf("Pairs.cs"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        // Each declared where another symbol is, which the server names once for each.
        foreach ((string at, string title, string declaration) in new[]
        {
            ("class Coun", "Usages of Counter", "class Counter(int start)"),
            ("record Pa", "Usages of Pair", "record Pair(int Left, int Right)"),
            ("pair.Lef", "Usages of Left", "record Pair(int Left, int Right)"),
        })
        {
            Ui.PlaceAfter(editor, at);
            Ui.Run(window, AppActions.FindUsages);
            Ui.WaitFor(() => find.Shown == title, $"the {title}");
            List<FindRow> declared = [.. Visible(find, "Declaration").Where(r => r.Usage is not null)];
            Assert.True(declared.Count == 1 && declared[0].Label == declaration,
                $"The Find window lists the declaration of {title[10..]} {declared.Count} times, where Rider lists each place once: {string.Join("; ", declared.Select(r => $"{r.Label} ({r.Detail})"))}");
            List<Usage> usages = [.. find.Rows.Select(r => r.Usage).OfType<Usage>()];
            Assert.True(usages.DistinctBy(u => (u.Path, u.Line, u.Column)).Count() == usages.Count,
                $"The Find window lists a place of {title[10..]} more than once: {string.Join("; ", usages.Select(u => $"{u.Line + 1}:{u.Column + 1} {u.LineText.Trim()}"))}");
        }
        TreeKeysCheck.Press(window, find.Rows, find.Rows[0], "the Find window");
        window.Close();

        // The rows under a group of what usages do, to the next group.
        static IEnumerable<FindRow> Visible(FindView find, string group) =>
            find.Rows.SkipWhile(r => r.Depth != 0 || r.Label != group).Skip(1).TakeWhile(r => r.Depth != 0);
    }

    [AvaloniaFact]
    public void The_error_stripe_marks_a_problem_in_its_colour_says_what_it_is_and_a_click_goes_there()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal((0, 0), editor.ProblemCount);
        Ui.PlaceAfter(editor, "        return total;");
        window.KeyTextInput(" int wrong = \"text\";");
        Ui.WaitFor(() => editor.ProblemCount.Errors == 1, "the error's diagnostic");
        CodeEditor.StripeMark error = Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.Error);
        Assert.Contains("convert", error.Message, StringComparison.Ordinal);
        Assert.Contains(editor.StripeMarks, m => m.Kind == StripeMarkKind.Caret);

        Point at = editor.TranslatePoint(editor.StripePoint(error), window)!.Value;

        editor.GoTo(new TextPosition(0, 0));
        Ui.Settle();
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.Equal(editor.Document.Snapshot.GetLineFromOffset(error.Offset), editor.CaretPosition.Line);

        Avalonia.Media.Color colour = Ui.Pixel(window, at);
        Assert.True(colour.R > 180 && colour.G < 140 && colour.B < 140, $"The error's mark is {colour}, not the error colour");

        window.Close();
    }

    [AvaloniaFact]
    public void A_right_click_on_a_call_puts_the_caret_there_and_its_menu_renames_it_in_every_file_that_uses_it_and_goes_to_an_implementation()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        int call = editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + 2;
        Ui.RightClick(editor, editor.PointOf(call) + new Vector(1, editor.LineHeight / 2));
        Assert.InRange(editor.Selections.Primary.Caret, call - 1, call + 1);
        Ui.Choose(window.OpenContextMenu, "Refactor", "Rename…");
        window.KeyTextInput("Double");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("Helper.Double(", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("public static int Double(", File.ReadAllText(project.PathOf("Helper.cs")), StringComparison.Ordinal);

        window.OpenFile(project.PathOf("Shapes.cs"));
        Ui.Settle();
        int sides = editor.Document.Snapshot.ToString().IndexOf("Sides", StringComparison.Ordinal) + 1;
        Ui.RightClick(editor, editor.PointOf(sides) + new Vector(1, editor.LineHeight / 2));
        Ui.Choose(window.OpenContextMenu, "Go To", "Go to Implementation");
        Ui.WaitFor(() => editor.CaretPosition.Line == 7, "the implementation in Square");
        Assert.Null(window.OpenContextMenu);
        window.Close();
    }

    [AvaloniaFact]
    public void A_reformat_started_before_switching_tabs_changes_only_its_own_file_and_breaks_lines_as_the_file_does()
    {
        MainWindow window = project.Open("Messy.cs");
        string helper = File.ReadAllText(project.PathOf("Helper.cs"));
        window.Perform(AppActions.Reformat);
        window.OpenFile(project.PathOf("Helper.cs"));
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument messy = window.EditorArea.Documents.First(d => d.Name == "Messy.cs");
        Ui.WaitFor(() => messy.Text.Snapshot.ToString().Contains("        List<string> names = [];", StringComparison.Ordinal), "Messy.cs reformatted");
        Assert.Equal("Helper.cs", window.EditorArea.Active!.Name);
        Assert.Equal(helper, window.EditorArea.CodeEditor.Document.Snapshot.ToString());

        // Roslyn writes the system's line break into whatever file it formats.
        window.OpenFile(project.PathOf("Crlf.cs"));
        Ui.Settle();
        Ui.Run(window, AppActions.Reformat);
        const string Formatted = "static class Crlf\r\n{\r\n    public static int One()\r\n    {\r\n        return 1;\r\n    }\r\n}\r\n";
        Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString() == Formatted, "Crlf.cs reformatted with its own line breaks");
        window.Close();
    }

    [AvaloniaFact]
    public void A_letter_deleted_inside_a_name_the_server_coloured_leaves_its_colour_on_the_name_left_until_the_server_answers()
    {
        MainWindow window = project.Open("Separators.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        int use = editor.Document.Snapshot.ToString().IndexOf("return total", StringComparison.Ordinal) + "return ".Length;
        Ui.WaitFor(() => document.Highlighter!.GetSpans(editor.Document.Snapshot, use, 1).Any(s => s.Start == use && s.Kind == TokenKind.Local),
            "the server's colour on the local's use");
        editor.Selections = SelectionSet.Single(use + "tot".Length);
        // The key alone, before the server is asked again: what shows is the colour carried through the delete.
        window.KeyPress(Avalonia.Input.Key.Back, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        window.KeyRelease(Avalonia.Input.Key.Back, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        Assert.Equal("toal", editor.Document.Snapshot.GetText(use, 4));
        SyntaxSpan[] spans = [.. document.Highlighter!.GetSpans(editor.Document.Snapshot, use, 4).Where(s => s.Kind == TokenKind.Local)];
        Assert.True(spans is [{ Length: 4 } local] && local.Start == use,
            $"After a letter was deleted inside the local's name, its colour was on [{string.Join(", ", spans.Select(s => $"{s.Start - use}+{s.Length}"))}] of the name left, not on all of it");
        window.Close();
    }

    [AvaloniaFact]
    public void Code_typed_below_a_comment_holding_line_breaks_only_roslyn_counts_is_squiggled_coloured_and_reformatted_in_place()
    {
        MainWindow window = project.Open("Separators.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        string before = editor.Document.Snapshot.ToString();
        Ui.PlaceAfter(editor, "int total = 1;");
        window.KeyTextInput(" int wrong = \"text\";");

        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the error typed");
        TextSnapshot text = editor.Document.Snapshot;
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("\"text\"", text.GetText(error.Range));
        Assert.Equal(5, text.GetLineFromOffset(error.Range.Start));

        // A use of the local is coloured as one only by the server, whose colours must land on the editor's line.
        int use = text.ToString().IndexOf("return total", StringComparison.Ordinal) + "return ".Length;
        Ui.WaitFor(() => document.Highlighter!.GetSpans(editor.Document.Snapshot, use, 1).Any(s => s.Start == use && s.Kind == TokenKind.Local),
            "the server's colour on the local's use");

        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => window.Status is "Reformatted" or "Already formatted", "the reformat");
        Assert.Equal(before.Replace("int total = 1;\n          return", "int total = 1; int wrong = \"text\";\n        return", StringComparison.Ordinal),
            editor.Document.Snapshot.ToString());
        window.Close();
    }

    /// <summary>Edits that make and unmake the line breaks only Roslyn counts (a lone '\r', a '\r' parted from its '\n'
    /// or given one, the Unicode separators, edits that meet at a break) keep the server's lines of the file in step
    /// without reading it again, so code typed below them afterwards is squiggled and coloured on its own words. So do
    /// changes that begin or end between a "\r\n"'s halves (text put between them, a line's end replaced up to its
    /// '\r', a '\n' taken with what follows it); and the server never ends over a change it cannot place.</summary>
    [AvaloniaFact]
    public void Edits_that_make_and_unmake_line_breaks_only_roslyn_counts_or_fall_between_a_crlfs_halves_keep_the_server_in_step()
    {
        string separator = ((char)0x2028).ToString();
        string paragraph = ((char)0x2029).ToString();
        string next = ((char)0x85).ToString();
        MainWindow window = project.Open("Mixed.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        List<string> ended = [];
        window.CSharpService!.Stopped += (_, why) => ended.Add(why);
        Ui.Settle();
        int At(string text) => document.Text.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal);
        void Edit(string what, params TextEdit[] edits)
        {
            long before = Passenger.Lsp.ServerLines.CharactersRead;
            document.Text.Edit(edits, editor.Selections, editor.Selections);
            TextSnapshot text = document.Text.Snapshot;
            Passenger.Lsp.ServerLines carried = Passenger.Lsp.ServerLines.Of(text);
            long read = Passenger.Lsp.ServerLines.CharactersRead - before;
            Assert.True(read < text.Length, $"{what}: {read} characters were read to find the server's lines, the whole file again");
            Passenger.Lsp.ServerLines fresh = Passenger.Lsp.ServerLines.Of(TextSnapshot.From(text.ToString()));
            Assert.True(fresh.LineCount == carried.LineCount,
                $"{what}: the server's lines as they were carried are {carried.LineCount}, the file's are {fresh.LineCount}: every position sent from here on is off");
            for (int offset = 0; offset <= text.Length; offset++)
            {
                Assert.True(carried.PositionOf(offset) == fresh.PositionOf(offset),
                    $"{what}: offset {offset} is at {carried.PositionOf(offset)} on the server's lines as they were carried, at {fresh.PositionOf(offset)} on the file's: every position sent from here on is off");
            }
            Ui.Hold(TimeSpan.FromMilliseconds(10), "a frame between edits, as keys come");
        }

        Edit("A lone carriage return put in", new TextEdit(At("one,"), 0, "\r"));
        Edit("A line feed given to it", new TextEdit(At("one,"), 0, "\n"));
        Edit("Text put between a CR and its LF, and more further on", new TextEdit(At("\r\ntwo,") + 1, 0, "y"), new TextEdit(At("three."), 0, "x"));
        Edit("The line feed taken from after a CR", new TextEdit(At("\r\none,") + 1, 1, string.Empty));
        Edit("Separators pasted", new TextEdit(At("Pasted:"), 0, $"a{paragraph}b\r\nc\rd{separator}"));
        Edit("A separator replaced", new TextEdit(At(separator + "\rone,"), 1, " "));
        Edit("Edits meeting at a break", new TextEdit(At("xthree."), 1, "\r"), new TextEdit(At("xthree.") + 1, 0, "\n"));
        Edit("Text ending in a CR put before a LF", new TextEdit(At("y\ntwo,") + 1, 0, "end\r"));
        Edit("A CR at the end of the text", new TextEdit(document.Text.Snapshot.Length, 0, "\r"));
        Random random = new(7);
        string[] pieces = ["\r", "\n", "\r\n", separator, paragraph, next, "a", "bc"];
        for (int batch = 0; batch < 150; batch++)
        {
            int from = At("/*") + 2;
            int to = At("*/");
            int[] cuts = [.. Enumerable.Range(0, random.Next(1, 4) * 2).Select(_ => random.Next(from, to + 1)).Order()];
            TextEdit[] edits = [.. Enumerable.Range(0, cuts.Length / 2).Select(i => new TextEdit(cuts[2 * i], random.Next(3) == 0 ? cuts[2 * i + 1] - cuts[2 * i] : 0,
                string.Concat(Enumerable.Range(0, random.Next(0, 4)).Select(_ => pieces[random.Next(pieces.Length)]))))];
            Edit($"Random batch {batch + 1} ({string.Join("; ", edits.Select(e => $"{e.Start}+{e.Length} '{e.Text.Replace("\r", "\\r").Replace("\n", "\\n")}'"))})", edits);
        }
        Edit("Everything after the comment replaced", new TextEdit(At("*/") + 2, document.Text.Snapshot.Length - At("*/") - 2,
            "\n    public static int Value()\n    {\n        int total = 1;\n          return total;\n    }\n}\n"));

        Ui.PlaceAfter(editor, "int total = 1;");
        window.KeyTextInput(" int wrong = \"text\";");
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the error typed");
        // A server that ended (a change it could not place) is started again with the whole file, which hides a drift.
        Assert.True(ended.Count == 0, $"The language server ended as the edits went to it ({string.Join("; ", ended)}): a change "
            + "reached it where its copy of the file has no such place, and only a server started again with the whole file had the file's lines");
        TextSnapshot after = editor.Document.Snapshot;
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("\"text\"", after.GetText(error.Range));
        int use = after.ToString().IndexOf("return total", StringComparison.Ordinal) + "return ".Length;
        Ui.WaitFor(() => document.Highlighter!.GetSpans(editor.Document.Snapshot, use, 1).Any(s => s.Start == use && s.Kind == TokenKind.Local),
            "the server's colour on the local's use");

        window.OpenFile(project.PathOf("Halves.cs"));
        Ui.Settle();
        OpenDocument halves = window.EditorArea.Active!;
        Assert.Equal("Halves.cs", halves.Name);
        int Within(string text) => halves.Text.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal);
        void Change(TextEdit edit)
        {
            halves.Text.Edit([edit], editor.Selections, editor.Selections);
            Ui.Settle();
        }

        Change(new TextEdit(Within("one,\r\n") + "one,\r".Length, 0, "x"));
        Change(new TextEdit(Within("two,\r\n"), "two,\r".Length, "deux,\r"));
        Change(new TextEdit(Within("deux,\r\n") + "deux,\r".Length, "\nthree".Length, string.Empty));
        Assert.Contains("/* one,\rx\ndeux,\r. */\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "int total = 1;");
        window.KeyTextInput(" int wrong = \"text\";");
        Ui.WaitFor(() => halves.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the error typed below the halves");
        Assert.True(ended.Count == 0, $"The language server ended as the changes went to it ({string.Join("; ", ended)}): one reached it "
            + "where its copy of the file has no such place");
        TextSnapshot changed = editor.Document.Snapshot;
        Marker[] errors = [.. halves.Markers.Where(m => m.Kind == MarkerKind.Error)];
        Assert.True(errors is [{ } only] && changed.GetText(only.Range) == "\"text\"",
            $"The errors are {string.Join("; ", errors.Select(e => $"'{changed.GetText(e.Range)}' at {e.Range.Start}: {e.Message}"))}, not one under the "
            + "string typed: the server's copy of the file is not the editor's");
        int below = changed.ToString().IndexOf("return total", StringComparison.Ordinal) + "return ".Length;
        Ui.WaitFor(() => halves.Highlighter!.GetSpans(editor.Document.Snapshot, below, 1).Any(s => s.Start == below && s.Kind == TokenKind.Local),
            "the server's colour on the local's use below the halves");
        window.Close();
    }

    /// <summary>A letter typed where Reformat Code's answer is about to delete a space, before the answer comes: the answer
    /// still lands, and deletes only the space it was made for, never the letter typed at its start.</summary>
    [AvaloniaFact]
    public void A_letter_typed_at_the_start_of_what_a_reformat_is_about_to_change_stays_once_the_reformat_lands()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result");
        window.KeyTextInput(" ");
        Ui.Settle();
        Assert.Contains("int result  = Helper.Twice(2);", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        Assert.Null(window.Actions.Run(AppActions.Reformat));
        window.KeyTextInput("s");
        Ui.WaitFor(() => window.Status is "Reformatted" or "Already formatted" || window.Status?.Contains("not applied", StringComparison.Ordinal) == true,
            "the reformat's answer");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(window.Status == "Reformatted" && text.Contains("int result s= Helper.Twice(2);", StringComparison.Ordinal),
            $"Once Reformat Code's answer came ({window.Status}), the line is not the letter typed with the space after it deleted:\n{text}");
        window.Close();
    }

    /// <summary>Undo and Redo of a paste at a file's end, the platform's input method following the editor as a
    /// desktop's does: each changes the text and keeps its step for the other, and the language server holds the
    /// editor's text.</summary>
    [AvaloniaFact]
    public void Undo_and_redo_of_a_paste_at_a_files_end_keep_their_steps_and_the_servers_copy_with_an_input_method_following()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument program = window.EditorArea.Active!;
        Ui.WaitFor(() => program.FoldRegions.Count > 0, "the file's folds");
        Ui.WaitFor(() => window.CSharpService?.For(program)?.Lsp is not null, "the server to have the file");
        editor.Focus();
        // Asked of the focused control by Avalonia on a platform with an input method (macOS's, Windows'), as headless has none.
        Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs asked = new() { RoutedEvent = Avalonia.Input.InputElement.TextInputMethodClientRequestedEvent };
        editor.RaiseEvent(asked);
        Avalonia.Input.TextInput.TextInputMethodClient client = asked.Client ?? throw new InvalidOperationException("The editor gave no input method client");
        List<string> told = [];
        client.SurroundingTextChanged += (_, _) => told.Add(client.SurroundingText);
        client.SelectionChanged += (_, _) => told.Add($"{client.Selection.Start}:{client.Selection.End}");

        // Pasted at the end, its caret is past where the text ends once it is undone.
        string before = editor.Document.Snapshot.ToString();
        editor.Selections = SelectionSet.Single(before.Length);
        Task copied = window.Clipboard!.SetTextAsync("\nstatic class Pasted { }");
        Ui.WaitFor(() => copied.IsCompleted, "the clipboard to take the text");
        Ui.Press(window, Ui.Shortcut(EditorActions.Paste));
        string pasted = editor.Document.Snapshot.ToString();
        string pastedLine = pasted[(pasted.LastIndexOf('\n') + 1)..];
        Assert.Contains("static class Pasted { }", pastedLine, StringComparison.Ordinal);

        told.Clear();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(before, editor.Document.Snapshot.ToString());
        Assert.True(editor.Document.CanRedo, "Undo of the paste left nothing to redo: its step was lost");
        Assert.Equal(before.Length, editor.Selections.Primary.Caret);
        Assert.True(told.Contains(string.Empty), $"The input method was not told the caret's line, empty at the end, once the paste was undone: [{string.Join(" | ", told)}]");
        ServerHolds(window, program, "the paste undone");

        told.Clear();
        Ui.Run(window, EditorActions.Redo);
        Assert.Equal(pasted, editor.Document.Snapshot.ToString());
        Assert.True(editor.Document.CanUndo, "Redo of the paste left nothing to undo: its step was lost");
        Assert.True(told.Contains(pastedLine), $"The input method was not told the caret's line, the pasted one, once the paste was redone: [{string.Join(" | ", told)}]");
        ServerHolds(window, program, "the paste redone");
        window.Close();
    }

    /// <summary>Waits for the language server's copy of an open file to be the editor's text.</summary>
    private static void ServerHolds(MainWindow window, OpenDocument document, string after)
    {
        string expected = Passenger.Roslyn.Extension.TextHash.Of(document.Text.Snapshot.ToString());
        Task<IReadOnlyList<Passenger.Roslyn.Extension.TextHeld>>? asking = null;
        Ui.WaitFor(() =>
        {
            if (asking is { IsCompletedSuccessfully: true } done && done.Result is [{ Hash: { } hash }] && hash == expected)
            {
                return true;
            }
            if (asking is null || asking.IsCompleted)
            {
                asking = window.CSharpService!.For(document)!.Lsp!.HeldTextsAsync([document.Path]);
            }
            return false;
        }, $"the language server to hold the editor's text after {after}");
    }

    [AvaloniaFact]
    public void A_line_typed_while_a_rename_is_out_still_leaves_the_new_name_on_the_symbol()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.Run(window, AppActions.Rename);
        window.KeyTextInput("Double");
        Ui.Settle();
        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        editor.ApplyEdits([new TextEdit(0, 0, "// typed meanwhile\n")]);
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        Assert.StartsWith("// typed meanwhile\nclass Program", text, StringComparison.Ordinal);
        Assert.Contains("int result = Helper.Double(2);", text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_the_open_folder_again_through_mcp_keeps_the_open_files_language_server()
    {
        MainWindow window = project.Open("Program.cs");
        Passenger.App.Workspace.OpenDocument program = window.EditorArea.Active!;
        Passenger.App.CSharp.CSharpService server = window.CSharpService!;
        Passenger.App.Mcp.PassengerTools tools = new(window);

        // Agents open the workspace first, often the one already open.
        Task<Passenger.App.Mcp.Workspace> opened = tools.Open(Path.GetDirectoryName(project.PathOf("Program.cs"))!);
        Ui.WaitFor(() => opened.IsCompleted, "the open", TimeSpan.FromSeconds(10));
        Assert.Same(server, window.CSharpService);
        Assert.NotNull(server.For(program));
        Task<object> found = tools.Diagnostics("Program.cs");
        Ui.WaitFor(() => found.IsCompleted, "the diagnostics of the file open before", TimeSpan.FromSeconds(15));
        Assert.True(found.IsCompletedSuccessfully, found.Exception?.Message);
        window.Close();
    }

    /// <summary>The server counts a line at each break in Separators.cs's and Separated.cs's comments that the editor does
    /// not, so a place it names is read in the file's own text, a file not open as it is on disk: Go to Symbol's rows, Go to
    /// Declaration and Implementation, what an agent asks of the code, and the lines an agent's refactoring changed.</summary>
    [AvaloniaFact]
    public void Go_to_symbol_declaration_implementation_and_a_refactorings_changed_lines_are_the_editors_below_line_breaks_only_roslyn_counts()
    {
        MainWindow window = project.Open("Separated.cs");
        Ui.Run(window, AppActions.GoToSymbol);
        SymbolPopup popup = Assert.IsType<SymbolPopup>(window.Popup);
        window.KeyTextInput("Separated");
        Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Name == "SecondSeparated"), "the server's symbols for what was typed");
        Assert.Equal("Separated.cs:7", popup.Rows.First(r => r.Name == "FirstSeparated").Where);
        Assert.Equal("Separated.cs:12", popup.Rows.First(r => r.Name == "SecondSeparated").Where);
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToSymbol);
        popup = Assert.IsType<SymbolPopup>(window.Popup);
        window.KeyTextInput("Value");
        Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Where?.StartsWith("Separators.cs", StringComparison.Ordinal) == true), "the closed file's Value");
        Assert.Equal("Separators.cs:4", popup.Rows.First(r => r.Where?.StartsWith("Separators.cs", StringComparison.Ordinal) == true).Where);
        Ui.Press(window, "ESCAPE");

        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "=> Separators.Va");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Separators.cs", window.EditorArea.Active?.Name);
        Assert.True(editor.CaretPosition == new TextPosition(3, 22),
            $"Go to Declaration landed on line {editor.CaretPosition.Line + 1} ('{editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}'), not on Value's name on line 4");

        window.OpenFile(project.PathOf("Separated.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "    int Val");
        Ui.Run(window, AppActions.GoToImplementation);
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the two implementations to choose from");
        Assert.Equal(["Separated.cs:14", "Separated.cs:9"], window.Assist.Offered.Order(StringComparer.Ordinal));
        Assert.Contains("public int Value() => 1;", window.Assist.OfferedDetails);
        int second = window.Assist.Offered.ToList().IndexOf("Separated.cs:14");
        for (int i = 0; i < second; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal("    public int |Value() => Separators.Value();", XamlTests.CaretLine(editor));

        Passenger.App.Mcp.PassengerTools tools = new(window);
        Passenger.App.Mcp.Place declared = Assert.Single(Await(tools.Definition("Separated.cs", 14, 39), "the declaration an agent asks for"));
        Assert.Equal(("Separators.cs", 4, 23, "public static int Value()"), (declared.Path, declared.Line, declared.Column, declared.Text));
        Assert.Contains("14:38 public int Value() => Separators.Value();",
            Assert.Single(Await(tools.Usages("Separators.cs", 4, 23), "the usages an agent asks for").Files, f => f.Path == "Separated.cs").Places);
        Assert.Contains(Await(tools.FindSymbol("FirstSeparated"), "the symbol an agent looks for"), s => s.Line == 7);
        Assert.Contains("FirstSeparated (class, line 7)", Await(tools.Outline("Separated.cs"), "the outline an agent asks for"), StringComparison.Ordinal);

        // A parameter added to Separators.Value: its declaration on the editor's line 4, its call in Separated.cs on 14.
        System.Text.Json.JsonElement options = System.Text.Json.JsonDocument.Parse("{\"parameters\": [{\"type\": \"int\", \"name\": \"extra\", \"callValue\": \"0\"}]}").RootElement;
        Passenger.App.Mcp.RefactorAnswer changed = Await(tools.Refactor("change_signature", "Separators.cs", 4, 23, options: options), "the agent's Change Signature");
        Assert.Equal(["Separated.cs changed on 14", "Separators.cs changed on 4"],
            changed.Files.Select(f => $"{f.Path} {f.Change} on {string.Join(", ", f.Lines)}").Order(StringComparer.Ordinal));
        Assert.Contains("public int Value() => Separators.Value(0);", File.ReadAllText(project.PathOf("Separated.cs")), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Rename's box and Move's list follow the text while open; closed by a switch of tab, which answers neither,
    /// they let it go, rather than keep every edit made to the file for as long as it is open.</summary>
    [AvaloniaFact]
    public void Rename_and_moves_list_closed_by_switching_tabs_stop_following_the_file()
    {
        MainWindow window = project.Open("Program.cs");
        window.OpenFile(project.PathOf("Helper.cs"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        TextDocument helper = editor.Document;
        int before = Trackers(helper);
        Ui.PlaceAfter(editor, "static int Tw");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Twice", "the rename box");
        Assert.Equal(before + 1, Trackers(helper));
        Ui.Run(window, AppActions.PreviousTab);
        Ui.Settle();
        Assert.Equal("Program.cs", window.EditorArea.Active?.Name);
        Assert.Null(window.Assist.Renaming);
        Assert.True(Trackers(helper) == before, $"Rename's box, closed by the switch of tab, still follows every edit to Helper.cs ({Trackers(helper) - before} more followers)");

        Ui.Run(window, AppActions.NextTab);
        Ui.Settle();
        Ui.PlaceAfter(editor, "static class Hel");
        Ui.Run(window, AppActions.Move);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Move to Another File…"), "Move's list");
        Assert.Equal(before + 1, Trackers(helper));
        Ui.Run(window, AppActions.PreviousTab);
        Ui.Settle();
        Assert.Empty(window.Assist.Offered);
        Assert.True(Trackers(helper) == before, $"Move's list, closed by the switch of tab, still follows every edit to Helper.cs ({Trackers(helper) - before} more followers)");
        window.Close();
    }

    /// <summary>How many requests' and popups' trackers follow a document's edits: those among the handlers of its
    /// <see cref="TextDocument.Changed"/>.</summary>
    private static int Trackers(TextDocument document)
    {
        const System.Reflection.BindingFlags Own = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object handlers = typeof(TextDocument).GetField("changed", Own)?.GetValue(document)
            ?? throw new MissingFieldException("TextDocument no longer keeps the handlers of Changed in a field named changed");
        Delegate[] heard = handlers.GetType().GetField("handlers", Own)?.GetValue(handlers) as Delegate[]
            ?? throw new MissingFieldException("A document's change handlers are no longer an array named handlers");
        return heard.Count(d => d.Target is ChangeTracker);
    }

    /// <summary>Go to Declaration opens the other file at the method, and Forward after Back goes there again when the caret
    /// moved in between; Back lands on the call it left after a line is added above it; and Go to Declaration on a library
    /// type opens its decompiled source, read-only.</summary>
    [AvaloniaFact]
    public void Go_to_declaration_back_and_forward_land_where_the_code_is_and_a_library_type_opens_its_decompiled_source_read_only()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.Settle();
        Assert.Equal("Helper.cs", window.EditorArea.Active?.Name);
        Assert.Equal(2, editor.CaretPosition.Line);
        TextPosition definition = editor.CaretPosition;

        Ui.Press(window, Ui.Shortcut(AppActions.Back));
        Assert.Equal("Program.cs", window.EditorArea.Active?.Name);
        Ui.Press(window, "RIGHT");
        Ui.Press(window, "DOWN");
        Ui.Press(window, Ui.Shortcut(AppActions.Forward));

        Assert.True(window.EditorArea.Active?.Name == "Helper.cs", "a caret moved after Back lost Forward's way to the definition");
        Assert.Equal(definition, editor.CaretPosition);

        window.OpenFile(project.PathOf("Area.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "=> O");
        TextPosition call = editor.CaretPosition;
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.Settle();
        Assert.True(editor.CaretPosition.Line < call.Line, "go to declaration never reached Of, above its call");

        Ui.Press(window, "END");
        Ui.Press(window, "ENTER");
        Ui.Press(window, Ui.Shortcut(AppActions.Back));

        Assert.Equal("    public static int Room() => Of(3, 4);", editor.Document.Snapshot.GetLineText(call.Line + 1));
        Assert.True(new TextPosition(call.Line + 1, call.Column) == editor.CaretPosition,
            $"Back landed at {editor.CaretPosition}, not on the call it left, which a line added above moved to {new TextPosition(call.Line + 1, call.Column)}");

        window.OpenFile(project.PathOf("Program.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "        in");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.IsDecompiled == true, "the decompiled source to open");
        Passenger.App.Workspace.OpenDocument decompiled = window.EditorArea.Active!;
        Assert.Equal("Int32.cs", decompiled.Name);
        Assert.Contains("struct Int32", decompiled.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("struct Int32", decompiled.Text.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);

        string before = decompiled.Text.Snapshot.ToString();
        window.KeyTextInput("x");
        Ui.Settle();
        Assert.Equal(before, decompiled.Text.Snapshot.ToString());
        Assert.True(editor.IsReadOnly);
        window.Close();
    }

    [AvaloniaFact]
    public void Code_cleanup_removes_unused_usings_sorts_the_rest_and_reformats()
    {
        MainWindow window = project.Open("Messy.cs");
        Passenger.App.Workspace.OpenDocument messy = window.EditorArea.Active!;
        Ui.WaitFor(() => window.CSharpService!.For(messy)!.Diagnostics.Any(d => d.Code?.ToString() == "IDE0005"), "the unused using to be found");
        Ui.Run(window, AppActions.CodeCleanup);
        Ui.WaitFor(() => window.Status?.StartsWith("Code cleanup", StringComparison.Ordinal) == true, "the cleanup to finish");
        string text = messy.Text.Snapshot.ToString();
        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text", text, StringComparison.Ordinal);
        Assert.Contains("\n        List<string> names = [];\n", text, StringComparison.Ordinal);
        Assert.Contains("\n        return names.Count;\n", text, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Refactor This lists only the refactorings at the selection; Extract Method asks for each new method's name at
    /// once, its rename box at the call offering the method just made, not the one before; and lines selected down to the
    /// next statement leave that statement on its own line.</summary>
    [AvaloniaFact]
    public void Extract_method_asks_each_new_methods_name_and_keeps_the_next_statement_on_its_line_and_refactor_this_lists_only_what_fits()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string text = editor.Document.Snapshot.ToString();
        int from = text.IndexOf("for (", StringComparison.Ordinal);
        int to = text.IndexOf("        }\n        return", StringComparison.Ordinal) + "        }".Length;
        editor.Selections = SelectionSet.Single(new Selection(from, to));
        Ui.Run(window, AppActions.RefactorThis);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the refactorings");
        IReadOnlyList<string> offered = window.Assist.Offered;
        Assert.Contains("Extract method", offered);
        Assert.DoesNotContain(offered, t => t.StartsWith("Suppress", StringComparison.Ordinal));
        Ui.Press(window, "ESCAPE");

        editor.Selections = SelectionSet.Single(new Selection(from, to));
        int version = editor.Document.Version;
        Ui.Run(window, AppActions.ExtractMethod);
        Ui.WaitFor(() => editor.Document.Version != version, "the loop to be extracted");
        Ui.Settle();
        string first = window.Assist.Renaming ?? throw new Xunit.Sdk.XunitException("No rename was offered for the method the loop became");
        string extracted = editor.Document.Snapshot.ToString();
        Assert.True(extracted.Contains($"total = {first}(total);", StringComparison.Ordinal), $"The rename offered {first}, not the method the loop became:\n{extracted}");
        // The suggested name is kept, so the method extracted next needs another.
        Ui.Press(window, "ESCAPE");

        int product = extracted.IndexOf("i * 2", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(product, product + "i * 2".Length));
        version = editor.Document.Version;
        Ui.Run(window, AppActions.ExtractMethod);
        Ui.WaitFor(() => editor.Document.Version != version, "the product to be extracted");
        Ui.Settle();
        string second = window.Assist.Renaming ?? throw new Xunit.Sdk.XunitException("No rename was offered for the method the product became");
        Assert.True(editor.Document.Snapshot.ToString().Contains($"total += {second}(i);", StringComparison.Ordinal),
            $"The rename offered {second}, not the method just made:\n{editor.Document.Snapshot}");
        window.KeyTextInput("Double");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("Double(", StringComparison.Ordinal), "the second method to take its name");
        string named = editor.Document.Snapshot.ToString();
        Assert.Contains("total += Double(i);", named, StringComparison.Ordinal);
        Assert.Contains($"total = {first}(total);", named, StringComparison.Ordinal);
        Assert.DoesNotContain(second, named, StringComparison.Ordinal);
        Assert.Matches(@"static \w+ Double\(int i\)", named);

        window.OpenFile(project.PathOf("Average.cs"));
        Ui.Settle();
        editor.GoTo(new TextPosition(8, 8));
        for (int i = 0; i < 5; i++)
        {
            Ui.Press(window, "shift Down");
        }
        version = editor.Document.Version;
        Ui.Run(window, AppActions.ExtractMethod);
        Ui.WaitFor(() => editor.Document.Version != version, "the average's loop to be extracted");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        string after = editor.Document.Snapshot.ToString();
        Assert.True(after.Contains("        return total / count;\n", StringComparison.Ordinal), $"The return after the loop was joined to the call that replaced it:\n{after}");
        window.Close();
    }

    [AvaloniaFact]
    public void Generate_lists_what_the_server_can_write_for_the_type_and_nothing_else()
    {
        MainWindow window = project.Open("Point.cs");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "public int X");
        Ui.Run(window, AppActions.Generate);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "what can be generated");
        Assert.Contains("Generate Equals and GetHashCode", window.Assist.Offered);
        Assert.All(window.Assist.Offered, title => Assert.StartsWith("Generate", title, StringComparison.Ordinal));
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>Choosing an override from completion writes the overriding method with the caret in its body, and what a
    /// completion or a fix writes takes the file's line breaks, whichever the server writes.</summary>
    [AvaloniaFact]
    public void What_a_completion_or_a_fix_writes_takes_the_files_line_breaks_whichever_the_server_writes()
    {
        string lfFile = File.ReadAllText(project.PathOf("Breaks/LfFile.cs"));
        try
        {
            MainWindow window = project.Open("Breaks/CrlfFile.cs");
            CodeEditor editor = window.EditorArea.CodeEditor;
            OverrideToString(window, editor);
            AssertBreaks(editor.Document.Snapshot.ToString(), "\r\n", "    }\r\n\r\n    static void Make()", "the override in the CRLF file");
            OpenDocument crlf = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "int made = 0;");
            window.KeyTextInput(" int twice = LfFile.Twice(made);");
            Ui.WaitFor(() => window.CSharpService!.For(crlf)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0117"), "Twice's error");
            Ui.PlaceAfter(editor, "LfFile.Tw");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Generate method 'Twice'"), "the offer to generate Twice");
            Choose(window, "Generate method 'Twice'");
            Ui.WaitFor(() => File.ReadAllText(project.PathOf("Breaks/LfFile.cs")).Contains("Twice", StringComparison.Ordinal), "Twice to be made in LfFile.cs");
            AssertBreaks(File.ReadAllText(project.PathOf("Breaks/LfFile.cs")), "\n", "Twice(int made)\n    {\n", "the method made in the LF file on disk");
            window.Close();

            File.WriteAllText(project.PathOf("Breaks/LfFile.cs"), lfFile);
            window = project.Open("Breaks/LfFile.cs");
            editor = window.EditorArea.CodeEditor;
            OverrideToString(window, editor);
            string overridden = editor.Document.Snapshot.ToString();
            Assert.True(overridden.Contains("    public int X { get; }\n    public override string ToString()\n    {\n        return base.ToString();\n    }\n", StringComparison.Ordinal),
                $"Choosing ToString() did not write the overriding method:\n{overridden}");
            AssertBreaks(overridden, "\n", "    }\n\n    static void Make()", "the override in the LF file");
            OpenDocument lf = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "int made = 0;");
            window.KeyTextInput(" Foo gadget = null;");
            Ui.WaitFor(() => window.CSharpService!.For(lf)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
            Ui.PlaceAfter(editor, "Fo");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Generate nested class 'Foo'"), "the offer to nest Foo");
            Choose(window, "Generate nested class 'Foo'");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("class Foo", StringComparison.Ordinal), "Foo to be generated");
            AssertBreaks(editor.Document.Snapshot.ToString(), "\n", "    }\n\n    private class Foo\n    {\n    }\n}\n", "the nested type in the LF file");
            window.Close();
        }
        finally
        {
            File.WriteAllText(project.PathOf("Breaks/LfFile.cs"), lfFile);
        }

        static void OverrideToString(MainWindow window, CodeEditor editor)
        {
            Ui.PlaceAfter(editor, "public int X { get; }");
            editor.Focus();
            Ui.Press(window, "ENTER");
            window.KeyTextInput("override ");
            window.KeyTextInput("T");
            Ui.Settle();
            window.KeyTextInput("oS");
            Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "ToString()", "ToString offered to override");
            Ui.Press(window, "ENTER");
            Ui.Settle();
            string text = editor.Document.Snapshot.ToString();
            Assert.True(text.IndexOf("return base.ToString();", StringComparison.Ordinal) + "return base.ToString();".Length == editor.Selections.Primary.Caret,
                $"The caret is at {editor.CaretPosition}, not after the call to the base method in the new body");
        }

        // The text with its line breaks shown, and whether each is the file's own.
        static void AssertBreaks(string text, string eol, string written, string what)
        {
            string shown = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n\n", StringComparison.Ordinal);
            string name = eol == "\n" ? "LF" : "CRLF";
            Assert.True(text.Contains(written, StringComparison.Ordinal), $"{what} is not written with the file's {name} line breaks:\n{shown}");
            bool mixed = eol == "\n" ? text.Contains('\r', StringComparison.Ordinal) : text.Replace("\r\n", "", StringComparison.Ordinal).AsSpan().IndexOfAny('\r', '\n') >= 0;
            Assert.False(mixed, $"After {what} the {name} file has another line break among its own:\n{shown}");
        }
    }

    [AvaloniaFact]
    public void A_tab_indented_editorconfig_has_enter_tab_a_template_a_completion_and_a_fix_indent_with_tabs()
    {
        MainWindow window = project.Open("Tabs/Tabbed.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("Tab, from .editorconfig", IndentShown(window));

        Ui.PlaceAfter(editor, "public int X { get; }");
        editor.Focus();
        Ui.Press(window, "ENTER");
        Ui.Press(window, "TAB");
        window.KeyTextInput("// note");
        Assert.Contains("\tpublic int X { get; }\n\t\t// note\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.Press(window, "shift TAB");
        Assert.Contains("\tpublic int X { get; }\n\t// note\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        OverrideAfter(window, editor, "// note");
        AssertTabbed(editor, "\t// note\n\tpublic override string ToString()\n\t{\n\t\treturn base.ToString();\n\t}\n", "the override");

        Ui.PlaceAfter(editor, "int made = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("if");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Ui.Press(window, "ESCAPE");
        AssertTabbed(editor, "\t\tint made = 0;\n\t\tif (true)\n\t\t{\n\t\t\t\n\t\t}\n", "the if template");

        OpenDocument tabbed = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "int made = 0;");
        window.KeyTextInput(" Foo gadget = null;");
        Ui.WaitFor(() => window.CSharpService!.For(tabbed)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
        Ui.PlaceAfter(editor, "Fo");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate nested class 'Foo'"), "the offer to nest Foo");
        Choose(window, "Generate nested class 'Foo'");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("class Foo", StringComparison.Ordinal), "Foo to be generated");
        AssertTabbed(editor, "\t}\n\n\tprivate class Foo\n\t{\n\t}\n}\n", "the nested type");
        window.Close();
    }

    [AvaloniaFact]
    public void Where_no_editorconfig_says_completions_and_reformatting_indent_as_the_files_own_lines()
    {
        MainWindow window = project.Open("Detected/TwoSpaces.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("2 spaces, detected", IndentShown(window));
        OverrideAfter(window, editor, "public int X { get; }");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.Contains("  public int X { get; }\n  public override string ToString()\n  {\n    return base.ToString();\n  }\n", StringComparison.Ordinal),
            $"The override in a file of two spaces is not indented with two:\n{text}");
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => window.Status is "Reformatted" or "Already formatted", "the reformat");
        Assert.True(editor.Document.Snapshot.ToString().Contains("\n    if (made > 0)\n    {\n      made--;\n", StringComparison.Ordinal),
            $"Reformatting a file of two spaces indented it otherwise:\n{editor.Document.Snapshot}");

        window.OpenFile(project.PathOf("Detected/TabsOwn.cs"));
        Ui.Settle();
        Assert.Equal("Tab, detected", IndentShown(window));
        OverrideAfter(window, editor, "public int X { get; }");
        AssertTabbed(editor, "\tpublic int X { get; }\n\tpublic override string ToString()\n\t{\n\t\treturn base.ToString();\n\t}\n", "the override in a file of tabs");
        window.Close();
    }

    /// <summary>Chooses ToString() from completion on a new line after some text, which overrides it there.</summary>
    private static void OverrideAfter(MainWindow window, CodeEditor editor, string after)
    {
        Ui.PlaceAfter(editor, after);
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("override ");
        window.KeyTextInput("T");
        Ui.Settle();
        window.KeyTextInput("oS");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "ToString()", "ToString offered to override");
        Ui.Press(window, "ENTER");
        Ui.Settle();
    }

    /// <summary>That the text holds what was written, and that no line of it is indented with a space.</summary>
    private static void AssertTabbed(CodeEditor editor, string written, string what)
    {
        string text = editor.Document.Snapshot.ToString();
        string shown = text.Replace("\t", "→", StringComparison.Ordinal);
        Assert.True(text.Contains(written, StringComparison.Ordinal), $"{what} is not indented with tabs:\n{shown}");
        Assert.False(text.Split('\n').Any(line => line.StartsWith(' ')), $"After {what} a line of the tab-indented file starts with a space:\n{shown}");
    }

    private static string? IndentShown(MainWindow window) => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(window, "IndentText")!.Text;

    [AvaloniaFact]
    public void Choosing_await_in_a_method_not_yet_async_makes_the_method_async_as_well()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("a");
        Ui.Settle();
        window.KeyTextInput("wa");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "await", "await offered");

        Ui.Press(window, "ENTER");
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        Assert.Matches(@"public static async [\w.<>]+ Sum\(\)\n    \{\n        int total = 0;\n        await\n", text);
        Assert.True(text.IndexOf("await", StringComparison.Ordinal) + "await".Length == editor.Selections.Primary.Caret,
            $"The caret is at {editor.CaretPosition}, not after the await:\n{text}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_fix_with_choices_offers_them_as_a_second_list_and_makes_the_one_chosen()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument program = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "Helper.Twice(2);");
        window.KeyTextInput(" Foo gadget = null;");
        Ui.WaitFor(() => window.CSharpService!.For(program)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
        Ui.PlaceAfter(editor, "Fo");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate type 'Foo'"), "the offer to generate Foo");
        Choose(window, "Generate type 'Foo'");

        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate class 'Foo'"), "the choices Generate type offers");
        Assert.Contains("Generate nested class 'Foo'", window.Assist.Offered);
        Choose(window, "Generate class 'Foo'");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("class Foo", StringComparison.Ordinal), "Foo to be generated");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.Contains("}\n\ninternal class Foo\n{\n}", StringComparison.Ordinal), $"Foo was not generated beside Program:\n{text}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_fix_the_language_server_will_not_make_once_chosen_says_why_in_the_status_bar()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument program = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "Helper.Twice(2);");
        window.KeyTextInput(" int more = Helper.Thrice(2);");
        Ui.WaitFor(() => window.CSharpService!.For(program)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0117"), "Thrice's error");
        Ui.PlaceAfter(editor, "Helper.Thr");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate method 'Thrice'"), "the offer to generate Thrice");

        // The call is changed under the open list, so the server no longer has the fix it offered when it is chosen.
        int at = editor.Document.Snapshot.ToString().IndexOf("Thrice", StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(at, "Thrice".Length, "Twice")]);
        Choose(window, "Generate method 'Thrice'");
        Ui.WaitFor(() => window.Status?.StartsWith("Generate method 'Thrice' was not applied", StringComparison.Ordinal) == true,
            $"the status bar to say the fix was not made (it says {window.Status})");
        Assert.StartsWith("Generate method 'Thrice' was not applied: the language server refused it: ", window.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Thrice(", File.ReadAllText(project.PathOf("Helper.cs")), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Fix_all_offers_its_scopes_and_fixes_every_occurrence_in_the_one_chosen()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument loop = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "int total = 0;");
        window.KeyTextInput(" int one = (int)1; int two = (int)2;");
        Ui.WaitFor(() => window.CSharpService!.For(loop)!.Diagnostics.Count(d => d.Code?.ToString() == "IDE0004") == 2, "both casts found redundant");
        Ui.PlaceAfter(editor, "(in");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Fix All: Remove Unnecessary Cast"), "the offer to remove every such cast");
        Choose(window, "Fix All: Remove Unnecessary Cast");

        Ui.WaitFor(() => window.Assist.Offered.Contains("Document"), "the scopes Fix All may cover");
        Assert.Contains("Project", window.Assist.Offered);
        Assert.Contains("Solution", window.Assist.Offered);
        Choose(window, "Document");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("int one = 1; int two = 2;", StringComparison.Ordinal), "both casts removed");
        window.Close();
    }

    [AvaloniaFact]
    public void Fix_all_in_the_solution_reaching_a_file_not_open_is_undone_in_every_file_it_changed_at_once()
    {
        string closed = project.PathOf("Casts.cs");
        string casts = File.ReadAllText(closed);
        MainWindow window = project.Open("Loop.cs");
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument loop = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "int total = 0;");
        window.KeyTextInput(" int one = (int)1;");
        string typed = editor.Document.Snapshot.ToString();
        Ui.WaitFor(() => window.CSharpService!.For(loop)!.Diagnostics.Any(d => d.Code?.ToString() == "IDE0004"), "the cast found redundant");
        Ui.PlaceAfter(editor, "(in");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Fix All: Remove Unnecessary Cast"), "the offer to remove every such cast");
        Choose(window, "Fix All: Remove Unnecessary Cast");
        Ui.WaitFor(() => window.Assist.Offered.Contains("Solution"), "the scopes Fix All may cover");
        Choose(window, "Solution");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("int one = 1;", StringComparison.Ordinal)
            && File.ReadAllText(closed).Contains("=> 3;", StringComparison.Ordinal), "the cast removed in the open file and in the one not open");

        editor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], asked);
        Assert.Equal(typed, editor.Document.Snapshot.ToString());
        string after = File.ReadAllText(closed);
        Assert.True(after == casts, $"Undo left the Fix All's change in Casts.cs, which was not open, with no way back but Local History:\n{after}");
        window.Close();
    }

    [AvaloniaFact]
    public void Through_mcp_an_action_with_choices_lists_them_asks_for_one_and_applies_the_one_named()
    {
        string source = File.ReadAllText(project.PathOf("Program.cs"));
        MainWindow window = project.Open("Program.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument program = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "Helper.Twice(2);");
            window.KeyTextInput(" Foo gadget = null;");
            Ui.WaitFor(() => window.CSharpService!.For(program)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
            Passenger.App.Mcp.PassengerTools tools = new(window);
            int column = editor.Document.Snapshot.GetLineText(4).IndexOf("Foo", StringComparison.Ordinal) + 2;

            IReadOnlyList<string> offered = Await(tools.CodeActions("Program.cs", 5, column), "the actions");
            Assert.Contains("Generate type 'Foo' [quickfix], choices: Generate class 'Foo'; Generate nested class 'Foo'", offered);
            Task<string> unnamed = tools.ApplyCodeAction("Program.cs", 5, column, "Generate type 'Foo'");
            Ui.WaitFor(() => unnamed.IsCompleted, "the refusal");
            Assert.Contains("Generate nested class 'Foo'", Assert.IsType<ModelContextProtocol.McpException>(unnamed.Exception?.InnerException).Message, StringComparison.Ordinal);

            string applied = Await(tools.ApplyCodeAction("Program.cs", 5, column, "Generate type 'Foo'", choice: "Generate nested class 'Foo'"), "the action");
            Assert.Equal("Applied 'Generate type 'Foo'' (Generate nested class 'Foo') to 1 file", applied);
            string saved = File.ReadAllText(project.PathOf("Program.cs"));
            Assert.True(saved.Contains("    private class Foo\n    {\n    }\n}", StringComparison.Ordinal), $"Foo was not nested in Program, and saved:\n{saved}");
        }
        finally
        {
            window.Close();
            File.WriteAllText(project.PathOf("Program.cs"), source);
        }
    }

    [AvaloniaFact]
    public void Show_context_actions_says_beside_each_whether_it_is_a_quick_fix_or_a_context_action_in_riders_words()
    {
        MainWindow window = project.Open("Actions.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument actions = window.EditorArea.Active!;
        Ui.WaitFor(() => window.CSharpService!.For(actions)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Missing's error");

        Ui.PlaceAfter(editor, "Missi");
        editor.Focus();
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes for Missing");
        List<(string, string)> fixes = Shown(window);
        Assert.True(fixes.Contains(("Generate class 'Missing'", "Quick-fix")), $"The error's fix does not say it is a quick-fix: {Said(fixes)}");
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "Half(4)");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the actions for half");
        List<(string, string)> offered = Shown(window);
        // Roslyn's kinds for these are refactor.inline and refactor.
        Assert.True(offered.Contains(("Inline temporary variable", "Context action")) && offered.Contains(("Use implicit type", "Context action")),
            $"Inline temporary variable and Use implicit type do not say they are context actions: {Said(offered)}");
        Ui.Press(window, "ESCAPE");

        string[] raw = [.. fixes.Concat(offered).Select(o => o.Item2).Where(d => System.Text.RegularExpressions.Regex.IsMatch(d, @"^(quickfix|refactor|source)(\.|$)"))];
        Assert.True(raw.Length == 0, $"Show Context Actions shows the language server's own kinds beside its actions, which are no words of Rider's: {string.Join(", ", raw)}");
        window.Close();

        static List<(string, string)> Shown(MainWindow window) => [.. window.Assist.Offered.Zip(window.Assist.OfferedDetails)];

        static string Said(List<(string Title, string Detail)> shown) => string.Join("; ", shown.Select(s => $"{s.Title} [{s.Detail}]"));
    }

    [AvaloniaFact]
    public void Parameter_info_keeps_a_sentence_apart_from_a_parameter_its_documentation_names_straight_after_it()
    {
        MainWindow window = project.Open("Joins.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "=> Join(");
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => window.Assist.SignatureText?.Contains("is put between", StringComparison.Ordinal) == true, "Join's parameter info with its separator's documentation");
        string said = window.Assist.SignatureText!;
        Assert.True(said.Contains("The string to use as a separator. separator is put between values only.", StringComparison.Ordinal),
            $"Parameter Info runs the sentence into the parameter its documentation names after it, as though a word were missing:\n{said}");

        // .NET's own, when the reference pack has its documentation.
        if (!project.ReferencePackDownloaded)
        {
            Ui.Press(window, "ESCAPE");
            Ui.PlaceAfter(editor, "+ string.Join(");
            Ui.Run(window, AppActions.ParameterInfo);
            Ui.WaitFor(() => window.Assist.SignatureText?.Contains("IEnumerable<string?> values", StringComparison.Ordinal) == true, "String.Join's parameter info");
            said = window.Assist.SignatureText!;
            Assert.True(said.Contains("separator. separator is included", StringComparison.Ordinal) && !said.Contains("separator.separator", StringComparison.Ordinal),
                $"String.Join's Parameter Info reads 'separator.separator':\n{said}");
        }
        window.Close();
    }

    private static T Await<T>(Task<T> task, string what)
    {
        Ui.WaitFor(() => task.IsCompleted, what, TimeSpan.FromSeconds(30));
        return task.Result;
    }

    /// <summary>Chooses an item of the open list of choices by its words, with the keys a person would press.</summary>
    private static void Choose(MainWindow window, string item)
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

    [AvaloniaFact]
    public void Renaming_a_file_to_match_its_type_with_unsaved_edits_leaves_one_file_of_the_new_name_holding_them()
    {
        string foo = project.PathOf("Foo.cs");
        string bar = project.PathOf("Bar.cs");
        project.Write("Foo.cs", "class Bar\n{\n}\n");
        MainWindow window = project.Open("Foo.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "{");
            window.KeyTextInput(" public int Edited; ");
            Ui.PlaceAfter(editor, "class B");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Rename file to Bar.cs"), "the offer to rename the file");
            for (int i = window.Assist.Offered.ToList().IndexOf("Rename file to Bar.cs"); i > 0; i--)
            {
                Ui.Press(window, "DOWN");
            }
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => !File.Exists(foo), "Foo.cs to be renamed");

            Assert.True(File.ReadAllText(bar) == "class Bar\n{ public int Edited; \n}\n", "Bar.cs does not hold the edits made before the rename");
            Assert.True(ReferenceEquals(document, Assert.Single(window.EditorArea.Documents)), "The renamed file's tab did not move to Bar.cs");
            Assert.Equal(PathNames.Canonical(bar), document.Path);
            Assert.Equal("Bar.cs", window.EditorArea.Active?.Name);
            Assert.EndsWith("/Bar.cs", window.CSharpService?.For(document)?.Lsp?.Uri, StringComparison.Ordinal);

            window.KeyTextInput("// ");
            window.EditorArea.SaveAll();
            Assert.False(File.Exists(foo), "Saving the moved file wrote Foo.cs back");
            Assert.StartsWith("class B// ar", File.ReadAllText(bar), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            File.Delete(foo);
            File.Delete(bar);
        }
    }

    /// <summary>The hints in one window: types the code leaves out show after their names, a hint's card says what its
    /// type is, and Settings turns type name hints off and on again, parameter name hints staying; the caret steps
    /// across a hint as one place and typing at a name keeps its type after it; and a literal argument shows its
    /// parameter's name before it, the text after making room, until Settings turns them off.</summary>
    [AvaloniaFact]
    public void Type_and_parameter_name_hints_show_where_Rider_shows_them_make_room_step_as_one_place_and_go_with_Settings()
    {
        MainWindow window = project.Open("Inferred.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string code = editor.Document.Snapshot.ToString();
        int After(string text) => code.IndexOf(text, StringComparison.Ordinal) + text.Length;
        Ui.WaitFor(() => editor.InlayHints.Any(h => h.Kind == InlayHintKind.Type), "the type name hints");

        // None where the code shows the type: new List<int>(), a declared Func, a declared List.
        Assert.Equal(
            [
                (":int", After("var total")),
                (":int", After("ForEach(item")),
                ("List<int>", After("Count(new")),
                (":int", After("var (first")),
                (":string", After("var (first, second")),
            ],
            editor.InlayHints.Where(h => h.Kind == InlayHintKind.Type).Select(h => (h.Label, h.Offset)));
        Assert.Equal(["width:", "height:"], editor.InlayHints.Where(h => h.Kind == InlayHintKind.Parameter).Select(h => h.Label));

        double Step(int offset) => editor.PointOf(offset + 1).X - editor.PointOf(offset).X;
        double character = Step(After("var tot"));
        Assert.True(Step(After("var total")) > character * 3, $"'total' and ' =' stand {Step(After("var total"))} apart against a character's {character}");

        Point hint = editor.PointOf(After("var total")) + new Point(character * 1.5, editor.LineHeight / 2);
        window.MouseMove(editor.TranslatePoint(hint, window)!.Value, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the hint's hover card");
        Assert.Contains("Int32", window.Assist.HoverText, StringComparison.Ordinal);
        window.MouseMove(new Point(1, 1), Avalonia.Input.RawInputModifiers.None);
        Ui.Settle();

        int name = After("var total");
        int argument = code.IndexOf("Of(3", StringComparison.Ordinal) + "Of(".Length;
        Assert.True(window.Settings.TypeHints, "Type name hints are not on by default");
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "type name hints";
        Ui.Settle();
        Assert.Equal(SettingsDialog.EditorPage, dialog.Page);
        Assert.Contains("Show type name hints", dialog.Shown);
        dialog.Change(s => s with { TypeHints = false });
        dialog.Ok();
        Ui.Settle();
        Assert.Equal(character, Step(name), 3);
        Assert.True(Step(argument - 1) > character * 4, "Turning type name hints off took the parameter name hints too");
        dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Change(s => s with { TypeHints = true });
        dialog.Ok();
        Ui.Settle();
        Assert.True(Step(name) > character * 3, "Type name hints turned on again did not come back");

        Ui.WaitFor(() => editor.InlayHints.Any(h => h.Kind == InlayHintKind.Type && h.Offset == name), "the hint after 'total'");
        double nameEnd = editor.PointOf(name - 1).X + character;
        double Caret() => editor.PointOf(editor.Selections.Primary.Caret).X;
        Ui.PlaceAfter(editor, "var tota");

        Ui.Press(window, "Right");
        Assert.Equal(name, editor.Selections.Primary.Caret);
        Assert.Equal(nameEnd, Caret(), 1);
        Ui.Press(window, "Right");
        Assert.Equal(name, editor.Selections.Primary.Caret);
        double pastHint = Caret();
        Assert.True(pastHint > nameEnd + 20, $"Right from before the hint left the caret at {pastHint}, the name ending at {nameEnd}");
        Ui.Press(window, "Right");
        Assert.Equal(name + 1, editor.Selections.Primary.Caret);

        Ui.Press(window, "Left");
        Assert.Equal((name, pastHint), (editor.Selections.Primary.Caret, Caret()));
        Ui.Press(window, "Left");
        Assert.Equal(name, editor.Selections.Primary.Caret);
        Assert.Equal(nameEnd, Caret(), 1);
        Ui.Press(window, "Left");
        Assert.Equal(name - 1, editor.Selections.Primary.Caret);

        Ui.PlaceAfter(editor, "Area.Of");
        double parenthesis = Caret();
        Ui.Press(window, "Right");
        Assert.Equal(argument, editor.Selections.Primary.Caret);
        double beforeWidth = Caret();
        Assert.True(beforeWidth - parenthesis < 20, $"Right past the parenthesis left the caret {beforeWidth - parenthesis} on, past 'width:'");
        Ui.Press(window, "Right");
        Assert.Equal(argument, editor.Selections.Primary.Caret);
        Assert.True(Caret() - beforeWidth > 20, "Right before 'width:' did not step the caret past it");

        editor.Selections = SelectionSet.Single(name);
        window.KeyTextInput("s");
        Assert.Contains(editor.InlayHints, h => h.Kind == InlayHintKind.Type && h.Offset == name + 1 && h.Label == ":int");
        Assert.Equal(name + 1, editor.Selections.Primary.Caret);
        Assert.Equal(nameEnd + character, Caret(), 1);

        window.OpenFile(project.PathOf("Area.cs"));
        Ui.Settle();
        int literal = editor.Document.Snapshot.ToString().IndexOf("Of(3, 4)", StringComparison.Ordinal) + "Of(".Length;
        Ui.WaitFor(() => editor.InlayHints.Count == 2, "the parameter name hints");
        Assert.Equal([("width:", literal), ("height:", literal + "3, ".Length)], editor.InlayHints.Select(h => (h.Label, h.Offset)));

        character = Step(literal);
        Assert.True(Step(literal - 1) > character * 4, $"{Step(literal - 1)} against a character's {character}");

        editor.ShowParameterHints = false;
        Assert.Equal(character, Step(literal - 1), 3);
        window.Close();
    }

    [AvaloniaFact]
    public void Extend_selection_grows_through_the_code_around_the_caret_and_shrink_takes_it_back()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "            to");
        string Selected() => editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length);
        List<string> grown = [];
        for (int i = 0; i < 6; i++)
        {
            Ui.Run(window, AppActions.ExtendSelection);
            grown.Add(Selected());
        }
        Assert.Equal("total", grown[0]);
        Assert.Equal("total += i * 2", grown[1]);
        Assert.Equal("total += i * 2;", grown[2]);
        Assert.StartsWith("{", grown[3], StringComparison.Ordinal);
        Assert.StartsWith("for (", grown[4], StringComparison.Ordinal);
        Assert.StartsWith("int total = 0;", grown[5], StringComparison.Ordinal);
        Assert.EndsWith("return total;", grown[5], StringComparison.Ordinal);

        Ui.Run(window, AppActions.ShrinkSelection);
        Ui.Run(window, AppActions.ShrinkSelection);
        Assert.Equal(grown[3], Selected());
        for (int i = 0; i < 4; i++)
        {
            Ui.Run(window, AppActions.ShrinkSelection);
        }
        Assert.True(editor.Selections.Primary.IsEmpty);
        window.Close();
    }

    /// <summary>Live and postfix templates in one window: an abbreviation and Tab become a live template where it
    /// belongs (prop in a type's body, cw in a method's); a live template fills every place of a variable at once, Tab
    /// goes on and back, and a caret moved away ends it; and a postfix template after an expression and a dot wraps it
    /// where it fits, asking which expression when more than one could be meant.</summary>
    [AvaloniaFact]
    public void Live_and_postfix_templates_expand_where_they_belong_fill_their_variables_and_wrap_what_they_follow()
    {
        MainWindow window = project.Open("Point.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Text() => editor.Document.Snapshot.ToString();
        string Selected() => editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length);
        Ui.PlaceAfter(editor, "public int Y { get; }");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("prop");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        Assert.Contains("    public int Name { get; set; }", Text(), StringComparison.Ordinal);
        Assert.Equal("int", Selected());
        window.KeyTextInput("string");
        Ui.Press(window, "TAB");
        window.KeyTextInput("Label");
        Ui.Press(window, "TAB");
        Assert.False(editor.IsInSnippet);
        Assert.Contains("    public string Label { get; set; }\n}", Text(), StringComparison.Ordinal);

        window.OpenFile(project.PathOf("Loop.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("cw");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Contains("        Console.WriteLine();\n", Text(), StringComparison.Ordinal);
        Assert.Equal(Text().IndexOf("Console.WriteLine(", StringComparison.Ordinal) + "Console.WriteLine(".Length, editor.Selections.Primary.Caret);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "int total = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("for");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        Assert.Equal(3, editor.Selections.Count);
        window.KeyTextInput("row");
        Assert.Contains("        for (int row = 0; row < length; row++)\n        {\n", Text(), StringComparison.Ordinal);
        Ui.Press(window, "TAB");
        Assert.Equal("length", Selected());
        window.KeyTextInput("rows");
        Ui.Press(window, "shift TAB");
        Assert.Equal(3, editor.Selections.Count);
        Ui.Press(window, "TAB");
        Ui.Press(window, "TAB");
        Assert.False(editor.IsInSnippet);
        string loop = "        for (int row = 0; row < rows; row++)\n        {\n            \n        }\n";
        Assert.Contains(loop, Text(), StringComparison.Ordinal);
        Assert.Equal(Text().IndexOf(loop, StringComparison.Ordinal) + loop.IndexOf("            \n", StringComparison.Ordinal) + 12, editor.Selections.Primary.Caret);

        Ui.Press(window, "ENTER");
        window.KeyTextInput("thr");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        Ui.Press(window, "UP");
        Assert.False(editor.IsInSnippet, "The template went on with the caret off its variables");
        Assert.Contains("throw new InvalidOperationException();", Text(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "int total = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Enumerable.Range(0, 3).foreach");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Contains("        foreach (var item in Enumerable.Range(0, 3))\n        {\n", Text(), StringComparison.Ordinal);
        Assert.Equal("item", Selected());
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "int total = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("total.var");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Contains("        var value = total;", Text(), StringComparison.Ordinal);
        window.KeyTextInput("copy");
        Ui.Press(window, "TAB");
        Assert.Contains("        var copy = total;", Text(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "var copy = total;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int doubled = 2 * total.par");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Equal(["total", "2 * total"], window.Assist.Offered);
        Ui.Press(window, "ENTER");
        Assert.Contains("        int doubled = 2 * (total)\n", Text(), StringComparison.Ordinal);
        Ui.Press(window, "ESCAPE");
        window.KeyTextInput(" + total.if");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.DoesNotContain("if (", Text(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_live_template_ends_when_its_editor_shows_another_file_and_enter_there_breaks_the_line()
    {
        MainWindow window = project.Open("Helper.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "value * 2;");
        string helper = editor.Document.Snapshot.ToString();
        window.OpenFile(project.PathOf("Loop.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("for");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        string loop = editor.Document.Snapshot.ToString();

        Ui.Run(window, AppActions.NextTab);
        Assert.Equal(PathNames.Canonical(project.PathOf("Helper.cs")), window.EditorArea.Active?.Path);
        Ui.Press(window, "ENTER");
        int end = helper.IndexOf("value * 2;", StringComparison.Ordinal) + "value * 2;".Length;
        Assert.True(editor.Document.Snapshot.ToString() == helper.Insert(end, "\n    "),
            $"Enter in Helper.cs stepped through Loop.cs's template instead of breaking the line:\n{editor.Document.Snapshot}");
        Assert.Equal(new TextPosition(3, 4), editor.CaretPosition);
        Assert.False(editor.IsInSnippet, "Helper.cs is drawn with boxes at the places of Loop.cs's template");

        Ui.Run(window, AppActions.NextTab);
        Assert.False(editor.IsInSnippet, "Loop.cs's template took up again after its file was left");
        Assert.Equal(loop, editor.Document.Snapshot.ToString());
        window.Close();
    }

    [AvaloniaFact]
    public void Call_and_type_hierarchies_show_who_calls_a_method_and_what_derives_from_a_type()
    {
        MainWindow window = project.Open("Helper.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static int Tw");
        Task<string?> calls = window.ShowCallHierarchyAsync();
        Ui.WaitFor(() => calls.IsCompleted, "the call hierarchy");
        Assert.Null(calls.Result);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Hierarchy));
        Assert.Equal(2, window.Hierarchy.Rows.Count);
        Assert.Contains("Twice", window.Hierarchy.Rows[0].Node.Item.Name, StringComparison.Ordinal);
        Assert.Contains("Main", window.Hierarchy.Rows[1].Node.Item.Name, StringComparison.Ordinal);
        HierarchyRow caller = window.Hierarchy.Rows[1];
        Assert.Equal(PathNames.Canonical(project.PathOf("Program.cs")), caller.Node.Path);
        Assert.Equal(4, caller.Node.Where.Line);
        TreeKeysCheck.Press(window, window.Hierarchy.Rows, window.Hierarchy.Rows[0], "the Hierarchy window", enterOpens: false);
        Ui.Press(window, "Right");
        Ui.Press(window, "Down");
        Ui.Press(window, "Enter");
        Assert.True(window.EditorArea.Active?.Path == caller.Node.Path, "Enter on a caller in the Hierarchy window did not go to its call");

        Task callees = window.Hierarchy.ChooseAsync(first: false);
        Ui.WaitFor(() => callees.IsCompleted, "the callees");
        Assert.False(window.Hierarchy.IsFirst);

        window.OpenFile(project.PathOf("Shapes.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "interface ISh");
        Task<string?> types = window.ShowTypeHierarchyAsync();
        Ui.WaitFor(() => types.IsCompleted, "the type hierarchy");
        Task subtypes = window.Hierarchy.ChooseAsync(first: false);
        Ui.WaitFor(() => subtypes.IsCompleted, "the subtypes");
        Assert.Contains(window.Hierarchy.Rows, r => r.Depth == 1 && r.Node.Item.Name.Contains("Square", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>The server's completion list is read where its answer arrives, never on the UI thread: a list of
    /// hundreds more items (a file with the usings a project's implicit ones bring, against one with none) costs the UI
    /// thread, from the key to the list open, less than a third of what reading those items takes, counted rather than
    /// timed. The window starts a server of its own: the class's warm server, once another test had it index the types
    /// of namespaces not imported, lists them in both files, each list cut at the server's thousand.</summary>
    [AvaloniaFact]
    public void A_statements_completion_list_is_read_off_the_UI_thread()
    {
        string bare = project.PathOf("Bare.cs");
        string used = project.PathOf("Used.cs");
        MainWindow window = Ui.OpenFolder(project.Folder, autosaves: false);
        window.OpenFile(bare);
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        (int fewItems, long fewRead, long fewFrame) = StatementCompletion(window, bare);
        (int manyItems, long manyRead, long manyFrame) = StatementCompletion(window, used);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{fewItems} items: read in {fewRead / 1024} KB, opened from the key in {fewFrame / 1024} KB; {manyItems} items: {manyRead / 1024} KB, {manyFrame / 1024} KB");
        Assert.True(manyItems > fewItems + 300, $"With the usings a statement's completion listed {manyItems} items, against {fewItems} without");
        Assert.True(manyFrame - fewFrame < (manyRead - fewRead) / 3,
            $"A list of {manyItems} items opened with {manyFrame / 1024} KB allocated on the UI thread from the key, against {fewFrame / 1024} KB for {fewItems} items: "
            + $"a third or more of the {(manyRead - fewRead) / 1024} KB reading the extra items takes, so they were read there");
        window.Close();
    }

    /// <summary>A statement's completion in a file: how many items it lists, what reading its answer allocates (the
    /// same list asked for from the thread pool, the window's thread waiting), and what the UI thread allocates from
    /// the key until the list is open, whichever of its frames the answer lands in; the least of three.</summary>
    private static (int Items, long Read, long Frame) StatementCompletion(MainWindow window, string path)
    {
        window.OpenFile(path);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "        int total = 0;");
        window.KeyTextInput(" ");
        editor.Focus();
        Ui.Settle();
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;
        int caret = editor.Selections.Primary.Caret;
        long read = long.MaxValue;
        int items = 0;
        for (int i = 0; i < 3; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            Passenger.Lsp.CompletionList? list = Task.Run(() => lsp.CompletionAsync(caret, null)).GetAwaiter().GetResult().List;
            read = Math.Min(read, GC.GetTotalAllocatedBytes(precise: true) - before);
            items = list?.Items.Count ?? 0;
        }
        long frame = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput("S");
            Ui.Pumped(() => window.Assist.IsCompletionOpen, TimeSpan.FromSeconds(60));
            frame = Math.Min(frame, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(window.Assist.IsCompletionOpen, $"Completion never opened for a statement in {Path.GetFileName(path)}");
            Ui.Press(window, "ESCAPE");
            Ui.Press(window, "BACK_SPACE");
        }
        return (items, read, frame);
    }

    /// <summary>Typing on into an open completion list narrows it off the UI thread and shows it in the rows the list
    /// has: as each letter narrows a statement's list, the controls on screen are the ones that showed it before, their
    /// words changed in place; and a key into a list of hundreds more items (a file with the usings a project's
    /// implicit ones bring, against one with none) costs the UI thread about what it does in the shorter one, counted
    /// rather than timed.</summary>
    [AvaloniaFact]
    public void Typing_into_a_completion_list_narrows_it_off_the_UI_thread_in_the_rows_it_shows()
    {
        MainWindow window = Ui.OpenFolder(project.Folder, autosaves: false);
        window.OpenFile(project.PathOf("Bare.cs"));
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        (int fewItems, long fewKey) = Narrowed(window, project.PathOf("Bare.cs"));
        (int manyItems, long manyKey) = Narrowed(window, project.PathOf("Used.cs"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key narrowing a statement's list of {fewItems} items allocated {fewKey / 1024} KB on the UI thread; of {manyItems} items, {manyKey / 1024} KB");
        Assert.True(manyItems > fewItems + 300, $"With the usings a statement's completion listed {manyItems} items, against {fewItems} without");
        // The server's thousand is a list it says is incomplete, asked for again at each key and gathered with the templates.
        Assert.True(manyKey <= fewKey + (32 * 1024),
            $"A key narrowing a list of {manyItems} items allocated {manyKey / 1024} KB on the UI thread, against {fewKey / 1024} KB for {fewItems} items: it scored them or made their rows there");
        window.Close();
    }

    /// <summary>A statement's list in a file, opened by an S and narrowed a letter at a time: how many items it has, and the
    /// least the UI thread allocates from a key to the narrower list shown; each key's rows shown in the controls the list
    /// showed before it.</summary>
    private static (int Items, long Key) Narrowed(MainWindow window, string path)
    {
        window.OpenFile(path);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "        int total = 0;");
        window.KeyTextInput(" ");
        editor.Focus();
        Ui.Settle();
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;
        int caret = editor.Selections.Primary.Caret;
        int items = Task.Run(() => lsp.CompletionAsync(caret, null)).GetAwaiter().GetResult().List?.Items.Count ?? 0;
        window.KeyTextInput("S");
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "S", $"a statement's list in {Path.GetFileName(path)}");
        string typed = "S";
        long key = long.MaxValue;
        foreach (string letter in new[] { "y", "s", "t" })
        {
            IReadOnlyList<Avalonia.Controls.Control> shown = window.Assist.CompletionRowControls;
            Assert.True(shown.Count > 0, $"No row of the list is on screen narrowed by '{typed}'");
            typed += letter;
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput(letter);
            Ui.Pumped(() => window.Assist.CompletionNarrowedBy == typed, TimeSpan.FromSeconds(30));
            key = Math.Min(key, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(window.Assist.CompletionNarrowedBy == typed, $"The list never showed its rows for '{typed}'");
            Assert.True(window.Assist.CompletionLabels.FirstOrDefault()?.StartsWith(typed, StringComparison.OrdinalIgnoreCase) == true,
                $"Narrowed by '{typed}' the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(5))} first");
            Ui.Settle();
            IReadOnlyList<Avalonia.Controls.Control> now = window.Assist.CompletionRowControls;
            Assert.True(now.Count > 0 && now.All(row => shown.Contains(row)),
                $"Narrowed by '{typed}', {now.Count(row => !shown.Contains(row))} of the list's {now.Count} rows on screen are controls made for the key");
        }
        Ui.Press(window, "ESCAPE");
        return (items, key);
    }

    /// <summary>A small console project on disk, restored once for every test, and the warm server they share on it.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("assist-")
        {
            File.WriteAllText(PathOf("Sample.csproj"),
                TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), "class Program\n{\n    static void Main()\n    {\n        int result = Helper.Twice(2);\n    }\n}\n");
            File.WriteAllText(PathOf("Helper.cs"), "static class Helper\n{\n    public static int Twice(int value) => value * 2;\n}\n");
            File.WriteAllText(PathOf("Shapes.cs"), "interface IShape\n{\n    int Sides { get; }\n}\n\nclass Square : IShape\n{\n    public int Sides => 4;\n}\n");
            File.WriteAllText(PathOf("Area.cs"), "static class Area\n{\n    public static int Of(int width, int height) => width * height;\n    public static int Room() => Of(3, 4);\n}\n");
            File.WriteAllText(PathOf("Point.cs"), "class Point\n{\n    public int X { get; }\n    public int Y { get; }\n}\n");
            File.WriteAllText(PathOf("Loop.cs"), "static class Loop\n{\n    public static int Sum()\n    {\n        int total = 0;\n        for (int i = 0; i < 10; i++)\n        {\n            total += i * 2;\n        }\n        return total;\n    }\n}\n");
            // Roslyn ends a line at each of these in the comment; the editor's lines end only at '\n'.
            File.WriteAllText(PathOf("Separators.cs"), "static class Separators\n{\n    /* Pasted:\u2028a line separator,\u2029a paragraph separator,\u0085a next line,\ra carriage return. */\n    public static int Value()\n    {\n        int total = 1;\n          return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Separated.cs"), $"interface ISeparated\n{{\n    int Value();\n}}\n\n/* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\n"
                + "class FirstSeparated : ISeparated\n{\n    public int Value() => 1;\n}\n\nclass SecondSeparated : ISeparated\n{\n    public int Value() => Separators.Value();\n}\n");
            File.WriteAllText(PathOf("Crlf.cs"), "static class Crlf\r\n{\r\n    public static int One()\r\n    {\r\n          return 1;\r\n    }\r\n}\r\n");
            // Each file under Breaks the server writes with the other line break than its own, on any system.
            Directory.CreateDirectory(PathOf("Breaks"));
            File.WriteAllText(PathOf("Breaks/.editorconfig"), "root = true\n\n[LfFile.cs]\nend_of_line = crlf\n\n[CrlfFile.cs]\nend_of_line = lf\n");
            File.WriteAllText(PathOf("Breaks/LfFile.cs"), "class LfFile\n{\n    public int X { get; }\n\n    static void Make()\n    {\n        int made = 0;\n    }\n}\n");
            File.WriteAllText(PathOf("Breaks/CrlfFile.cs"), "class CrlfFile\r\n{\r\n    public int X { get; }\r\n\r\n    static void Make()\r\n    {\r\n        int made = 0;\r\n    }\r\n}\r\n");
            // Tabs: an .editorconfig asks for tabs. Detected: no .editorconfig says, and each file's own lines do.
            Directory.CreateDirectory(PathOf("Tabs"));
            File.WriteAllText(PathOf("Tabs/.editorconfig"), "[*.cs]\nindent_style = tab\n");
            File.WriteAllText(PathOf("Tabs/Tabbed.cs"), "class Tabbed\n{\n\tpublic int X { get; }\n\n\tstatic void Make()\n\t{\n\t\tint made = 0;\n\t}\n}\n");
            Directory.CreateDirectory(PathOf("Detected"));
            File.WriteAllText(PathOf("Detected/TwoSpaces.cs"), "class TwoSpaces\n{\n  public int X { get; }\n\n  static void Make()\n  {\n    int made = 0;\n    if (made > 0)\n    {\n      made--;\n    }\n  }\n}\n");
            File.WriteAllText(PathOf("Detected/TabsOwn.cs"), "class TabsOwn\n{\n\tpublic int X { get; }\n\n\tstatic void Make()\n\t{\n\t\tint made = 0;\n\t}\n}\n");
            File.WriteAllText(PathOf("Inferred.cs"), "using System;\nusing System.Collections.Generic;\n\nstatic class Inferred\n{\n    public static int Run()\n    {\n"
                + "        var list = new List<int>();\n        var total = Area.Of(3, 4);\n        Func<int, int> square = n => n * n;\n"
                + "        list.ForEach(item => total += square(item));\n        List<int> kept = new();\n        total += Count(new());\n"
                + "        var (first, second) = (total, \"two\");\n        return first + second.Length + kept.Count;\n    }\n\n"
                + "    private static int Count(List<int> values) => values.Count;\n}\n");
            File.WriteAllText(PathOf("Messy.cs"), "using System.Text;\nusing System;\nusing System.Collections.Generic;\n\nstatic class Messy\n{\n    public static int Count()\n    {\n          List<string> names = [];\n        Console.WriteLine(names.Count);\n      return names.Count;\n    }\n}\n");
            // Each one test's own, which no other file uses: written by the test, it cost the warm server before and after it.
            File.WriteAllText(PathOf("Pairs.cs"), "class Counter(int start)\n{\n    public int Start => start;\n}\n\nrecord Pair(int Left, int Right)\n{\n"
                + "    public static int Sum(Pair pair) => pair.Left + new Counter(pair.Right).Start;\n}\n");
            File.WriteAllText(PathOf("Mixed.cs"), "static class Mixed\n{\n    /* Pasted:\u2028one,\r\ntwo,\u0085three. */\n    public static int Value()\n    {\n        int total = 1;\n          return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Halves.cs"), "static class Halves\n{\n    /* one,\r\ntwo,\r\nthree. */\n    public static int Value()\n    {\n        int total = 1;\n        return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Average.cs"), "using System.Collections.Generic;\n\nstatic class Average\n{\n    public static double Of(IEnumerable<int> values)\n    {\n"
                + "        int total = 0;\n        int count = 0;\n        foreach (int value in values)\n        {\n            total += value;\n            count++;\n        }\n"
                + "        return total / count;\n    }\n}\n");
            File.WriteAllText(PathOf("Casts.cs"), "static class Casts\n{\n    public static int Three() => (int)3;\n}\n");
            File.WriteAllText(PathOf("Wide.cs"), "static class Wide\n{\n    public static int Four()\n    {\n        return Area.Of(2, 2);\n    }\n}\n", Encoding.Unicode);
            File.WriteAllText(PathOf("Actions.cs"), "static class Actions\n{\n    public static int Go()\n    {\n        int half = Half(4);\n"
                + "        Missing gone = null;\n        return half;\n    }\n\n    private static int Half(int value) => value / 2;\n}\n");
            // Documented as .NET documents String.Join(String, IEnumerable<String>): no space between the full stop and the paramref.
            File.WriteAllText(PathOf("Joins.cs"), "using System.Collections.Generic;\n\nstatic class Joins\n{\n"
                + "    /// <summary>Joins the values.</summary>\n"
                + "    /// <param name=\"separator\">The string to use as a separator.<paramref name=\"separator\"/> is put between values only.</param>\n"
                + "    /// <param name=\"values\">The values to join.</param>\n"
                + "    public static string Join(string separator, IEnumerable<string> values) => string.Join(separator, values);\n\n"
                + "    public static string Both(List<string> names) => Join(\", \", names) + string.Join(\", \", names);\n}\n");
            // A statement's completion with no usings, and with the usings a project's implicit ones bring.
            const string Statement = "\nstatic class Statement\n{\n    public static int Sum()\n    {\n        int total = 0;\n        return total;\n    }\n}\n";
            File.WriteAllText(PathOf("Bare.cs"), Statement.Replace("Statement", "Bare", StringComparison.Ordinal));
            File.WriteAllText(PathOf("Used.cs"), "using System;\nusing System.Collections.Generic;\nusing System.IO;\nusing System.Linq;\nusing System.Net.Http;\n"
                + "using System.Threading;\nusing System.Threading.Tasks;\n" + Statement.Replace("Statement", "Used", StringComparison.Ordinal));
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
            Prepare();
        }

        /// <summary>Whether restore downloaded .NET's reference pack, as it does when no installed SDK has the version the
        /// newest SDK targets for net10.0: the package has no IntelliSense files beside its assemblies, which the SDK's own
        /// copy has, so no documentation shows for .NET's types.</summary>
        public bool ReferencePackDownloaded =>
            File.ReadAllText(PathOf("obj/project.assets.json")).Contains("\"name\": \"Microsoft.NETCore.App.Ref\"", StringComparison.Ordinal);
    }
}

/// <summary>How soon the window answers while the language server's longest lists land, on the code help's project with
/// thousands of usages and implementations written into it: held to a time, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class CodeAssistResponsivenessTests : IClassFixture<CodeAssistTests.Project>
{
    private readonly CodeAssistTests.Project project;

    public CodeAssistResponsivenessTests(CodeAssistTests.Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>The window takes keys while Find Usages lists ten thousand usages and while Derived Symbols lists four
    /// thousand implementations, each with its line of code read from its closed file. Both lists' files are written at
    /// once, so one server loads them.</summary>
    [AvaloniaFact]
    public void The_window_takes_keys_while_find_usages_lists_ten_thousand_usages_and_derived_symbols_four_thousand_implementations()
    {
        for (int file = 0; file < 4; file++)
        {
            StringBuilder text = new($"static class Uses{file}\n{{\n    public static int All()\n    {{\n        int total = 0;\n");
            for (int call = 0; call < 2500; call++)
            {
                text.Append("        total += Helper.Twice(").Append(call).Append(");\n");
            }
            project.Write($"Uses{file}.cs", text.Append("        return total;\n    }\n}\n").ToString());
        }
        project.Write("Notes.txt", string.Empty);
        project.Write("Measure.cs", "interface IMeasure\n{\n    int Size();\n}\n");
        for (int file = 0; file < 4; file++)
        {
            StringBuilder text = new();
            for (int type = 0; type < 1000; type++)
            {
                text.Append("sealed class Measure").Append(file).Append('_').Append(type).Append(" : IMeasure\n{\n    public int Size() => ").Append(type).Append(";\n}\n");
            }
            project.Write($"Measures{file}.cs", text.ToString());
        }
        MainWindow window = project.Open("Helper.cs");
        window.OpenFile(project.PathOf("Notes.txt"));
        window.OpenFile(project.PathOf("Helper.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Twi");
        // The server has the folder loaded before the window is timed, which is the server's work, not the window's.
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, "the language server");
        Ui.Settle();
        FindView find = window.Find;
        TimeSpan longest;
        using (InputWait waits = new())
        {
            Ui.Run(window, AppActions.FindUsages);
            Ui.PumpUntil(() => find.Shown == "Usages of Twice" && find.Rows.Count(r => r.Usage is not null) == 10_002, "the usages in the Find window", TimeSpan.FromMinutes(2));
            waits.Stop();
            longest = waits.Longest;
        }
        Assert.True(longest < TimeSpan.FromMilliseconds(250),
            $"the window answered no input for {longest.TotalMilliseconds:F0} ms while Find Usages listed {find.Rows.Count(r => r.Usage is not null)} usages");

        window.OpenFile(project.PathOf("Measure.cs"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "int Si");
        Ui.Settle();
        using (InputWait waits = new())
        {
            Ui.Run(window, AppActions.DerivedSymbols);
            Ui.PumpUntil(() => window.Assist.Offered.Count == 4000, "the four thousand implementations to choose from", TimeSpan.FromMinutes(2));
            waits.Stop();
            longest = waits.Longest;
        }
        Assert.True(longest < TimeSpan.FromMilliseconds(250),
            $"the window answered no input for {longest.TotalMilliseconds:F0} ms while Derived Symbols listed {window.Assist.Offered.Count} implementations");
        Ui.Press(window, "ESCAPE");
        window.Close();
    }
}
