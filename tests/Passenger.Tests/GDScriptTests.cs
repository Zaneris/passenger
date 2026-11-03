using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>GDScript through Godot's own language server, in the headless Godot editor Passenger starts for a project the
/// test writes (its user folders the run's): a script is coloured, completes a Godot class's member, hovers its docs and
/// goes to a declaration, keeping the project's editor layout as it was, and its project.godot where an editor plugin
/// saves it; an error typed shows in Problems as typed, a warning with its name; and a project deleted under that editor
/// is let go of, said once; what that editor makes (a script's .uid) is taken away, a plugin runs not there from Godot 4.5,
/// and another program's writes are kept. Without a Godot (GODOT, the PATH, Applications) each test skips saying why. A Godot editor of
/// its own each, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptTests
{
    private const string Script = "extends Node2D\n\n## How fast it spins.\n@export var speed: float = 2.5\nvar counter := 0\n\n\n"
        + "func _process(delta: float) -> void:\n\tcounter += 1\n\tvar doubled := twice(counter)\n\tif doubled > 100:\n\t\tget_tree()\n\n\n"
        + "func twice(value: int) -> int:\n\treturn value * 2\n";

    /// <summary>The person's own editor layout, which the headless editor would write its own over.</summary>
    private const string Layout = "[docks]\n\nmine=1\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptTests()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests");
        folder = TestFolders.Create("gdscript-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Turns\"\n"
            + "run/main_scene=\"res://main.tscn\"\n");
        TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n");
        TestFolders.Write(folder, "main.gd", Script);
        TestFolders.Write(folder, Path.Combine(".godot", "editor", "editor_layout.cfg"), Layout);
    }

    [AvaloniaFact]
    public void A_script_is_coloured_completes_a_Godot_classs_member_hovers_its_docs_and_goes_to_a_declaration()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "func"));
        string layout = Path.Combine(folder, ".godot", "editor", "editor_layout.cfg");
        Ui.WaitFor(() => File.ReadAllText(layout) == Layout, () => $"the project's own editor layout put back over the headless editor's:\n{File.ReadAllText(layout)}");
        string card = Hover(window, editor, "get_tree");
        Assert.Contains("func Node.get_tree() -> SceneTree", card, StringComparison.Ordinal);
        Assert.Contains("Returns the SceneTree that contains this node", card.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "\t\tget_tree()");
        editor.Focus();
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Any(l => l.StartsWith("change_scene_to_file", StringComparison.Ordinal)),
            () => $"SceneTree's members after get_tree()'s dot; the list has {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
        window.KeyTextInput("change_scene_to_f");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault()?.StartsWith("change_scene_to_file", StringComparison.Ordinal) == true,
            () => $"the list narrowed to change_scene_to_file; it has {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("\t\tget_tree().change_scene_to_file(", StringComparison.Ordinal),
            () => $"the member put in; the line is {editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}");
        Ui.Press(window, "ESCAPE");


        Ui.PlaceAfter(editor, "var doubled := tw");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        int declared = Script.Split('\n').ToList().FindIndex(l => l.StartsWith("func twice", StringComparison.Ordinal));
        Ui.WaitFor(() => editor.CaretPosition.Line == declared, () => $"the caret at twice's declaration, line {declared + 1}; it is at {editor.CaretPosition.Line + 1}");
        Ui.WaitFor(() => File.ReadAllText(layout) == Layout, () => $"the project's own editor layout kept while the headless editor runs:\n{File.ReadAllText(layout)}");
        window.Close();
    }

    /// <summary>Once Godot's server serves the script, a node its scene runs a C# script on (<c>$Player</c>) completes the C#
    /// class's members, each once beside what the server lists, the card on one says it as C# declares it, and Go to
    /// Declaration opens the C# file at it: what Godot's server, which never built the C# script, cannot answer.</summary>
    [AvaloniaFact]
    public void A_nodes_csharp_scripts_members_join_godots_servers_answers_each_said_once()
    {
        TestFolders.Write(folder, "Player.cs", "using Godot;\n\npublic partial class Player : Node2D\n{\n    [Signal]\n    public delegate void HitEventHandler(int amount);\n\n    public int Health { get; set; } = 3;\n\n    public void TakeDamage(int amount)\n    {\n        Health -= amount;\n    }\n}\n");
        TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n[ext_resource type=\"Script\" path=\"res://Player.cs\" id=\"2\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Player\" type=\"Node2D\" parent=\".\"]\nscript = ExtResource(\"2\")\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "\t\tget_tree()");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("$Player");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count > 0, "the members after $Player's dot");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("TakeDamage") && window.Assist.CompletionLabels.Contains("Health") && window.Assist.CompletionLabels.Contains("Hit"),
            () => $"Player.cs's TakeDamage, Health and Hit after $Player's dot; the list has {string.Join(", ", window.Assist.CompletionLabels.Take(12))}");
        Assert.Equal(1, window.Assist.CompletionLabels.Count(l => l == "TakeDamage" || l.StartsWith("TakeDamage(", StringComparison.Ordinal)));
        window.KeyTextInput("TakeDamage");
        Ui.Press(window, "ESCAPE");
        Assert.Contains("void TakeDamage(int amount)", Hover(window, editor, "TakeDamage"), StringComparison.Ordinal);
        Ui.PlaceAfter(editor, "$Player.Take");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("Player.cs", StringComparison.Ordinal) == true && window.EditorArea.CodeEditor.CaretPosition.Line == 9,
            "Player.cs open at TakeDamage, line 10");
        window.Close();
    }

    /// <summary>Context Help on a call of Godot's engine browses its method's place on its class's page of Godot's class
    /// reference, for the version of the Godot found; on the script's own function it says there is none, browsing nothing.</summary>
    [AvaloniaFact]
    public void Context_help_on_a_call_of_godots_engine_browses_its_place_in_the_class_reference()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "\t\tget_tr");
        Ui.Press(window, Ui.Shortcut(AppActions.ContextHelp));
        Uri page = new($"https://docs.godotengine.org/en/{GodotDocs.VersionOf(godot!.Version)}/classes/class_node.html#class-node-method-get-tree");
        Ui.WaitFor(() => window.Browsed.Contains(page), () => $"get_tree's place in the class reference to be browsed ({window.Status}): {string.Join(", ", window.Browsed)}");

        Ui.PlaceAfter(editor, "var doubled := tw");
        Ui.Press(window, Ui.Shortcut(AppActions.ContextHelp));
        Ui.WaitFor(() => window.Status == Refusals.NoGodotDocumentation, () => $"Context Help on the script's own function to say it has no page ({window.Status})");
        Assert.Equal([page], window.Browsed);
        window.Close();
    }

    [AvaloniaFact]
    public void An_error_typed_shows_in_Problems_as_typed_and_a_warning_with_its_name()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Warning),
            () => $"the warning of _process's unused parameter; Problems has {string.Join("; ", problems.FileProblems.Select(p => p.Message))}");
        Problem unused = problems.FileProblems.First(p => p.Severity == ProblemSeverity.Warning);
        Assert.StartsWith("UNUSED_PARAMETER: The parameter \"delta\" is never used", unused.Message, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "\treturn value * 2");
        editor.Focus();
        window.KeyTextInput(" +");
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error),
            () => $"the error of the expression left unfinished; Problems has {string.Join("; ", problems.FileProblems.Select(p => p.Message))}");
        Problem error = problems.FileProblems.First(p => p.Severity == ProblemSeverity.Error);
        Assert.Contains("Expected expression after \"+\" operator", error.Message, StringComparison.Ordinal);
        Ui.Press(window, "BACK_SPACE");
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => !problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error),
            () => $"the error gone with what made it; Problems has {string.Join("; ", problems.FileProblems.Select(p => p.Message))}");
        window.Close();
    }

    /// <summary>A project deleted under the headless editor Passenger started for it, in that editor's first seconds, ends the
    /// putting back of what the editor writes there, said once rather than at every look.</summary>
    [AvaloniaFact]
    public void A_project_deleted_under_its_headless_editor_is_let_go_of_said_once()
    {
        MainWindow window = Open();
        string project = PathNames.Canonical(folder);
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Windows keeps a folder the editor works in, and what it holds open: the project.godot goes all the same.
            File.Delete(Path.Combine(folder, "project.godot"));
        }
        // The project as the log names it, by the path its script was opened by or its real one.
        string[] Said() => [.. Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Split('\n')
            .Where(l => l.Contains("GDScript: ", StringComparison.Ordinal) && (l.Contains(project, StringComparison.Ordinal) || l.Contains(folder, StringComparison.Ordinal)))];
        static bool Gone(string line) => line.Contains(" is gone, so what Godot's headless editor wrote there is no longer put back", StringComparison.Ordinal);
        Ui.WaitFor(() => Said().Any(Gone), () => $"the IDE's log to say {project} is gone ({string.Join(" | ", Said())})");
        Ui.Hold(TimeSpan.FromSeconds(1), "what the headless editor writes looked at again, as it is every 200 ms in its first seconds");
        // A file the deletion held a moment (Windows) may be said before; nothing is tried once the going is said.
        string[] said = Said();
        Assert.True(said.Count(Gone) == 1 && Gone(said[^1]),
            $"The project's going was to be said once, and nothing tried there after; the log says:\n{string.Join('\n', said)}");
        window.Close();
    }

    /// <summary>A project whose editor plugin saves the project's settings as it loads (as Dialogic's does), and again well
    /// after, keeps its project.godot byte for byte, where the headless editor's save writes what it holds (its header,
    /// features, the .NET editor's assembly name): from Godot 4.5 that editor runs in recovery mode, where the plugin never
    /// runs; before it, its first save is put back. A save of the person's in Passenger meanwhile is theirs and stays.</summary>
    [AvaloniaFact]
    public void A_projects_settings_an_editor_plugin_would_save_in_its_headless_editor_stay_as_they_were()
    {
        string settings = Path.Combine(folder, "project.godot");
        TestFolders.Write(folder, "project.godot", File.ReadAllText(settings) + "\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/saver/plugin.cfg\")\n");
        TestFolders.Write(folder, Path.Combine("addons", "saver", "plugin.cfg"), "[plugin]\n\nname=\"Saver\"\ndescription=\"Saves the settings.\"\n"
            + "author=\"Tests\"\nversion=\"1.0\"\nscript=\"plugin.gd\"\n");
        TestFolders.Write(folder, Path.Combine("addons", "saver", "plugin.gd"), "@tool\nextends EditorPlugin\n\n\nfunc _enter_tree() -> void:\n"
            + "\tProjectSettings.save()\n\tFileAccess.open(\"res://.godot/saver_saved\", FileAccess.WRITE).store_string(\"saved\")\n"
            + "\tawait get_tree().create_timer(20.0).timeout\n\tProjectSettings.set_setting(\"application/config/description\", \"saved later\")\n\tProjectSettings.save()\n");
        byte[] before = File.ReadAllBytes(settings);
        string marker = Path.Combine(folder, ".godot", "saver_saved");
        bool recovering = GodotExecutables.HasRecoveryMode(Ui.Await(GodotExecutables.VersionAsync(godot!), "the Godot's version"));
        MainWindow window = Open();
        if (recovering)
        {
            Ui.Hold(TimeSpan.FromSeconds(25), "the plugin's saves, as it loads and 20 s after, were it to run in the headless editor");
            Assert.False(File.Exists(marker), "The project's editor plugin ran in Passenger's headless editor, which from Godot 4.5 runs in recovery mode");
        }
        else
        {
            Ui.WaitFor(() => File.Exists(marker), "the plugin's save of the project's settings in the headless editor", TimeSpan.FromSeconds(60));
        }
        Ui.WaitFor(() => File.ReadAllBytes(settings).AsSpan().SequenceEqual(before),
            () => $"project.godot as it was, not as the headless editor's plugin saved it:\n{File.ReadAllText(settings)}");

        Assert.Null(window.OpenFile(settings));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "config/name=\"Turns");
        editor.Focus();
        window.KeyTextInput("s");
        Ui.Run(window, AppActions.SaveAll);
        Ui.Hold(TimeSpan.FromSeconds(4), "what the headless editor wrote looked at again and put back, as it is 3 s after it is found in the editor's start work");
        Assert.True(File.ReadAllText(settings).Contains("config/name=\"Turnss\"", StringComparison.Ordinal),
            $"The person's own save of project.godot was put back over as if the headless editor had written it:\n{File.ReadAllText(settings)}");
        window.Close();
    }

    /// <summary>A project of Godot 4.3 or older, whose script has no .uid, keeps none: the .uid the headless editor makes beside
    /// it as it scans the project is taken away, the person's own Godot making its own as it opens the project.</summary>
    [AvaloniaFact]
    public void A_uid_the_headless_editor_makes_beside_a_script_of_an_older_project_is_taken_away()
    {
        string uid = Path.Combine(folder, "main.gd.uid");
        Assert.False(File.Exists(uid), "The test's project of Godot 4.3 has a .uid already");
        MainWindow window = Open();
        string named = Path.Combine(Path.GetFileName(folder), "main.gd.uid");
        Ui.WaitFor(() => Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Contains($"{named}, which Godot's headless editor made, is taken away", StringComparison.Ordinal),
            () => $"the .uid the headless editor makes beside main.gd to be taken away ({(File.Exists(uid) ? "it is there" : "none is there")})", TimeSpan.FromSeconds(90));
        Ui.Hold(TimeSpan.FromSeconds(2), "the headless editor's scan to go on, a .uid made again taken away as it comes");
        Assert.False(File.Exists(uid), "The .uid the headless editor made beside main.gd was left in the project");
        window.Close();
    }

    /// <summary>project.godot replaced whole by another program, as Git's checkout writes a file, is kept: as the headless
    /// editor starts, where the folder's watcher saw it come with another file, as one operation brings them; and later,
    /// alone, once the editor's start work is done, after which nothing written there is put back.</summary>
    [AvaloniaFact]
    public void Project_settings_replaced_whole_by_another_program_as_a_checkout_does_are_kept()
    {
        string settings = Path.Combine(folder, "project.godot");
        string incoming = TestFolders.Create("gdscript-incoming-");
        string project = PathNames.Canonical(folder);
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            StartsLanguageServer = false,
            GodotEnvironment = GodotTests.Game.Home(folder + "-home"),
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
        OpenDocument document = window.EditorArea.Active!;
        Ui.PumpUntil(() => HeadlessEditorsOn(folder) > 0, "Passenger's headless Godot editor to start on the project", TimeSpan.FromSeconds(60));
        Assert.True(window.Session.Web?.For(document)?.Lsp is null, "Godot's server answered before the test could write as it starts");
        // As the server starts, a checkout: a new project.godot and a file beside it, each written whole and moved into its place.
        Replace(settings, File.ReadAllText(settings) + "\n[passenger_test]\n\nchecked_out=true\n");
        Replace(Path.Combine(folder, "README.md"), "# Turns\n");
        string Log() => Ui.Shared(IdeLog.PathOf(IdeLog.Ide));
        Ui.WaitFor(() => Log().Contains($"{Path.Combine(project, "project.godot")} came with 1 other file, as one operation", StringComparison.Ordinal),
            () => $"project.godot taken for the checkout's, as the watcher saw README.md come with it:\n{File.ReadAllText(settings)}", TimeSpan.FromSeconds(30));
        Ui.WaitFor(() => window.Session.Web?.For(document)?.Lsp is not null && window.Session.Web.StateOf(document) is null,
            () => $"Godot's server for {document.Name}: {window.Session.Web?.StateOf(document)} {document.Notice}", TimeSpan.FromSeconds(90));
        Ui.WaitFor(() => Log().Contains($"GDScript: Godot's headless editor on {project} has done its start work", StringComparison.Ordinal),
            "the headless editor's start work done, past which nothing written in the project is put back", TimeSpan.FromMinutes(2));
        Assert.True(File.ReadAllText(settings).Contains("checked_out=true", StringComparison.Ordinal),
            $"project.godot as a checkout wrote it with another file as the headless editor started was put back over:\n{File.ReadAllText(settings)}");

        // Mid-session, alone: a pull that changes project.godot only.
        Replace(settings, File.ReadAllText(settings) + "\n[passenger_pulled]\n\npulled=true\n");
        Ui.Hold(TimeSpan.FromSeconds(5), "a change found waits 3 s before it is put back, were anything still putting back");
        Assert.True(File.ReadAllText(settings).Contains("pulled=true", StringComparison.Ordinal),
            $"project.godot replaced whole by another program mid-session was put back over:\n{File.ReadAllText(settings)}");
        window.Close();

        // A file written whole beside the project, then moved over the one in it: a new file in its place, as Git's checkout leaves it.
        void Replace(string path, string text)
        {
            string written = Path.Combine(incoming, Path.GetFileName(path));
            File.WriteAllText(written, text);
            File.Move(written, path, overwrite: true);
        }
    }

    /// <summary>project.godot written in place by another program as the headless editor starts is the person's and is kept,
    /// never put back as if the editor had written it: the editor's own saves replace the file, a write in place does not. On
    /// Windows, which does not tell the two apart, the test skips.</summary>
    [AvaloniaFact]
    public void Project_settings_another_program_writes_as_the_headless_editor_starts_are_kept()
    {
        Assert.SkipUnless(FileIdentity.Told, "Windows tells no file replaced from one written in place");
        string settings = Path.Combine(folder, "project.godot");
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            StartsLanguageServer = false,
            GodotEnvironment = GodotTests.Game.Home(folder + "-home"),
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
        OpenDocument document = window.EditorArea.Active!;
        Ui.PumpUntil(() => HeadlessEditorsOn(folder) > 0, "Passenger's headless Godot editor to start on the project", TimeSpan.FromSeconds(60));
        Assert.True(window.Session.Web?.For(document)?.Lsp is null, "Godot's server answered before the test could write as it starts");
        // As the server starts: another program adds a setting of its own, in place.
        File.AppendAllText(settings, "\n[passenger_test]\n\nkept=true\n");
        Ui.WaitFor(() => window.Session.Web?.For(document)?.Lsp is not null && window.Session.Web.StateOf(document) is null,
            () => $"Godot's server for {document.Name}: {window.Session.Web?.StateOf(document)} {document.Notice}", TimeSpan.FromSeconds(90));
        Ui.Hold(TimeSpan.FromSeconds(12), "the headless editor's first seconds, whose writes over the project's files are put back, to pass");
        Assert.True(File.ReadAllText(settings).Contains("kept=true", StringComparison.Ordinal),
            $"The setting another program wrote in place as the headless editor started was put back over:\n{File.ReadAllText(settings)}");
        window.Close();
    }

    /// <summary>How many of Passenger's headless Godot editors run on a folder: Godot started on it with a server's port.</summary>
    private static int HeadlessEditorsOn(string project)
    {
        string named = PathNames.Canonical(project);
        int found = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && DotnetProcesses.Describe(process.Id) is { } running
                    && running.CommandLine.Contains("--lsp-port", StringComparison.Ordinal) && running.CommandLine.Contains(named, StringComparison.Ordinal))
                {
                    found++;
                }
            }
        }
        return found;
    }

    /// <summary>The project's folder open on its script, once Godot's server answers for it.</summary>
    private MainWindow Open()
    {
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            StartsLanguageServer = false,
            GodotEnvironment = GodotTests.Game.Home(folder + "-home"),
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => window.Session.Web?.For(document)?.Lsp is not null && window.Session.Web.StateOf(document) is null,
            () => $"Godot's server for {document.Name}: {window.Session.Web?.StateOf(document)} {document.Notice}", TimeSpan.FromSeconds(90));
        return window;
    }

    /// <summary>The card shown with the pointer resting on some text of the editor's.</summary>
    private static string Hover(MainWindow window, CodeEditor editor, string text)
    {
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, $"the card on {text}");
        return window.Assist.HoverText!;
    }

    private static TokenKind? KindOf(CodeEditor editor, string stretch)
    {
        TextSnapshot text = editor.Document.Snapshot;
        int start = text.ToString().IndexOf(stretch, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{stretch}' is not in the text");
        return editor.Highlighter!.GetSpans(text, start, stretch.Length).Where(s => s.Start <= start && s.End > start).Select(s => (TokenKind?)s.Kind).FirstOrDefault();
    }
}
