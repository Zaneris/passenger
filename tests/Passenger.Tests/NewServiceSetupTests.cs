using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>New Solution and New Project set a service up to run as chosen with it, by Add's own actions: a Dockerfile with
/// a Docker launch profile beside the template's own, its container configuration chosen; compose.yaml with it as a
/// service, its Compose configuration chosen and brought up by the first Run; a class library offered none, saying why;
/// the choice kept for the next service; and one Undo taking the whole creation back.</summary>
public sealed class NewServiceSetupTests : IDisposable
{
    private readonly string settings = TestFolders.Create("settings-");
    private readonly string place = TestFolders.Create("svc-");

    public void Dispose()
    {
        NewProjectDialog.Remembered = ProjectSetup.None;
        TestFolders.Delete(settings);
        TestFolders.Delete(place);
    }

    /// <summary>Skips a test of a Docker or Compose set-up where no docker command is installed, which New Project needs to
    /// offer one; the command is looked for off the window's thread.</summary>
    private static void SkipWithoutDockerCommand()
    {
        string? why = Ui.AwaitPumped(Waiting.Apart(() => ContainerEngine.Shared.Cli is null
            ? "No docker command is installed here, so New Project offers no Docker or Compose set-up" : null),
            "the docker command to be looked for", TimeSpan.FromMinutes(1));
        Assert.SkipWhen(why is not null, why ?? string.Empty);
    }

    /// <summary>New Project into a solution, with a Dockerfile: the project, its Dockerfile and a Docker launch profile beside
    /// the template's own are made, its container configuration chosen; a class library is offered no set-up, saying why,
    /// and the choice comes back with the next service; one Undo in the Solution view takes the project, its files and its
    /// line in the solution back, and Redo makes them again.</summary>
    [AvaloniaFact]
    public void New_Project_with_a_Dockerfile_is_chosen_to_run_in_its_container_and_one_Undo_takes_the_whole_creation_back()
    {
        SkipWithoutDockerCommand();
        TestFolders.Write(place, "Tool/Tool.csproj", TestProjects.Console());
        TestFolders.Write(place, "Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        string solution = Path.Combine(PathNames.Canonical(place), "Shop.slnx");
        byte[] before = File.ReadAllBytes(solution);
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "web");
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            Assert.True(dialog.Offers(ProjectSetup.Dockerfile), $"A web project was offered no Dockerfile: {dialog.SetupNote}");
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Dockerfile;
            Ui.Settle();
            Assert.Contains("Docker launch profile", dialog.SetupNote, StringComparison.Ordinal);

            Choose(dialog, "classlib");
            Assert.False(dialog.SetupChooser.IsEnabled, "A class library was offered a container or Aspire set-up");
            Assert.EndsWith("is not a service: containers and Aspire are set up for web, API, worker, Blazor and gRPC projects", dialog.SetupNote, StringComparison.Ordinal);
            Assert.Equal(ProjectSetup.None, dialog.Setup);
            Choose(dialog, "web");
            Assert.Equal(ProjectSetup.Dockerfile, dialog.Setup);
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            Created(dialog, "the web project");

            Assert.Null(SetUp(window));
            string api = Path.Combine(PathNames.Canonical(place), "Api");
            Assert.True(File.Exists(Path.Combine(api, "Dockerfile")), $"The new project has no Dockerfile: {window.Status}");
            string settingsFile = File.ReadAllText(Path.Combine(api, "Properties", "launchSettings.json"));
            Assert.Contains("\"Container (Dockerfile)\": {", settingsFile, StringComparison.Ordinal);
            Assert.Contains("\"commandName\": \"Docker\"", settingsFile, StringComparison.Ordinal);
            Assert.Contains("\"http\": {", settingsFile, StringComparison.Ordinal);
            Assert.True(window.Configuration is { Name: "Api: Container (Dockerfile)", Kind: RunKind.Dockerfile },
                $"The configuration chosen is {window.Configuration?.Name} ({window.Configuration?.Kind}), not the new project's container: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
            Assert.Contains("Api", window.WorkspaceModel!.Projects.Select(p => p.Name));

            List<string> asked = Answer(window);
            Ui.ClickRow(window, Row(window, NodeKind.Solution, "Shop"));
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(["Undo"], asked);
            Assert.False(Directory.Exists(api), $"Undo left the new project's folder: {window.Status}");
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(solution)), $"Undo left the solution file as {File.ReadAllText(solution)}");
            Ui.WaitFor(() => !window.WorkspaceModel!.Projects.Any(p => p.Name == "Api"), "the solution to be read without Api");

            Ui.ClickRow(window, Row(window, NodeKind.Solution, "Shop"));
            Ui.Run(window, EditorActions.Redo);
            Assert.True(File.Exists(Path.Combine(api, "Dockerfile")) && File.ReadAllText(Path.Combine(api, "Properties", "launchSettings.json")) == settingsFile,
                $"Redo did not make the project and its set-up again: {window.Status}");
            Assert.Contains("Api/Api.csproj", File.ReadAllText(solution), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>New Project with a Dockerfile while the person types in another open file as its set-up waits for the new
    /// project's evaluation: Undo in that file takes back only the typing, the new project staying, and New Project's own
    /// Undo names no such file, the typing kept.</summary>
    [AvaloniaFact]
    public void Typing_in_another_file_while_New_Project_sets_its_service_up_stays_out_of_its_Undo()
    {
        SkipWithoutDockerCommand();
        TestFolders.Write(place, "Tool/Tool.csproj", TestProjects.Console());
        TestFolders.Write(place, "Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
        const string Had = "static class Greeting\n{\n}\n";
        TestFolders.Write(place, "Tool/Greeting.cs", Had);
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        string greeting = Path.Combine(PathNames.Canonical(place), "Tool", "Greeting.cs");
        string api = Path.Combine(PathNames.Canonical(place), "Api");
        TaskCompletionSource evaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            window.OpenFile(greeting);
            Ui.Settle();
            Task held = window.Session.Models.HoldEvaluation(Path.Combine(api, "Api.csproj"), evaluated.Task);
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "web");
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Dockerfile;
            Ui.Settle();
            // Pumped, not settled: the set-up holds the window's background work while it waits for the evaluation held.
            Ui.Click(dialog.CreateButton, settle: false);
            Ui.PumpUntil(() => held.IsCompleted || dialog.Problem is not null, "the new project's evaluation to be held", TimeSpan.FromMinutes(5));
            Assert.True(dialog.Problem is null, $"The web project was not made: {dialog.Problem}");
            Assert.True(window.ServiceSetUp is { IsCompleted: false }, $"The set-up did not wait for the project's evaluation: {window.Status}");

            window.EditorArea.CodeEditor.Focus();
            Assert.Equal(greeting, window.EditorArea.Active?.Path);
            window.KeyTextInput("Typed");
            Assert.Equal("Typed" + Had, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
            evaluated.SetResult();
            Assert.Null(SetUp(window));
            Assert.True(File.Exists(Path.Combine(api, "Dockerfile")), $"The new project has no Dockerfile: {window.Status}");

            List<(string Title, string Message)> asked = [];
            window.Confirm = (title, message, _) =>
            {
                asked.Add((title, message));
                return Task.FromResult(true);
            };
            window.OpenFile(greeting);
            Ui.Settle();
            Ui.Run(window, EditorActions.Undo);
            Assert.True(asked.Count == 0, $"Undo in Greeting.cs asked {string.Join("; ", asked.Select(a => a.Message))}, as though the typing were New Project's");
            Assert.Equal(Had, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
            Assert.True(Directory.Exists(api), $"Undo of the typing took the new project away: {window.Status}");

            Ui.Run(window, EditorActions.Redo);
            Assert.Equal("Typed" + Had, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            (string title, string message) = Assert.Single(asked);
            Assert.Equal("Undo", title);
            Assert.DoesNotContain("Greeting.cs", message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(api), $"Undo left the new project's folder: {window.Status}");
            Assert.Equal("Typed" + Had, window.EditorArea.Find(greeting)?.Text.Snapshot.ToString());
        }
        finally
        {
            evaluated.TrySetResult();
            window.Close();
        }
    }

    /// <summary>New Project with a Dockerfile while the person renames a file of their own as its set-up waits for the new
    /// project's evaluation: New Project's Undo neither names nor moves that file, and the rename's own Undo then names it
    /// back, where New Project's took the rename as its own and moved the file back, its own Undo then refused.</summary>
    [AvaloniaFact]
    public void Renaming_a_file_while_New_Project_sets_its_service_up_stays_out_of_its_Undo()
    {
        SkipWithoutDockerCommand();
        TestFolders.Write(place, "Tool/Tool.csproj", TestProjects.Console());
        TestFolders.Write(place, "Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
        TestFolders.Write(place, "notes.md", "# Notes\n");
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        string notes = Path.Combine(PathNames.Canonical(place), "notes.md");
        string guide = Path.Combine(PathNames.Canonical(place), "guide.md");
        string api = Path.Combine(PathNames.Canonical(place), "Api");
        TaskCompletionSource evaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            Task held = window.Session.Models.HoldEvaluation(Path.Combine(api, "Api.csproj"), evaluated.Task);
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "web");
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Dockerfile;
            Ui.Settle();
            // Pumped, not settled: the set-up holds the window's background work while it waits for the evaluation held.
            Ui.Click(dialog.CreateButton, settle: false);
            Ui.PumpUntil(() => held.IsCompleted || dialog.Problem is not null, "the new project's evaluation to be held", TimeSpan.FromMinutes(5));
            Assert.True(dialog.Problem is null, $"The web project was not made: {dialog.Problem}");
            Assert.True(window.ServiceSetUp is { IsCompleted: false }, $"The set-up did not wait for the project's evaluation: {window.Status}");

            Assert.Null(Ui.AwaitPumped(window.RenameAsync(notes, "guide.md"), "the rename of notes.md as the set-up waits"));
            Assert.True(File.Exists(guide), $"notes.md was not renamed: {window.Status}");
            evaluated.SetResult();
            Assert.Null(SetUp(window));
            Assert.True(File.Exists(Path.Combine(api, "Dockerfile")), $"The new project has no Dockerfile: {window.Status}");

            List<(string Title, string Message)> asked = [];
            window.Confirm = (title, message, _) =>
            {
                asked.Add((title, message));
                return Task.FromResult(true);
            };
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            (string title, string message) = Assert.Single(asked);
            Assert.Equal("Undo", title);
            Assert.StartsWith("Undo New Project Api?", message, StringComparison.Ordinal);
            Assert.DoesNotContain("guide.md", message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(api), $"Undo left the new project's folder: {window.Status}");
            Assert.True(File.Exists(guide) && !File.Exists(notes), $"New Project's Undo moved guide.md, renamed meanwhile, back to notes.md: {window.Status}");

            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(2, asked.Count);
            Assert.Equal("Undo Rename notes.md to guide.md? It changes guide.md.", asked[1].Message);
            Assert.True(File.Exists(notes) && !File.Exists(guide), $"The rename's own Undo did not name guide.md back: {window.Status}");
        }
        finally
        {
            evaluated.TrySetResult();
            window.Close();
        }
    }

    /// <summary>New Project of an MVC app, a folder of megabytes (its scripts and styles): its Undo keeps the folder as each
    /// file's length and time, not its bytes, so the undo history grows by a small part of the folder; Undo takes the folder
    /// away and Redo puts every file back as it was.</summary>
    [AvaloniaFact]
    public void New_Project_of_an_MVC_app_keeps_its_folder_for_Undo_without_its_bytes()
    {
        TestFolders.Write(place, "Tool/Tool.csproj", TestProjects.Console());
        TestFolders.Write(place, "Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        string site = Path.Combine(PathNames.Canonical(place), "Site");
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "mvc");
            dialog.ProjectName = "Site";
            dialog.Framework = TestProjects.Framework;
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.None;
            Ui.Settle();
            Created(dialog, "the MVC app");
            Assert.Null(SetUp(window));
            Dictionary<string, byte[]> made = Made(site);
            long bytes = made.Values.Sum(b => (long)b.Length);
            Assert.True(bytes > 1_000_000, $"The MVC app's folder holds only {bytes} bytes in {made.Count} files, too few to tell its bytes from its list");

            List<string> asked = Answer(window);
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(["Undo"], asked);
            Assert.False(Directory.Exists(site), $"Undo left the new project's folder: {window.Status}");
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Redo);
            Assert.True(Directory.Exists(site), $"Redo did not put the new project's folder back: {window.Status}");
            Dictionary<string, byte[]> back = Made(site);
            Assert.True(back.Count == made.Count && made.All(f => back.TryGetValue(f.Key, out byte[]? now) && now.AsSpan().SequenceEqual(f.Value)),
                $"Redo did not put the folder back as it was: {window.Status}");

            Ui.Settle();
            long held = GC.GetTotalMemory(forceFullCollection: true);
            Assert.True(Undoes(window), "New Project left no command for Undo to take back");
            window.EditorArea.Commands.Clear();
            long growth = held - GC.GetTotalMemory(forceFullCollection: true);
            Assert.True(growth < bytes / 10, $"New Project's Undo held {growth} bytes of the history for a folder of {bytes} bytes in {made.Count} files");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Undo of New Project refused once a file of the folder it made has changed, naming that file, the folder kept.</summary>
    [AvaloniaFact]
    public void Undo_of_New_Project_refuses_once_a_file_in_its_folder_has_changed_naming_it()
    {
        TestFolders.Write(place, "Tool/Tool.csproj", TestProjects.Console());
        TestFolders.Write(place, "Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        string lib = Path.Combine(PathNames.Canonical(place), "Lib");
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "classlib");
            dialog.ProjectName = "Lib";
            dialog.Framework = TestProjects.Framework;
            Created(dialog, "the class library");
            Assert.Null(SetUp(window));
            TestFolders.Write(place, "Lib/Class1.cs", "namespace Lib;\n\npublic class Changed;\n");

            List<string> asked = Answer(window);
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Assert.Empty(asked);
            Assert.Equal($"Cannot undo New Project Lib: {Path.Join("Lib", "Class1.cs")} has been changed since", window.Status);
            Assert.True(File.Exists(Path.Combine(lib, "Class1.cs")), "Undo refused took the folder away all the same");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Undo of New Project whose folder the disk will not let it set aside (a file in it held open on Windows, the
    /// folder read-only elsewhere) says it could not, leaves the folder whole and the solution as New Project left it, and
    /// keeps the command for Undo: asked again once the folder can move, it takes the project away.</summary>
    [AvaloniaFact]
    public void Undo_of_New_Project_whose_folder_cannot_be_set_aside_says_so_leaves_the_folder_whole_and_can_be_asked_again()
    {
        TestFolders.Write(place, "Tool/Tool.csproj", TestProjects.Console());
        TestFolders.Write(place, "Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        string solution = Path.Combine(PathNames.Canonical(place), "Shop.slnx");
        byte[] before = File.ReadAllBytes(solution);
        string lib = Path.Combine(PathNames.Canonical(place), "Lib");
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        IDisposable? held = null;
        try
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "classlib");
            dialog.ProjectName = "Lib";
            dialog.Framework = TestProjects.Framework;
            Created(dialog, "the class library");
            Assert.Null(SetUp(window));
            byte[] added = File.ReadAllBytes(solution);
            Dictionary<string, byte[]> made = Made(lib);

            held = Unmovable(lib);
            List<string> asked = Answer(window);
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(["Undo"], asked);
            Assert.True(window.Status?.StartsWith("Cannot undo New Project Lib: Lib could not be set aside: ", StringComparison.Ordinal) == true,
                $"Undo of New Project whose folder could not be set aside said '{window.Status}'");
            Dictionary<string, byte[]> left = Made(lib);
            Assert.True(left.Count == made.Count && made.All(f => left.TryGetValue(f.Key, out byte[]? now) && now.AsSpan().SequenceEqual(f.Value)),
                $"Undo refused left the new project's folder with [{string.Join(", ", left.Keys)}] of [{string.Join(", ", made.Keys)}]");
            Assert.True(added.AsSpan().SequenceEqual(File.ReadAllBytes(solution)), $"Undo refused changed the solution file to {File.ReadAllText(solution)}");
            Assert.True(Undoes(window), "Undo refused left New Project out of the history, so it cannot be asked again");

            held.Dispose();
            held = null;
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal("Undone: New Project Lib", window.Status);
            Assert.False(Directory.Exists(lib), $"Undo asked again left the new project's folder: {window.Status}");
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(solution)), $"Undo asked again left the solution file as {File.ReadAllText(solution)}");
        }
        finally
        {
            held?.Dispose();
            window.Close();
        }
    }

    /// <summary>Undo and Redo of New Project whose folder lies on another volume than the IDE's state (a disk image of the
    /// test's own, as an external drive is) carry the folder off the window's thread, the window's work saying so: keys
    /// typed while a carry is held reach the editor, and Redo brings the folder back whole, what builds wrote in it too.</summary>
    [AvaloniaFact]
    public void Undo_and_Redo_of_New_Project_on_another_volume_carry_its_folder_while_the_window_answers_keys()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "A volume of the test's own is a disk image hdiutil makes, on macOS alone");
        string volume = Directory.CreateDirectory(Path.Combine(place, "volume")).FullName;
        string disk = Path.Combine(place, "volume.dmg");
        ChildResult image = Ran("/usr/bin/hdiutil", "create", "-size", "64m", "-fs", "HFS+", "-volname", "Passenger Undo Tests", "-ov", disk, "-quiet");
        Assert.SkipUnless(image.Succeeded, $"hdiutil made no disk image (exit code {image.ExitCode}): {image.Error}{image.Output}");
        ChildResult attached = Ran("/usr/bin/hdiutil", "attach", disk, "-nobrowse", "-noautoopen", "-mountpoint", volume, "-quiet");
        Assert.SkipUnless(attached.Succeeded, $"hdiutil attached no disk image (exit code {attached.ExitCode}): {attached.Error}{attached.Output}");
        MainWindow? window = null;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Spotlight indexes a volume wherever it is mounted, unless it holds this marker.
            File.WriteAllText(Path.Combine(volume, ".metadata_never_index"), string.Empty);
            TestFolders.Write(volume, "Tool/Tool.csproj", TestProjects.Console());
            const string Program = "System.Console.WriteLine(\"tool\");\n";
            TestFolders.Write(volume, "Tool/Program.cs", Program);
            TestFolders.Write(volume, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
            string root = PathNames.Canonical(volume);
            string lib = Path.Combine(root, "Lib");
            window = Ui.OpenFolder(root, languageServer: false);
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "classlib");
            dialog.ProjectName = "Lib";
            dialog.Framework = TestProjects.Framework;
            Created(dialog, "the class library");
            Assert.Null(SetUp(window));
            string built = TestFolders.Write(lib, "bin/Debug/built.txt", "built\n");
            Dictionary<string, byte[]> made = Made(lib);
            window.OpenFile(Path.Combine(root, "Tool", "Program.cs"));
            Ui.Settle();
            List<string> asked = Answer(window);

            Task held = MadeFolders.HoldNextCarry(release.Task);
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo, settle: false);
            Ui.PumpUntil(() => held.IsCompleted || window.Status?.StartsWith("Cannot undo", StringComparison.Ordinal) == true,
                "Undo's carry of the folder to the state's volume to be held");
            Assert.True(held.IsCompleted, $"Undo of New Project on another volume carried nothing: {window.Status}");
            Assert.Equal("Setting Lib aside: copying it to another disk…", window.Status);
            Assert.True(window.Actions.WhyNot(EditorActions.Undo, window.Actions.Context()) is null,
                $"Undo is greyed while the folder is carried, so the typing's own Undo cannot be asked: {window.Actions.WhyNot(EditorActions.Undo, window.Actions.Context())}");
            Typed(window, "a", "a" + Program);
            release.SetResult();
            Ui.WaitFor(() => window.Status == "Undone: New Project Lib", () => $"Undo of New Project once its folder is carried; the status says '{window.Status}'");
            Assert.Equal(["Undo"], asked);
            Assert.False(Directory.Exists(lib), $"Undo left the new project's folder: {window.Status}");

            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            held = MadeFolders.HoldNextCarry(release.Task);
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Redo, settle: false);
            Ui.PumpUntil(() => held.IsCompleted || window.Status?.StartsWith("Cannot redo", StringComparison.Ordinal) == true,
                "Redo's carry of the folder back to the test's volume to be held");
            Assert.True(held.IsCompleted, $"Redo of New Project on another volume carried nothing: {window.Status}");
            Assert.Equal("Putting Lib back: copying it from another disk…", window.Status);
            Typed(window, "b", "ba" + Program);
            release.SetResult();
            Ui.WaitFor(() => window.Status == "Redone: New Project Lib", () => $"Redo of New Project once its folder is carried back; the status says '{window.Status}'");
            Dictionary<string, byte[]> back = Made(lib);
            Assert.True(back.Count == made.Count && made.All(f => back.TryGetValue(f.Key, out byte[]? now) && now.AsSpan().SequenceEqual(f.Value)),
                $"Redo did not put the folder back as it was: [{string.Join(", ", back.Keys)}] of [{string.Join(", ", made.Keys)}]");
            Assert.True(File.Exists(built) && File.ReadAllText(built) == "built\n", "Redo put the folder back without what builds wrote in its bin");
        }
        finally
        {
            release.TrySetResult();
            if (window is not null)
            {
                window.Close();
                Ui.AwaitPumped(window.Ended ?? Task.CompletedTask, "what the closed window started to end", MainWindow.EndLimit + TimeSpan.FromSeconds(5));
            }
            Detach(volume);
        }
    }

    /// <summary>A key typed into the open file while the window's thread is free, read back from the editor at once.</summary>
    private static void Typed(MainWindow window, string key, string text)
    {
        window.EditorArea.CodeEditor.Focus();
        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(0);
        window.KeyTextInput(key);
        Assert.True(window.EditorArea.CodeEditor.Document.Snapshot.ToString() == text,
            $"A key typed while the folder was carried did not reach the editor, so the window was not answering: '{window.EditorArea.CodeEditor.Document.Snapshot}'");
    }

    /// <summary>The test's disk image detached, even while something still reads it.</summary>
    private static void Detach(string volume)
    {
        ChildResult detached = null!;
        Assert.True(Waiting.Until(() => (detached = Ran("/usr/bin/hdiutil", "detach", volume, "-force", "-quiet")).Succeeded, TimeSpan.FromSeconds(10), every: TimeSpan.FromSeconds(1)),
            $"The test's volume {volume} could not be detached (hdiutil exited with code {detached.ExitCode}): {detached.Error}");
    }

    /// <summary>A system tool run on the thread pool and waited for with the window's loop running.</summary>
    private static ChildResult Ran(string tool, params string[] arguments)
    {
        TimeSpan limit = TimeSpan.FromMinutes(1);
        return Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tool, arguments), limit: limit.Scaled())),
            $"{tool} {string.Join(' ', arguments)}", limit.Scaled() + TestFolders.Spare);
    }

    /// <summary>New Solution of a web API with Docker Compose: the solution's compose.yaml has it as a service built from its
    /// Dockerfile, the Compose configuration is chosen, and the first Run brings it up in its container (where a Linux
    /// container engine runs).</summary>
    [AvaloniaFact]
    public void New_Solution_of_a_web_API_with_Docker_Compose_is_chosen_to_run_with_Compose_and_the_first_Run_brings_it_up()
    {
        SkipWithoutDockerCommand();
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        try
        {
            Ui.Click(window.Welcome.NewSolutionButton);
            NewProjectDialog dialog = Opened(window);
            dialog.Folder = place;
            Choose(dialog, "webapi");
            dialog.SolutionName = "Shop";
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            // Its OpenAPI package would come from nuget.org, which the tests never reach.
            if (dialog.OptionLabels.FirstOrDefault(l => l.Contains("OpenAPI", StringComparison.Ordinal)) is { } openApi && dialog.OptionEditor(openApi) is CheckBox { IsChecked: true } box)
            {
                Ui.Click(box);
            }
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Compose;
            Ui.Settle();
            Created(dialog, "the web API");

            Assert.Null(SetUp(window));
            string folder = PathNames.Canonical(Path.Combine(place, "Shop"));
            string compose = File.ReadAllText(Path.Combine(folder, "compose.yaml"));
            Assert.Contains("services:\n  api:\n    build:\n      context: .\n      dockerfile: Api/Dockerfile\n", compose, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(folder, "Api", "Dockerfile")), "The web API has no Dockerfile for its service");
            Assert.True(window.Configuration is { Name: "Compose: compose.yaml", Kind: RunKind.Compose, Mode: ContainerMode.Fast },
                $"The configuration chosen is {window.Configuration?.Name}, not the solution's compose.yaml in fast mode: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");

            Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
            Ui.WaitFor(() => !window.IsBuilding, "the new solution's build", TimeSpan.FromMinutes(3));
            string? refused = Ui.Await(window.RunAsync(), "the first Run to bring the web API up", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            Assert.True(window.IsRunning, $"The first Run did not bring the service up: {window.Status}");
            Assert.StartsWith("Running Compose: compose.yaml", window.Status, StringComparison.Ordinal);
            string project = window.Builds.Containers?.Plan.ProjectName ?? throw new InvalidOperationException($"The first Run made no container run: {window.Status}");
            Assert.True(ContainerRunsTests.Container(project, "api").Length > 0, $"The first Run left api with no container running: {ContainerRunsTests.Run(window)}");
            Docker.Stopped(window, project, "Stop to take api down", "com.docker.compose.service=api");
            Ui.WaitFor(() => !window.IsRunning, "the log to stop being followed");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>New Project with Docker Compose into a solution whose compose.yaml has services already: the new project
    /// joins it after them, indented as they are, every line of theirs kept, with a Dockerfile of its own, and the Compose
    /// configuration is chosen.</summary>
    [AvaloniaFact]
    public void New_Project_with_Docker_Compose_joins_the_solutions_compose_file_beside_its_services()
    {
        SkipWithoutDockerCommand();
        TestFolders.Write(place, "Tool/Tool.csproj", TestProjects.Console());
        TestFolders.Write(place, "Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        const string Had = "services:\n    cache:\n        image: redis:8\n        # kept as written\n\nvolumes:\n    data:\n";
        TestFolders.Write(place, "compose.yaml", Had);
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = Opened(window);
            Choose(dialog, "web");
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Compose;
            Ui.Settle();
            Created(dialog, "the web project");

            Assert.Null(SetUp(window));
            string compose = File.ReadAllText(Path.Combine(place, "compose.yaml"));
            Assert.StartsWith("services:\n    cache:\n        image: redis:8\n        # kept as written\n    api:\n      build:\n        context: .\n        dockerfile: Api/Dockerfile\n", compose, StringComparison.Ordinal);
            Assert.EndsWith("\nvolumes:\n    data:\n", compose, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(place, "Api", "Dockerfile")), "The new project has no Dockerfile for its service");
            Assert.True(window.Configuration is { Name: "Compose: compose.yaml", Kind: RunKind.Compose },
                $"The configuration chosen is {window.Configuration?.Name}, not the solution's compose.yaml: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The set-up the dialog's Create asked for, once it is done: null when made and chosen, else why not.</summary>
    internal static string? SetUp(MainWindow window)
    {
        Ui.WaitFor(() => window.ServiceSetUp is { IsCompleted: true }, $"the new service's set-up: {window.Status}", TimeSpan.FromMinutes(3));
        return Ui.Await(window.ServiceSetUp!, "the new service's set-up");
    }

    /// <summary>The template of a short name chosen, its family's row clicked and its Type chosen.</summary>
    internal static void Choose(NewProjectDialog dialog, string shortName)
    {
        ProjectTemplate wanted = dialog.Templates.FirstOrDefault(t => t.Pack is null && t.ShortNames.Contains(shortName))
            ?? throw new InvalidOperationException($"dotnet new has no {shortName} here: [{string.Join(", ", dialog.Templates.Select(t => t.ShortName))}]");
        Ui.Click(dialog.FamilyRow(wanted.Family) ?? throw new InvalidOperationException($"New Project lists no {wanted.Family}"));
        if (dialog.Chosen?.ShortName != wanted.ShortName)
        {
            dialog.TypeChooser.SelectedItem = dialog.Family!.Types.First(t => t.ShortName == wanted.ShortName);
            Ui.Settle();
        }
        Assert.Equal(wanted.ShortName, dialog.Chosen?.ShortName);
    }

    /// <summary>Create, as its button does, failing with why when it made nothing.</summary>
    internal static void Created(NewProjectDialog dialog, string what)
    {
        Ui.Click(dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, $"dotnet new to make {what}", TimeSpan.FromMinutes(5));
        Assert.True(dialog.Problem is null, $"{what} was not made: {dialog.Problem}");
    }

    /// <summary>The dialog an action opened, once its folder's templates are read.</summary>
    internal static NewProjectDialog Opened(MainWindow window)
    {
        Ui.Settle();
        NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>(), d => d.IsVisible);
        Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
        Assert.True(dialog.Families.Count > 0, $"The dialog lists no templates: {dialog.Problem}");
        return dialog;
    }

    /// <summary>Whether Undo in the Solution view has a command to take back, asked apart so no reference to it outlives the
    /// question in the test's frame.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool Undoes(MainWindow window) => window.EditorArea.Commands.ToUndo(null) is not null;

    /// <summary>Every file the template and set-up made in a project's folder, but what builds write, with its bytes.</summary>
    private static Dictionary<string, byte[]> Made(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" || PathNames.IsSystemFile(part)))
            .ToDictionary(f => Path.GetRelativePath(folder, f), File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>A folder the disk will not move to another folder until disposed: on Windows a file in it held open, as a
    /// program working there holds one; elsewhere the folder read-only, as a move to another folder writes in it.</summary>
    private static IDisposable Unmovable(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(Directory.EnumerateFiles(folder, "*.csproj").First(), FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        return new ReadOnlyFolder(folder);
    }

    /// <summary>A folder made read-only, its own mode given back once disposed.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private sealed class ReadOnlyFolder : IDisposable
    {
        private readonly string folder;
        private readonly UnixFileMode was;

        public ReadOnlyFolder(string folder)
        {
            this.folder = folder;
            was = File.GetUnixFileMode(folder);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        public void Dispose() => File.SetUnixFileMode(folder, was);
    }

    private static List<string> Answer(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        return asked;
    }

    private static ExplorerNode Row(MainWindow window, NodeKind kind, string name) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Name == name)
        ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
}
