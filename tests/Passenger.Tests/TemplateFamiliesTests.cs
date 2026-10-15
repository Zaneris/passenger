using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Templates;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>New Solution's templates in families, as Rider's New Solution lists its project types, read ahead: the dialog
/// opens with every family listed, the templates read once the window started; related templates are one family (Unit
/// Test's xUnit, NUnit and MSTest, Web's ASP.NET Core and Blazor apps), each a Type with its own options, a value set on
/// one kept by another that has it; the search finds a family by any of its Types; and a template installed or
/// uninstalled while the dialog is open fills in its family, the family and Type chosen kept.</summary>
public sealed class TemplateFamiliesTests(ITestOutputHelper output) : IDisposable
{
    private const string TestRunner = "Test runner";
    private const string Platform = "Microsoft.Testing.Platform";

    private readonly string settings = TestFolders.Create("settings-");
    private readonly string place = TestFolders.Create("families-");

    public void Dispose()
    {
        TestFolders.Delete(settings);
        TestFolders.Delete(place);
    }

    [AvaloniaFact]
    public void New_Solution_opens_with_every_family_listed_at_once_the_templates_read_ahead_and_the_search_finds_one_by_any_of_its_Types()
    {
        using IDisposable theme = TestsTheme();
        MainWindow window = Started();
        Ui.WaitFor(() => ProjectTemplates.Ready(Environment.CurrentDirectory) is not null, "New Solution's startup template preload", TimeSpan.FromMinutes(3));

        // The templates were read as the window started: the dialog lists them as it opens, before any read of its own.
        Assert.True(window.Perform(AppActions.NewSolution));
        NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>());
        string[] atOnce = [.. dialog.Families.Select(f => f.Name)];
        // MAUI is workload-dependent: ask the SDK in the run's hive independently of Passenger's catalogue.
        string listed = DotnetNew(["list", "--type", "project", "--columns", "tags"]);
        output.WriteLine($"dotnet new list in {ProjectTemplates.Hive ?? "the run's template home"}:\n{listed}");
        bool maui = listed.Split('\n').Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Split('/').Contains("MAUI", StringComparer.OrdinalIgnoreCase) == true);
        string[] riders = ["Console", "Class Library", .. OperatingSystem.IsWindows() ? ["Desktop"] : Array.Empty<string>(),
            "Web", "Services", "AI Extensions", "Unit Test", .. maui ? ["MAUI"] : Array.Empty<string>(), "Aspire", "Azure Functions", "Avalonia"];
        Assert.Equal(riders, atOnce);
        Assert.True(dialog.Chosen is { Name: "Console App" } && dialog.Family?.Name == "Console", $"The dialog opened on {dialog.Family?.Name}, {dialog.Chosen?.Name}");
        Assert.Empty(dialog.TypeNames);

        // Its own read, landed, changes nothing it showed.
        Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "the dialog's folder's templates to be read");
        Assert.True(dialog.Problem is null, $"The templates could not be read: {dialog.Problem}");
        Assert.Equal(atOnce, dialog.Families.Select(f => f.Name));
        Assert.Equal("Console App", dialog.Chosen?.Name);

        // Families by the templates' classifications (a test template is a Unit Test), each Type named for what it adds.
        Assert.All(dialog.Templates.Where(t => t.Classifications.Contains("Test") && !t.Classifications.Contains("Aspire")),
            t => Assert.True(t.Family == "Unit Test", $"{t.Name}, a test template, is in {t.Family}"));
        Assert.Equal(["xUnit", "NUnit", "MSTest"], Family(dialog, "Unit Test").Types.Take(3).Select(t => t.TypeName));
        Assert.Equal(["Web App (Razor Pages)", "Web App (Model-View-Controller)", "Web API", "Empty"], Family(dialog, "Web").Types.Take(4).Select(t => t.TypeName));
        Assert.Contains("Blazor Web App", Family(dialog, "Web").Types.Select(t => t.TypeName));
        Assert.Equal(["Class Library", "Blazor Web Worker"], Family(dialog, "Class Library").Types.Take(2).Select(t => t.TypeName));
        Assert.Equal(["Worker Service"], Family(dialog, "Services").Types.Select(t => t.Name));
        Assert.True(Family(dialog, "Aspire").NotInstalled, "Aspire's family is not marked not installed though dotnet new has none of it");
        Assert.Equal(["App", "MVVM App", "Cross Platform Application"], Family(dialog, "Avalonia").Types.Select(t => t.TypeName));

        // The search finds a family by a Type's name, opened on that Type; each family found opens on the one it names.
        Click(dialog, dialog.SearchBox);
        Type(dialog, "nunit");
        Assert.Equal(["Unit Test", "Aspire"], dialog.Listed.Select(f => f.Name));
        Assert.True(dialog.Family?.Name == "Unit Test" && dialog.Chosen?.ShortName == "nunit", $"The search for nunit opened {dialog.Family?.Name} on {dialog.Chosen?.Name}");
        Assert.Equal("NUnit", dialog.TypeChooser.SelectedItem is ProjectTemplate { TypeName: var shown } ? shown : null);
        Click(dialog, dialog.FamilyRow("Aspire")!);
        Assert.Equal("aspire-nunit", dialog.Chosen?.ShortName);

        // A Type's words find its family on it, a family's name on its last Type; Down chooses the next family found.
        Search(dialog, "razor pages");
        Assert.True(dialog.Family?.Name == "Web" && dialog.Chosen?.ShortName == "webapp", $"The search for razor pages opened {dialog.Family?.Name} on {dialog.Chosen?.Name}");
        Search(dialog, "mvvm");
        Assert.True(dialog.Family?.Name == "Avalonia" && dialog.Chosen?.ShortName == "avalonia.mvvm", $"The search for mvvm opened {dialog.Family?.Name} on {dialog.Chosen?.Name}");
        Search(dialog, "test");
        Assert.Equal(["Unit Test", "Aspire"], dialog.Listed.Select(f => f.Name));
        Assert.True(dialog.Family?.Name == "Unit Test" && dialog.Chosen?.ShortName == "nunit", $"Unit Test, found by its name, did not open on the Type chosen in it last: {dialog.Chosen?.Name}");
        Click(dialog, dialog.SearchBox);
        Press(dialog, Key.Down);
        Assert.True(dialog.Family?.Name == "Aspire" && dialog.Chosen?.ShortName == "aspire-nunit", $"Down from the search chose {dialog.Family?.Name}, {dialog.Chosen?.Name}");

        // A search that finds nothing lists nothing and offers nothing to create.
        Search(dialog, "nothing like it");
        Assert.True(dialog.Listed.Count == 0 && dialog.Chosen is null, $"A search for nothing listed [{string.Join(", ", dialog.Listed.Select(f => f.Name))}], {dialog.Chosen?.Name} chosen");
        Assert.False(dialog.CreateButton.IsEnabled, "Create was offered with nothing found");

        // Escape clears the search, the family and Type chosen before it back; then Escape cancels.
        Press(dialog, Key.Escape);
        Assert.Equal(string.Empty, dialog.Query);
        Assert.Equal(atOnce, dialog.Listed.Select(f => f.Name));
        Assert.True(dialog.IsVisible && dialog.Chosen?.ShortName == "aspire-nunit", $"Escape in the search left {dialog.Chosen?.Name} chosen, the dialog shown: {dialog.IsVisible}");
        Press(dialog, Key.Escape);
        Assert.False(dialog.IsVisible, "Escape with no search left the dialog open");
        window.Close();
    }

    [AvaloniaFact]
    public void Unit_Test_shows_each_frameworks_options_keeps_a_value_set_where_the_next_Type_has_it_and_makes_that_kind_of_project()
    {
        using IDisposable theme = TestsTheme();
        // Restores take the run's own feed alone, so a project made never reaches nuget.org for its packages.
        AvaloniaApp.UseFeed(place);
        MainWindow window = Started();
        Click(window, window.Welcome.NewSolutionButton);
        NewProjectDialog dialog = Opened(window);
        dialog.Folder = place;

        Click(dialog, dialog.FamilyRow("Unit Test")!);
        Assert.Equal("xunit", dialog.Chosen?.ShortName);
        Assert.Equal(["xUnit", "NUnit", "MSTest"], dialog.TypeNames.Take(3));
        Assert.Equal(["xUnit.net version", TestRunner], dialog.OptionLabels);
        Assert.StartsWith("dotnet new xunit ", dialog.Command, StringComparison.Ordinal);

        Choose(dialog, "NUnit");
        Assert.Equal([TestRunner], dialog.OptionLabels);
        Assert.StartsWith("dotnet new nunit ", dialog.Command, StringComparison.Ordinal);
        Assert.Equal("VSTest", Value(dialog, TestRunner));
        Pick(dialog, TestRunner, Platform);
        Assert.Contains($"--test-runner {Platform}", dialog.Command, StringComparison.Ordinal);

        // MSTest has its own options, and the test runner set on NUnit, which it has too.
        Choose(dialog, "MSTest");
        Assert.Equal(["Use MSTest.Sdk", TestRunner, "Testing extensions profile", "Use explicit Program.cs file", "Coverage tool", "Fixture"], dialog.OptionLabels);
        Assert.Equal(Platform, Value(dialog, TestRunner));
        Assert.StartsWith("dotnet new mstest ", dialog.Command, StringComparison.Ordinal);
        Assert.Contains($"--test-runner {Platform}", dialog.Command, StringComparison.Ordinal);
        Pick(dialog, "Fixture", "TestInitialize");

        // xUnit has no fixture to keep it; MSTest, chosen again, has it still.
        Choose(dialog, "xUnit");
        Assert.Equal(["xUnit.net version", TestRunner], dialog.OptionLabels);
        Assert.Equal(Platform, Value(dialog, TestRunner));
        Assert.DoesNotContain("--fixture", dialog.Command, StringComparison.Ordinal);
        Choose(dialog, "MSTest");
        Assert.Equal("TestInitialize", Value(dialog, "Fixture"));
        Assert.Contains("--fixture TestInitialize", dialog.Command, StringComparison.Ordinal);

        dialog.SolutionName = "Checks";
        Created(dialog, "the MSTest project");
        string solution = Path.Combine(PathNames.Canonical(place), "Checks");
        Assert.True(window.WorkspaceModel?.OpenedAs?.StartsWith(Path.Combine(solution, "Checks."), StringComparison.Ordinal) == true,
            $"The new solution was made, but the window opened {window.WorkspaceModel?.OpenedAs ?? "nothing"}");
        Assert.Matches("Include=\"MSTest\"|Sdk=\"MSTest\\.Sdk", Text(solution, "Checks", "Checks.csproj"));
        Assert.Contains("TestInitialize", string.Concat(Directory.EnumerateFiles(Path.Combine(solution, "Checks"), "*.cs").Select(File.ReadAllText)), StringComparison.Ordinal);

        // Add, New Project makes the other frameworks' projects into it, each chosen as a Type of Unit Test.
        foreach ((string type, string name, string package) in ((string, string, string)[])[("NUnit", "Checks.NUnit", "Include=\"NUnit\""), ("xUnit", "Checks.XUnit", "Include=\"xunit")])
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog adding = Opened(window);
            adding.ProjectName = name;
            Click(adding, adding.FamilyRow("Unit Test")!);
            Choose(adding, type);
            Assert.Equal(name, adding.ProjectName);
            Created(adding, $"the {type} project");
            Assert.Contains(package, Text(solution, name, $"{name}.csproj"), StringComparison.Ordinal);
            Assert.Contains(name, window.WorkspaceModel!.Projects.Select(p => p.Name));
        }
        window.Close();
    }

    [AvaloniaFact]
    public void A_template_installed_while_the_dialog_is_open_fills_in_its_family_the_family_Type_and_values_chosen_kept()
    {
        using IDisposable theme = TestsTheme();
        string probe = WriteProbeTemplate();
        MainWindow window = Started();
        Assert.True(window.Perform(AppActions.NewSolution));
        NewProjectDialog dialog = Opened(window);
        Type(dialog, "Probing");
        Click(dialog, dialog.FamilyRow("Unit Test")!);
        Choose(dialog, "NUnit");
        Pick(dialog, TestRunner, Platform);
        bool installed = false;
        try
        {
            // A terminal's dotnet new install into the run's own home: another author's test template joins Unit Test.
            DotnetNew(["install", probe]);
            installed = true;
            Ui.WaitFor(() => dialog.Family?.Types.Any(t => t.ShortName == "probetest") == true, "the template installed to join Unit Test in the open dialog", TimeSpan.FromSeconds(60));
            Assert.Contains("Probe", dialog.TypeNames);
            Assert.True(dialog.Family?.Name == "Unit Test" && dialog.Chosen?.ShortName == "nunit", $"The installed template moved the choice to {dialog.Family?.Name}, {dialog.Chosen?.Name}");
            Assert.Equal("Probing", dialog.SolutionName);
            Assert.Equal(Platform, Value(dialog, TestRunner));
            Assert.Equal("Unit Test", Family(dialog, "Unit Test").Types.Single(t => t.ShortName == "probetest").Family);

            DotnetNew(["uninstall", probe]);
            installed = false;
            Ui.WaitFor(() => dialog.Family?.Types.All(t => t.ShortName != "probetest") == true, "the template uninstalled to leave Unit Test in the open dialog", TimeSpan.FromSeconds(60));
            Assert.True(dialog.Family?.Name == "Unit Test" && dialog.Chosen?.ShortName == "nunit", $"The uninstall moved the choice to {dialog.Family?.Name}, {dialog.Chosen?.Name}");
        }
        finally
        {
            if (installed)
            {
                DotnetNew(["uninstall", probe]);
            }
        }
        dialog.Cancel();
        window.Close();
    }

    /// <summary>A test project template of the tests' own, in the test's folder: Probe Test Project (probetest), classified
    /// as a test.</summary>
    private string WriteProbeTemplate()
    {
        string folder = Directory.CreateDirectory(Path.Combine(place, "ProbeTemplate", ".template.config")).Parent!.FullName;
        File.WriteAllText(Path.Combine(folder, ".template.config", "template.json"), """
            {
              "$schema": "http://json.schemastore.org/template",
              "author": "Passenger's tests",
              "classifications": [ "Test", "Probe" ],
              "identity": "Passenger.Tests.ProbeTest.CSharp",
              "groupIdentity": "Passenger.Tests.ProbeTest",
              "name": "Probe Test Project",
              "shortName": "probetest",
              "sourceName": "ProbeTest",
              "preferNameDirectory": true,
              "tags": { "language": "C#", "type": "project" }
            }
            """);
        File.WriteAllText(Path.Combine(folder, "ProbeTest.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        return PathNames.Canonical(folder);
    }

    /// <summary>dotnet new with these arguments in the run's own template home, as a terminal runs it.</summary>
    private string DotnetNew(string[] arguments)
    {
        ChildResult ran = Task.Run(() => ChildProcesses.RunAsync(ProjectTemplates.NewCommand(arguments, place), limit: TimeSpan.FromMinutes(2))).GetAwaiter().GetResult();
        Assert.True(ran.Succeeded, $"dotnet new {string.Join(' ', arguments)} failed:\n{ran.Output}{ran.Error}");
        return ran.Output;
    }

    /// <summary>A search put in the search box, as typing it there would.</summary>
    private static void Search(NewProjectDialog dialog, string query)
    {
        dialog.Query = query;
        Ui.Settle();
    }

    private static TemplateFamily Family(NewProjectDialog dialog, string name) =>
        dialog.Families.FirstOrDefault(f => f.Name == name)
        ?? throw new InvalidOperationException($"The dialog lists no {name}: [{string.Join(", ", dialog.Families.Select(f => f.Name))}]");

    /// <summary>A Type of the family chosen, chosen as the Type chooser's list chooses one.</summary>
    private static void Choose(NewProjectDialog dialog, string type)
    {
        Assert.True(dialog.TypeNames.Count > 1, $"{dialog.Family?.Name} shows no Type to choose");
        dialog.TypeChooser.SelectedItem = dialog.Family!.Types.FirstOrDefault(t => t.TypeName == type)
            ?? throw new InvalidOperationException($"{dialog.Family.Name} has no Type {type}: [{string.Join(", ", dialog.TypeNames)}]");
        Ui.Settle();
        Assert.Equal(type, dialog.Chosen?.TypeName);
    }

    /// <summary>A choice of an option's list, as its drop-down chooses one.</summary>
    private static void Pick(NewProjectDialog dialog, string option, string choice)
    {
        ComboBox editor = dialog.OptionEditor(option) as ComboBox ?? throw new InvalidOperationException($"{dialog.Chosen?.Name} shows no list for {option}: [{string.Join(", ", dialog.OptionLabels)}]");
        editor.SelectedItem = choice;
        Ui.Settle();
        Assert.Equal(choice, Value(dialog, option));
    }

    private static string? Value(NewProjectDialog dialog, string option) =>
        dialog.OptionEditor(option) is ComboBox { SelectedItem: string chosen } ? chosen
        : throw new InvalidOperationException($"{dialog.Chosen?.Name} shows no list for {option}: [{string.Join(", ", dialog.OptionLabels)}]");

    /// <summary>Create, as its button does, failing with why when it made nothing.</summary>
    private static void Created(NewProjectDialog dialog, string what)
    {
        Click(dialog, dialog.CreateButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, $"dotnet new to make {what}", TimeSpan.FromMinutes(5));
        Assert.True(dialog.Problem is null, $"{what} was not made: {dialog.Problem}");
    }

    private static string Text(params string[] parts) => File.ReadAllText(Path.Combine(parts));

    /// <summary>The dialog an action opened, once its folder's templates are read.</summary>
    private static NewProjectDialog Opened(MainWindow window)
    {
        Ui.Settle();
        NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>(), d => d.IsVisible);
        Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
        Assert.True(dialog.Families.Count > 0, $"The dialog lists no templates: {dialog.Problem}");
        return dialog;
    }

    /// <summary>The window a person starts, its state in the test's folder, on the Welcome screen.</summary>
    private MainWindow Started()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.KeepSettings();
        window.Start(LaunchOptions.Empty);
        Ui.Settle();
        return window;
    }

    private static void Type(TopLevel window, string text)
    {
        window.KeyTextInput(text);
        Ui.Settle();
    }

    private static void Press(TopLevel window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible)
        {
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        }
        Ui.Settle();
    }

    private static void Click(TopLevel window, Control control)
    {
        Point at = control.TranslatePoint(new Point(Math.Min(12, control.Bounds.Width / 2), control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The control is not in the window");
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }

    /// <summary>Puts the tests' theme back once a window that keeps its settings has put the person's in force.</summary>
    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

    private sealed class ThemeBack(Avalonia.Styling.ThemeVariant? theme) : IDisposable
    {
        public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = theme;
    }
}
