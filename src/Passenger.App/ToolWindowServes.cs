using Passenger.App.Migrations;
using Passenger.App.Run;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The kinds of project, or what runs, that a tool window may serve, as the open folder holds them
/// (<see cref="FolderHolding"/>).</summary>
[Flags]
public enum FolderHolds
{
    None = 0,

    /// <summary>A Godot project: a game a project builds on Godot.NET.Sdk, or one of GDScript alone.</summary>
    GodotProject = 1,

    /// <summary>An Aspire AppHost: a project Aspire's SDK makes one, or a single-file AppHost.</summary>
    AppHost = 2,

    /// <summary>An ASP.NET Core app: a project a web SDK builds.</summary>
    WebApp = 4,

    /// <summary>An .http or .rest file of requests.</summary>
    HttpFile = 8,

    /// <summary>A project referencing EF Core or a provider of it.</summary>
    EfCore = 16,

    /// <summary>A Reqnroll or SpecFlow project.</summary>
    StepDefinitions = 32,

    /// <summary>An app Passenger's inspector looks into (an Avalonia, WPF or .NET MAUI app), or one it looks into now.</summary>
    InspectedApp = 64,
}

/// <summary>What a tool window serves, declared as each is made (<see cref="Docking.ToolWindow.Serves"/>): every folder, or
/// kinds of project the open folder must hold one of. Such a window shows (its stripe icon, its tab, its place in a layout)
/// only while the folder holds one, and View › Tool Windows and its action grey it saying why (<see cref="WhyNot"/>); its
/// place in a kept or a theme's layout stays meanwhile, so it comes back where it stood.</summary>
public sealed class ToolWindowServes
{
    private readonly string? why;

    private ToolWindowServes(FolderHolds kinds, string? why)
    {
        Kinds = kinds;
        this.why = why;
    }

    /// <summary>The kinds of project it serves, any one of which the folder must hold; none for a window of every folder.</summary>
    public FolderHolds Kinds { get; }

    /// <summary>Every folder: the Explorer, Find, the Terminal, Problems, Git and the like.</summary>
    public static ToolWindowServes EveryFolder { get; } = new(FolderHolds.None, null);

    /// <summary>A Godot project, whose game the Game window draws.</summary>
    public static ToolWindowServes GodotProjects { get; } = new(FolderHolds.GodotProject, Refusals.NoGodotProject);

    /// <summary>An Aspire AppHost, whose resources the Aspire window shows.</summary>
    public static ToolWindowServes AppHosts { get; } = new(FolderHolds.AppHost, Refusals.NoAppHost);

    /// <summary>An ASP.NET Core app, whose live requests the Requests window hears.</summary>
    public static ToolWindowServes WebApps { get; } = new(FolderHolds.WebApp, Refusals.NoWebApp);

    /// <summary>An ASP.NET Core app's routes, or an .http file's requests, which the Endpoints window sends.</summary>
    public static ToolWindowServes WebRequests { get; } = new(FolderHolds.WebApp | FolderHolds.HttpFile, Refusals.NoWebRequests);

    /// <summary>An EF Core project, whose migrations the Migrations window lists.</summary>
    public static ToolWindowServes EfCore { get; } = new(FolderHolds.EfCore, Refusals.NoEfCore);

    /// <summary>A Reqnroll or SpecFlow project, whose step definitions the Step Library lists.</summary>
    public static ToolWindowServes StepDefinitions { get; } = new(FolderHolds.StepDefinitions, Refusals.NoStepDefinitions);

    /// <summary>An app Live Visual Tree looks into: an Avalonia, WPF or .NET MAUI app, a Godot game, or one inspected now.</summary>
    public static ToolWindowServes InspectedApps { get; } = new(FolderHolds.InspectedApp | FolderHolds.GodotProject, Refusals.NoInspectedApp);

    /// <summary>Why it serves nothing in a folder holding <paramref name="holds"/>, in Refusals' words; null where it serves
    /// it. With no folder open (null), only a window of every folder serves.</summary>
    public string? WhyNot(FolderHolds? holds) => Kinds == FolderHolds.None ? null
        : holds is not { } held ? Refusals.NoFolder
        : (held & Kinds) != 0 ? null
        : why;
}

/// <summary>What the open folder holds of the kinds of project a tool window may serve (<see cref="ToolWindowServes"/>), read
/// from its projects as evaluated and what the session found and runs, never from a walk of its own.</summary>
public static class FolderHolding
{
    /// <summary>The packages, each with its family, a Reqnroll or SpecFlow project takes.</summary>
    private static readonly string[] StepPackages = ["Reqnroll", "SpecFlow"];

    /// <summary>What a folder's projects and files hold: its <paramref name="projects"/> as evaluated, the Godot projects of
    /// GDScript alone its walk found, and whether it holds a single-file AppHost and an .http or .rest file. An app inspected
    /// now is the session's to add (<see cref="FolderHolds.InspectedApp"/>).</summary>
    public static FolderHolds Of(IReadOnlyList<ProjectModel> projects, IReadOnlyList<string> godotGames, bool appHostFiles, bool httpFiles)
    {
        FolderHolds holds = FolderHolds.None;
        if (godotGames.Count > 0 || projects.Any(p => p.GodotFolder is not null))
        {
            holds |= FolderHolds.GodotProject;
        }
        if (appHostFiles || projects.Any(p => p.IsAspireHost))
        {
            holds |= FolderHolds.AppHost;
        }
        if (projects.Any(p => p.IsWeb))
        {
            holds |= FolderHolds.WebApp;
        }
        if (httpFiles)
        {
            holds |= FolderHolds.HttpFile;
        }
        if (projects.Any(MigrationsController.UsesEf))
        {
            holds |= FolderHolds.EfCore;
        }
        if (projects.Any(p => p.Packages.Any(package => StepPackages.Any(family => Of(package.Name, family)))))
        {
            holds |= FolderHolds.StepDefinitions;
        }
        if (projects.Any(p => p.IsRunnable && DesktopPrograms.Inspected(p, projects)))
        {
            holds |= FolderHolds.InspectedApp;
        }
        return holds;
    }

    /// <summary>Whether a file is one of requests the Endpoints window sends: an .http or a .rest file.</summary>
    public static bool IsRequests(string path) =>
        path.EndsWith(".http", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rest", StringComparison.OrdinalIgnoreCase);

    private static bool Of(string package, string family) => package.Equals(family, StringComparison.OrdinalIgnoreCase)
        || (package.Length > family.Length && package[family.Length] == '.' && package.StartsWith(family, StringComparison.OrdinalIgnoreCase));
}
