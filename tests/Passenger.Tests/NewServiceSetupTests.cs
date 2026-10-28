using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
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

    /// <summary>New Project into a solution, with a Dockerfile: the project, its Dockerfile and a Docker launch profile beside
    /// the template's own are made, its container configuration chosen; a class library is offered no set-up, saying why,
    /// and the choice comes back with the next service; one Undo in the Solution view takes the project, its files and its
    /// line in the solution back, and Redo makes them again.</summary>
    [AvaloniaFact]
    public void New_Project_with_a_Dockerfile_is_chosen_to_run_in_its_container_and_one_Undo_takes_the_whole_creation_back()
    {
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

    /// <summary>New Solution of a web API with Docker Compose: the solution's compose.yaml has it as a service built from its
    /// Dockerfile, the Compose configuration is chosen, and the first Run brings it up in its container (where a Linux
    /// container engine runs).</summary>
    [AvaloniaFact]
    public void New_Solution_of_a_web_API_with_Docker_Compose_is_chosen_to_run_with_Compose_and_the_first_Run_brings_it_up()
    {
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
