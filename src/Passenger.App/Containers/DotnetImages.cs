using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>.NET's own images for a project: the runtime image its app runs in (aspnet's for an ASP.NET Core app, else
/// runtime's) and the SDK's that builds it, each tagged with the .NET of its newest framework from .NET 5 on (10.0 of
/// net10.0 and NET10.0-windows), .NET 10's for a project that names none.</summary>
public static class DotnetImages
{
    private const string Repository = "mcr.microsoft.com/dotnet/";

    /// <summary>The tag of a project that names no .NET 5 or later framework.</summary>
    private const string Unknown = "10.0";

    /// <summary>The newest .NET 5 or later framework of those given (net10.0 of net8.0;net10.0), as written; null for none.</summary>
    public static string? Newest(IEnumerable<string> frameworks) =>
        frameworks.Where(f => FrameworkNames.Major(f) >= 5).MaxBy(FrameworkNames.VersionOf);

    /// <summary>The tag for a framework: 10.0 of net10.0, and of none or one before .NET 5.</summary>
    public static string Tag(string? framework) =>
        FrameworkNames.VersionOf(framework) is { Major: >= 5 } version ? $"{version.Major}.{Math.Max(0, version.Minor)}" : Unknown;

    /// <summary>The image an app runs in at a tag: aspnet's for an ASP.NET Core app, else runtime's.</summary>
    public static string Runtime(bool web, string tag) => $"{Repository}{(web ? "aspnet" : "runtime")}:{tag}";

    /// <summary>The SDK's image at a tag.</summary>
    public static string Sdk(string tag) => $"{Repository}sdk:{tag}";

    /// <summary>Whether the images at a tag have an unprivileged user an app can run as (<c>$APP_UID</c>), from .NET 8.</summary>
    public static bool HasAppUser(string tag) => VersionOrder.Major(tag) >= 8;
}
