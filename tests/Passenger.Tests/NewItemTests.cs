using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Templates;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Add, New Item, from the item templates dotnet new has in the run's own template home and Passenger's own:
/// an API Controller in a web project's Controllers folder takes the folder's namespace and builds, a Razor Component goes
/// into a Blazor project's pages, Razor's templates listed first there, a .gitignore at the solution's folder, which lists
/// only the templates of no language; each is opened once made, and one Undo takes it away. The Add menu makes a folder's
/// .gitignore, .gitattributes and .editorconfig, opening one already there, and a .gitattributes is read as git reads it.
/// New Project's Web API offers Use controllers, which makes its Controllers folder.</summary>
public sealed class NewItemTests : IDisposable
{
    private readonly string place = TestFolders.Create("item-");

    public void Dispose() => TestFolders.Delete(place);

    /// <summary>New Item puts an API controller, a Razor component and a .gitignore where their project keeps them,
    /// building, one Undo taking each away, as one flow, each step the check one test made in an instance of its
    /// own.</summary>
    [AvaloniaFact]
    public void New_Item_puts_each_template_where_its_project_keeps_it_one_Undo_taking_it_away()
    {
        Flow.StepApart<NewItemTests>("an API Controller goes into a web projects Controllers folder in its namespace builds and one Undo takes it away", t => t.An_API_Controller_goes_into_a_web_projects_Controllers_folder_in_its_namespace_builds_and_one_Undo_takes_it_away());
        Flow.StepApart<NewItemTests>("a Razor Component goes into a Blazor projects pages Razor templates listed first and one Undo takes it away", t => t.A_Razor_Component_goes_into_a_Blazor_projects_pages_Razor_templates_listed_first_and_one_Undo_takes_it_away());
        Flow.StepApart<NewItemTests>("a gitignore goes into the solutions folder from its New Item and one Undo takes it away", t => t.A_gitignore_goes_into_the_solutions_folder_from_its_New_Item_and_one_Undo_takes_it_away());
        Flow.StepApart<NewItemTests>("the Add menu makes a gitignore and a gitattributes in the folder and opens the editorconfig already there", t => t.The_Add_menu_makes_a_gitignore_and_a_gitattributes_in_the_folder_and_opens_the_editorconfig_already_there());
    }

    /// <summary>An API Controller added to a web project's Controllers folder from the folder's Add menu: the web project's
    /// controllers lead the list, the namespace offered is the folder's, the file made is an [ApiController] in it, opened,
    /// and the project builds; one Undo in the Solution view takes the file away.</summary>
    private void An_API_Controller_goes_into_a_web_projects_Controllers_folder_in_its_namespace_builds_and_one_Undo_takes_it_away()
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
    private void A_Razor_Component_goes_into_a_Blazor_projects_pages_Razor_templates_listed_first_and_one_Undo_takes_it_away()
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
    private void A_gitignore_goes_into_the_solutions_folder_from_its_New_Item_and_one_Undo_takes_it_away()
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

    /// <summary>The solution's Add menu makes its folder's .gitignore, dotnet new's with Passenger's section after, and its
    /// .gitattributes, each opened, one Undo taking the last away; the .editorconfig already there is opened as it is. A
    /// .gitattributes marks a driver nothing declares and an eol git does not take, and completes a diff driver, git's own
    /// and the repository's.</summary>
    private void The_Add_menu_makes_a_gitignore_and_a_gitattributes_in_the_folder_and_opens_the_editorconfig_already_there()
    {
        WebProject(controllers: true);
        const string Own = "root = true\n\n# The person's own.\n";
        TestFolders.Write(place, ".editorconfig", Own);
        TestFolders.Write(place, "Docs/.gitattributes", "*.md diff=mine\n*.txt diff=nosuch eol=cr\n");
        TestGit.Init(place);
        TestGit.Run(place, "config", "diff.mine.textconv", "cat");
        string folder = PathNames.Canonical(place);
        MainWindow window = Ui.OpenFolder(place, languageServer: false);
        try
        {
            string ignore = Path.Combine(folder, ".gitignore");
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", ".gitignore File");
            Ui.WaitFor(() => window.EditorArea.Active?.Path == ignore, () => $"the .gitignore made and opened: {window.Status}", TimeSpan.FromMinutes(3));
            string ignored = File.ReadAllText(ignore);
            Assert.True(ignored.Contains("dotnet new gitignore", StringComparison.Ordinal) && ignored.EndsWith(RepositoryFiles.PassengerIgnores, StringComparison.Ordinal),
                $"The .gitignore made is not dotnet new's with Passenger's section after:\n{ignored}");

            string attributes = Path.Combine(folder, ".gitattributes");
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", ".gitattributes File");
            Ui.WaitFor(() => window.EditorArea.Active?.Path == attributes, () => $"the .gitattributes made and opened: {window.Status}");
            Assert.Equal(RepositoryFiles.GitAttributes, File.ReadAllText(attributes));

            string config = Path.Combine(folder, ".editorconfig");
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", "Open .editorconfig");
            Ui.WaitFor(() => window.EditorArea.Active?.Path == config, () => $"the .editorconfig there opened: {window.Status}");
            Assert.Equal(Own, File.ReadAllText(config));

            Undone(window, attributes);

            window.OpenFile(Path.Combine(folder, "Docs", ".gitattributes"));
            OpenDocument docs = window.EditorArea.Active!;
            string[] marked = ["nosuch = No diff driver nosuch: git has none built in, and its configuration declares no [diff \"nosuch\"]", "cr = eol takes lf or crlf, not cr"];
            Ui.WaitFor(() => Marked().SequenceEqual(marked), () => $"The .gitattributes' marks are {string.Join("; ", Marked())}");
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            Ui.PlaceAfter(editor, "eol=cr");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("*.cs diff=");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("csharp") && window.Assist.CompletionLabels.Contains("mine"),
                () => $"diff= lists {string.Join(", ", window.Assist.CompletionLabels)}, not git's own drivers and the repository's");
            window.KeyTextInput("csh");
            Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "csharp", () => $"diff=csh lists {string.Join(", ", window.Assist.CompletionLabels)} first");
            Ui.Press(window, "ENTER");
            Assert.EndsWith("\n*.cs diff=csharp\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

            IEnumerable<string> Marked() => docs.Markers.Select(m => $"{docs.Text.Snapshot.GetText(m.Range.Start, m.Range.Length)} = {m.Message}");
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
