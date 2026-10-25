using System.Xml.Linq;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Containers;

/// <summary>The container run configurations a workspace makes as it opens: one for each compose file (its overrides with
/// it) beside the solution, the folder or a project; one for each Docker Compose project (.dcproj), from its own properties
/// and each of its launch profiles; one for each runnable project with a Dockerfile; and one for each project the .NET SDK
/// makes a container of (<see cref="AsksForSdkContainer"/>); a project whose launch profile is a Docker or SdkContainer one
/// has that profile's instead. A project's properties are MSBuild's evaluation of it (<see cref="ProjectModel.Property"/>),
/// the rest the files' text, on the window's thread: the few files it reads are kept by their time.</summary>
public static class ContainerConfigurations
{
    /// <summary>The names a compose file has, as <c>docker compose</c> looks for them in a folder, in its order.</summary>
    public static readonly IReadOnlyList<string> ComposeNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    private static readonly Dictionary<string, (DateTime Written, string Text)> Read = new(StringComparer.Ordinal);

    public static bool IsComposeProject(string path) => WorkspaceModel.IsComposeProject(path);

    /// <summary>Every container configuration the workspace's files make; <paramref name="projects"/> are those loaded.</summary>
    public static IReadOnlyList<RunConfiguration> Generate(WorkspaceModel workspace, IReadOnlyList<ProjectModel> projects)
    {
        List<RunConfiguration> made = [];
        HashSet<string> covered = new(StringComparer.Ordinal);
        foreach (string dcproj in workspace.ComposeProjects)
        {
            foreach (RunConfiguration configuration in FromComposeProject(dcproj))
            {
                covered.UnionWith(configuration.ComposeFiles);
                made.Add(configuration);
            }
        }
        IEnumerable<string?> folders = (string?[])[workspace.Folder, workspace.Solution is { } solution ? Path.GetDirectoryName(solution.Path) : null,
            .. projects.SelectMany(p => (string?[])[p.Folder, Path.GetDirectoryName(p.Folder)])];
        foreach (string folder in folders.OfType<string>().Where(f => PathNames.Within(f, workspace.Folder)).Distinct(StringComparer.Ordinal))
        {
            foreach (string name in ComposeNames)
            {
                string file = Path.Combine(folder, name);
                if (covered.Contains(file) || !File.Exists(file))
                {
                    continue;
                }
                List<string> files = [file, .. Overrides(file)];
                covered.UnionWith(files);
                string relative = Path.GetRelativePath(workspace.Folder, file).Replace('\\', '/');
                made.Add(new RunConfiguration
                {
                    Name = $"Compose: {relative}",
                    Kind = RunKind.Compose,
                    ComposeFiles = files,
                    // The first .NET service's published port, once it answers; none for an application that publishes none.
                    LaunchBrowser = true,
                    BrowserUrl = ServiceUrl,
                    Origin = $"Generated for {relative}{(files.Count > 1 ? $" with {string.Join(", ", files.Skip(1).Select(Path.GetFileName))}" : string.Empty)}",
                });
                // docker compose reads the first of these names a folder has, and so does a configuration of it.
                break;
            }
        }
        foreach (ProjectModel project in projects.Where(p => p.Exists && p.IsRunnable))
        {
            IReadOnlyList<LaunchProfile> profiles = LaunchProfile.Read(project);
            string? dockerfile = profiles.Any(p => p.Is(LaunchProfile.DockerCommand)) ? null : Dockerfile(project);
            bool sdk = !profiles.Any(p => p.Is(LaunchProfile.SdkContainerCommand)) && AsksForSdkContainer(project);
            bool web = (dockerfile is not null || sdk) && IsWeb(project, projects);
            if (dockerfile is not null)
            {
                made.Add(new RunConfiguration
                {
                    Name = $"{project.Name}: Dockerfile",
                    Kind = RunKind.Dockerfile,
                    Project = project.Path,
                    Dockerfile = dockerfile,
                    // Every port its image exposes, each on a port the engine chooses (publishAllPorts).
                    RunArguments = "-P",
                    LaunchBrowser = web,
                    BrowserUrl = web ? ServiceUrl : string.Empty,
                    Origin = $"Generated for {project.Name}'s {Path.GetFileName(dockerfile)}",
                    Unsupported = WindowsContainer(project),
                });
            }
            if (sdk)
            {
                made.Add(new RunConfiguration
                {
                    Name = $"{project.Name}: SDK container",
                    Kind = RunKind.SdkContainer,
                    Project = project.Path,
                    RunArguments = "-P",
                    LaunchBrowser = web,
                    BrowserUrl = web ? ServiceUrl : string.Empty,
                    Origin = $"Generated for {project.Name}, whose project has the .NET SDK make its container",
                });
            }
        }
        return made;
    }

    /// <summary>Visual Studio's address for a container's web app, filled from the port the engine published.</summary>
    public const string ServiceUrl = "{Scheme}://{ServiceHost}:{ServicePort}";

    /// <summary>A Docker or SdkContainer launch profile as a container configuration of its project: its ports (httpPort to
    /// the container's 8080, sslPort to its 8081 when it serves HTTPS, every exposed one with publishAllPorts), its env
    /// files and <c>docker run</c> words, its environment and browser.</summary>
    public static RunConfiguration From(ProjectModel project, LaunchProfile profile)
    {
        List<string> run = [];
        if (profile.HttpPort is { } http)
        {
            run.Add($"-p {http}:8080");
        }
        if (profile.UseSsl && profile.SslPort is { } ssl)
        {
            run.Add($"-p {ssl}:8081");
        }
        if (profile.PublishAllPorts || (profile.HttpPort is null && profile.SslPort is null))
        {
            run.Add("-P");
        }
        foreach (string file in (profile.EnvironmentFiles ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            run.Add($"--env-file \"{file}\"");
        }
        if (profile.RunArguments is { Length: > 0 } words)
        {
            run.Add(words);
        }
        Dictionary<string, string> environment = new(profile.EnvironmentVariables);
        if (profile.UseSsl)
        {
            environment.TryAdd("ASPNETCORE_HTTPS_PORTS", "8081");
        }
        string url = profile.LaunchUrl is { Length: > 0 } launch
            ? launch.Contains('{', StringComparison.Ordinal) || Uri.TryCreate(launch, UriKind.Absolute, out _) ? launch : $"{ServiceUrl}/{launch.TrimStart('/')}"
            : ServiceUrl;
        return new RunConfiguration
        {
            Name = profile.Name,
            Kind = profile.Is(LaunchProfile.SdkContainerCommand) ? RunKind.SdkContainer : RunKind.Dockerfile,
            Project = project.Path,
            LaunchProfile = profile.Name,
            Environment = environment,
            RunArguments = string.Join(' ', run),
            LaunchBrowser = profile.LaunchBrowser,
            BrowserUrl = url,
        };
    }

    /// <summary>A Docker Compose project's configurations: one of its own properties (its files, the service it launches
    /// and that service's URL), and one for each DockerCompose profile of its launchSettings.json (the services each starts,
    /// debugs or leaves, the Compose profiles it turns on).</summary>
    public static IReadOnlyList<RunConfiguration> FromComposeProject(string dcproj)
    {
        if (Text(dcproj) is not { } text)
        {
            return [];
        }
        XElement root;
        try
        {
            root = XDocument.Parse(text).Root!;
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
        string folder = Path.GetDirectoryName(dcproj)!;
        string? Property(string name) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();
        string basis = Property("DockerComposeBaseFilePath") is { Length: > 0 } named ? named : "docker-compose";
        string file = new[] { ".yml", ".yaml" }.Select(e => Path.Combine(folder, basis + e)).FirstOrDefault(File.Exists) ?? Path.Combine(folder, basis + ".yml");
        List<string> files = [PathNames.Canonical(file), .. Overrides(file).Select(PathNames.Canonical)];
        foreach (string more in (Property("AdditionalComposeFilePaths") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            files.Add(PathNames.Canonical(Path.Combine(folder, more.Replace('\\', '/'))));
        }
        string name = Path.GetFileNameWithoutExtension(dcproj);
        string service = Property("DockerServiceName") ?? string.Empty;
        string url = Property("DockerServiceUrl") ?? string.Empty;
        bool browser = string.Equals(Property("DockerLaunchAction"), "LaunchBrowser", StringComparison.OrdinalIgnoreCase);
        RunConfiguration own = new()
        {
            Name = name,
            Kind = RunKind.Compose,
            Project = dcproj,
            ComposeFiles = files,
            LaunchService = service,
            LaunchBrowser = browser,
            BrowserUrl = url.Length > 0 ? url : browser ? ServiceUrl : string.Empty,
            Origin = $"Generated for the Docker Compose project {Path.GetFileName(dcproj)}",
        };
        List<RunConfiguration> made = [own];
        foreach (LaunchProfile profile in LaunchProfile.Read(LaunchProfile.SettingsIn(folder)).Where(p => p.Is(LaunchProfile.DockerComposeCommand)))
        {
            Dictionary<string, ServiceStart> actions = profile.ServiceActions.ToDictionary(told => told.Key, told => told.Value switch
            {
                "StartDebugging" => ServiceStart.Debug,
                "DoNotStart" => ServiceStart.DontStart,
                _ => ServiceStart.Run,
            });
            string action = profile.ComposeLaunchAction ?? string.Empty;
            made.Add(own with
            {
                Name = $"{name}: {profile.Name}",
                LaunchProfile = profile.Name,
                ServiceActions = actions,
                Profiles = [.. profile.ComposeProfiles],
                LaunchService = profile.ComposeLaunchService ?? service,
                LaunchBrowser = action.Length > 0 ? string.Equals(action, "LaunchBrowser", StringComparison.OrdinalIgnoreCase) : browser,
                BrowserUrl = profile.ComposeLaunchUrl ?? own.BrowserUrl,
                Origin = $"Generated from the launch profile {profile.Name} of the Docker Compose project {Path.GetFileName(dcproj)}",
            });
        }
        return made;
    }

    /// <summary>A compose file's overrides beside it, as Compose and Visual Studio pair them: compose.override.yaml for
    /// compose.yaml, docker-compose.override.yml for docker-compose.yml, and any other <c>*.override.*</c> of its name.</summary>
    public static IReadOnlyList<string> Overrides(string file)
    {
        string folder = Path.GetDirectoryName(file)!;
        string stem = Path.GetFileNameWithoutExtension(file);
        try
        {
            return [.. Directory.EnumerateFiles(folder, $"{stem}.override.*")
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".yaml" or ".yml")
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A project's Dockerfile: the one its evaluated DockerfileFile names (from the project's folder), else
    /// Dockerfile or Containerfile beside it; null for none.</summary>
    public static string? Dockerfile(ProjectModel project)
    {
        if (project.Property(CarriedProperties.DockerfileFile) is { } named)
        {
            string file = Path.GetFullPath(Path.Combine(project.Folder, named.Trim().Replace('\\', '/')));
            if (File.Exists(file))
            {
                return PathNames.Canonical(file);
            }
        }
        return new[] { "Dockerfile", "Containerfile" }.Select(n => Path.Combine(project.Folder, n)).FirstOrDefault(File.Exists);
    }

    /// <summary>Whether the .NET SDK makes the project's container and the project asks it to: EnableSdkContainerSupport
    /// evaluates true (the SDK turns it on for every executable, which alone asks for nothing) and the project's own files
    /// set it, or name the image (ContainerRepository, the older ContainerImageName) or its base (ContainerBaseImage).</summary>
    public static bool AsksForSdkContainer(ProjectModel project) =>
        string.Equals(project.Property(CarriedProperties.EnableSdkContainerSupport), "true", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(project.OwnProperty(CarriedProperties.EnableSdkContainerSupport), "true", StringComparison.OrdinalIgnoreCase)
            || project.OwnProperty(CarriedProperties.ContainerRepository) is not null
            || project.OwnProperty(CarriedProperties.ContainerImageName) is not null
            || project.OwnProperty(CarriedProperties.ContainerBaseImage) is not null);

    /// <summary>Why a project's Dockerfile cannot run here: its DockerDefaultTargetOS says it builds a Windows container,
    /// where Passenger runs Linux ones; null otherwise.</summary>
    private static string? WindowsContainer(ProjectModel project) =>
        string.Equals(project.Property(CarriedProperties.DockerDefaultTargetOS), "Windows", StringComparison.OrdinalIgnoreCase)
            ? $"{project.Name}'s Dockerfile builds a Windows container (its DockerDefaultTargetOS), and Passenger runs Linux containers"
            : null;

    /// <summary>Whether a project is an ASP.NET Core app, whose container serves a page: as Add Docker Support decides its
    /// image (<see cref="DockerFiles.Describe"/>), through the projects it references and the packages its restore took.</summary>
    public static bool IsWeb(ProjectModel project, IReadOnlyList<ProjectModel> all) => DockerFiles.Describe(project, all).Web;

    /// <summary>A file's text, read again only once it was written since; null for one that is not there.</summary>
    public static string? Text(string path)
    {
        try
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (written.Year < 1700)
            {
                return null;
            }
            lock (Read)
            {
                if (Read.TryGetValue(path, out (DateTime Written, string Text) kept) && kept.Written == written)
                {
                    return kept.Text;
                }
            }
            string text = File.ReadAllText(path);
            lock (Read)
            {
                Read[path] = (written, text);
            }
            return text;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
