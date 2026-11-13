namespace Passenger.App;

/// <summary>Each tool window's id, named once: what its <see cref="Docking.ToolWindow"/> is made with, what shows, hides
/// and asks after it, and what a kept layout (layout.json, each folder's) records it by, so a value here never changes.</summary>
[IdentifierHome]
public static class ToolWindowIds
{
    public const string Explorer = "explorer";
    public const string Structure = "structure";
    public const string Bookmarks = "bookmarks";
    public const string Commit = "commit";
    public const string Run = "run";
    public const string Debug = "debug";
    public const string Tests = "tests";
    public const string Build = "build";
    public const string Problems = "problems";
    public const string Find = "find";
    public const string Git = "git";
    public const string NuGet = "nuget";
    public const string Terminal = "terminal";
    public const string LocalHistory = "localHistory";
    public const string Containers = "containers";
    public const string Profiler = "profiler";
    public const string Hierarchy = "hierarchy";
    public const string Endpoints = "endpoints";
    public const string Requests = "requests";
    public const string LiveTree = "liveTree";
    public const string Game = "game";
    public const string Aspire = "aspire";
    public const string Migrations = "migrations";

    /// <summary>Every id above, which a theme's layout names windows by.</summary>
    public static IReadOnlySet<string> All { get; } = typeof(ToolWindowIds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
}
