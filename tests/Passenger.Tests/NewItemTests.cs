using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Add, New Item, from the item templates dotnet new has in the run's own template home and Passenger's own:
/// an API Controller in a web project's Controllers folder takes the folder's namespace and builds, a Razor Component goes
/// into a Blazor project's pages, Razor's templates listed first there, a .gitignore at the solution's folder, which lists
/// only the templates of no language; each is opened once made, and one Undo takes it away. New Project's Web API offers
/// Use controllers, which makes its Controllers folder.</summary>
public sealed class NewItemTests : IDisposable
{
    private readonly string place = TestFolders.Create("item-");

    public void Dispose() => TestFolders.Delete(place);

    /// <summary>An API Controller added to a web project's Controllers folder from the folder's Add menu: the web project's
    /// controllers lead the list, the namespace offered is the folder's, the file made is an [ApiController] in it, opened,
    /// and the project builds; one Undo in the Solution view takes the file away.</summary>
    [AvaloniaFact]
    public void An_API_Controller_goes_into_a_web_projects_Controllers_folder_in_its_namespace_builds_and_one_Undo_takes_it_away()
    {
        string api = WebProject(controllers: true);
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            string controllers = PathNames.Canonical(Path.Combine(api, "Controllers"));
            window.ExplorerView.Reveal(Path.Combine(controllers, "PingController.cs"));
            Ui.Settle();
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Folder, "Controllers"), "Add", "New Item…");
            NewItemDialog dialog = Opened(window);
            Assert.True(dialog.Listed.Take(2).Select(t => t.ShortName).SequenceEqual(["apicontroller", "mvccontroller"]),
                $"A web project's New Item does not lead with its controllers: [{string.Join(", ", dialog.Listed.Take(6).Select(t => t.ShortName))}]");
            Assert.DoesNotContain(dialog.Listed, t => t.Languages.Count > 0 && !t.Languages.Contains("C#"));
            Assert.Contains(dialog.Listed, t => t is { Own: not null, Name: "Class" });
            Assert.Equal("apicontroller", dialog.Chosen?.ShortName);
            DialogButtonsTests.Check(dialog);
            Assert.Equal("Shop.Api.Controllers", dialog.Namespace);
            dialog.ItemName = "OrdersController";
            Ui.Settle();
            Assert.Contains("--namespace Shop.Api.Controllers", dialog.Command, StringComparison.Ordinal);
            Created(window, dialog, "the API controller");

            string made = Path.Combine(controllers, "OrdersController.cs");
            string text = File.ReadAllText(made);
            Assert.True(text.Contains("namespace Shop.Api.Controllers", StringComparison.Ordinal) && text.Contains("[ApiController]", StringComparison.Ordinal)
                && text.Contains("class OrdersController", StringComparison.Ordinal), $"The controller made is not the folder's API controller:\n{text}");
            Assert.Equal(made, window.EditorArea.Active?.Path);
            Built(Path.Combine(api, "Shop.Api.csproj"));

            Undone(window, made);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Razor Component added to a Blazor project's pages: Razor's templates lead its list, and the component is
    /// made and opened; one Undo takes it away.</summary>
    [AvaloniaFact]
    public void A_Razor_Component_goes_into_a_Blazor_projects_pages_Razor_templates_listed_first_and_one_Undo_takes_it_away()
    {
        string web = WebProject(controllers: false);
        string pages = PathNames.Canonical(Path.Combine(web, "Components", "Pages"));
        TestFolders.Write(pages, "Home.razor", "@page \"/\"\n\n<h1>Home</h1>\n");
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            window.ExplorerView.Reveal(Path.Combine(pages, "Home.razor"));
            Ui.Settle();
            Ui.ClickRow(window, Row(window, NodeKind.Folder, "Pages"));
            Ui.Run(window, AppActions.NewItem);
            NewItemDialog dialog = Opened(window);
            Assert.True(dialog.Listed.Take(2).Select(t => t.ShortName).SequenceEqual(["razorcomponent", "page"]),
                $"A Blazor project's New Item does not lead with Razor's templates: [{string.Join(", ", dialog.Listed.Take(6).Select(t => t.ShortName))}]");
            dialog.ItemName = "Counter";
            Ui.Settle();
            Created(window, dialog, "the Razor component");

            string made = Path.Combine(pages, "Counter.razor");
            Assert.Contains("<h3>Counter</h3>", File.ReadAllText(made), StringComparison.Ordinal);
            Assert.Equal(made, window.EditorArea.Active?.Path);

            Undone(window, made);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>New Item on the solution lists the templates of no language (no controller, no class), and its .gitignore,
    /// which takes no name, goes into the solution's folder; one Undo takes it away.</summary>
    [AvaloniaFact]
    public void A_gitignore_goes_into_the_solutions_folder_from_its_New_Item_and_one_Undo_takes_it_away()
    {
        WebProject(controllers: true);
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", "New Item…");
            NewItemDialog dialog = Opened(window);
            Assert.True(dialog.Listed.Count > 0 && dialog.Listed.All(t => t.Languages.Count == 0),
                $"The solution's New Item lists templates of a language: [{string.Join(", ", dialog.Listed.Where(t => t.Languages.Count > 0).Select(t => t.ShortName))}]");
            dialog.Query = "gitignore";
            Ui.Settle();
            Assert.Equal("gitignore", dialog.Chosen?.ShortName);
            Assert.False(dialog.AsksName, "New Item asks a name of the .gitignore template, which takes none");
            Created(window, dialog, "the .gitignore");

            string made = Path.Combine(PathNames.Canonical(place), ".gitignore");
            Assert.True(File.Exists(made), $"No .gitignore at the solution's folder: {window.Status}");
            Assert.Equal(made, window.EditorArea.Active?.Path);

            Undone(window, made);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>New Project's Web API offers the template's Use controllers (ide.host.json's UseMinimalAPIs, its opposite),
    /// once, and choosing it makes the project with its Controllers folder; Blazor's Include sample pages, once, is its
    /// command line's --empty.</summary>
    [AvaloniaFact]
    public void New_Projects_Web_API_offers_Use_controllers_once_which_makes_its_Controllers_folder()
    {
        TestFolders.Write(place, "Shop.slnx", "<Solution />\n");
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            // Blazor's Include sample pages, which only an IDE's host reads, reaches the command line as its --empty.
            NewServiceSetupTests.Choose(dialog, "blazor");
            Assert.DoesNotContain(dialog.OptionLabels, l => l.Contains("omit sample pages", StringComparison.Ordinal));
            Ui.Click(dialog.OptionEditor("Include sample pages") ?? throw new InvalidOperationException($"Blazor offers no Include sample pages: [{string.Join(", ", dialog.OptionLabels)}]"));
            Assert.Contains("--empty true", dialog.Command, StringComparison.Ordinal);
            NewServiceSetupTests.Choose(dialog, "webapi");
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            Assert.Single(dialog.OptionLabels, l => l.Contains("controllers", StringComparison.OrdinalIgnoreCase));
            Ui.Click(dialog.OptionEditor("Use controllers") ?? throw new InvalidOperationException($"Web API offers no Use controllers: [{string.Join(", ", dialog.OptionLabels)}]"));
            // Its OpenAPI package would come from nuget.org, which the tests never reach.
            if (dialog.OptionLabels.FirstOrDefault(l => l.Contains("OpenAPI", StringComparison.Ordinal)) is { } openApi && dialog.OptionEditor(openApi) is CheckBox { IsChecked: true } box)
            {
                Ui.Click(box);
            }
            Assert.Contains("--use-minimal-apis false", dialog.Command, StringComparison.Ordinal);
            NewServiceSetupTests.Created(dialog, "the Web API");
            Assert.Null(NewServiceSetupTests.SetUp(window));
            string folder = Path.Combine(PathNames.Canonical(place), "Api", "Controllers");
            Assert.True(Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*Controller.cs").Any(), $"Use controllers made no Controllers folder: {window.Status}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A solution of one web project, Shop.Api, with a controller in Controllers or a Blazor page in Components;
    /// its folder.</summary>
    private string WebProject(bool controllers)
    {
        TestFolders.Write(place, "Shop.slnx", "<Solution>\n  <Project Path=\"Shop.Api/Shop.Api.csproj\" />\n</Solution>\n");
        TestFolders.Write(place, "Shop.Api/Shop.Api.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
        TestFolders.Write(place, "Shop.Api/Program.cs",
            "WebApplicationBuilder builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddControllers();\nWebApplication app = builder.Build();\napp.MapControllers();\napp.Run();\n");
        if (controllers)
        {
            TestFolders.Write(place, "Shop.Api/Controllers/PingController.cs",
                "using Microsoft.AspNetCore.Mvc;\n\nnamespace Shop.Api.Controllers;\n\n[ApiController]\n[Route(\"ping\")]\npublic class PingController : ControllerBase\n{\n    [HttpGet]\n    public string Get() => \"pong\";\n}\n");
        }
        return Path.Combine(PathNames.Canonical(place), "Shop.Api");
    }

    /// <summary>The New Item dialog an action opened, once its templates are read.</summary>
    private static NewItemDialog Opened(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<NewItemDialog>().Any(d => d.IsVisible), "New Item to open");
        NewItemDialog dialog = window.OwnedWindows.OfType<NewItemDialog>().Single(d => d.IsVisible);
        Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
        Assert.True(dialog.Problem is null, $"The templates could not be read: {dialog.Problem}");
        return dialog;
    }

    /// <summary>Create, as its button does, then what it made added to the window, failing with why when it was not.</summary>
    private static void Created(MainWindow window, NewItemDialog dialog, string what)
    {
        Ui.Click(dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, $"dotnet new to make {what}", TimeSpan.FromMinutes(3));
        Assert.True(dialog.Problem is null, $"{what} was not made: {dialog.Problem}");
        Ui.WaitFor(() => window.ItemAdded is { IsCompleted: true }, $"{what} to be added: {window.Status}");
        Assert.Null(Ui.Await(window.ItemAdded!, $"{what} to be added"));
    }

    /// <summary>One Undo in the Solution view takes the file made away, closing its tab.</summary>
    private static void Undone(MainWindow window, string made)
    {
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Solution));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => !File.Exists(made), $"Undo to take {Path.GetFileName(made)} away: {window.Status}");
        Assert.DoesNotContain(window.EditorArea.Documents, d => d.Path == made);
    }

    /// <summary>A project built with the .NET command line, as a person would check it.</summary>
    private static void Built(string project)
    {
        ChildResult built = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", project, "-nologo", "-v:q"]), limit: Restore.Limit.Scaled())),
            $"{Path.GetFileName(project)}'s build", Restore.Limit.Scaled() + TestFolders.Spare);
        Assert.True(built.Succeeded, $"{Path.GetFileName(project)} does not build with the controller made:\n{built.Output}{built.Error}");
    }

    private static ExplorerNode Row(MainWindow window, NodeKind kind, string name) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Name == name)
        ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
}
