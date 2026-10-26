using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Run configurations as Edit Configurations makes them: one added in the dialog runs with what was typed there
/// and is there when the folder opens again; a generated one is changed in place, its changes kept with the folder over
/// its launch profile or written to it, or copied to be the person's own; one of the person's own is saved as a launch
/// profile of its project; one removed is gone for good. The dialog opens from the Run menu's action and the toolbar's
/// chooser. A test that runs Hello takes a restored folder (<see cref="Restored"/>); the rest open one no restore reached,
/// as a fresh clone opens, since they build nothing.</summary>
public sealed class RunConfigurationsTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly RestoredFolders folders;
    private readonly string settings = TestFolders.Create("settings-");
    private string folder = TestFolders.Create("configs-");
    private string project;

    public RunConfigurationsTests(RestoredFolders folders)
    {
        this.folders = folders;
        Hello(folder);
        project = PathNames.Canonical(Path.Combine(folder, "Hello"));
    }

    /// <summary>Hello's project and its program, which says its variable, arguments and folder; the folder to restore.</summary>
    private static string Hello(string root)
    {
        TestFolders.Write(root, "Hello/Hello.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(root, "Hello/Program.cs", "Console.WriteLine($\"{Environment.GetEnvironmentVariable(\"GREETING\")} [{string.Join(\"|\", args)}] in {Environment.CurrentDirectory}\");\n");
        return Path.Combine(root, "Hello");
    }

    /// <summary>The test's folder made one of the class's restored ones, for a test that builds and runs Hello, before
    /// anything else of the test uses it.</summary>
    private void Restored()
    {
        TestFolders.Delete(folder);
        folder = folders.Take("configs-", Hello);
        project = PathNames.Canonical(Path.Combine(folder, "Hello"));
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    [Fact]
    public void A_generated_Aspire_override_keeps_linked_AppHost_paths_relative_in_storage()
    {
        RunConfiguration source = new() { Name = "Aspire", Project = Path.Combine(folder, "Host", "Host.csproj"), Aspire = true };
        string linked = Path.Combine(folder, "Linked", "Linked.csproj");
        JsonObject edit = Assert.IsType<JsonObject>(ConfigurationEdit.Between(source, source with { LinkedAppHosts = [linked] }));
        JsonObject stored = ConfigurationEdit.Mapped(edit, path => Path.GetRelativePath(folder, path));
        Assert.Equal(Path.GetRelativePath(folder, linked), stored["linkedAppHosts"]![0]!.GetValue<string>());
        JsonObject loaded = ConfigurationEdit.Mapped(stored, path => Path.GetFullPath(path, folder));
        Assert.Equal(new[] { linked }, ConfigurationEdit.Over(source, loaded).LinkedAppHosts);
    }

    /// <summary>While a run of a configuration is up, the toolbar, its chooser and Edit Configurations' row say "(running)"
    /// after its name, and stop saying it once the run has ended.</summary>
    [AvaloniaFact]
    public void A_configuration_says_it_is_running_in_the_toolbar_its_chooser_and_Edit_Configurations_while_a_run_of_it_is_up()
    {
        // Hello made a program that runs until stopped, which it never is otherwise.
        Restored();
        Write("Hello/Program.cs", "Console.WriteLine(\"waiting\");\nThread.Sleep(Timeout.Infinite);\n");
        MainWindow window = Keeping();
        TextBlock shown = window.FindControl<TextBlock>("RunConfigName")!;
        Ui.WaitFor(() => shown.Text == "Hello", $"the project's configuration to be chosen: {shown.Text}");
        try
        {
            Ui.Launched(window, window.RunAsync(), "Hello to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => shown.Text == "Hello (running)", $"the toolbar to say Hello runs: {shown.Text}");
            Ui.Click(window.FindControl<Button>("RunConfigButton")!);
            ContextMenu chooser = Assert.IsType<ContextMenu>(window.OpenContextMenu);
            Assert.Equal(["Hello (running)", "Edit Configurations…"], chooser.Items.OfType<MenuItem>().Select(i => i.Header as string));
            Ui.Click(chooser.Items.OfType<MenuItem>().Last());
            chooser.Close();
            RunConfigurationsDialog dialog = Assert.IsType<RunConfigurationsDialog>(window.RunConfigurationsDialog);
            Ui.Settle();
            string[] texts = [.. dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
            Assert.True(texts.Contains("Hello (running)"), $"Edit Configurations' row does not say Hello runs: {string.Join(" | ", texts.Take(8))}");
            // The configuration keeps its own name: the words are the row's alone.
            Assert.Equal("Hello", dialog.Chosen?.Name);
            dialog.Close();
            Ui.Settle();
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "Hello to stop");
        }
        Ui.WaitFor(() => shown.Text == "Hello", $"the toolbar to stop saying Hello runs: {shown.Text}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_configuration_added_in_the_dialog_runs_with_what_was_typed_and_is_there_when_the_folder_opens_again()
    {
        Restored();
        MainWindow window = Keeping();
        Assert.Equal(["Hello"], Names(window));

        Ui.Click(window.FindControl<Button>("RunConfigButton")!);
        ContextMenu chooser = Assert.IsType<ContextMenu>(window.OpenContextMenu);
        Assert.Equal(["Hello", "Edit Configurations…"], chooser.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
        Ui.Click(chooser.Items.OfType<MenuItem>().Last());
        chooser.Close();
        RunConfigurationsDialog dialog = Assert.IsType<RunConfigurationsDialog>(window.RunConfigurationsDialog);
        Assert.Equal("Hello", dialog.Chosen?.Name);

        Add(dialog);
        Assert.Equal("Unnamed", dialog.Chosen?.Name);
        Assert.True(dialog.NameBox.IsEnabled, "A configuration just added cannot be named");
        Assert.True(dialog.NameBox.IsFocused, "A configuration just added does not have its name ready to type");
        dialog.NameBox.SelectAll();
        dialog.KeyTextInput("Greeting");
        Ui.Settle();
        Type(dialog.ArgumentsBox, "one \"two three\"");
        Type(dialog.WorkingDirectoryBox, "$(ProjectDir)");
        Ui.Click(dialog.AddVariableButton);
        (TextBox name, TextBox value) = Assert.Single(dialog.Variables);
        Type(name, "GREETING");
        Type(value, "Hi");
        Assert.Equal(["Hello", "Greeting"], dialog.Rows.Select(r => r.Name));
        Ui.Click(dialog.OkButton);
        Ui.Settle();
        Assert.Null(window.RunConfigurationsDialog);
        Assert.Equal(["Hello", "Greeting"], Names(window));
        Assert.Equal("Greeting", window.Configuration?.Name);

        Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end");
        string ran = window.BottomPanel.Run.Document.Snapshot.ToString();
        Assert.True(ran.Contains($"Hi [one|two three] in {project}", StringComparison.Ordinal),
            $"The program did not get the arguments, variable and folder typed into the dialog:\n{ran}");
        window.Close();

        MainWindow again = Keeping();
        try
        {
            Assert.Equal(["Hello", "Greeting"], Names(again));
            Assert.Equal("Greeting", again.Configuration?.Name);
            RunConfiguration kept = again.Configurations[1];
            Assert.Equal(Path.Combine(project, "Hello.csproj"), kept.Project);
            Assert.Equal("one \"two three\"", kept.Arguments);
            Assert.Equal(new Dictionary<string, string> { ["GREETING"] = "Hi" }, kept.Environment);
            Assert.Equal("$(ProjectDir)", kept.WorkingDirectory);
            Assert.False(kept.Generated);
        }
        finally
        {
            again.Close();
        }
    }

    /// <summary>Each launch profile is a generated configuration: one with arguments and variables runs with them in the
    /// project's folder, arguments given for one run replacing the profile's; a Docker profile is a container configuration
    /// of its project; and a generated configuration copied is the person's own, the generated one staying as its profile
    /// makes it.</summary>
    [AvaloniaFact]
    public void A_generated_configuration_copied_is_the_persons_own_and_the_generated_one_stays_as_its_launch_profile_makes_it()
    {
        Restored();
        Write("Hello/Properties/launchSettings.json", """
            {
              // Comments and trailing commas, as Visual Studio writes them.
              "profiles": {
                "Greet": { "commandName": "Project", "commandLineArgs": "--name \"Ada Lovelace\"", "environmentVariables": { "GREETING": "Hi" }, },
                "Docker": { "commandName": "Docker" }
              }
            }
            """);
        MainWindow window = Keeping();
        try
        {
            Assert.Equal(["Hello: Greet", "Hello: Docker"], Names(window));
            RunConfiguration docker = window.Configurations.Single(c => c.Name == "Hello: Docker");
            Assert.Equal(RunKind.Dockerfile, docker.Kind);
            Assert.Null(window.WhyNotHere(docker));

            // A profile that names no working folder runs in the project's.
            Ui.Launched(window, window.RunAsync(), "the profile's run to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the profile's run to end");
            string profiled = window.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(profiled.Contains($"Hi [--name|Ada Lovelace] in {project}", StringComparison.Ordinal),
                $"The program did not get its launch profile's arguments and variable in the project's folder:\n{profiled}");
            Ui.Launched(window, window.RunAsync("one \"two three\""), "the run with arguments of its own to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run with arguments of its own to end");
            string given = window.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(given.Contains("Hi [one|two three]", StringComparison.Ordinal), $"The arguments given for one run did not replace the profile's:\n{given}");
            // This project, with a Docker profile and no Dockerfile, cannot make a container, so Profile says so.
            Assert.True(window.SelectConfiguration("Hello: Docker"));
            Assert.Equal("Hello: Docker cannot run: Hello has no Dockerfile", Ui.Await(window.ProfileAsync(), "the profiler's refusal"));
            Assert.True(window.SelectConfiguration("Hello: Greet"));

            Assert.True(window.Perform(AppActions.EditConfigurations));
            RunConfigurationsDialog dialog = Assert.IsType<RunConfigurationsDialog>(window.RunConfigurationsDialog);
            Assert.Equal("Hello: Greet", dialog.Chosen?.Name);
            Assert.True(dialog.Chosen!.Generated);
            Assert.False(dialog.NameBox.IsEnabled || dialog.ProfileBox.IsEnabled, "A generated configuration's name or launch profile, what links it to its source, can be changed in place");
            Assert.False(dialog.RemoveButton.IsEnabled, "A generated configuration can be removed, though its project makes it again");
            Assert.Equal("--name \"Ada Lovelace\"", dialog.ArgumentsBox.Text);
            Assert.Equal("Greet", dialog.ProfileBox.SelectedItem);

            Ui.Click(dialog.CopyButton);
            Assert.Equal("Hello: Greet (1)", dialog.Chosen?.Name);
            Assert.False(dialog.Chosen!.Generated);
            Assert.True(dialog.ArgumentsBox.IsEnabled, "The copy of a generated configuration cannot be changed");
            Assert.Equal("--name \"Ada Lovelace\"", dialog.ArgumentsBox.Text);
            Type(dialog.ArgumentsBox, "--name Grace");
            Ui.Click(dialog.OkButton);
            Ui.Settle();

            Assert.Equal(["Hello: Greet", "Hello: Docker", "Hello: Greet (1)"], Names(window));
            Assert.Equal("--name \"Ada Lovelace\"", window.Configurations[0].Arguments);
            Assert.Equal("Hello: Greet (1)", window.Configuration?.Name);
            Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end");
            string ran = window.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(ran.Contains("Hi [--name|Grace] in ", StringComparison.Ordinal), $"The copy did not run with its own arguments and the profile's variable:\n{ran}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A generated configuration's arguments changed in place, with no copy, are what Run gives the program, and are
    /// there when the folder opens again; its launch profile's environment changed on disk then reaches it while the
    /// arguments stay the person's, the Explorer left as it was, and the dialog marks the arguments alone as changed.</summary>
    [AvaloniaFact]
    public void A_generated_configurations_arguments_changed_in_place_are_run_kept_with_the_folder_and_stay_over_its_launch_profile_as_it_changes()
    {
        Restored();
        Write("Hello/Properties/launchSettings.json", Greet("Hi"));
        MainWindow window = Keeping();
        RunConfigurationsDialog dialog = window.ShowRunConfigurations();
        Assert.Equal("Hello: Greet", dialog.Chosen?.Name);
        Assert.True(dialog.ArgumentsBox.IsEnabled, "A generated configuration's arguments cannot be changed in place");
        Assert.Empty(dialog.Marked);
        Type(dialog.ArgumentsBox, "--name Grace");
        Assert.Equal(["Program arguments"], dialog.Marked);
        Assert.Equal("Changed here from its launch profile: Program arguments.", dialog.Changes);
        Assert.True(dialog.WriteButton.IsEffectivelyVisible && dialog.ResetButton.IsEffectivelyVisible, "A changed generated configuration offers neither Write to Launch Profile nor Reset");
        Assert.Equal("Reset to Launch Profile", dialog.ResetButton.Content);
        DialogButtonsTests.Check(dialog);
        Ui.Click(dialog.OkButton);
        Assert.Equal(["Hello: Greet"], Names(window));
        Assert.Equal("--name Grace", window.Configuration?.Arguments);

        Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end");
        string ran = window.BottomPanel.Run.Document.Snapshot.ToString();
        Assert.True(ran.Contains("Hi [--name|Grace] in ", StringComparison.Ordinal), $"The program did not get the arguments changed in place:\n{ran}");
        window.Close();

        MainWindow again = Keeping();
        try
        {
            RunConfiguration kept = Assert.Single(again.Configurations);
            Assert.True(kept.Generated && kept.Arguments == "--name Grace", $"The change in place was not kept with the folder: {kept.Name} runs with {kept.Arguments}");

            again.ExplorerView.Reveal(Path.Combine(project, "Hello.csproj"));
            Ui.Settle();
            ExplorerNode hello = again.ExplorerView.Tree.Rows.First(r => r.Name == "Hello");
            Ui.ClickRow(again, hello);
            Write("Hello/Properties/launchSettings.json", Greet("Hello"));
            Ui.WaitFor(() => again.Configurations[0].Environment.GetValueOrDefault("GREETING") == "Hello", "the launch profile's new variable to reach the configuration");
            Assert.Equal("--name Grace", again.Configurations[0].Arguments);
            Assert.True(again.ExplorerView.Tree.Rows.Contains(hello) && again.ExplorerView.Rows().IsKeyboardFocusWithin && again.ExplorerView.SelectedNodes is [{ Name: "Hello" }],
                "launchSettings.json changed on disk had the Explorer's rows made again, as a project changed does, or took its keys or its choice");
            RunConfigurationsDialog reopened = again.ShowRunConfigurations();
            Assert.Equal(["Program arguments"], reopened.Marked);
            reopened.Close();
            Ui.Settle();

            Ui.Launched(again, again.RunAsync(), "the second run to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => again.Runner.Ended.IsCompleted, "the second run to end");
            string second = again.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(second.Contains("Hello [--name|Grace] in ", StringComparison.Ordinal), $"The program did not get the profile's new variable with the arguments kept:\n{second}");
        }
        finally
        {
            again.Close();
        }
    }

    /// <summary>Write to Launch Profile puts the arguments and a variable changed in place into the profile, the rest of
    /// launchSettings.json (the other profile, a comment, the spacing) byte for byte, and keeps here what the file has no
    /// place for; Undo takes the write back whole: the file as it was, the changes kept with the folder as they were.</summary>
    [AvaloniaFact]
    public void Write_to_launch_profile_puts_the_changes_into_launchSettings_json_the_rest_as_it_was_and_Undo_takes_it_back()
    {
        string settingsFile = Path.Combine(project, "Properties", "launchSettings.json");
        Write("Hello/Properties/launchSettings.json", Profiles);
        MainWindow window = Keeping();
        try
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Assert.True(dialog.Choose("Hello: Greet"));
            Type(dialog.ArgumentsBox, "--name \"Grace Hopper\"");
            Ui.Click(dialog.AddVariableButton);
            (TextBox name, TextBox value) = dialog.Variables[^1];
            Type(name, "NEW");
            Type(value, "1");
            dialog.ConsoleBox.IsChecked = true;
            Ui.Settle();
            Assert.Equal(["Program arguments", "Environment variables", "Use external console"], dialog.Marked);
            Assert.True(dialog.WriteButton.IsEffectivelyEnabled, "Write to Launch Profile cannot be used for arguments and a variable");
            Ui.Click(dialog.WriteButton);

            string written = Profiles
                .Replace("\"commandLineArgs\": \"--name Ada\"", "\"commandLineArgs\": \"--name \\\"Grace Hopper\\\"\"", StringComparison.Ordinal)
                .Replace("\"GREETING\": \"Hi\"\n", "\"GREETING\": \"Hi\",\n        \"NEW\": \"1\"\n", StringComparison.Ordinal);
            Assert.Equal(written, File.ReadAllText(settingsFile));
            Assert.Equal("Written to its launch profile in launchSettings.json: Program arguments, Environment variables. "
                + "Kept here, as launchSettings.json has no place for it: Use external console.", dialog.Changes);
            Assert.Equal(["Use external console"], dialog.Marked);
            Ui.Click(dialog.OkButton);
            RunConfiguration greet = window.Configurations.Single(c => c.Name == "Hello: Greet");
            Assert.Equal(("--name \"Grace Hopper\"", "1", ConsoleKind.Terminal), (greet.Arguments, greet.Environment.GetValueOrDefault("NEW"), greet.Console));
            Assert.Equal("--name \"Grace Hopper\"", greet.Source?.Arguments);

            List<string> asked = [];
            window.Confirm = (title, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            window.ExplorerView.Reveal(Path.Combine(project, "Hello.csproj"));
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Name.StartsWith("Hello", StringComparison.Ordinal)));
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Assert.Equal(["Undo Write Hello: Greet to Its Launch Profile? It changes launchSettings.json and the run configuration Hello: Greet."], asked);
            Assert.Equal(Profiles, File.ReadAllText(settingsFile));
            Ui.WaitFor(() => window.Configurations.Single(c => c.Name == "Hello: Greet").Source?.Arguments == "--name Ada", "the configuration to follow launchSettings.json taken back");
            greet = window.Configurations.Single(c => c.Name == "Hello: Greet");
            Assert.True((greet.Arguments, greet.Environment.GetValueOrDefault("NEW"), greet.Console) == ("--name \"Grace Hopper\"", "1", ConsoleKind.Terminal),
                $"Undo did not put the configuration's changes back over its launch profile: {greet.Arguments}, NEW={greet.Environment.GetValueOrDefault("NEW")}, {greet.Console}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Save as Launch Profile, greyed with why on a generated configuration and a name a profile has, adds one made
    /// in the dialog (its project's name taken off its own) after the profiles of a launchSettings.json, the rest byte for
    /// byte; dotnet run runs it by that name, listed as generated from it, the console kept here. Undo in the Explorer takes
    /// the profile out, the folder's own configuration back, the keys left there for Redo to save it again; the folder
    /// opened again lists it as generated, the console still kept here.</summary>
    [AvaloniaFact]
    public void Save_as_launch_profile_adds_a_configuration_made_here_to_launchSettings_json_which_dotnet_run_runs_and_Undo_takes_it_back()
    {
        Restored();
        string settingsFile = Path.Combine(project, "Properties", "launchSettings.json");
        Write("Hello/Properties/launchSettings.json", Profiles);
        MainWindow window = Keeping();
        try
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Assert.True(dialog.Choose("Hello: Greet"));
            Ui.Settle();
            Assert.Equal("Save as Launch Profile", dialog.SaveAsProfileButton.Content);
            Assert.False(dialog.SaveAsProfileButton.IsEnabled, "Save as Launch Profile can be used on a configuration generated from a launch profile");
            Assert.Equal("Hello: Greet is generated from its launch profile already: Write to Launch Profile writes its changes there", ToolTip.GetTip(dialog.SaveAsProfileButton));
            Add(dialog);
            Type(dialog.NameBox, "Greet");
            Assert.False(dialog.SaveAsProfileButton.IsEnabled, "Save as Launch Profile can be used on a name a launch profile has already");
            Assert.Equal("Hello's launchSettings.json has a profile named Greet already", ToolTip.GetTip(dialog.SaveAsProfileButton));
            Type(dialog.NameBox, "Hello: Staging");
            Type(dialog.ArgumentsBox, "one \"two three\"");
            Type(dialog.WorkingDirectoryBox, "$(ProjectDir)");
            Ui.Click(dialog.AddVariableButton);
            (TextBox name, TextBox value) = Assert.Single(dialog.Variables);
            Type(name, "GREETING");
            Type(value, "Hi");
            dialog.ConsoleBox.IsChecked = true;
            Ui.Settle();
            Assert.True(dialog.SaveAsProfileButton.IsEffectivelyEnabled, $"Save as Launch Profile cannot be used on a configuration made here: {ToolTip.GetTip(dialog.SaveAsProfileButton)}");
            DialogButtonsTests.Check(dialog);
            Ui.Click(dialog.SaveAsProfileButton);
            Assert.True(dialog.Problem.Length == 0, $"Save as Launch Profile was refused: {dialog.Problem}");

            string written = Profiles.Replace("      \"commandLineArgs\": \"--other\"\n    }\n",
                "      \"commandLineArgs\": \"--other\"\n    },\n    \"Staging\": {\n      \"commandName\": \"Project\",\n      \"commandLineArgs\": \"one \\\"two three\\\"\",\n"
                + "      \"environmentVariables\": {\n        \"GREETING\": \"Hi\"\n      }\n    }\n", StringComparison.Ordinal);
            Assert.Equal(written, File.ReadAllText(settingsFile));
            Assert.Equal(["Hello: Greet", "Hello: Other", "Hello: Staging"], dialog.Rows.Select(r => r.Name));
            Assert.True(dialog.Chosen is { Generated: true, LaunchProfile: "Staging" }, $"The configuration saved is not chosen as generated from its new profile: {dialog.Chosen?.Name}");
            Assert.Equal("Saved as the launch profile Staging in Hello's Properties/launchSettings.json: it is now Hello: Staging, generated from that profile. "
                + "Kept here, as launchSettings.json has no place for it: Use external console.", dialog.Changes);
            Assert.Equal(["Use external console"], dialog.Marked);
            Ui.Click(dialog.OkButton);
            Assert.Equal(["Hello: Greet", "Hello: Other", "Hello: Staging"], Names(window));
            Assert.True(window.Configuration is { Name: "Hello: Staging", Generated: true, Console: ConsoleKind.Terminal, Source.Console: ConsoleKind.Internal },
                $"The toolbar's configuration is not the one saved, generated with the console kept here: {window.Configuration?.Name}, {window.Configuration?.Console}");

            ChildResult ran = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["run", "--launch-profile", "Staging"], project), limit: TimeSpan.FromMinutes(3).Scaled()),
                "dotnet run of the launch profile saved", TimeSpan.FromMinutes(3));
            Assert.True(ran.Succeeded && ran.Output.Contains($"Hi [one|two three] in {project}", StringComparison.Ordinal),
                $"dotnet run --launch-profile Staging did not run the program with the arguments and variable saved:\n{ran.Output}{ran.Error}");

            List<string> asked = [];
            window.Confirm = (title, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            window.ExplorerView.Reveal(Path.Combine(project, "Hello.csproj"));
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Name.StartsWith("Hello", StringComparison.Ordinal)));
            Heard(window, settingsFile, () => Ui.Run(window, Passenger.Editor.EditorActions.Undo));
            Assert.Equal(["Undo Save Hello: Staging as a Launch Profile? It changes launchSettings.json and the run configuration Hello: Staging."], asked);
            Assert.Equal(Profiles, File.ReadAllText(settingsFile));
            Assert.Equal(["Hello: Greet", "Hello: Other", "Hello: Staging"], Names(window));
            Assert.True(window.Configuration is { Name: "Hello: Staging", Generated: false, Arguments: "one \"two three\"", Console: ConsoleKind.Terminal },
                $"Undo did not bring the folder's own configuration back, chosen: {window.Configuration?.Name}, generated {window.Configuration?.Generated}");
            Assert.True(window.ExplorerView.Rows().IsKeyboardFocusWithin && window.ExplorerView.SelectedNodes is [{ Name: "Hello" }],
                $"Undo, the files read again and the folder watcher's word of launchSettings.json, left the Explorer's keys elsewhere, [{string.Join(", ", window.ExplorerView.SelectedNodes.Select(n => n.Name))}] chosen");

            Ui.Run(window, Passenger.Editor.EditorActions.Redo);
            Assert.True(File.ReadAllText(settingsFile) == written, $"Redo did not add the profile again: {window.Status} ({string.Join(" | ", asked)})");
            Assert.True(window.Configuration is { Name: "Hello: Staging", Generated: true, Console: ConsoleKind.Terminal },
                $"Redo did not save the configuration as its launch profile again: {window.Configuration?.Name}, generated {window.Configuration?.Generated}");
        }
        finally
        {
            window.Close();
        }

        MainWindow again = Keeping();
        try
        {
            Assert.Equal(["Hello: Greet", "Hello: Other", "Hello: Staging"], Names(again));
            RunConfiguration kept = again.Configurations[^1];
            Assert.True(kept is { Generated: true, LaunchProfile: "Staging", Console: ConsoleKind.Terminal, Arguments: "one \"two three\"" },
                $"The folder opened again does not list the configuration as generated from its profile with the console kept here: {kept.Name}, {kept.Console}");
        }
        finally
        {
            again.Close();
        }
    }

    /// <summary>Save as Launch Profile in a project with no launchSettings.json makes the file as dotnet new does, its schema
    /// then its profiles, which dotnet run runs by the profile's name; the project then runs only as that profile, its
    /// building first kept here. Undo takes the file and the folder made for it away, the folder's own configuration back;
    /// Redo makes them again, and the folder opened again lists it as generated, still not built first.</summary>
    [AvaloniaFact]
    public void Save_as_launch_profile_in_a_project_with_no_launchSettings_json_makes_it_as_dotnet_new_does_and_Undo_takes_it_away()
    {
        Restored();
        string properties = Path.Combine(project, "Properties");
        string settingsFile = Path.Combine(properties, "launchSettings.json");
        MainWindow window = Keeping();
        try
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Add(dialog);
            Type(dialog.NameBox, "Greeting");
            Type(dialog.ArgumentsBox, "--name Grace");
            Type(dialog.WorkingDirectoryBox, "$(ProjectDir)");
            Ui.Click(dialog.AddVariableButton);
            (TextBox name, TextBox value) = Assert.Single(dialog.Variables);
            Type(name, "GREETING");
            Type(value, "Hi");
            dialog.BuildBox.IsChecked = false;
            Ui.Settle();
            Ui.Click(dialog.SaveAsProfileButton);
            Assert.True(dialog.Problem.Length == 0, $"Save as Launch Profile was refused: {dialog.Problem}");

            Assert.Equal("{\n  \"$schema\": \"https://json.schemastore.org/launchsettings.json\",\n  \"profiles\": {\n    \"Greeting\": {\n      \"commandName\": \"Project\",\n"
                + "      \"commandLineArgs\": \"--name Grace\",\n      \"environmentVariables\": {\n        \"GREETING\": \"Hi\"\n      }\n    }\n  }\n}\n",
                File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "no file");
            Assert.Equal("Saved as the launch profile Greeting in Hello's Properties/launchSettings.json, made for it: it is now Hello: Greeting, generated from that profile. "
                + "Kept here, as launchSettings.json has no place for it: Before launch.", dialog.Changes);
            // A project one of whose profiles runs it is listed by its profiles alone.
            Assert.Equal(["Hello: Greeting"], dialog.Rows.Select(r => r.Name));
            Ui.Click(dialog.OkButton);
            Assert.True(window.Configuration is { Name: "Hello: Greeting", Generated: true, BuildBeforeLaunch: false },
                $"The toolbar's configuration is not the one saved, generated, not built first: {window.Configuration?.Name}, {window.Configuration?.BuildBeforeLaunch}");

            ChildResult ran = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["run", "--launch-profile", "Greeting"], project), limit: TimeSpan.FromMinutes(3).Scaled()),
                "dotnet run of the launch profile saved", TimeSpan.FromMinutes(3));
            Assert.True(ran.Succeeded && ran.Output.Contains($"Hi [--name|Grace] in {project}", StringComparison.Ordinal),
                $"dotnet run --launch-profile Greeting did not run the program with the arguments and variable saved:\n{ran.Output}{ran.Error}");

            List<string> asked = [];
            window.Confirm = (title, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            window.ExplorerView.Reveal(Path.Combine(project, "Hello.csproj"));
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Name.StartsWith("Hello", StringComparison.Ordinal)));
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Assert.Equal(["Undo Save Greeting as a Launch Profile? It changes launchSettings.json, the Properties folder and the run configuration Greeting."], asked);
            Assert.False(File.Exists(settingsFile) || Directory.Exists(properties), "Undo left launchSettings.json or the Properties folder made for it");
            Assert.Equal(["Hello", "Greeting"], Names(window));
            Assert.True(window.Configuration is { Name: "Greeting", Generated: false, BuildBeforeLaunch: false },
                $"Undo did not bring the folder's own configuration back, chosen: {window.Configuration?.Name}");

            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Name.StartsWith("Hello", StringComparison.Ordinal)));
            Ui.Run(window, Passenger.Editor.EditorActions.Redo);
            Assert.True(File.Exists(settingsFile), $"Redo did not make launchSettings.json again: {window.Status} ({string.Join(" | ", asked)})");
            Assert.Equal(["Hello: Greeting"], Names(window));
        }
        finally
        {
            window.Close();
        }

        MainWindow again = Keeping();
        try
        {
            RunConfiguration kept = Assert.Single(again.Configurations);
            Assert.True(kept is { Name: "Hello: Greeting", Generated: true, BuildBeforeLaunch: false, Arguments: "--name Grace" },
                $"The folder opened again does not list the configuration as generated from its profile, not built first: {kept.Name}, {kept.BuildBeforeLaunch}");
        }
        finally
        {
            again.Close();
        }
    }

    /// <summary>A file-based program's launch profiles, in its app.run.json, changed on disk reach its run configuration as
    /// a launchSettings.json's do, the Explorer left as it was.</summary>
    [AvaloniaFact]
    public void A_file_based_programs_run_json_changed_on_disk_reaches_its_run_configuration()
    {
        TestFolders.Delete(folder);
        folder = TestFolders.Create("program-");
        Write("app.cs", "#!/usr/bin/env dotnet\n#:property PublishAot=false\nConsole.WriteLine(Environment.GetEnvironmentVariable(\"GREETING\"));\n");
        Write("app.run.json", Greet("Hi"));
        MainWindow window = Keeping();
        try
        {
            Ui.WaitFor(() => window.Configurations is [{ Name: "app: Greet" } greet] && greet.Environment.GetValueOrDefault("GREETING") == "Hi",
                "app.cs to be a program with its app.run.json profile's configuration", TimeSpan.FromMinutes(1));
            List<ExplorerNode> rows = [.. window.ExplorerView.Tree.Rows];
            Write("app.run.json", Greet("Hello"));
            Ui.WaitFor(() => window.Configurations is [{ Name: "app: Greet" } greet] && greet.Environment.GetValueOrDefault("GREETING") == "Hello",
                "the profile's new variable, written in app.run.json on disk, to reach the configuration");
            Assert.True(window.ExplorerView.Tree.Rows.SequenceEqual(rows, ReferenceEqualityComparer.Instance), "app.run.json changed on disk had the Explorer's rows made again");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>In a solution of many projects, every one shown open: Undo of Save as Launch Profile in a project with no
    /// Properties folder takes that folder's row out and Go to File's launchSettings.json with it, and Redo puts them back,
    /// every other row the row it was; Undo and Redo of one saved into that launchSettings.json, a command of its text alone,
    /// leave every row as it was, with no project checked again and the folder never walked.</summary>
    [AvaloniaFact]
    public void Undo_and_Redo_of_Save_as_launch_profile_in_a_solution_of_many_projects_make_again_only_the_rows_of_what_they_change()
    {
        const int Libraries = 40;
        for (int i = 0; i < Libraries; i++)
        {
            Write($"Lib{i:00}/Lib{i:00}.csproj", TestProjects.Library());
            Write($"Lib{i:00}/Part{i}.cs", $"public class Part{i} {{ }}\n");
        }
        Write("Shop.slnx", $"<Solution>\n{string.Concat(Enumerable.Range(0, Libraries).Select(i => $"  <Project Path=\"Lib{i:00}/Lib{i:00}.csproj\" />\n"))}  <Project Path=\"Hello/Hello.csproj\" />\n</Solution>\n");
        string properties = Path.Combine(project, "Properties");
        string settingsFile = Path.Combine(properties, "launchSettings.json");
        MainWindow window = Keeping();
        try
        {
            ExplorerTree tree = window.ExplorerView.Tree;
            Ui.WaitFor(() => window.Projects.Count == Libraries + 1 && window.Projects.All(p => p.IsEvaluated), "every project evaluated");
            foreach (ExplorerNode row in tree.Rows.Where(r => r.Kind == NodeKind.Project).ToList())
            {
                tree.Expand(row);
            }
            Ui.Settle();
            int walks = 0;
            window.FileIndex.Walked += (_, _) => walks++;
            List<string> asked = [];
            window.Confirm = (_, question, _) =>
            {
                asked.Add(question);
                return Task.FromResult(true);
            };
            Heard(window, settingsFile, () => Save("Greeting"));
            Ui.WaitFor(() => tree.Rows.Any(r => r.Path == PathNames.Canonical(properties)), "the Properties folder made for launchSettings.json to show");

            List<ExplorerNode> before = [.. tree.Rows];
            ExplorerNode made = tree.Rows.Single(r => r.Path == PathNames.Canonical(properties));
            Ui.ClickRow(window, tree.Rows.First(r => r.Kind == NodeKind.Project && r.Name == "Hello"));
            Heard(window, settingsFile, () => Ui.Run(window, Passenger.Editor.EditorActions.Undo));
            Assert.False(Directory.Exists(properties), $"Undo left the Properties folder it made: {window.Status} ({string.Join(" | ", asked)})");
            Ui.WaitFor(() => !tree.Rows.Contains(made), "the Properties folder's row to go");
            Assert.True(tree.Rows.SequenceEqual(before.Where(r => r != made), ReferenceEqualityComparer.Instance),
                $"Undo made rows other than the Properties folder's again: [{string.Join(", ", tree.Rows.Where(r => !before.Contains(r, ReferenceEqualityComparer.Instance)).Select(r => r.Name))}] are new");
            Assert.DoesNotContain(window.FileIndex.Files, f => f.EndsWith("launchSettings.json", StringComparison.Ordinal));

            Ui.Run(window, Passenger.Editor.EditorActions.Redo);
            Assert.True(File.Exists(settingsFile), $"Redo did not make launchSettings.json again: {window.Status}");
            Ui.WaitFor(() => tree.Rows.Any(r => r.Path == PathNames.Canonical(properties)), "the Properties folder's row to come back");
            Ui.Settle();
            Assert.True(tree.Rows.Where(r => r.Path != PathNames.Canonical(properties)).SequenceEqual(before.Where(r => r != made), ReferenceEqualityComparer.Instance),
                "Redo made rows other than the Properties folder's again");
            Assert.Contains(window.FileIndex.Files, f => f.EndsWith("launchSettings.json", StringComparison.Ordinal));

            Heard(window, settingsFile, () => Save("Staging"));
            before = [.. tree.Rows];
            int rounds = window.Session.Models.Rounds;
            string saved = File.ReadAllText(settingsFile);
            Ui.ClickRow(window, tree.Rows.First(r => r.Kind == NodeKind.Project && r.Name == "Hello"));
            Heard(window, settingsFile, () => Ui.Run(window, Passenger.Editor.EditorActions.Undo));
            Assert.DoesNotContain("Staging", File.ReadAllText(settingsFile), StringComparison.Ordinal);
            Heard(window, settingsFile, () => Ui.Run(window, Passenger.Editor.EditorActions.Redo));
            Assert.Equal(saved, File.ReadAllText(settingsFile));
            Assert.True(tree.Rows.SequenceEqual(before, ReferenceEqualityComparer.Instance),
                $"Undo and Redo of launchSettings.json's text made rows again: [{string.Join(", ", tree.Rows.Where(r => !before.Contains(r, ReferenceEqualityComparer.Instance)).Select(r => r.Name))}] are new");
            Assert.True(window.Session.Models.Rounds == rounds,
                $"Undo and Redo of launchSettings.json's text had the {Libraries + 1} projects checked again {window.Session.Models.Rounds - rounds} times");
            Assert.True(walks == 0, $"Undo and Redo walked the folder for Go to File {walks} times");
        }
        finally
        {
            window.Close();
        }

        // A configuration made in Edit Configurations and saved as a launch profile of Hello.
        void Save(string name)
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Add(dialog);
            Type(dialog.NameBox, name);
            Ui.Click(dialog.SaveAsProfileButton);
            Assert.True(dialog.Problem.Length == 0, $"Save as Launch Profile was refused: {dialog.Problem}");
            Ui.Click(dialog.OkButton);
        }
    }

    /// <summary>Reset to Launch Profile puts a generated configuration changed in place back as its profile makes it, and
    /// the folder opened again has no change kept for it.</summary>
    [AvaloniaFact]
    public void Reset_to_launch_profile_puts_a_changed_configuration_back_as_its_profile_makes_it_for_good()
    {
        Write("Hello/Properties/launchSettings.json", Greet("Hi"));
        MainWindow window = Keeping();
        RunConfigurationsDialog dialog = window.ShowRunConfigurations();
        Type(dialog.ArgumentsBox, "--name Grace");
        dialog.BuildBox.IsChecked = false;
        Ui.Settle();
        Ui.Click(dialog.OkButton);
        Assert.Equal(("--name Grace", false), (window.Configuration?.Arguments, window.Configuration?.BuildBeforeLaunch));

        dialog = window.ShowRunConfigurations();
        Assert.Equal(["Program arguments", "Before launch"], dialog.Marked);
        Ui.Click(dialog.ResetButton);
        Assert.Empty(dialog.Marked);
        Assert.Equal(("--name Ada", true), (dialog.ArgumentsBox.Text, dialog.BuildBox.IsChecked == true));
        Assert.False(dialog.ResetButton.IsEffectivelyVisible, "Reset to Launch Profile is offered for a configuration as its profile makes it");
        Ui.Click(dialog.OkButton);
        Assert.Equal(("--name Ada", true), (window.Configuration?.Arguments, window.Configuration?.BuildBeforeLaunch));
        window.Close();

        MainWindow again = Keeping();
        try
        {
            RunConfiguration reset = Assert.Single(again.Configurations);
            Assert.True(reset.Source is null && reset.Arguments == "--name Ada", $"The configuration reset to its profile opened again with {reset.Arguments}");
        }
        finally
        {
            again.Close();
        }
    }

    /// <summary>A runConfigurations.json an older Passenger wrote, with a copy the person made of a generated configuration,
    /// opens as it was; changing the generated one in place leaves the copy in the file as it stood.</summary>
    [AvaloniaFact]
    public void An_older_runConfigurations_json_opens_as_it_was_and_keeps_the_persons_copies_as_they_stand()
    {
        Write("Hello/Properties/launchSettings.json", Greet("Hi"));
        JsonArray copies = [new JsonObject
        {
            ["name"] = "Hello: Greet (1)", ["kind"] = "Project", ["project"] = "Hello/Hello.csproj", ["framework"] = "", ["launchProfile"] = "Greet", ["program"] = "",
            ["arguments"] = "--name Grace", ["environment"] = new JsonObject { ["GREETING"] = "Hey" }, ["workingDirectory"] = "$(ProjectDir)",
            ["buildBeforeLaunch"] = true, ["console"] = "Internal", ["launchBrowser"] = false, ["browserUrl"] = "",
        }];
        string file = Path.Combine(ProjectState.FolderOf(settings, PathNames.Canonical(folder)), "runConfigurations.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, new JsonObject { ["version"] = 2, ["configurations"] = copies.DeepClone(), ["selected"] = "Hello: Greet (1)" }.ToJsonString());

        MainWindow window = Keeping();
        try
        {
            Assert.Equal(["Hello: Greet", "Hello: Greet (1)"], Names(window));
            Assert.Equal("Hello: Greet (1)", window.Configuration?.Name);
            RunConfiguration copy = window.Configurations[1];
            Assert.Equal(("--name Grace", "Hey", false), (copy.Arguments, copy.Environment["GREETING"], copy.Generated));
            Assert.True(window.Configurations[0] is { Source: null, Arguments: "--name Ada" }, "The generated configuration of an older file opened changed");

            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Assert.True(dialog.Choose("Hello: Greet"));
            Type(dialog.ArgumentsBox, "--name Ada --loud");
            Ui.Click(dialog.OkButton);
        }
        finally
        {
            window.Close();
        }
        JsonObject kept = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(3, (int)kept["version"]!);
        JsonObject keptCopy = kept["configurations"]!.AsArray().Single()!.AsObject();
        Assert.True(copies[0]!.AsObject().All(p => JsonNode.DeepEquals(p.Value, keptCopy[p.Key])),
            $"The person's copy changed as the generated configuration was changed in place:\n{keptCopy.ToJsonString()}");
        Assert.Equal("--name Ada --loud", (string?)kept["edits"]?["Hello: Greet"]?["arguments"]);
    }

    /// <summary>Hello's launchSettings.json with one profile, Greet, as dotnet new writes one: its arguments and GREETING.</summary>
    private static string Greet(string greeting) =>
        $"{{\n  \"profiles\": {{\n    \"Greet\": {{\n      \"commandName\": \"Project\",\n      \"commandLineArgs\": \"--name Ada\",\n"
        + $"      \"environmentVariables\": {{\n        \"GREETING\": \"{greeting}\"\n      }}\n    }}\n  }}\n}}\n";

    /// <summary>A launchSettings.json of two profiles with a comment and spacing of its own, which a write to one keeps.</summary>
    private const string Profiles = """
        {
          // Kept as written.
          "profiles": {
            "Greet": {
              "commandName": "Project",
              "commandLineArgs": "--name Ada",
              "environmentVariables": {
                "GREETING": "Hi"
              }
            },
            "Other": {
              "commandName":   "Project",
              "commandLineArgs": "--other"
            }
          }
        }

        """;

    /// <summary>Use external console, chosen in the dialog for a copy of the generated configuration (whose own stays the
    /// Run window's), runs the program in a tab of the Terminal window named for the configuration, and is still chosen when
    /// the folder opens again.</summary>
    [AvaloniaFact]
    public void Use_external_console_chosen_in_the_dialog_runs_the_program_in_the_terminal_and_is_kept_with_the_configuration()
    {
        Restored();
        MainWindow window = Keeping();
        RunConfigurationsDialog dialog = window.ShowRunConfigurations();
        Assert.Equal("Hello", dialog.Chosen?.Name);
        Assert.False(dialog.ConsoleBox.IsChecked == true, "A generated configuration runs in the terminal");
        Ui.Click(dialog.CopyButton);
        Assert.True(dialog.ConsoleBox.IsEnabled, "The copy of a generated configuration cannot choose its console");
        Type(dialog.NameBox, "Hello in the terminal");
        Type(dialog.ArgumentsBox, "one");
        dialog.ConsoleBox.IsChecked = true;
        Ui.Settle();
        Ui.Click(dialog.OkButton);
        Ui.Settle();
        Assert.Equal(ConsoleKind.Internal, window.Configurations[0].Console);
        Assert.Equal(ConsoleKind.Terminal, window.Configuration?.Console);

        Ui.Launched(window, window.RunAsync(), "the run to start", TimeSpan.FromMinutes(2));
        TerminalRun run = Assert.IsType<TerminalRun>(window.RunInTerminal);
        Assert.Equal("Hello in the terminal", window.TerminalPanel.ActiveTab?.Name);
        Ui.WaitFor(() => run.Ended.IsCompleted, "the run to end");
        // Rows the terminal wrapped joined again, as a copy of them reads.
        string shown = string.Concat(run.Session.Screen.Scrollback.Concat(run.Session.Screen.Lines).Select(l => l.Text() + (l.Wrapped ? string.Empty : "\n")));
        Assert.True(shown.Contains($" [one] in {project}", StringComparison.Ordinal) && shown.Contains("Process finished with exit code 0.", StringComparison.Ordinal),
            $"The program did not run in the terminal with the configuration's arguments and folder:\n{shown}");
        Assert.Equal("Hello in the terminal exited with code 0", window.Status);
        window.Close();

        MainWindow again = Keeping();
        try
        {
            Assert.Equal(ConsoleKind.Terminal, again.Configuration?.Console);
            RunConfigurationsDialog reopened = again.ShowRunConfigurations();
            Assert.Equal("Hello in the terminal", reopened.Chosen?.Name);
            Assert.True(reopened.ConsoleBox.IsChecked == true, "Use external console was not kept with the configuration");
            reopened.Close();
        }
        finally
        {
            again.Close();
        }
    }

    /// <summary>Open browser, After launch: a launch profile's launchBrowser shows ticked with the URL its launchUrl and
    /// applicationUrl make, and opens that URL once the program listens; turned off in a copy, the copy opens none.</summary>
    [AvaloniaFact]
    public void A_launch_profiles_browser_shows_under_open_browser_and_a_copy_with_it_turned_off_opens_none()
    {
        Restored();
        // The port held until each run's program, waiting at a gate, is let listen there; it listens until let go.
        using HeldPort held = new();
        int port = held.Port;
        string gates = TestFolders.Create("gates-");
        string listen = Path.Combine(gates, "listen");
        string done = Path.Combine(gates, "done");
        Write("Hello/Program.cs", "using System.Net;\nusing System.Net.Sockets;\n\n"
            + $"while (!File.Exists(@\"{listen}\"))\n{{\n    Thread.Sleep(20);\n}}\n"
            + "TcpListener listener = new(IPAddress.Loopback, int.Parse(Environment.GetEnvironmentVariable(\"PORT\")!));\n"
            + $"listener.Start();\nConsole.WriteLine(\"Listening\");\nwhile (!File.Exists(@\"{done}\"))\n{{\n    Thread.Sleep(20);\n}}\n");
        Write("Hello/Properties/launchSettings.json", "{ \"profiles\": { \"Serve\": { \"commandName\": \"Project\", \"launchBrowser\": true, \"launchUrl\": \"status\", "
            + $"\"applicationUrl\": \"http://127.0.0.1:{port}\", \"environmentVariables\": {{ \"PORT\": \"{port}\" }} }} }} }}");
        MainWindow window = Keeping();
        try
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Assert.Equal("Hello: Serve", dialog.Chosen?.Name);
            Assert.True(dialog.BrowserBox.IsChecked == true, "The launch profile's launchBrowser does not show as Open browser after launch");
            Assert.Equal($"http://127.0.0.1:{port}/status", dialog.BrowserUrlBox.Text);
            Ui.Click(dialog.CopyButton);
            Assert.True(dialog.BrowserBox.IsEnabled, "The copy of a generated configuration cannot turn its browser off");
            dialog.BrowserBox.IsChecked = false;
            Ui.Settle();
            Ui.Click(dialog.OkButton);
            Ui.Settle();
            Assert.Equal("Hello: Serve (1)", window.Configuration?.Name);
            Assert.False(window.Configuration!.LaunchBrowser);

            Ui.Launched(window, window.RunAsync(), "the copy's run to start", TimeSpan.FromMinutes(2));
            held.LetGo(listen);
            Ui.WaitFor(() => window.BottomPanel.Run.Document.Snapshot.ToString().Contains("Listening", StringComparison.Ordinal), "the copy's program to listen");
            Ui.Hold(TimeSpan.FromSeconds(1.5), "the copy's program listening: no browser is to open meanwhile");
            File.WriteAllText(done, string.Empty);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the copy's run to end");
            Assert.Empty(window.Browsed);

            File.Delete(done);
            held.Hold(listen);
            Assert.True(window.SelectConfiguration("Hello: Serve"));
            Ui.Launched(window, window.RunAsync(), "the profile's run to start", TimeSpan.FromMinutes(2));
            held.LetGo(listen);
            Ui.WaitFor(() => window.Browsed.Count > 0, "the browser to open once the program listens", TimeSpan.FromSeconds(60));
            Assert.Equal([$"http://127.0.0.1:{port}/status"], window.Browsed.Select(u => u.AbsoluteUri));
            File.WriteAllText(done, string.Empty);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the profile's run to end");
        }
        finally
        {
            // A program a failure left listening ends; one still waiting to listen ends with the window.
            File.WriteAllText(done, string.Empty);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_configuration_removed_in_the_dialog_is_gone_from_the_toolbar_and_the_folder()
    {
        MainWindow window = Keeping();
        window.SetConfigurations(
        [
            new RunConfiguration { Name = "First", Project = Path.Combine(project, "Hello.csproj"), Arguments = "1" },
            new RunConfiguration { Name = "Second", Project = Path.Combine(project, "Hello.csproj"), Arguments = "2" },
        ], "Second");
        Assert.Equal(["Hello", "First", "Second"], Names(window));

        RunConfigurationsDialog dialog = window.ShowRunConfigurations();
        Assert.Equal("Second", dialog.Chosen?.Name);
        Assert.True(dialog.Choose("First"));
        Ui.Click(dialog.RemoveButton);
        Assert.Equal(["Hello", "Second"], dialog.Rows.Select(r => r.Name));
        Assert.Equal("Second", dialog.Chosen?.Name);
        Ui.Click(dialog.OkButton);
        Ui.Settle();
        Assert.Equal(["Hello", "Second"], Names(window));
        window.Close();

        MainWindow again = Keeping();
        try
        {
            Assert.Equal(["Hello", "Second"], Names(again));
        }
        finally
        {
            again.Close();
        }
    }

    [AvaloniaFact]
    public void A_compose_configuration_added_in_the_dialog_shows_its_kinds_page_and_is_kept_with_its_files_and_choices()
    {
        Write("compose.yaml", "services:\n  web:\n    image: busybox:1.37\n");
        Write("compose.override.yaml", "services:\n  web:\n    environment: { A: \"1\" }\n");
        MainWindow window = Keeping();
        RunConfigurationsDialog dialog = window.ShowRunConfigurations();
        Ui.Click(dialog.AddButton);
        Ui.Click(dialog.AddMenu!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Docker Compose"));
        Assert.Equal("Docker Compose", dialog.KindText.Text);
        Assert.True(dialog.ComposeFilesBox.IsEffectivelyVisible && dialog.ModeBox.IsEffectivelyVisible && dialog.KeepBackingBox.IsEffectivelyVisible, "The Compose page is not shown");
        Assert.False(dialog.ProgramBox.IsEffectivelyVisible || dialog.WorkingDirectoryBox.IsEffectivelyVisible || dialog.ProjectBox.IsEffectivelyVisible,
            "A Compose configuration shows a project's fields");
        Assert.Equal("compose.yaml; compose.override.yaml", dialog.ComposeFilesBox.Text);
        Type(dialog.NameBox, "Everything");
        Type(dialog.ProfilesBox, "tools");
        dialog.ModeBox.SelectedIndex = 1;
        dialog.KeepBackingBox.IsChecked = true;
        Ui.Settle();
        Ui.Click(dialog.OkButton);
        Assert.Null(window.RunConfigurationsDialog);
        RunConfiguration made = window.Configurations.Single(c => c.Name == "Everything");
        Assert.Equal(RunKind.Compose, made.Kind);
        Assert.Equal([Path.Combine(PathNames.Canonical(folder), "compose.yaml"), Path.Combine(PathNames.Canonical(folder), "compose.override.yaml")], made.ComposeFiles);
        Assert.Equal(["tools"], made.Profiles);
        Assert.Equal(ContainerMode.Images, made.Mode);
        Assert.True(made.KeepBackingServices, "Keep backing services was not kept");
        window.Close();

        MainWindow again = Keeping();
        RunConfiguration kept = again.Configurations.Single(c => c.Name == "Everything");
        Assert.Equal(made.ComposeFiles, kept.ComposeFiles);
        Assert.Equal(ContainerMode.Images, kept.Mode);
        again.Close();
    }

    [AvaloniaFact]
    public void A_name_taken_or_missing_is_refused_with_why_and_nothing_changes()
    {
        MainWindow window = Keeping();
        try
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Add(dialog);
            Type(dialog.NameBox, "Hello");
            Ui.Click(dialog.OkButton);
            Assert.Equal("Two configurations are named Hello", dialog.Problem);
            Assert.Same(dialog, window.RunConfigurationsDialog);
            Assert.Equal(["Hello"], Names(window));
            Type(dialog.NameBox, string.Empty);
            Ui.Click(dialog.OkButton);
            Assert.Equal("A configuration needs a name", dialog.Problem);
            dialog.Close();
            Ui.Settle();
            Assert.Equal(["Hello"], Names(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A name longer than the list is wide ends in an ellipsis inside its row.</summary>
    [AvaloniaFact]
    public void A_configuration_named_longer_than_the_list_is_wide_ends_in_an_ellipsis_inside_its_row()
    {
        const string Long = "TopLevelStatementsConsoleApp project, named at more length than the list has room for";
        MainWindow window = Keeping();
        try
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Add(dialog);
            Type(dialog.NameBox, Long);
            Ui.Click(dialog.OkButton);
            dialog = window.ShowRunConfigurations();
            Ui.Settle();
            ListBox list = dialog.GetVisualDescendants().OfType<ListBox>().First();
            TextBlock name = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Long && t.IsEffectivelyVisible);
            ListBoxItem row = name.FindAncestorOfType<ListBoxItem>()!;
            double right = Avalonia.VisualExtensions.TranslatePoint(name, new Avalonia.Point(name.Bounds.Width, 0), row)!.Value.X;
            Assert.True(right <= row.Bounds.Width + 0.5 && name.TextTrimming == Avalonia.Media.TextTrimming.CharacterEllipsis,
                $"'{Long}' runs {right:0.#} px into a row {row.Bounds.Width:0.#} px wide, cut at its edge rather than ending in an ellipsis");
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A window that keeps its state in the tests' own store, on the folder.</summary>
    private MainWindow Keeping()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private static string[] Names(MainWindow window) => [.. window.Configurations.Select(c => c.Name)];

    /// <summary>A field's text replaced, as typing over it does, and the dialog having heard it.</summary>
    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        Ui.Settle();
    }

    /// <summary>Add New Configuration, its menu's .NET Project chosen, as a person adds one; the menu lists every kind.</summary>
    private static void Add(RunConfigurationsDialog dialog)
    {
        Ui.Click(dialog.AddButton);
        ContextMenu kinds = Assert.IsType<ContextMenu>(dialog.AddMenu);
        Assert.Equal([".NET Project", "Docker Compose", "Dockerfile", "SDK Container"], kinds.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
        Ui.Click(kinds.Items.OfType<MenuItem>().First());
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);

    /// <summary>Does something that writes a file, and waits for the folder watcher to hear of it and for what it heard to
    /// land, which a settle alone does not wait for.</summary>
    private static void Heard(MainWindow window, string path, Action change)
    {
        bool heard = false;
        void Hear(object? sender, FolderChanges changes) => heard |= changes.Paths.Contains(PathNames.Canonical(path));
        window.FolderWatcher!.Changed += Hear;
        try
        {
            change();
            Ui.WaitFor(() => heard, $"the folder watcher to hear of {Path.GetFileName(path)} written");
            Ui.Settle();
        }
        finally
        {
            window.FolderWatcher!.Changed -= Hear;
        }
    }
}
