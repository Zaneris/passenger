using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a project builds, as its evaluation says it: each kind from what MSBuild evaluated (its SDK's flags, its
/// output type, its properties, its frameworks' references and its Razor content), never its name; a project of several
/// kinds the one that says most of it; and each kind's own words.</summary>
public class ProjectKindsTests
{
    private const string Folder = "/work/Shop";
    private const string Project = Folder + "/Shop.csproj";

    public static TheoryData<string, ProjectKind> Kinds => new()
    {
        { "an executable", ProjectKind.Console },
        { "a windowed executable", ProjectKind.Desktop },
        { "a Mac Catalyst app", ProjectKind.Desktop },
        { "a library", ProjectKind.Library },
        { "an AppHost", ProjectKind.AspireHost },
        { "a test project", ProjectKind.Test },
        { "a test project running as a program", ProjectKind.Test },
        { "a Godot game", ProjectKind.GodotGame },
        { "a Godot game's tests", ProjectKind.Test },
        { "a Functions app", ProjectKind.Functions },
        { "a MAUI app", ProjectKind.Maui },
        { "a MAUI library", ProjectKind.Library },
        { "an Android app", ProjectKind.Mobile },
        { "an iOS app", ProjectKind.Mobile },
        { "an Avalonia app for Android", ProjectKind.Mobile },
        { "an Avalonia app", ProjectKind.Avalonia },
        { "an Avalonia library", ProjectKind.Library },
        { "a WPF app", ProjectKind.Wpf },
        { "a WPF library", ProjectKind.Library },
        { "a Windows Forms app", ProjectKind.WindowsForms },
        { "a WPF and Windows Forms app", ProjectKind.Wpf },
        { "a web app of pages", ProjectKind.WebApp },
        { "a web app of components", ProjectKind.Blazor },
        { "a web app of components and pages", ProjectKind.Blazor },
        { "a web API", ProjectKind.WebApi },
        { "a standalone Blazor WebAssembly app", ProjectKind.Blazor },
        { "a Blazor Web App's client", ProjectKind.Blazor },
        { "a Razor class library", ProjectKind.Library },
        { "a library named like an API", ProjectKind.Library },
        { "a project not evaluated", ProjectKind.Other },
        { "a project MSBuild could not evaluate", ProjectKind.Other },
        { "a project of another SDK", ProjectKind.Other },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_project_is_the_kind_its_evaluation_says(string project, ProjectKind kind) =>
        Assert.Equal(kind, ProjectKinds.Of(Made(project)));

    [Fact]
    public void A_docker_compose_project_and_a_shared_project_are_their_kinds_by_their_files_never_evaluated()
    {
        Assert.Equal(ProjectKind.Compose, ProjectKinds.Of(ProjectModel.Unevaluated("/work/docker-compose.dcproj")));
        Assert.Equal(ProjectKind.Shared, ProjectKinds.Of(ProjectModel.Unevaluated("/work/Shop.Shared/Shop.Shared.shproj")));
    }

    [Fact]
    public void Every_kind_but_other_has_words_of_its_own_said_after_a_projects_name()
    {
        ProjectKind[] told = [.. Enum.GetValues<ProjectKind>().Where(k => k != ProjectKind.Other)];
        string?[] words = [.. told.Select(ProjectKinds.Words)];
        Assert.True(words.All(w => w is { Length: > 0 }), $"A kind has no words: {string.Join(", ", told.Where(k => ProjectKinds.Words(k) is null))}");
        Assert.True(words.Distinct(StringComparer.OrdinalIgnoreCase).Count() == told.Length, $"Two kinds share their words: {string.Join(", ", words)}");
        Assert.Null(ProjectKinds.Words(ProjectKind.Other));
        Assert.Equal("Shop.Api, web API", ProjectKinds.Said("Shop.Api", ProjectKind.WebApi));
        Assert.Equal("Shop", ProjectKinds.Said("Shop", ProjectKind.Other));
    }

    /// <summary>A project as MSBuild would evaluate the one described.</summary>
    private static ProjectModel Made(string described) => described switch
    {
        "an executable" => Evaluated("Exe"),
        "a windowed executable" => Evaluated("WinExe"),
        "a Mac Catalyst app" => Evaluated("Exe", frameworks: ["net11.0-maccatalyst"]),
        "a library" => Evaluated("Library"),
        "an AppHost" => ProjectModel.From(Project, Plain("Exe") with { IsAspireHost = true }),
        "a test project" => Evaluated("Library", test: true),
        "a test project running as a program" => Evaluated("Exe", test: true),
        "a Godot game" => Evaluated("Library", properties: Godot()),
        "a Godot game's tests" => Evaluated("Library", properties: Godot(), test: true),
        "a Functions app" => Evaluated("Exe", properties: new() { [CarriedProperties.AzureFunctionsVersion] = "v4" }),
        "a MAUI app" => Evaluated("Library", frameworks: ["net11.0-android", "net11.0-ios"],
            properties: new() { [MSBuildNames.UseMaui] = "true", [CarriedProperties.AndroidApplication] = "true" }),
        "a MAUI library" => Evaluated("Library", frameworks: ["net11.0-android", "net11.0-ios"], properties: new() { [MSBuildNames.UseMaui] = "true" }),
        "an Android app" => Evaluated("Library", frameworks: ["net11.0-android"], properties: new() { [CarriedProperties.AndroidApplication] = "true" }),
        "an iOS app" => Evaluated("Exe", frameworks: ["net11.0-ios"]),
        "an Avalonia app for Android" => Evaluated("Library", frameworks: ["net11.0-android"],
            properties: new() { [CarriedProperties.AndroidApplication] = "true", [CarriedProperties.EnableAvaloniaXamlCompilation] = "true" }),
        "an Avalonia app" => Evaluated("WinExe", properties: new() { [CarriedProperties.EnableAvaloniaXamlCompilation] = "true" }),
        "an Avalonia library" => Evaluated("Library", properties: new() { [CarriedProperties.EnableAvaloniaXamlCompilation] = "true" }),
        "a WPF app" => Evaluated("WinExe", frameworks: ["net11.0-windows"], shared: [FrameworkNames.Wpf]),
        "a WPF library" => Evaluated("Library", frameworks: ["net11.0-windows"], shared: [FrameworkNames.Wpf]),
        "a Windows Forms app" => Evaluated("WinExe", frameworks: ["net11.0-windows"], shared: [FrameworkNames.WindowsForms]),
        "a WPF and Windows Forms app" => Evaluated("WinExe", frameworks: ["net11.0-windows"], shared: [FrameworkNames.WindowsDesktop]),
        "a web app of pages" => Evaluated("Exe", web: true, content: ["Pages/Index.cshtml"]),
        "a web app of components" => Evaluated("Exe", web: true, content: ["Components/App.razor"]),
        "a web app of components and pages" => Evaluated("Exe", web: true, content: ["Components/App.razor", "Pages/Error.cshtml"]),
        "a web API" => Evaluated("Exe", web: true, content: ["appsettings.json"]),
        "a standalone Blazor WebAssembly app" => Evaluated("Exe", web: true,
            packages: ["Microsoft.AspNetCore.Components.WebAssembly", "Microsoft.AspNetCore.Components.WebAssembly.DevServer"]),
        "a Blazor Web App's client" => Evaluated("Exe", web: true, packages: ["Microsoft.AspNetCore.Components.WebAssembly"]),
        "a Razor class library" => Evaluated("Library", web: true, content: ["Component1.razor"]),
        "a library named like an API" => ProjectModel.From(Folder + "/Shop.Api/Shop.Api.csproj", Plain("Library") with { Path = Folder + "/Shop.Api/Shop.Api.csproj" }),
        "a project not evaluated" => ProjectModel.Unevaluated(Project),
        "a project MSBuild could not evaluate" => ProjectModel.From(Project, new EvaluatedProject { Path = Project, Problem = "The SDK 'Missing.Sdk' was not found." }),
        "a project of another SDK" => Evaluated(null, frameworks: []),
        _ => throw new ArgumentOutOfRangeException(nameof(described), described, "No such project here"),
    };

    private static ProjectModel Evaluated(string? output, IReadOnlyList<string>? frameworks = null, Dictionary<string, string>? properties = null,
        IReadOnlyList<string>? shared = null, bool test = false, bool web = false, IReadOnlyList<string>? content = null, IReadOnlyList<string>? packages = null) =>
        ProjectModel.From(Project, Plain(output, frameworks) with
        {
            IsTestProject = test,
            IsWeb = web,
            Dependencies = [new FrameworkDependencies(null, [.. (packages ?? []).Select(p => new PackageDependency(p, "11.0.0", false))], []) { FrameworkReferences = shared ?? [] }],
            Items = [.. (content ?? []).Select(c => new EvaluatedItem(MSBuildNames.Content, $"{Folder}/{c}"))],
            Properties = (properties ?? []).ToDictionary(p => p.Key, p => new EvaluatedProperty(p.Value, true)),
        });

    private static EvaluatedProject Plain(string? output, IReadOnlyList<string>? frameworks = null) => new()
    {
        Path = Project,
        OutputType = output,
        TargetFrameworks = frameworks ?? ["net11.0"],
    };

    /// <summary>What Godot.NET.Sdk's props set of a game's project: it builds it, for the Godot project in its folder.</summary>
    private static Dictionary<string, string> Godot() => new()
    {
        [CarriedProperties.UsingGodotNETSdk] = "true",
        [CarriedProperties.GodotProjectDir] = Folder + "/",
    };
}
