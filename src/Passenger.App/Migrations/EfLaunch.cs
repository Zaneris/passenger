using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Migrations;

/// <summary>The start dotnet ef 10 and 11 make of their own ef.dll, made from what the project host evaluated: <c>dotnet
/// exec</c> with the startup project's deps and runtime configuration, then the command with the projects' assemblies,
/// folders, namespace, language and Design assembly. A command is then one process, never waiting on dotnet ef and the
/// MSBuild it starts for each project. Only for dotnet-ef 10 and 11, whose start this is; any other version, or a project
/// either would start otherwise or refuse (.NET Framework's, a platform's or a Design assembly of its own, options of
/// .config/dotnet-ef.json beyond those the window gives), keeps dotnet ef.</summary>
internal sealed class EfLaunch
{
    /// <summary>What dotnet ef reads of each project, by MSBuild's names.</summary>
    public static readonly IReadOnlyList<string> Properties =
    [
        "AssemblyName", "Language", "OutputPath", "ProjectAssetsFile", "ProjectDir", "RootNamespace", "RuntimeFrameworkVersion", "TargetFileName",
        "TargetFrameworkMoniker", "Nullable", "TargetFramework", "RuntimeIdentifier", "ProjectDepsFileName", "ProjectRuntimeConfigFileName", "DesignAssembly",
        "PlatformTarget", "TargetPlatformIdentifier",
    ];

    private readonly string efDll;
    private readonly IReadOnlyList<string> host;
    private readonly IReadOnlyList<string> tail;
    private readonly string folder;
    private readonly IReadOnlyList<(string From, string To)> buildHosts;
    private readonly Lazy<Task> copied;

    private EfLaunch(string efDll, IReadOnlyList<string> host, IReadOnlyList<string> tail, string folder, IReadOnlyList<(string From, string To)> buildHosts)
    {
        this.efDll = efDll;
        this.host = host;
        this.tail = tail;
        this.folder = folder;
        this.buildHosts = buildHosts;
        copied = new Lazy<Task>(() => BackgroundWork.Run(CopyBuildHosts));
    }

    /// <summary>The launch for the tool and the two projects' evaluated properties (the same for one project), from
    /// <paramref name="working"/>, with the defaults the tool reads (<paramref name="defaults"/>); null where dotnet ef would
    /// start ef.dll otherwise or something it reads is not there. On any thread.</summary>
    public static EfLaunch? Of(EfTool tool, EfDefaults? defaults, string projectPath, IReadOnlyDictionary<string, string> project, string startupPath,
        IReadOnlyDictionary<string, string> startup, string working)
    {
        bool eleven = tool.Major >= 11;
        if (tool.EfDll(Path.GetDirectoryName(startupPath)!) is not { } efDll
            || !Value(startup, "TargetFrameworkMoniker").StartsWith(".NETCoreApp,Version=v", StringComparison.Ordinal)
            || !System.Version.TryParse(Value(startup, "TargetFrameworkMoniker")[".NETCoreApp,Version=v".Length..], out Version? version)
            || version.Major < (eleven ? 10 : 2)
            || (eleven && (defaults?.Beyond == true || Value(startup, "TargetPlatformIdentifier").Length > 0 || Value(startup, "DesignAssembly").Length > 0
                || (Value(startup, "PlatformTarget") is { Length: > 0 } platform && !platform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)))))
        {
            return null;
        }
        string startupFolder = Path.TrimEndingDirectorySeparator(Separated(Value(startup, "ProjectDir")));
        string target = Path.GetFullPath(Path.Combine(startupFolder, Separated(Value(startup, "OutputPath"))));
        string assets = Separated(Value(startup, "ProjectAssetsFile"));
        // dotnet-ef 11 names them by the SDK's properties, 10 by the assembly.
        string deps = Path.Combine(target, eleven && Value(startup, "ProjectDepsFileName") is { Length: > 0 } depsFile ? depsFile : Value(startup, "AssemblyName") + ".deps.json");
        string runtimeConfig = Path.Combine(target, eleven && Value(startup, "ProjectRuntimeConfigFileName") is { Length: > 0 } configFile
            ? configFile : Value(startup, "AssemblyName") + ".runtimeconfig.json");
        if (startupFolder.Length == 0 || !File.Exists(deps) || Read(assets) is not { } restored)
        {
            return null;
        }
        using (restored)
        {
            string[] folders = restored.RootElement.TryGetProperty("packageFolders", out JsonElement packageFolders) && packageFolders.ValueKind == JsonValueKind.Object
                ? [.. packageFolders.EnumerateObject().Select(p => p.Name)] : [];
            if (Runtime(restored, folders, startup, "Microsoft.EntityFrameworkCore.Design") is not { } design)
            {
                return null;
            }
            List<string> host = ["exec", "--depsfile", deps, .. folders.SelectMany(f => (string[])["--additionalprobingpath", f.TrimEnd(Path.DirectorySeparatorChar)])];
            if (File.Exists(runtimeConfig))
            {
                host.AddRange(["--runtimeconfig", runtimeConfig]);
            }
            else if (Value(startup, "RuntimeFrameworkVersion") is { Length: > 0 } fx)
            {
                host.AddRange(["--fx-version", fx]);
            }
            List<string> projects =
            [
                "--assembly", Path.Combine(target, Value(project, "TargetFileName")), "--project", projectPath,
                "--startup-assembly", Path.Combine(target, Value(startup, "TargetFileName")), "--startup-project", startupPath,
                "--project-dir", Separated(Value(project, "ProjectDir")), "--root-namespace", Value(project, "RootNamespace"), "--language", Value(project, "Language"),
                "--framework", Value(startup, "TargetFramework"), "--design-assembly", design,
            ];
            if (Value(project, "Nullable") is { } nullable && (nullable.Equals("enable", StringComparison.OrdinalIgnoreCase) || nullable.Equals("annotations", StringComparison.OrdinalIgnoreCase)))
            {
                projects.Add("--nullable");
            }
            projects.AddRange(["--working-dir", working]);
            // Roslyn's MSBuild build host, which ef.dll looks for beside the Workspaces.MSBuild copied into the output.
            List<(string, string)> buildHosts = [];
            if (Runtime(restored, folders, startup, "Microsoft.CodeAnalysis.Workspaces.MSBuild", whole: true) is { } workspaces
                && File.Exists(Path.Combine(target, Path.GetFileName(workspaces))))
            {
                buildHosts.Add((Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workspaces)!, "..", "..", "contentFiles", "any", "any")), target));
            }
            return new EfLaunch(efDll, host, projects, startupFolder, buildHosts);
        }
    }

    /// <summary>The start of one command (its words, its options and <c>--context</c>), its output prefixed as dotnet ef's is
    /// asked to be, in the startup project's folder.</summary>
    public ProcessStartInfo Command(IEnumerable<string> command) =>
        ChildProcesses.DotnetCommand([.. host, efDll, .. command, .. tail, "--prefix-output"], folder);

    /// <summary>Roslyn's build host copied where it lacks, once for the launch, before any of its commands starts: what dotnet
    /// ef copies as each starts, which two copying at once fail at.</summary>
    public Task ReadyAsync() => copied.Value;

    private void CopyBuildHosts()
    {
        foreach ((string from, string to) in buildHosts)
        {
            try
            {
                CopyLacking(new DirectoryInfo(from), to);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A copy that cannot be made leaves ef.dll what it had: the commands that need the host say so themselves.
            }
        }
    }

    private static void CopyLacking(DirectoryInfo from, string to)
    {
        if (!from.Exists)
        {
            return;
        }
        Directory.CreateDirectory(to);
        foreach (FileInfo file in from.GetFiles())
        {
            string target = Path.Combine(to, file.Name);
            if (!File.Exists(target))
            {
                file.CopyTo(target, overwrite: false);
            }
        }
        foreach (DirectoryInfo folder in from.GetDirectories())
        {
            CopyLacking(folder, Path.Combine(to, folder.Name));
        }
    }

    /// <summary>The runtime assembly of a package the startup project restored, in the first package folder holding it, as
    /// ResolvePackageAssets finds it: the one whose path holds the name, or (<paramref name="whole"/>) whose file is it.</summary>
    private static string? Runtime(JsonDocument restored, string[] folders, IReadOnlyDictionary<string, string> startup, string package, bool whole = false)
    {
        if (!restored.RootElement.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object
            || !restored.RootElement.TryGetProperty("libraries", out JsonElement libraries) || libraries.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string framework = Value(startup, "TargetFramework");
        string runtime = Value(startup, "RuntimeIdentifier");
        JsonProperty[] all = [.. targets.EnumerateObject()];
        JsonProperty? chosen = all.Cast<JsonProperty?>().FirstOrDefault(t => runtime.Length > 0 && t!.Value.Name.Equals($"{framework}/{runtime}", StringComparison.OrdinalIgnoreCase))
            ?? all.Cast<JsonProperty?>().FirstOrDefault(t => t!.Value.Name.Equals(framework, StringComparison.OrdinalIgnoreCase))
            ?? (all.Length == 1 ? all[0] : null);
        if (chosen is not { Value.ValueKind: JsonValueKind.Object } target)
        {
            return null;
        }
        foreach (JsonProperty library in target.Value.EnumerateObject())
        {
            if (!library.Name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase) || !library.Value.TryGetProperty("runtime", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Object || !libraries.TryGetProperty(library.Name, out JsonElement described)
                || !described.TryGetProperty("path", out JsonElement path) || path.GetString() is not { Length: > 0 } relative)
            {
                continue;
            }
            foreach (JsonProperty asset in assets.EnumerateObject())
            {
                bool named = whole ? Path.GetFileNameWithoutExtension(asset.Name).Equals(package, StringComparison.OrdinalIgnoreCase)
                    : asset.Name.Contains(package, StringComparison.Ordinal);
                if (named && folders.Select(f => Path.GetFullPath(Path.Combine(f, Separated(relative), Separated(asset.Name)))).FirstOrDefault(File.Exists) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static JsonDocument? Read(string assets)
    {
        if (assets.Length == 0)
        {
            return null;
        }
        try
        {
            using FileStream stream = new(assets, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> properties, string name) => properties.GetValueOrDefault(name) ?? string.Empty;

    private static string Separated(string path) => path.Replace('\\', Path.DirectorySeparatorChar);
}
