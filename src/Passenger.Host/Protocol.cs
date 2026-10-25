using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
    /// <summary><see cref="EvaluateParams"/>: an <see cref="EvaluatedProject"/> partial per project as each is evaluated,
    /// then <see cref="EvaluateDone"/>.</summary>
    public const string Evaluate = "evaluate";

    /// <summary><see cref="BuildParams"/>: a <see cref="BuiltProject"/> partial per project as its design-time build ends,
    /// then <see cref="BuildDone"/>.</summary>
    public const string Build = "build";

    /// <summary><see cref="CancelParams"/>: ends a request still running (an evaluation, a build round, a hold) that the
    /// app gave up on, so the host's one build session is free for the next folder's; answered once it is asked, the
    /// request itself answering that it was cancelled once it has stopped.</summary>
    public const string Cancel = "cancel";

    /// <summary><see cref="InspectParams"/> to <see cref="ProjectInsight"/>: one project evaluated for the code help in its
    /// MSBuild files (its properties with where each was set, its items, targets and imports, its evaluation's errors and
    /// warnings), the open files' unsaved text taken in place of what is on disk; cached while what it read holds.</summary>
    public const string Inspect = "inspect";

    /// <summary><see cref="HoldParams"/>: a build of the IDE's own is to run, so no design-time build that runs WPF's markup
    /// compilation or Avalonia's resources starts until the request is cancelled; a <see cref="HoldState"/> partial says it is held once none runs,
    /// after one naming those still running, if any.</summary>
    public const string Hold = "hold";

    /// <summary><see cref="PropertiesParams"/> to <see cref="PropertiesAnswer"/>: the values of some properties of one project
    /// evaluated with the global properties given (a publish profile's, which the SDK imports by PublishProfile), as a build
    /// with those properties would see them.</summary>
    public const string Properties = "properties";

    /// <summary><see cref="InputsParams"/> to <see cref="BuildInputs"/>: what a build of one project with the global properties
    /// given reads (stamped) and makes, its references' too, or why a build of it reads more than its evaluations say.</summary>
    public const string Inputs = "inputs";
}

public static class HostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Named, as the serializer names it by default, so each message's metadata can be had before the first message.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>A request's line as the host reads it.</summary>
    public static JsonTypeInfo<HostRequest> Request { get; } = (JsonTypeInfo<HostRequest>)Options.GetTypeInfo(typeof(HostRequest));

    /// <summary>An answer's line as the app reads it.</summary>
    public static JsonTypeInfo<HostReply> Reply { get; } = (JsonTypeInfo<HostReply>)Options.GetTypeInfo(typeof(HostReply));
}

/// <summary>The SDK the host loaded: its folder (MSBuild's) and version.</summary>
public sealed record HostReady(string Sdk, string Version);

/// <summary>Projects to evaluate, with the global properties they are evaluated with (the solution's).</summary>
public sealed record EvaluateParams(IReadOnlyList<string> Projects, IReadOnlyDictionary<string, string> Properties);

public sealed record EvaluateDone(int Evaluated, double Seconds);

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

    /// <summary>Whether the evaluated project uses Aspire's AppHost SDK.</summary>
    public bool IsAspireHost { get; init; }

    /// <summary>Its packages and project references, a target framework each, or one with none for a project with one.</summary>
    public IReadOnlyList<FrameworkDependencies> Dependencies { get; init; } = [];

    /// <summary>The files the project holds, every framework's, each once.</summary>
    public IReadOnlyList<EvaluatedItem> Items { get; init; } = [];

    /// <summary>The project file and the files it imports outside the SDK and the package folders (which change only
    /// with their versions, and so with these), and those it would import were they there (the Directory.* files above it,
    /// its restore's props and targets), as they were when it was evaluated.</summary>
    public IReadOnlyList<Stamp> Inputs { get; init; } = [];

    /// <summary>The folders its items' wildcards walked, as they were: one whose files came or went changed.</summary>
    public IReadOnlyList<Stamp> Folders { get; init; } = [];

    /// <summary>The properties of <see cref="CarriedProperties.Names"/> that have a value, as evaluated: what finds and makes
    /// its container run configurations, wherever a build would take them from (a Directory.Build.props, an import, a
    /// condition that holds), never a comment.</summary>
    public IReadOnlyDictionary<string, EvaluatedProperty> Properties { get; init; } = new Dictionary<string, EvaluatedProperty>();
}

/// <summary>A property's evaluated value, and whether the project's own files set it (the project, a Directory.Build.props,
/// an import outside the SDK and the package folders) rather than the SDK's or a package's defaults.</summary>
public sealed record EvaluatedProperty(string Value, bool Own);

/// <summary>The properties an evaluation carries beyond what the Solution view shows (<see cref="EvaluatedProject.Properties"/>):
/// Docker's and the .NET SDK container's, by which a project's container run configurations are found and made, and the
/// user secrets a container of it is given.</summary>
public static class CarriedProperties
{
    public const string EnableSdkContainerSupport = "EnableSdkContainerSupport";
    public const string ContainerRepository = "ContainerRepository";
    public const string ContainerImageName = "ContainerImageName";
    public const string ContainerBaseImage = "ContainerBaseImage";
    public const string DockerfileFile = "DockerfileFile";
    public const string DockerfileContext = "DockerfileContext";
    public const string DockerfileFastModeStage = "DockerfileFastModeStage";
    public const string ContainerDevelopmentMode = "ContainerDevelopmentMode";
    public const string DockerDefaultTargetOS = "DockerDefaultTargetOS";
    public const string UserSecretsId = "UserSecretsId";

    /// <summary>Whether Publish offers the project: an executable's unless it says false, a library's only when it says true.</summary>
    public const string IsPublishable = "IsPublishable";

    /// <summary>Where the project's restore writes its project.assets.json, wherever its obj folder was moved.</summary>
    public const string ProjectAssetsFile = "ProjectAssetsFile";

    /// <summary>The package folder the project restores into.</summary>
    public const string NuGetPackageRoot = "NuGetPackageRoot";

    /// <summary>The assembly the project's build makes, wherever its output folder was moved.</summary>
    public const string TargetPath = "TargetPath";

    public static readonly IReadOnlyList<string> Names =
    [
        EnableSdkContainerSupport, ContainerRepository, ContainerImageName, ContainerBaseImage, DockerfileFile, DockerfileContext,
        DockerfileFastModeStage, ContainerDevelopmentMode, DockerDefaultTargetOS, UserSecretsId, IsPublishable, ProjectAssetsFile,
        NuGetPackageRoot, TargetPath,
    ];
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
/// framework), what they read (the project file, its imports outside the SDK and package folders and those it would import
/// were they there, its packages' assets, its folders), and why they failed, when they did; with the errors and warnings
/// the builds logged.</summary>
public sealed record BuiltProject(string Path, string Infos, IReadOnlyList<Stamp> Inputs, string? Problem, IReadOnlyList<BuildDiagnostic>? Diagnostics = null);

/// <summary>An error or warning a design-time build logged, with the project file it was logged for.</summary>
public sealed record BuildDiagnostic(bool Error, string Message, string Project);

/// <summary>What a hold is asked with: nothing.</summary>
public sealed record HoldParams;

/// <summary>A hold's progress: the projects whose design-time builds it waits for, or that it holds them off.</summary>
public sealed record HoldState(IReadOnlyList<string> Running, bool Held);

/// <summary>A file's last write (UTC ticks) and length, 0 and -1 for one not there; for a folder, the hash of its names and
/// how many (<see cref="Stamps.Folder"/>).</summary>
public sealed record Stamp(string Path, long Ticks, long Length, bool Folder = false);

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

    /// <summary>The folders its items' wildcards walked, as they were (<see cref="EvaluatedProject.Folders"/>'): a file come
    /// into or gone from one changes its item counts.</summary>
    public IReadOnlyList<Stamp> Folders { get; init; } = [];

    public double Seconds { get; init; }
}

/// <summary>A project to evaluate with these global properties (the solution's, and what a build would be given), and the
/// properties whose values are wanted.</summary>
public sealed record PropertiesParams(string Project, IReadOnlyDictionary<string, string> Properties, IReadOnlyList<string> Names);

/// <summary>Each property asked for that has a value, as evaluated, or why the project did not evaluate.</summary>
public sealed record PropertiesAnswer(IReadOnlyDictionary<string, string> Values, string? Problem);

/// <summary>A project a build is about to build, with the global properties the build gives it (its framework among them).</summary>
public sealed record InputsParams(string Project, IReadOnlyDictionary<string, string> Properties);

/// <summary>What a build of a project reads and makes, as its evaluations say, with the projects it references in turn.</summary>
public sealed record BuildInputs
{
    /// <summary>Why a build of it reads more than its evaluations say (a target of the project's own, a project of a kind
    /// not known, an evaluation that failed); null when they say it all.</summary>
    public string? Unknown { get; init; }

    /// <summary>The files it reads, stamped before they were read (as not there too, for those whose coming would change it):
    /// each project file and its imports, its restore's assets, its items' files, the analyzers' settings over its sources
    /// and the repository's HEAD.</summary>
    public IReadOnlyList<Stamp> Reads { get; init; } = [];

    /// <summary>The project's output folder, and the file its build lists what it wrote in (FileListAbsolute.txt).</summary>
    public string? Output { get; init; }

    public string? Written { get; init; }

    /// <summary>The assemblies of the projects it references, every framework's, which its build copies from.</summary>
    public IReadOnlyList<string> Referenced { get; init; } = [];

    /// <summary>Those of <see cref="Reads"/> a restore writes (each project's assets, lock file and the props and targets it
    /// imports), which the build's own restore may write before anything reads them.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];
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
