using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>What a folder publish does, as the Publish dialog shows it and a profile keeps it: the configuration, the target
/// framework (a multi-targeted project's), the runtime (none for a portable app), framework-dependent or self-contained, the
/// SDK's single-file, ReadyToRun and trimming, and the output folder as the profile names it (null while it is MSBuild's
/// own default, which the profile then leaves to MSBuild).</summary>
public sealed record PublishOptions(
    string Configuration,
    string? Framework,
    string? Runtime,
    bool SelfContained,
    bool SingleFile,
    bool ReadyToRun,
    bool Trimmed,
    string? Folder);

/// <summary>A publish profile of a project: its name, its .pubxml and, for one that publishes elsewhere than to a folder
/// (Web Deploy, FTP, Azure: its WebPublishMethod), that method, which Passenger does not publish by yet.</summary>
public sealed record PublishProfileFile(string Name, string Path, string? Remote);

/// <summary>A publish to run: the project, the profile it publishes with (none for MSBuild's defaults), the configuration and
/// the framework given on the command line (the SDK takes neither from a profile: the CLI's PublishRelease chooses Release,
/// and a multi-targeted project's outer build never reads it), each the profile's own when null, and the folder it lands
/// in, as MSBuild resolved it.</summary>
public sealed record PublishRequest(string Project, string? Profile, string? Configuration = null, string? Framework = null)
{
    /// <summary>The folder the publish puts the app in, resolved before it starts; null until then.</summary>
    public string? Folder { get; init; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Project);

    /// <summary>How the publish is named in the status bar and the Build window: "Hello (FolderProfile)".</summary>
    public string Shown => Profile is null ? Name : $"{Name} ({Profile})";

    /// <summary>The dotnet publish command line: the same as a person types to publish with the profile, plus the Build
    /// window's logger options (one line a problem, read once) and Windows targeting where the project host gave it.</summary>
    public IReadOnlyList<string> Arguments(bool windowsTargeting)
    {
        List<string> arguments = ["publish", Project, .. Command()];
        if (windowsTargeting)
        {
            arguments.Add("-p:EnableWindowsTargeting=true");
        }
        arguments.AddRange(["-nologo", "-tl:off", "-clp:NoSummary;ForceNoAlign"]);
        return arguments;
    }

    /// <summary>What follows dotnet publish and the project: its configuration, framework and profile.</summary>
    public IReadOnlyList<string> Command()
    {
        List<string> arguments = ["-c", Configuration ?? "Release"];
        if (Framework is not null)
        {
            arguments.AddRange(["-f", Framework]);
        }
        if (Profile is not null)
        {
            arguments.Add($"-p:PublishProfile={Profile}");
        }
        return arguments;
    }
}

/// <summary>A project's publish profiles, the SDK's own .pubxml files under Properties/PublishProfiles (the folder the SDK
/// imports PublishProfile from), read and written as MSBuild reads them: their values are always MSBuild's evaluation of
/// the project with the profile (through the project host), never the file's text read here, so a path or a condition in
/// one resolves as the publish resolves it.</summary>
public static class PublishProfiles
{
    /// <summary>What a profile's or a project's evaluation is asked for.</summary>
    private static readonly string[] Names =
    [
        "Configuration", "LastUsedBuildConfiguration", "TargetFramework", "TargetFrameworks", "RuntimeIdentifier", "RuntimeIdentifiers",
        "SelfContained", "PublishSingleFile", "PublishReadyToRun", "PublishTrimmed", "PublishAot", "PublishDir", "PublishUrl",
        "WebPublishMethod", "_PublishProfileRootFolder", "NETCoreSdkRuntimeIdentifier",
    ];

    /// <summary>What a profile's file names a folder publish by: Visual Studio's WebPublishMethod or PublishProtocol, or none.</summary>
    private static readonly string[] Methods = ["WebPublishMethod", "PublishProtocol", "PublishProvider"];

    /// <summary>The runtimes offered beside the project's own: this machine's, then the systems .NET runs on.</summary>
    public static IReadOnlyList<string> CommonRuntimes { get; } =
        [RuntimeInformation.RuntimeIdentifier, "win-x64", "win-arm64", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"];

    /// <summary>The name a new profile is offered: FolderProfile, as the SDK's folder profiles are commonly named, with a
    /// number after it while one of that name is there.</summary>
    public static string NewName(IReadOnlyCollection<PublishProfileFile> existing)
    {
        for (int number = 0; ; number++)
        {
            string name = number == 0 ? "FolderProfile" : $"FolderProfile{number}";
            if (!existing.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }
    }

    /// <summary>Why a project is not offered to publish, as Publish… greys it: a test project, a library that does not ask to
    /// be published, one that says IsPublishable false, a Windows desktop app off Windows, or one not read yet or that did
    /// not evaluate; null when it is.</summary>
    public static string? WhyNot(ProjectModel project)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(project.Path);
        if (!project.Exists)
        {
            return $"{name} is not on disk";
        }
        if (project.Problem is { } problem)
        {
            return $"{name} did not load: {problem}";
        }
        if (!project.IsEvaluated)
        {
            return $"{name} is still being read";
        }
        if (project.IsTestProject)
        {
            return $"{name} is a test project, which is run rather than published";
        }
        string? publishable = project.Property(CarriedProperties.IsPublishable);
        if (string.Equals(publishable, "false", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} sets IsPublishable to false";
        }
        if (!project.IsRunnable && !string.Equals(project.OwnProperty(CarriedProperties.IsPublishable), "true", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} is a library: set IsPublishable to true in it to publish it";
        }
        if (project.WindowsOnly is [(_, string desktop), ..])
        {
            return $"{name} is a {desktop} app, which Passenger publishes only on Windows";
        }
        return null;
    }

    /// <summary>The folder the SDK imports a project's profiles from: Properties/PublishProfiles, or under its own
    /// AppDesignerFolder, as evaluated.</summary>
    public static string Folder(string project, IReadOnlyDictionary<string, string>? evaluated) =>
        evaluated?.GetValueOrDefault("_PublishProfileRootFolder") is { Length: > 0 } root
            ? System.IO.Path.GetFullPath(Slashed(root), System.IO.Path.GetDirectoryName(project)!)
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, "Properties", "PublishProfiles");

    /// <summary>The profiles in that folder, by name: each a .pubxml, with the method of one that publishes elsewhere.</summary>
    public static IReadOnlyList<PublishProfileFile> Find(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        List<PublishProfileFile> found = [];
        foreach (string path in Directory.EnumerateFiles(folder, "*.pubxml").Order(StringComparer.OrdinalIgnoreCase))
        {
            string? remote = null;
            try
            {
                IReadOnlyDictionary<string, string> raw = PubXml.Read(File.ReadAllText(path));
                remote = Methods.Select(raw.GetValueOrDefault).FirstOrDefault(m => m is { Length: > 0 } && !string.Equals(m, "FileSystem", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException)
            {
                remote = "a file that does not read";
            }
            found.Add(new PublishProfileFile(System.IO.Path.GetFileNameWithoutExtension(path), PathNames.Canonical(path), remote));
        }
        return found;
    }

    /// <summary>The global properties a publish gives a project's evaluation: the solution's, Windows targeting where the host
    /// gave it, dotnet publish's own, and the profile, configuration, framework and runtime asked for.</summary>
    public static Dictionary<string, string> Globals(WorkspaceModel? workspace, ProjectModel project, string? profile, string? configuration, string? framework,
        string? runtime = null)
    {
        Dictionary<string, string> globals = ProjectModels.Properties(workspace, project);
        globals["_IsPublishing"] = "true";
        if (profile is not null)
        {
            globals["PublishProfile"] = profile;
        }
        if (configuration is not null)
        {
            globals["Configuration"] = configuration;
        }
        if (framework is not null)
        {
            globals["TargetFramework"] = framework;
        }
        if (runtime is not null)
        {
            globals["RuntimeIdentifier"] = runtime;
        }
        return globals;
    }

    /// <summary>The project evaluated with these global properties, as the project host's MSBuild sees it: the values asked
    /// for that have one, or why it could not be.</summary>
    public static async Task<PropertiesAnswer> EvaluateAsync(string folder, string project, IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
    {
        try
        {
            ProjectHost host = await ProjectHosts.ForAsync(folder, cancel);
            return await host.PropertiesAsync(project, globals, Names, cancel);
        }
        catch (ProjectHostFailed failed)
        {
            return new PropertiesAnswer(new Dictionary<string, string>(), $"MSBuild could not read the project: {failed.Reason}");
        }
    }

    /// <summary>A publish worked out as MSBuild sees it: the configuration and framework the profile names (Release when it
    /// names none, as dotnet publish takes; a multi-targeted project's first framework when it names none) and the folder it
    /// lands in; or why it cannot be (no such profile, a project or profile that does not read).</summary>
    public static async Task<(PublishRequest? Resolved, string? Problem)> ResolveAsync(string open, WorkspaceModel? workspace, ProjectModel project,
        PublishRequest request, CancellationToken cancel)
    {
        bool multi = project.TargetFrameworks.Count > 1;
        string? framework = request.Framework;
        try
        {
            if (multi && framework is null && request.Profile is { } named && File.Exists(Path(project.Path, null, named)))
            {
                // The outer build of a multi-targeted project never imports the profile: its framework is read from the file.
                framework = PubXml.Read(File.ReadAllText(Path(project.Path, null, named))).GetValueOrDefault("TargetFramework");
            }
            framework = multi ? framework is { Length: > 0 } ? framework : project.TargetFrameworks[0] : null;
            PropertiesAnswer read = await EvaluateAsync(open, project.Path, Globals(workspace, project, request.Profile, request.Configuration, framework), cancel);
            if (read.Problem is { } problem)
            {
                return (null, $"{request.Name} could not be read: {problem}");
            }
            string? configuration = request.Configuration;
            if (request.Profile is { } profile)
            {
                string file = Path(project.Path, read.Values, profile);
                if (!File.Exists(file))
                {
                    return (null, $"{request.Name} has no publish profile {profile}: {file} is not there");
                }
                IReadOnlyDictionary<string, string> raw = PubXml.Read(await File.ReadAllTextAsync(file, cancel));
                configuration ??= raw.ContainsKey("Configuration") ? read.Values.GetValueOrDefault("Configuration")
                    : raw.GetValueOrDefault("LastUsedBuildConfiguration") is { Length: > 0 } last ? last : null;
            }
            configuration ??= "Release";
            if (!string.Equals(read.Values.GetValueOrDefault("Configuration"), configuration, StringComparison.OrdinalIgnoreCase))
            {
                read = await EvaluateAsync(open, project.Path, Globals(workspace, project, request.Profile, configuration, framework), cancel);
            }
            return (request with { Configuration = configuration, Framework = framework, Folder = FolderOf(project.Path, read.Values) }, null);
        }
        catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException)
        {
            return (null, $"{request.Name}'s publish profile {request.Profile} does not read: {error.Message}");
        }
    }

    /// <summary>Where a profile of a project is, by its name.</summary>
    public static string Path(string project, IReadOnlyDictionary<string, string>? evaluated, string profile) =>
        System.IO.Path.Combine(Folder(project, evaluated), $"{profile}.pubxml");

    /// <summary>The folder a publish puts the app in, from its evaluation: PublishDir, from the project's folder; never a web
    /// profile's PublishUrl, which dotnet publish does not copy to.</summary>
    public static string? FolderOf(string project, IReadOnlyDictionary<string, string> evaluated) =>
        evaluated.GetValueOrDefault("PublishDir") is { } named ? Full(project, named) : null;

    /// <summary>Where Visual Studio copies a web profile's app on to after the publish (its FileSystem PublishUrl), which
    /// dotnet publish leaves where it published it; null for any other profile.</summary>
    public static string? VisualStudioCopy(string project, IReadOnlyDictionary<string, string> evaluated) =>
        string.Equals(evaluated.GetValueOrDefault("WebPublishMethod"), "FileSystem", StringComparison.OrdinalIgnoreCase)
            && evaluated.GetValueOrDefault("PublishUrl") is { Length: > 0 } url ? Full(project, url) : null;

    /// <summary>A folder as a profile names it, made a full path from the project's folder, as MSBuild's tasks take it.</summary>
    public static string Full(string project, string named) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Slashed(named.Trim()), System.IO.Path.GetDirectoryName(project)!));

    /// <summary>A folder as a new profile names it: from the project's folder when it is inside the open folder (as a
    /// repository's profiles go everywhere it does), else in full; with forward slashes, which every system's MSBuild reads,
    /// and a slash at its end, as PublishDir has.</summary>
    public static string Named(string project, string full, string? open)
    {
        string folder = System.IO.Path.GetDirectoryName(project)!;
        string named = open is not null && PathNames.IsUnder(full, open) || PathNames.Same(full, folder) ? System.IO.Path.GetRelativePath(folder, full) : full;
        named = named.Replace('\\', '/');
        return named.EndsWith('/') ? named : named + "/";
    }

    /// <summary>A path MSBuild wrote with Windows' separators, in this system's.</summary>
    private static string Slashed(string path) => OperatingSystem.IsWindows() ? path : path.Replace('\\', '/');

    /// <summary>What a profile's evaluation makes of the dialog's options: its configuration (one it sets itself, else Release,
    /// as dotnet publish takes), framework, runtime, deployment and the SDK's options; the folder is what the file names.</summary>
    public static PublishOptions Options(IReadOnlyDictionary<string, string> evaluated, IReadOnlyDictionary<string, string> raw, string? framework)
    {
        string configuration = raw.ContainsKey("Configuration") ? evaluated.GetValueOrDefault("Configuration") ?? "Release"
            : evaluated.GetValueOrDefault("LastUsedBuildConfiguration") ?? "Release";
        string? runtime = evaluated.GetValueOrDefault("RuntimeIdentifier");
        bool selfContained = True(evaluated, "SelfContained") && runtime is not null;
        string? folder = raw.GetValueOrDefault("PublishDir");
        return new PublishOptions(configuration, framework, runtime, selfContained, True(evaluated, "PublishSingleFile") && runtime is not null,
            True(evaluated, "PublishReadyToRun") && runtime is not null, True(evaluated, "PublishTrimmed") && selfContained, folder);
    }

    private static bool True(IReadOnlyDictionary<string, string> values, string name) =>
        string.Equals(values.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a profile is changed by for the options: its configuration always (the publish gives it), and each other
    /// property the dialog shows where the file sets it already or the option differs from what the profile (or, for a new
    /// one, the project) made of it before, so a change touches what changed and a new profile says what it changes. A
    /// multi-targeted project's framework is named; a single one's is the project's own, never pinned in a profile that
    /// would outlive a change of it. A new profile names its folder target, so Visual Studio shows it as one.</summary>
    public static IReadOnlyList<(string Name, string? Value)> Properties(PublishOptions options, PublishOptions? before, IReadOnlyDictionary<string, string> raw,
        bool multiTargeted, bool fresh)
    {
        List<(string, string?)> set = [("Configuration", options.Configuration)];
        if (raw.ContainsKey("LastUsedBuildConfiguration"))
        {
            set.Add(("LastUsedBuildConfiguration", options.Configuration));
        }
        if (multiTargeted)
        {
            Changed("TargetFramework", options.Framework, before?.Framework);
        }
        bool runtime = options.Runtime is not null;
        Changed("RuntimeIdentifier", options.Runtime, before?.Runtime);
        Changed("SelfContained", Word(options.SelfContained && runtime), Word(before?.SelfContained == true));
        Changed("PublishSingleFile", Word(options.SingleFile && runtime), Word(before?.SingleFile == true));
        Changed("PublishReadyToRun", Word(options.ReadyToRun && runtime), Word(before?.ReadyToRun == true));
        Changed("PublishTrimmed", Word(options.Trimmed && options.SelfContained && runtime), Word(before?.Trimmed == true));
        if (options.Folder is { Length: > 0 } folder)
        {
            Changed("PublishDir", folder, before?.Folder);
        }
        if (fresh)
        {
            set.Add(("PublishProtocol", "FileSystem"));
            set.Add(("_TargetId", "Folder"));
        }
        return set;

        void Changed(string name, string? value, string? was)
        {
            if (raw.ContainsKey(name) || !string.Equals(value, was, StringComparison.OrdinalIgnoreCase))
            {
                // None where the project sets one (its own RuntimeIdentifier) is said as empty, which takes it away.
                set.Add((name, value ?? (raw.ContainsKey(name) ? null : string.Empty)));
            }
        }

        static string Word(bool value) => value ? "true" : "false";
    }
}

/// <summary>A .pubxml's text, read and changed as a project file is: the properties of its unconditioned property groups by
/// name, and a change that sets or takes out only the properties it names, every other character of the file as it was.</summary>
public static class PubXml
{
    /// <summary>What a new profile's file starts as.</summary>
    public const string Empty = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project>\n  <PropertyGroup>\n  </PropertyGroup>\n</Project>\n";

    /// <summary>The properties its unconditioned property groups set, by name, each as written (the last of a name).</summary>
    public static IReadOnlyDictionary<string, string> Read(string text)
    {
        Dictionary<string, string> found = new(StringComparer.OrdinalIgnoreCase);
        XDocument document = XDocument.Parse(text);
        foreach (XElement property in Groups(document).SelectMany(g => g.Elements()))
        {
            found[property.Name.LocalName] = property.Value.Trim();
        }
        return found;
    }

    /// <summary>The text with each property given set to its value (the last of that name in an unconditioned group
    /// changed in place, else added to the first group, indented as its others are) or, given null, taken out with its
    /// line. An XmlException when the text does not read.</summary>
    public static string Apply(string text, IReadOnlyList<(string Name, string? Value)> properties)
    {
        XDocument document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        XElement project = document.Root ?? throw new XmlException("The profile has no Project element");
        XNamespace space = project.Name.Namespace;
        XElement? group = Groups(document).FirstOrDefault();
        if (group is null)
        {
            group = new XElement(space + "PropertyGroup", new XText("\n  "));
            project.AddFirst(new XText("\n  "), group);
        }
        foreach ((string name, string? value) in properties)
        {
            XElement? had = Groups(document).SelectMany(g => g.Elements()).LastOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                if (had is not null)
                {
                    if (had.PreviousNode is XText indent && string.IsNullOrWhiteSpace(indent.Value))
                    {
                        indent.Remove();
                    }
                    had.Remove();
                }
                continue;
            }
            if (had is not null)
            {
                if (had.Value != value)
                {
                    had.Value = value;
                }
                continue;
            }
            string indentation = group.Elements().LastOrDefault()?.PreviousNode is XText before && string.IsNullOrWhiteSpace(before.Value)
                ? before.Value
                : group.PreviousNode is XText outer && string.IsNullOrWhiteSpace(outer.Value) ? outer.Value + "  " : "\n    ";
            XElement added = new(space + name, value);
            if (group.Elements().LastOrDefault() is { } last)
            {
                last.AddAfterSelf(new XText(indentation), added);
            }
            else if (group.LastNode is XText closing && string.IsNullOrWhiteSpace(closing.Value))
            {
                closing.AddBeforeSelf(new XText(indentation), added);
            }
            else
            {
                group.Add(new XText(indentation), added, new XText(indentation.Length >= 2 ? indentation[..^2] : "\n"));
            }
        }
        // Its declaration as it was, the rest through a writer that keeps its breaks.
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int start = text.IndexOf("<?xml", StringComparison.Ordinal);
        StringBuilder written = new(document.Declaration is null || start < 0 ? null : text[start..(text.IndexOf("?>", start, StringComparison.Ordinal) + 2)]);
        XmlWriterSettings settings = new() { OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.Replace, NewLineChars = newline };
        using (XmlWriter writer = XmlWriter.Create(written, settings))
        {
            document.Save(writer);
        }
        return text.EndsWith('\n') && written.Length > 0 && written[^1] != '\n' ? written.Append(newline).ToString() : written.ToString();
    }

    private static IEnumerable<XElement> Groups(XDocument document) =>
        document.Root?.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && e.Attribute("Condition") is null) ?? [];
}

/// <summary>A folder's publishing as it keeps it: the profile each project last published with, by the project's path, and
/// the project published last, which Publish Again publishes when the one in context has none.</summary>
public sealed record PublishState : IStateDocument<PublishState>
{
    public Dictionary<string, string> Profiles { get; init; } = [];

    public string? Last { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public PublishState Clamped() => this with
    {
        Profiles = (Profiles ?? []).Where(p => p.Key is { Length: > 0 } && p.Value is { Length: > 0 }).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
    };
}

/// <summary>The session's memory of what each project last published with, kept with the folder when its window keeps
/// state (<see cref="PublishState"/>). A UI-thread object.</summary>
public sealed class PublishMemory
{
    private readonly Dictionary<string, string> profiles = new(StringComparer.Ordinal);
    private StateDocument<PublishState>? kept;

    /// <summary>The project published last, or null.</summary>
    public string? Last { get; private set; }

    /// <summary>The folder's documents from now on: what it kept taken first.</summary>
    public void Keep(StateDocument<PublishState> state)
    {
        kept = state;
        foreach ((string project, string profile) in state.Value.Profiles)
        {
            profiles[project] = profile;
        }
        Last = state.Value.Last ?? Last;
    }

    /// <summary>The profile a project last published with, or null.</summary>
    public string? ProfileOf(string project) => profiles.GetValueOrDefault(project);

    /// <summary>A project published with a profile: what Publish Again publishes from now on.</summary>
    public void Published(string project, string profile)
    {
        UiThread.Check();
        profiles[project] = profile;
        Last = project;
        if (kept is not null)
        {
            PublishState now = kept.Value;
            kept.Set(now with { Profiles = new Dictionary<string, string>(now.Profiles, StringComparer.Ordinal) { [project] = profile }, Last = project });
        }
    }
}
