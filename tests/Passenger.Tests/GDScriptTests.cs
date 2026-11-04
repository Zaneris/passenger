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
/// another program's writes are kept, as is what the person's own editor (a stand-in) makes there, an editor on a project
/// whose path begins with this one's being none on it, and the editor is let go once no script is open; and, as a person's window opens a
/// script, none is started until something Passenger's own reading has no answer for asks for it. Without a Godot (GODOT, the PATH,
/// Applications) each test skips saying why. A Godot editor of its own each, so run alone.</summary>
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

    /// <summary>A C# game's script keeps no .uid as the headless editor, past its start work, reloads a build of the game:
    /// .NET's tools in it reload the built assembly within a second and make a .uid beside each C# script with none, which is
    /// taken away as one it makes as it starts is; a .uid another program wrote beside a script before that build is kept.
    /// Needs Godot's .NET build.</summary>
    [AvaloniaFact]
    public void A_uid_the_headless_editor_makes_as_it_reloads_a_game_built_after_its_start_work_is_taken_away()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, "using Godot;\n\npublic partial class Main : Node\n{\n}\n",
            "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Main.cs\" id=\"1\"]\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"2\"]\n\n"
            + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Spinner\" type=\"Node2D\" parent=\".\"]\nscript = ExtResource(\"2\")\n");
        TestFolders.Write(folder, "turner.gd", "extends Node\n");
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        string uid = Path.Combine(folder, "Main.cs.uid");
        string theirs = Path.Combine(folder, "turner.gd.uid");
        string assembly = Path.Combine(folder, ".godot", "mono", "temp", "bin", "Debug", "Spinner.dll");
        string project = PathNames.Canonical(folder);
        string Log() => Ui.Shared(IdeLog.PathOf(IdeLog.Ide));
        MainWindow window = Open();
        Ui.WaitFor(() => Log().Contains($"GDScript: Godot's headless editor on {project} has done its start work", StringComparison.Ordinal),
            "the headless editor's start work done", TimeSpan.FromMinutes(2));
        Assert.False(File.Exists(uid), "The .uid the headless editor made beside Main.cs as it started was left in the project");
        File.WriteAllText(theirs, "uid://cturner1\n");

        window.Perform(AppActions.Build);
        Ui.WaitFor(() => File.Exists(assembly), () => $"the game built ({window.Status})", TimeSpan.FromMinutes(3));
        int before = Log().Split($"{Path.Combine(project, "Main.cs.uid")}, which Godot's headless editor made, is taken away").Length;
        Ui.WaitFor(() => Log().Split($"{Path.Combine(project, "Main.cs.uid")}, which Godot's headless editor made, is taken away").Length > before || File.Exists(uid),
            () => "the .uid the headless editor makes as it reloads the build", TimeSpan.FromSeconds(30));
        Ui.Hold(TimeSpan.FromSeconds(5), "a .uid made as the build is reloaded waits 3 s, for the watcher to say what came with it, before it is taken away");
        Assert.False(File.Exists(uid), "The .uid the headless editor made beside Main.cs as it reloaded the game's build was left in the project");
        Assert.True(File.Exists(theirs), "A .uid another program wrote beside turner.gd before the build was taken away as the headless editor's");
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
            GodotHeadlessAtOpen = true,
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
        Ui.WaitFor(() => Log().Contains($"{Path.Combine(project, "project.godot")} came with README.md, as one operation", StringComparison.Ordinal),
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
            GodotHeadlessAtOpen = true,
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

    /// <summary>A file the person's own Godot editor makes as it opens the project while Passenger's headless editor does its
    /// start work there (the messaging file of Godot's .NET tools, by which Passenger finds that editor) stays as that editor
    /// wrote it: nothing there is put back or taken away while the person's editor runs on the project.</summary>
    [AvaloniaFact]
    public void A_file_the_persons_own_editor_makes_as_the_headless_editor_starts_stays()
    {
        MainWindow window = Open();
        string project = PathNames.Canonical(folder);
        string meta = Passenger.Workspace.GodotIdeMessaging.MetaPath(project);
        string Log() => Ui.Shared(IdeLog.PathOf(IdeLog.Ide));
        bool Done() => Log().Contains($"GDScript: Godot's headless editor on {project} has done its start work", StringComparison.Ordinal);
        Assert.False(Done(), "The headless editor's start work was done before the person's editor could open the project during it");
        Process persons = PersonsEditor(project);
        try
        {
            Ui.WaitFor(() => MetaPort(meta) == PersonsPort, () => $"the person's editor to write its messaging file ({Shown(meta)})");
            Ui.WaitFor(Done, "the headless editor's start work done", TimeSpan.FromMinutes(2));
            Assert.True(MetaPort(meta) == PersonsPort,
                $"The messaging file the person's own editor made as the headless editor started was taken away or written over, so Passenger would not find that editor:\n{Shown(meta)}");
        }
        finally
        {
            End(persons);
            window.Close();
        }
    }

    /// <summary>A Godot editor of the person's open on another project, whose folder's path begins with this one's (a copy
    /// kept beside it), is no editor on this project: what Passenger's headless editor writes over here (its layout over
    /// the person's) is put back still.</summary>
    [AvaloniaFact]
    public void An_editor_on_a_project_whose_path_begins_with_this_ones_leaves_the_headless_editors_writes_put_back()
    {
        string project = PathNames.Canonical(folder);
        string other = project + "-old";
        TestFolders.Write(other, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Turns (old)\"\n");
        string layout = Path.Combine(project, ".godot", "editor", "editor_layout.cfg");
        string Log() => Ui.Shared(IdeLog.PathOf(IdeLog.Ide));
        bool Kept() => Log().Contains($"GDScript: {layout} was written by another program", StringComparison.Ordinal);
        Process persons = PersonsEditor(other);
        try
        {
            MainWindow window = Open();
            Ui.WaitFor(() => Log().Contains($"GDScript: {layout}, which Godot's headless editor wrote over, is put back as it was", StringComparison.Ordinal) || Kept(),
                () => $"the layout the headless editor writes over the person's to be put back ({Shown(layout)})", TimeSpan.FromSeconds(90));
            Assert.False(Kept(), $"The headless editor's layout was kept as the person's, as if their editor on {other} were one on {project}:\n{Shown(layout)}");
            Ui.WaitFor(() => File.ReadAllText(layout) == Layout, () => $"the person's own layout as it was, not the headless editor's:\n{Shown(layout)}");
            window.Close();
        }
        finally
        {
            End(persons);
        }
    }

    /// <summary>The port the stand-in for the person's editor names in its messaging file.</summary>
    private const int PersonsPort = 47113;

    /// <summary>A stand-in for the person's own Godot editor on a project, started as Godot's project manager starts the
    /// editor (<c>--path</c> the project, then <c>--editor</c>): it writes the messaging file Godot's .NET editor writes as
    /// it opens a project, naming <see cref="PersonsPort"/>, and waits until it is ended.</summary>
    private static Process PersonsEditor(string project)
    {
        string folder = TestFolders.Create("persons-godot-");
        string meta = Passenger.Workspace.GodotIdeMessaging.MetaPath(project);
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            string script = TestFolders.Write(folder, "godot.ps1", $"New-Item -ItemType Directory -Force -Path '{Path.GetDirectoryName(meta)}' | Out-Null\r\n"
                + $"Set-Content -Path '{meta}' -Value \"{PersonsPort}`n$PSCommandPath\"\r\nwhile ($true) {{ Start-Sleep -Seconds 1 }}\r\n");
            start = ChildProcesses.Command("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "--path", project, "--editor"], folder);
        }
        else
        {
            string script = TestFolders.Write(folder, "godot", $"#!/bin/sh\nmkdir -p '{Path.GetDirectoryName(meta)}'\nprintf '%s\\n%s\\n' {PersonsPort} \"$0\" > '{meta}'\n"
                + "while :; do sleep 1; done\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            start = ChildProcesses.Command(script, ["--path", project, "--editor"], folder);
        }
        return ChildProcesses.Start(start);
    }

    /// <summary>The stand-in for the person's editor ended, with what it started.</summary>
    private static void End(Process persons)
    {
        int id = persons.Id;
        ChildProcesses.Kill(persons);
        Assert.True(Processes.Gone(id), $"The stand-in for the person's editor (process {id}) still runs after it was ended");
    }

    /// <summary>The port a messaging file names, or null where there is none.</summary>
    private static int? MetaPort(string meta) => File.Exists(meta) && Passenger.Workspace.GodotIdeMessaging.MetaOf(File.ReadAllText(meta)) is (int port, _) ? port : null;

    /// <summary>A file's text, or that none is there.</summary>
    private static string Shown(string path) => File.Exists(path) ? File.ReadAllText(path) : "none is there";

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
    /// <summary>GDScript's server, a headless Godot editor of about 800 MB, is let go once no GDScript has been open for a
    /// while (shortened here to a second), and starts again as a script opens, rather than lasting as long as the folder.</summary>
    [AvaloniaFact]
    public void Godots_headless_editor_is_let_go_once_no_script_is_open_and_starts_again_as_one_opens()
    {
        string? was = Environment.GetEnvironmentVariable(Passenger.App.Web.WebLanguages.GDScriptIdleVariable);
        Environment.SetEnvironmentVariable(Passenger.App.Web.WebLanguages.GDScriptIdleVariable, "1000");
        try
        {
            MainWindow window = Open();
            Assert.True(EditorsServing(folder) == 1, $"{EditorsServing(folder)} headless Godot editors serve the project's GDScript, where one was to");
            Ui.Run(window, AppActions.CloseTab);
            Ui.WaitFor(() => EditorsServing(folder) == 0, () => $"the headless Godot editor let go once no GDScript was open ({EditorsServing(folder)} still run)", TimeSpan.FromSeconds(30));

            Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => window.Session.Web?.For(document)?.Lsp is not null && window.Session.Web.StateOf(document) is null,
                () => $"Godot's server started again for {document.Name}: {window.Session.Web?.StateOf(document)} {document.Notice}", TimeSpan.FromSeconds(90));
            Assert.Equal(1, EditorsServing(folder));
            window.Close();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Passenger.App.Web.WebLanguages.GDScriptIdleVariable, was);
        }
    }

    /// <summary>A script opened starts no Godot editor where none of the person's serves the project: Passenger's own reading
    /// marks and answers it. Go to Declaration of a name that reading declares nowhere (an engine's method) asks for one,
    /// a headless editor, which serves the script from then.</summary>
    [AvaloniaFact]
    public void A_script_opened_starts_no_godot_editor_until_a_question_passengers_reading_has_no_answer_for_asks_for_one()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(EditorsServing(folder) == 0 && window.Session.Web?.For(document)?.Lsp is null,
                $"{EditorsServing(folder)} headless Godot editors started as the script opened, where none was to");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "get_tre");
            Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
            Ui.WaitFor(() => window.Session.Web?.For(document)?.Lsp is not null && window.Session.Web.StateOf(document) is null,
                () => $"Godot's server asked for by Go to Declaration of an engine method: {window.Session.Web?.StateOf(document)} {document.Notice}", TimeSpan.FromSeconds(90));
            Assert.Equal(1, EditorsServing(folder));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>How many headless Godot editors serve a project's GDScript: Godot started on it with a language server's port.</summary>
    private static int EditorsServing(string project)
    {
        string named = PathNames.Canonical(project);
        int found = 0;
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
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

    private MainWindow Open()
    {
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            StartsLanguageServer = false,
            GodotEnvironment = GodotTests.Game.Home(folder + "-home"),
            GodotHeadlessAtOpen = true,
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
