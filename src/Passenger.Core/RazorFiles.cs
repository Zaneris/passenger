namespace Passenger.Core;

/// <summary>What names a Razor file: a component, or a page or view. Framework-free, since Passenger's extension inside Roslyn's
/// server compiles it in.</summary>
[IdentifierHome]
public static class RazorFiles
{
    /// <summary>A Razor component's extension.</summary>
    public const string Component = ".razor";

    /// <summary>A Razor page's or MVC view's extension.</summary>
    public const string Page = ".cshtml";

    /// <summary>Whether a path (or a URI) names a Razor file, its extension in any case.</summary>
    public static bool Is(string? path) =>
        path is not null && (path.EndsWith(Component, StringComparison.OrdinalIgnoreCase) || path.EndsWith(Page, StringComparison.OrdinalIgnoreCase));
}
