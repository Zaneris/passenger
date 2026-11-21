using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>What a project builds, as MSBuild's evaluation of it says (<see cref="ProjectKinds.Of"/>): its icon wherever a
/// project is listed, and its words (<see cref="ProjectKinds.Words"/>) to a screen reader, on hover and to an agent.
/// <see cref="Other"/> until it is evaluated, while it is unloaded, and where none of these fits.</summary>
public enum ProjectKind
{
    Other,

    /// <summary>A Docker Compose project (.dcproj), which is never evaluated.</summary>
    Compose,

    /// <summary>A shared project (.shproj), which is never evaluated: files each project importing its items compiles.</summary>
    Shared,

    /// <summary>An Aspire AppHost (<see cref="ProjectModel.IsAspireHost"/>).</summary>
    AspireHost,

    /// <summary>A project of tests (<see cref="ProjectModel.IsTestProject"/>).</summary>
    Test,

    /// <summary>A Godot game's C#, which Godot.NET.Sdk builds and Godot runs (<see cref="ProjectModel.GodotFolder"/>).</summary>
    GodotGame,

    /// <summary>An Azure Functions app (<see cref="ProjectModel.IsFunctionsApp"/>).</summary>
    Functions,

    /// <summary>A .NET MAUI app (UseMaui).</summary>
    Maui,

    /// <summary>An app for Android, iOS or tvOS of no UI framework drawn apart.</summary>
    Mobile,

    /// <summary>A Blazor app: a web app of Razor components, or one running in the browser.</summary>
    Blazor,

    /// <summary>An app of Avalonia's (<see cref="ProjectModel.CompilesAvaloniaXaml"/>).</summary>
    Avalonia,

    /// <summary>A WPF app.</summary>
    Wpf,

    /// <summary>A Windows Forms app.</summary>
    WindowsForms,

    /// <summary>A web app of Razor pages or MVC views.</summary>
    WebApp,

    /// <summary>A web app with no Razor pages, views or components: an API's.</summary>
    WebApi,

    /// <summary>Another windowed app (OutputType WinExe, or a Mac app).</summary>
    Desktop,

    /// <summary>Another executable.</summary>
    Console,

    /// <summary>A class library.</summary>
    Library,
}

public static class ProjectKinds
{
    /// <summary>The kind a project is, the first that fits in this order, so a project of several kinds (an AppHost and a
    /// .NET MAUI app are executables too) shows the one that says most of it: a program by its UI framework, then by where
    /// it runs. Read from its evaluation alone, never its name or the files in its folder.</summary>
    public static ProjectKind Of(ProjectModel project) => project switch
    {
        _ when WorkspaceModel.IsComposeProject(project.Path) => ProjectKind.Compose,
        _ when SharedProjects.Is(project.Path) => ProjectKind.Shared,
        { IsEvaluated: false } => ProjectKind.Other,
        { IsAspireHost: true } => ProjectKind.AspireHost,
        { IsTestProject: true } => ProjectKind.Test,
        { GodotFolder: not null, IsScriptGame: false } => ProjectKind.GodotGame,
        { IsFunctionsApp: true } => ProjectKind.Functions,
        { IsRunnable: true } => ProgramOf(project),
        { IsBrowserClient: true } => ProjectKind.Blazor,
        _ when Is(project.OutputType, Library) => ProjectKind.Library,
        _ => ProjectKind.Other,
    };

    /// <summary>A kind in the words a list says it in after a project's name ("Shop.Api, web API"); null for
    /// <see cref="ProjectKind.Other"/>.</summary>
    public static string? Words(ProjectKind kind) => kind switch
    {
        ProjectKind.Compose => "Docker Compose project",
        ProjectKind.Shared => "shared project",
        ProjectKind.AspireHost => "Aspire AppHost",
        ProjectKind.Test => "test project",
        ProjectKind.GodotGame => "Godot game",
        ProjectKind.Functions => "Azure Functions app",
        ProjectKind.Maui => ".NET MAUI app",
        ProjectKind.Mobile => "mobile app",
        ProjectKind.Blazor => "Blazor app",
        ProjectKind.Avalonia => "Avalonia app",
        ProjectKind.Wpf => "WPF app",
        ProjectKind.WindowsForms => "Windows Forms app",
        ProjectKind.WebApp => "web app",
        ProjectKind.WebApi => "web API",
        ProjectKind.Desktop => "desktop app",
        ProjectKind.Console => "console app",
        ProjectKind.Library => "class library",
        _ => null,
    };

    /// <summary>A project's name with its kind's words after it, as a screen reader hears a row naming it; its name alone
    /// for <see cref="ProjectKind.Other"/>.</summary>
    public static string Said(string name, ProjectKind kind) => Words(kind) is { } words ? $"{name}, {words}" : name;

    private const string Library = "Library";

    /// <summary>A program's kind (<see cref="ProjectModel.IsRunnable"/>): by the UI framework it draws with, then by where
    /// it runs, then by whether it opens a window.</summary>
    private static ProjectKind ProgramOf(ProjectModel program)
    {
        IReadOnlyList<string> references = [.. program.Evaluated?.Dependencies.SelectMany(d => d.FrameworkReferences) ?? []];
        return program switch
        {
            _ when Is(program.Property(MSBuildNames.UseMaui), "true") => ProjectKind.Maui,
            _ when program.IsAndroidApp || Platforms(program).Any(p => p is FrameworkNames.Ios or FrameworkNames.Tvos) => ProjectKind.Mobile,
            _ when program.RunsInBrowser || (program.IsWeb && Holds(program, RazorFiles.Component)) => ProjectKind.Blazor,
            { CompilesAvaloniaXaml: true } => ProjectKind.Avalonia,
            _ when FrameworkNames.UsesWpf(references) => ProjectKind.Wpf,
            _ when FrameworkNames.UsesWindowsForms(references) => ProjectKind.WindowsForms,
            { IsWeb: true } => Holds(program, RazorFiles.Page) ? ProjectKind.WebApp : ProjectKind.WebApi,
            _ when Is(program.OutputType, "WinExe") || Platforms(program).Any(p => p is FrameworkNames.Macos or FrameworkNames.MacCatalyst) => ProjectKind.Desktop,
            _ => ProjectKind.Console,
        };
    }

    /// <summary>The platforms a project's target frameworks name (android of net11.0-android), each in lower case.</summary>
    private static IEnumerable<string> Platforms(ProjectModel project) => project.TargetFrameworks.Select(FrameworkNames.PlatformOf).OfType<string>();

    /// <summary>Whether a project's evaluation holds content of a Razor extension: the web SDK's Content items of its
    /// components (.razor) or of its pages and views (.cshtml).</summary>
    private static bool Holds(ProjectModel project, string extension) =>
        project.Evaluated?.Items.Any(i => i.Type == MSBuildNames.Content && i.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
