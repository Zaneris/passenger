using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>GDScript through Godot's own language server, in the headless Godot editor Passenger starts for a project
/// the test writes (its user folders the run's): a script is coloured, completes a Godot class's member, hovers its
/// docs and goes to a declaration, keeping the project's editor layout as it was, and its project.godot where an editor
/// plugin saves it; an error typed shows in Problems as typed, a warning with its name; a plugin runs not there from
/// Godot 4.5, another program's writes are kept, as is what the person's own editor (a stand-in) makes there, and the
/// editor is let go once no script is open; and, as a person's window opens a script, none is started until something
/// Passenger's own reading has no answer for asks for it. Without a Godot (GODOT, the PATH, Applications) each test
/// skips saying why. A Godot editor of its own each, so run alone.</summary>
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
        TestFolders.Write(folder, Path.Combine(IgnoredFolders.GodotCache, "editor", "editor_layout.cfg"), Layout);
    }

    /// <summary>A GDScript is coloured, completed, hovered and checked by Godot's server beside Passenger's reading,
    /// its headless editor started only when asked.</summary>
    [AvaloniaFact]
    public void A_gdscript_is_read_by_godots_server_beside_passengers_own()
    {
        Flow.StepApart<GDScriptTests>(nameof(A_script_is_coloured_completes_a_Godot_classs_member_hovers_its_docs_and_goes_to_a_declaration),
            test => test.A_script_is_coloured_completes_a_Godot_classs_member_hovers_its_docs_and_goes_to_a_declaration());
        OpenWindows.CloseAll();
        Flow.StepApart<GDScriptTests>(nameof(An_error_typed_shows_in_Problems_as_typed_and_a_warning_with_its_name),
            test => test.An_error_typed_shows_in_Problems_as_typed_and_a_warning_with_its_name());
        OpenWindows.CloseAll();
        Flow.StepApart<GDScriptTests>(nameof(A_script_opened_starts_no_godot_editor_until_a_question_passengers_reading_has_no_answer_for_asks_for_one),
            test => test.A_script_opened_starts_no_godot_editor_until_a_question_passengers_reading_has_no_answer_for_asks_for_one());
        OpenWindows.CloseAll();
        Flow.StepApart<GDScriptTests>(nameof(Godots_headless_editor_is_let_go_once_no_script_is_open_and_starts_again_as_one_opens),
            test => test.Godots_headless_editor_is_let_go_once_no_script_is_open_and_starts_again_as_one_opens());
    }

    /// <summary>What Godot's headless editor writes into the project is put back, and what another program writes as it
    /// starts is kept.</summary>
    [AvaloniaFact]
    public void Godots_headless_editor_leaves_the_project_as_others_wrote_it()
    {
        Flow.StepApart<GDScriptTests>(nameof(A_projects_settings_an_editor_plugin_would_save_in_its_headless_editor_stay_as_they_were),
            test => test.A_projects_settings_an_editor_plugin_would_save_in_its_headless_editor_stay_as_they_were());
        OpenWindows.CloseAll();
        Flow.StepApart<GDScriptTests>(nameof(Project_settings_replaced_whole_by_another_program_as_a_checkout_does_are_kept),
            test => test.Project_settings_replaced_whole_by_another_program_as_a_checkout_does_are_kept());
        OpenWindows.CloseAll();
        Flow.StepApart<GDScriptTests>(nameof(Project_settings_another_program_writes_as_the_headless_editor_starts_are_kept),
            test => test.Project_settings_another_program_writes_as_the_headless_editor_starts_are_kept());
        OpenWindows.CloseAll();
        Flow.StepApart<GDScriptTests>(nameof(Project_settings_saved_through_Godots_safe_save_as_the_headless_editor_starts_are_put_back),
            test => test.Project_settings_saved_through_Godots_safe_save_as_the_headless_editor_starts_are_put_back());
        OpenWindows.CloseAll();
        Flow.StepApart<GDScriptTests>(nameof(A_file_the_persons_own_editor_makes_as_the_headless_editor_starts_stays),
            test => test.A_file_the_persons_own_editor_makes_as_the_headless_editor_starts_stays());
    }

    private void A_script_is_coloured_completes_a_Godot_classs_member_hovers_its_docs_and_goes_to_a_declaration()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "func"));
        string layout = Path.Combine(PathNames.Canonical(folder), IgnoredFolders.GodotCache, "editor", "editor_layout.cfg");
        // By the log, since until the headless editor writes its own the file holds the person's layout anyway.
        Ui.WaitFor(() => Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Contains($"GDScript: {layout}, which Godot's headless editor wrote over, is put back as it was", StringComparison.Ordinal),
            () => $"the layout the headless editor writes over the person's to be put back ({Shown(layout)})", TimeSpan.FromSeconds(90));
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

    private void An_error_typed_shows_in_Problems_as_typed_and_a_warning_with_its_name()
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

    /// <summary>A project whose editor plugin saves the project's settings as it loads (as Dialogic's does), and again well
    /// after, keeps its project.godot byte for byte, where the headless editor's save writes what it holds (its header,
    /// features, the .NET editor's assembly name): from Godot 4.5 that editor runs in recovery mode, where the plugin never
    /// runs; before it, its first save is put back. A save of the person's in Passenger meanwhile is theirs and stays.</summary>
    private void A_projects_settings_an_editor_plugin_would_save_in_its_headless_editor_stay_as_they_were()
    {
        string settings = Path.Combine(folder, "project.godot");
        TestFolders.Write(folder, "project.godot", File.ReadAllText(settings) + "\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/saver/plugin.cfg\")\n");
        TestFolders.Write(folder, Path.Combine("addons", "saver", "plugin.cfg"), "[plugin]\n\nname=\"Saver\"\ndescription=\"Saves the settings.\"\n"
            + "author=\"Tests\"\nversion=\"1.0\"\nscript=\"plugin.gd\"\n");
        TestFolders.Write(folder, Path.Combine("addons", "saver", "plugin.gd"), "@tool\nextends EditorPlugin\n\n\nfunc _enter_tree() -> void:\n"
            + "\tProjectSettings.save()\n\tFileAccess.open(\"res://.godot/saver_saved\", FileAccess.WRITE).store_string(\"saved\")\n"
            + "\tawait get_tree().create_timer(20.0).timeout\n\tProjectSettings.set_setting(\"application/config/description\", \"saved later\")\n\tProjectSettings.save()\n");
        byte[] before = File.ReadAllBytes(settings);
        string marker = Path.Combine(folder, IgnoredFolders.GodotCache, "saver_saved");
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

    /// <summary>project.godot replaced whole by another program, as Git's checkout writes a file, is kept: as the headless
    /// editor starts, where the folder's watcher saw it come with another file, as one operation brings them; and later,
    /// alone, once the editor's start work is done, after which nothing written there is put back.</summary>
    private void Project_settings_replaced_whole_by_another_program_as_a_checkout_does_are_kept()
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
    /// never put back as if the editor had written it: no word of Godot's own safe save came with it.</summary>
    private void Project_settings_another_program_writes_as_the_headless_editor_starts_are_kept()
    {
        string settings = Path.Combine(folder, "project.godot");
        (MainWindow window, OpenDocument document) = HeadlessStarting();
        // As the server starts: another program adds a setting of its own, in place.
        File.AppendAllText(settings, "\n[passenger_test]\n\nkept=true\n");
        Ui.WaitFor(() => window.Session.Web?.For(document)?.Lsp is not null && window.Session.Web.StateOf(document) is null,
            () => $"Godot's server for {document.Name}: {window.Session.Web?.StateOf(document)} {document.Notice}", TimeSpan.FromSeconds(90));
        Ui.Hold(TimeSpan.FromSeconds(12), "the headless editor's first seconds, whose writes over the project's files are put back, to pass");
        Assert.True(File.ReadAllText(settings).Contains("kept=true", StringComparison.Ordinal),
            $"The setting another program wrote in place as the headless editor started was put back over:\n{File.ReadAllText(settings)}");
        window.Close();
    }

    /// <summary>project.godot saved through Godot's own safe save, its file of a dash and six letters or digits heard beside it,
    /// is the headless editor's and put back, as another program's atomic save is not. The save is the test's own, so it is
    /// there to put back whatever the Godot found writes.</summary>
    private void Project_settings_saved_through_Godots_safe_save_as_the_headless_editor_starts_are_put_back()
    {
        string settings = Path.Combine(folder, "project.godot");
        string shown = Path.Combine(PathNames.Canonical(folder), "project.godot");
        byte[] before = File.ReadAllBytes(settings);
        (MainWindow window, _) = HeadlessStarting();
        SavedThrough(settings + "-a1B2c3", settings, File.ReadAllText(settings) + "\n[dotnet]\n\nproject/assembly_name=\"Turns\"\n");
        Ui.WaitFor(() => Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Contains($"GDScript: {shown}, which Godot's headless editor wrote over, is put back as it was", StringComparison.Ordinal),
            () => $"Godot's safe save of project.godot to be put back:\n{File.ReadAllText(settings)}", TimeSpan.FromSeconds(30));
        Assert.True(File.ReadAllBytes(settings).AsSpan().SequenceEqual(before), $"project.godot as it was, not as Godot's safe save left it:\n{File.ReadAllText(settings)}");
        window.Close();
    }

    /// <summary>A file written whole under another name beside it, then moved over it, as a safe save writes one.</summary>
    private static void SavedThrough(string temporary, string path, string text)
    {
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>A window on the project whose headless Godot editor has started and whose server has yet to answer, with the
    /// script that asked for it.</summary>
    private (MainWindow Window, OpenDocument Script) HeadlessStarting()
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
        OpenDocument document = window.EditorArea.Active!;
        Ui.PumpUntil(() => HeadlessEditorsOn(folder) > 0, "Passenger's headless Godot editor to start on the project", TimeSpan.FromSeconds(60));
        Assert.True(window.Session.Web?.For(document)?.Lsp is null, "Godot's server answered before the test could write as it starts");
        return (window, document);
    }

    /// <summary>A file the person's own Godot editor makes as it opens the project while Passenger's headless editor does its
    /// start work there (the messaging file of Godot's .NET tools, by which Passenger finds that editor) stays as that editor
    /// wrote it: nothing there is put back or taken away while the person's editor runs on the project.</summary>
    private void A_file_the_persons_own_editor_makes_as_the_headless_editor_starts_stays()
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

    /// <summary>GDScript's server, a headless Godot editor of about 800 MB, is let go once no GDScript has been open for a
    /// while (shortened here to a second), and starts again as a script opens, rather than lasting as long as the folder.</summary>
    /// <summary>On Windows, where Godot listens without sharing its port, a port another program's connection holds as the
    /// headless editor starts has the editor started again on another, which serves the script, the log saying why: the test
    /// holds the port it has Passenger tell the first editor with a connection's local end.</summary>
    [AvaloniaFact]
    public void A_port_another_programs_connection_holds_as_the_headless_editor_starts_has_it_started_again_on_another()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Godot listens with SO_REUSEADDR off Windows, so a connection's local end leaves its listen be there");
        using System.Net.Sockets.TcpListener far = new(System.Net.IPAddress.Loopback, 0);
        far.Start();
        using System.Net.Sockets.Socket holder = new(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        holder.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int held = ((System.Net.IPEndPoint)holder.LocalEndPoint!).Port;
        holder.Connect(far.LocalEndpoint);
        using System.Net.Sockets.Socket accepted = far.AcceptSocket();
        GodotPorts.GiveNext(held);
        MainWindow window = Open();
        try
        {
            string said = $"could not serve: port {held} was taken by a connection from 127.0.0.1:{held}";
            Assert.True(Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Contains(said, StringComparison.Ordinal),
                $"The log never said the headless editor was started again because port {held} was held");
            Assert.Equal(1, EditorsServing(folder));
            string card = Hover(window, window.EditorArea.CodeEditor, "get_tree");
            Assert.Contains("func Node.get_tree() -> SceneTree", card, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private void Godots_headless_editor_is_let_go_once_no_script_is_open_and_starts_again_as_one_opens()
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
    private void A_script_opened_starts_no_godot_editor_until_a_question_passengers_reading_has_no_answer_for_asks_for_one()
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

    /// <summary>The project's folder open on its script, once Godot's server answers for it.</summary>
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
