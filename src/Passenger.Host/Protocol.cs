using System.Text.Json;
using System.Text.Json.Serialization;

namespace Passenger.Host;

// The project host's protocol, one definition for both ends (the app compiles this file too): one JSON object a line
// each way over the host's standard input and output. The app asks (HostRequest); the host answers each request with
// partial results as they come, then with its result or why it failed (HostReply, by the request's id). The host's
// first line, id 0, says which SDK's MSBuild it loaded, or why it loaded none.

public sealed record HostRequest(int Id, string Method, JsonElement Params);

public sealed record HostReply
{
    public int Id { get; init; }

    public JsonElement? Partial { get; init; }

    public JsonElement? Result { get; init; }

    public string? Error { get; init; }
}

public static class HostMethods
{
    /// <summary><see cref="SdkParams"/> to <see cref="SdkAnswer"/>: the SDK a folder's projects would load with.</summary>
    public const string Sdk = "sdk";

    /// <summary><see cref="EvaluateParams"/>: an <see cref="EvaluatedProject"/> partial per project as each is evaluated,
    /// then <see cref="EvaluateDone"/>.</summary>
    public const string Evaluate = "evaluate";

    /// <summary><see cref="BuildParams"/>: a <see cref="BuiltProject"/> partial per project as its design-time build ends,
    /// then <see cref="BuildDone"/>.</summary>
    public const string Build = "build";

    /// <summary><see cref="CancelParams"/>: ends a request still running (an evaluation or a build round) that the app gave
    /// up on, so the host's one build session is free for the next folder's; answered once it is asked, the request
    /// itself answering that it was cancelled once it has stopped.</summary>
    public const string Cancel = "cancel";

    /// <summary><see cref="InspectParams"/> to <see cref="ProjectInsight"/>: one project evaluated for the code help in its
    /// MSBuild files (its properties with where each was set, its items, targets and imports, its evaluation's errors and
    /// warnings), the open files' unsaved text taken in place of what is on disk; cached while what it read holds.</summary>
    public const string Inspect = "inspect";

    /// <summary>Checks the original graph/framework imports before Passenger composes its WPF population build hook.</summary>
    public const string WpfBuild = "wpfBuild";
}

public static class HostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The SDK the host loaded: its folder (MSBuild's) and version.</summary>
public sealed record HostReady(string Sdk, string Version);

public sealed record SdkParams(string Folder);

/// <summary>The SDK a folder picks (its global.json, else the newest), or why none.</summary>
public sealed record SdkAnswer(string? Sdk, string? Problem);

/// <summary>Projects to evaluate, with the global properties they are evaluated with (the solution's).</summary>
public sealed record EvaluateParams(IReadOnlyList<string> Projects, IReadOnlyDictionary<string, string> Properties);

public sealed record EvaluateDone(int Evaluated, double Seconds);

public sealed record WpfBuildParams(IReadOnlyList<string> Projects, IReadOnlyDictionary<string, string> Properties,
    IReadOnlyDictionary<string, string>? DesignTimeProperties = null);

/// <summary>A uniform existing CustomBefore import, or why the graph cannot safely compose the population hook.</summary>
public sealed record WpfBuildPrepared(string? Before, string? Problem, IReadOnlyList<Stamp> Inputs, bool Wpf = false);

/// <summary>One project as MSBuild evaluates it, every target framework of it: what the Solution view shows and what
/// knows when it changed. A project that does not evaluate has its <see cref="Problem"/> and nothing else.</summary>
public sealed record EvaluatedProject
{
    public required string Path { get; init; }

    /// <summary>False for a multi-targeted project known so far by its outer evaluation alone, which its frameworks'
    /// follow (the same project again, complete): the outer one is a fraction of the cost and shows the same files.</summary>
    public bool Complete { get; init; } = true;

    /// <summary>Why it did not evaluate: MSBuild's message, with the file and line it names.</summary>
    public string? Problem { get; init; }

    public string? AssemblyName { get; init; }

    public string? OutputType { get; init; }

    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary>IsTestProject as evaluated (a test SDK's props set it).</summary>
    public bool IsTestProject { get; init; }

    /// <summary>Its packages and project references, a target framework each, or one with none for a project with one.</summary>
    public IReadOnlyList<FrameworkDependencies> Dependencies { get; init; } = [];

    /// <summary>The files the project holds, every framework's, each once.</summary>
    public IReadOnlyList<EvaluatedItem> Items { get; init; } = [];

    /// <summary>The project file and the files it imports outside the SDK and the package folders (which change only
    /// with their versions, and so with these), as they were when it was evaluated.</summary>
    public IReadOnlyList<Stamp> Inputs { get; init; } = [];

    /// <summary>The folders its items' wildcards walked, as they were: one whose files came or went changed.</summary>
    public IReadOnlyList<Stamp> Folders { get; init; } = [];
}

public sealed record FrameworkDependencies(string? Framework, IReadOnlyList<PackageDependency> Packages, IReadOnlyList<string> Projects)
{
    /// <summary>The shared frameworks it references (FrameworkReference: Microsoft.NETCore.App, WPF's
    /// Microsoft.WindowsDesktop.App.WPF), the SDK's implicit ones included.</summary>
    public IReadOnlyList<string> FrameworkReferences { get; init; } = [];

    /// <summary>Whether the host evaluated it with EnableWindowsTargeting, which a Windows-only framework needs off Windows,
    /// so a build of it needs it too.</summary>
    public bool WindowsTargeting { get; init; }
}

/// <summary>A package reference, its version as restore takes it: its own, its VersionOverride, or the central one.</summary>
public sealed record PackageDependency(string Name, string? Version, bool Implicit);

/// <summary>A file of the project: its item type, full path, its Link (where it shows when it lives elsewhere) and the file
/// it depends on, relative to its own folder, as MSBuild's DependentUpon is.</summary>
public sealed record EvaluatedItem(string Type, string Path, string? Link = null, string? DependentUpon = null);

/// <summary>The request to end, by its id.</summary>
public sealed record CancelParams(int Request);

/// <summary>Projects to build at design time, with the global properties (the solution's, Roslyn's server's own).</summary>
public sealed record BuildParams(IReadOnlyList<string> Projects, IReadOnlyDictionary<string, string> Properties);

public sealed record BuildDone(int Built, double Seconds);

/// <summary>A project's design-time builds as Roslyn's server takes them (a JSON array of its ProjectFileInfo, one a target
/// framework), what they read (the project file, its imports outside the SDK and package folders, its packages' assets,
/// its folders), and why they failed, when they did; with the errors and warnings the builds logged.</summary>
public sealed record BuiltProject(string Path, string Infos, IReadOnlyList<Stamp> Inputs, string? Problem, IReadOnlyList<BuildDiagnostic>? Diagnostics = null);

/// <summary>An error or warning a design-time build logged, with the project file it was logged for.</summary>
public sealed record BuildDiagnostic(bool Error, string Message, string Project);

/// <summary>A file's last write (UTC ticks) and length, 0 and -1 for one not there; for a folder, the hash of its names and
/// how many (<see cref="Stamps.Folder"/>).</summary>
public sealed record Stamp(string Path, long Ticks, long Length, bool Folder = false, string? Pattern = null);

/// <summary>A project to evaluate for code help, with the global properties (the solution's) and the text of the open
/// MSBuild files that differ from what is on disk, by path, which its evaluation reads instead.</summary>
public sealed record InspectParams(string Project, IReadOnlyDictionary<string, string> Properties, IReadOnlyDictionary<string, string> Texts);

/// <summary>What a project's evaluation knows for the code help in its files. A project that did not evaluate (an unknown
/// SDK, a malformed condition, XML that does not read) has <see cref="Evaluated"/> false and its diagnostics alone. Places
/// name their file by its index in <see cref="Files"/>; what lies in the SDK and the package folders (<see cref="Installed"/>)
/// is named only where code help goes into it (a target's or a property's definition, an import), never listed.</summary>
public sealed record ProjectInsight
{
    public required string Project { get; init; }

    public bool Evaluated { get; init; }

    /// <summary>The target framework evaluated, for a multi-targeted project (its first); null for one with one.</summary>
    public string? Framework { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The evaluation's errors and warnings in the project's own files (an unknown SDK, a malformed condition, an
    /// import not found), as MSBuild words them.</summary>
    public IReadOnlyList<InsightDiagnostic> Diagnostics { get; init; } = [];

    public IReadOnlyList<InsightProperty> Properties { get; init; } = [];

    public IReadOnlyList<InsightItemType> ItemTypes { get; init; } = [];

    /// <summary>The items each element of the project's own files made, by the element's place.</summary>
    public IReadOnlyList<InsightItem> Items { get; init; } = [];

    public IReadOnlyList<InsightTarget> Targets { get; init; } = [];

    /// <summary>What each import of the project's own files brought in, an SDK's implicit ones included.</summary>
    public IReadOnlyList<InsightImport> Imports { get; init; } = [];

    /// <summary>The target frameworks the SDK knows (SupportedTargetFramework's aliases), newest first, with their names.</summary>
    public IReadOnlyList<InsightFramework> Frameworks { get; init; } = [];

    /// <summary>The folders whose files only change with their versions (the .NET install, the package folders), which an
    /// editor opens read-only.</summary>
    public IReadOnlyList<string> Installed { get; init; } = [];

    /// <summary>The project file and its imports outside <see cref="Installed"/>, as they were when evaluated.</summary>
    public IReadOnlyList<Stamp> Inputs { get; init; } = [];

    public double Seconds { get; init; }
}

/// <summary>A place in a file of <see cref="ProjectInsight.Files"/>, its line and column from 1 as MSBuild counts them.</summary>
public sealed record InsightPlace(int File, int Line, int Column);

public sealed record InsightDiagnostic(bool Error, string? Code, string Message, string File, int Line, int Column);

public enum PropertyOrigin
{
    Set,
    Global,
    Environment,
    Reserved,
}

/// <summary>A property's evaluated value and the places that set it, the one that last did first.</summary>
public sealed record InsightProperty(string Name, string Value, PropertyOrigin Origin, IReadOnlyList<InsightPlace> Set);

/// <summary>An item type: how many items it has, the first of them, the metadata they carry and where the project's own
/// files add to it.</summary>
public sealed record InsightItemType(string Name, int Count, IReadOnlyList<string> Includes, IReadOnlyList<string> Metadata, IReadOnlyList<InsightPlace> Defined);

/// <summary>What one item element made: its items' includes (the first of them) and how many, and the first's metadata.</summary>
public sealed record InsightItem(string Type, InsightPlace Element, int Count, IReadOnlyList<string> Includes, IReadOnlyDictionary<string, string> Metadata);

/// <summary>A target and every place it is defined, the one in force first.</summary>
public sealed record InsightTarget(string Name, IReadOnlyList<InsightPlace> Defined);

/// <summary>An import element's place and the file it brought in: an SDK's implicit import names the SDK, at the place of
/// the attribute or element that names it.</summary>
public sealed record InsightImport(InsightPlace Element, string Imported, string? Sdk);

public sealed record InsightFramework(string Alias, string Name);
