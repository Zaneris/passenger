namespace Passenger.App.Templates;

/// <summary>A template package New Project offers to install when dotnet new does not have it: by its NuGet id, what
/// the offer says it is for, the version its kinds were taken from, and the kinds it gives. One a .NET workload brings
/// (<see cref="IsWorkload"/>) is named by the workload's id and installed with it.</summary>
public sealed record TemplatePack(string Id, string Offer, string Version, string Author, IReadOnlyList<PackKind> Kinds)
{
    /// <summary>Whether a .NET workload of this id brings the templates, which dotnet workload install installs.</summary>
    public bool IsWorkload { get; init; }

    /// <summary>The package or workload as the dialog names it.</summary>
    public string Named => IsWorkload ? $"the .NET {Id} workload" : Id;

    /// <summary>The .NET command line that installs it.</summary>
    public IReadOnlyList<string> Install => IsWorkload ? ["workload", "install", Id] : ProjectTemplates.New(["install", Id]);

    /// <summary>What Install runs, as the dialog says it.</summary>
    public string Command => IsWorkload ? $"dotnet workload install {Id}" : $"dotnet new install {Id}";
}

/// <summary>A project (or a solution, which only New Solution makes) a template package gives, as its template.json
/// names it.</summary>
public sealed record PackKind(string Name, string ShortName, bool IsSolution, IReadOnlyList<string> Languages, string Description,
    IReadOnlyList<string> Classifications, string DefaultName);

/// <summary>The template packages Passenger knows before they are installed: Avalonia's, .NET Aspire's, Azure
/// Functions' and AI Extensions', and those the mobile workloads bring (<see cref="Workloads"/>). Their kinds are kept here, from each package at
/// the version named, rather than read from the package, which would mean downloading every one each time the dialog
/// opens; once installed, dotnet new's own templates take their place, at the version installed.</summary>
public static class TemplatePacks
{
    private static readonly string[] Desktop = ["Desktop", "Xaml", "Avalonia", "Windows", "Linux", "macOS"];

    private static readonly string[] AspireService = ["Common", "Aspire", "Cloud", "Web", "Web API", "API", "Service"];

    private static readonly string[] Maui = ["MAUI", "Android", "iOS", "macOS", "Mac Catalyst", "Windows", "Mobile"];

    private static readonly string[] Android = ["Android", "Mobile"];

    private static readonly string[] Ios = ["iOS", "Mobile"];

    /// <summary>The workloads whose templates New Project offers where they are not installed, each on the systems it
    /// installs on (MAUI's on Linux as its Android part alone), their kinds from each workload's templates at the version named.</summary>
    private static IEnumerable<TemplatePack> Workloads()
    {
        bool apple = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();
        yield return new TemplatePack(apple ? "maui" : "maui-android", "Install the .NET MAUI workload for creating apps for Android, iOS, Mac Catalyst and Windows from one project",
            "11.0.0", "Microsoft",
        [
            new(".NET MAUI App", "maui", false, ["C#"], "A project for creating a .NET MAUI application for iOS, Android, Mac Catalyst and WinUI", Maui, "MauiApp1"),
            new(".NET MAUI Blazor Hybrid App", "maui-blazor", false, ["C#"], "A project for creating a .NET MAUI application for iOS, Android, Mac Catalyst and WinUI, using Blazor Hybrid",
                [.. Maui, "Blazor", "Blazor Hybrid"], "MauiApp1"),
            new(".NET MAUI Multi-Project App", "maui-multiproject", true, ["C#"],
                "A project for creating a .NET MAUI application for iOS, Android, Mac Catalyst and WinUI with multiple, separate app projects.", Maui, "MauiApp1"),
            new(".NET MAUI Class Library", "mauilib", false, ["C#"], "A project for creating a .NET MAUI class library", Maui, "MauiLib1"),
        ]) { IsWorkload = true };
        yield return new TemplatePack("android", "Install the .NET android workload for creating Android apps", "37.2.31", "Microsoft",
        [
            new("Android Application", "android", false, ["C#"], "A project for creating a .NET for Android application", Android, "AndroidApp1"),
            new("Android Class Library", "androidlib", false, ["C#"], "A project for creating a .NET for Android class library", Android, "AndroidLib1"),
            new("Android Wear Application", "androidwear", false, ["C#"], "A project for creating a Wear OS app using .NET for Android", Android, "AndroidApp1"),
        ]) { IsWorkload = true };
        if (!apple)
        {
            yield break;
        }
        yield return new TemplatePack("ios", "Install the .NET ios workload for creating iOS apps", "27.2.11039", "Microsoft",
        [
            new("iOS Application", "ios", false, ["C#"], "A project for creating a .NET iOS application", Ios, "iOSApp1"),
            new("iOS Tabbed Application", "ios-tabbed", false, ["C#"], "A project for creating a .NET iOS tabbed application", Ios, "iOSTabbedApp1"),
        ]) { IsWorkload = true };
        yield return new TemplatePack("maccatalyst", "Install the .NET maccatalyst workload for creating Mac Catalyst apps", "27.2.11039", "Microsoft",
        [
            new("Mac Catalyst Application", "maccatalyst", false, ["C#"], "A project for creating a .NET Mac Catalyst application", ["Desktop", "macOS", "Mac Catalyst"],
                "MacCatalystApp1"),
        ]) { IsWorkload = true };
    }

    public static IReadOnlyList<TemplatePack> All { get; } =
    [
        new("Avalonia.Templates", "Install Avalonia templates for creating cross-platform desktop, mobile and browser apps", "12.1.6", "AvaloniaUI",
        [
            new("Avalonia .NET App", "avalonia.app", false, ["C#", "F#"], "A cross-platform Avalonia UI Application targeting Desktop", Desktop, "AvaloniaApplication"),
            new("Avalonia .NET MVVM App", "avalonia.mvvm", false, ["C#", "F#"], "A cross-platform Avalonia UI Application using the MVVM pattern targeting Desktop",
                Desktop, "AvaloniaApplication"),
            new("Avalonia Cross Platform Application", "avalonia.xplat", true, ["C#", "F#"],
                "A cross-platform Avalonia UI Application using the MVVM pattern targeting Desktop, Mobile and Browser with WebAssembly",
                ["Desktop", "Xaml", "Avalonia", "Browser", "Mobile", "Android", "iOS"], "AvaloniaApplication"),
        ]),
        new("Aspire.ProjectTemplates", "Install Aspire templates for creating cloud-native apps", "13.6.5", "Microsoft",
        [
            new("Aspire Starter App (ASP.NET Core/Blazor)", "aspire-starter", true, ["C#"],
                "A project template for creating an Aspire app with a Blazor web frontend and web API backend service, with optional Redis caching.",
                [.. AspireService, "Blazor", "Test", "MSTest", "NUnit", "xUnit"], "AspireApp"),
            new("Aspire Starter App (ASP.NET Core/React, C# AppHost)", "aspire-ts-cs-starter", true, ["C#"],
                "A project template for creating an Aspire app with a React frontend and a C# Minimal API backend service, with optional Redis caching.",
                [.. AspireService, "TypeScript", "React"], "AspireApp"),
            new("Aspire Empty App", "aspire", true, ["C#"], "A project template for creating an empty Aspire app.", AspireService, "AspireApp"),
            new("Aspire AppHost", "aspire-apphost", false, ["C#"], "A project template for creating an Aspire AppHost (orchestrator) project.",
                ["Common", "Aspire", "Cloud"], "AppHost"),
            new("Aspire Single-File App Host", "aspire-apphost-singlefile", false, ["C#"], "A minimal Aspire AppHost for single-file scenarios (.NET 10+).",
                ["Common", "Aspire", "AppHost", "SingleFile"], "AspireSingleFileHost"),
            new("Aspire Service Defaults", "aspire-servicedefaults", false, ["C#"], "A project template for creating an Aspire service defaults project.",
                AspireService, "ServiceDefaults"),
            new("Aspire Test Project (MSTest)", "aspire-mstest", false, ["C#"], "A project that contains MSTest integration tests of an Aspire AppHost project.",
                [.. AspireService, "Test", "MSTest"], "Tests"),
            new("Aspire Test Project (NUnit)", "aspire-nunit", false, ["C#"], "A project that contains NUnit integration tests of an Aspire AppHost project.",
                [.. AspireService, "Test", "NUnit"], "Tests"),
            new("Aspire Test Project (xUnit)", "aspire-xunit", false, ["C#"], "A project that contains xUnit.net integration tests of an Aspire AppHost project.",
                [.. AspireService, "Test", "xUnit"], "Tests"),
        ]),
        // Its template has no description of its own; the package's says what it makes.
        new("Microsoft.Azure.Functions.Worker.ProjectTemplates", "Install Azure Functions templates for creating serverless apps", "4.0.5708", "Microsoft",
        [
            new("Azure Functions", "func", false, ["C#", "F#"], "Azure Functions .NET Isolated Project Template Pack for Microsoft Template Engine",
                ["Azure Functions", "Solution"], "Company.FunctionApp"),
        ]),
        // Published as previews only, which dotnet new install takes when there is no release.
        new("Microsoft.Extensions.AI.Templates", "Install Microsoft AI Extensions templates for creating and integrating AI services", "10.11.0-preview.3.26513.7", "Microsoft",
        [
            new("AI Chat Web App", "aichatweb", false, ["C#"],
                "A project template for creating an AI chat application, which uses retrieval-augmented generation (RAG) to chat with your own data.",
                ["Common", "AI", "Web", "Blazor", "Aspire"], "ChatApp"),
        ]),
    ];

    /// <summary>Each known kind dotnet new has no template for (none of the installed has its short name), as a template
    /// whose <see cref="ProjectTemplate.Pack"/> is the package that brings it.</summary>
    public static IEnumerable<ProjectTemplate> Offered(IReadOnlyList<ProjectTemplate> installed)
    {
        HashSet<string> had = new(installed.SelectMany(t => t.ShortNames), StringComparer.OrdinalIgnoreCase);
        foreach (TemplatePack pack in All.Concat(Workloads()))
        {
            foreach (PackKind kind in pack.Kinds.Where(k => !had.Contains(k.ShortName)))
            {
                List<TemplateVariant> variants = [.. kind.Languages.Select(language =>
                    new TemplateVariant($"{pack.Id}::{kind.ShortName}::{language}", language, 0, kind.Description, null, [], null, []))];
                yield return new ProjectTemplate(kind.Name, [kind.ShortName], pack.Author, kind.Classifications, kind.DefaultName, variants)
                {
                    IsSolution = kind.IsSolution,
                    Pack = pack,
                };
            }
        }
    }
}
