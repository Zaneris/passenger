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
            // The files docker compose reads in the folder, as a configuration of it reads them.
            if (ComposeFiles.Unasked(folder) is not [string file, ..] files || covered.Contains(file))
            {
                continue;
            }
            covered.UnionWith(files);
            string relative = Path.GetRelativePath(workspace.Folder, file).Replace('\\', '/');
            made.Add(new RunConfiguration
            {
                Name = $"Compose: {relative}",
                Kind = RunKind.Compose,
                ComposeFiles = [.. files],
                // The first .NET service's published port, once it answers; none for an application that publishes none.
                LaunchBrowser = true,
                BrowserUrl = ServiceUrl,
                Origin = $"Generated for {relative}{(files.Count > 1 ? $" with {string.Join(", ", files.Skip(1).Select(Path.GetFileName))}" : string.Empty)}",
            });
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
            environment.TryAdd(AspNetCoreVariables.HttpsPorts, "8081");
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
        if (Text(dcproj) is not { } text || ComposeProjectFile.Read(text) is not { } read)
        {
            return [];
        }
        string folder = Path.GetDirectoryName(dcproj)!;
        List<string> files = ComposeFilesOf(folder, read);
        string name = Path.GetFileNameWithoutExtension(dcproj);
        string service = read.ServiceName;
        string url = read.ServiceUrl;
        bool browser = read.LaunchesBrowser;
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
                LaunchBrowser = action.Length > 0 ? string.Equals(action, ComposeProjectFile.LaunchBrowserAction, StringComparison.OrdinalIgnoreCase) : browser,
                BrowserUrl = profile.ComposeLaunchUrl ?? own.BrowserUrl,
                Origin = $"Generated from the launch profile {profile.Name} of the Docker Compose project {Path.GetFileName(dcproj)}",
            });
        }
        return made;
    }

    /// <summary>A Docker Compose project's compose files, as its runs take them: its base file (DockerComposeBaseFilePath's,
    /// .yml else .yaml), the override beside it where there is one, then its AdditionalComposeFilePaths.</summary>
    private static List<string> ComposeFilesOf(string folder, ComposeProjectFile read)
    {
        string file = new[] { ".yml", ".yaml" }.Select(e => Path.Combine(folder, read.BaseFilePath.Replace('\\', '/') + e)).FirstOrDefault(File.Exists)
            ?? Path.Combine(folder, read.BaseFilePath.Replace('\\', '/') + ".yml");
        List<string> files = [PathNames.Canonical(file), .. ((string?[])[Override(file)]).OfType<string>().Select(PathNames.Canonical)];
        files.AddRange(read.AdditionalFiles.Select(more => PathNames.Canonical(Path.Combine(folder, more.Replace('\\', '/')))));
        return files;
    }

    /// <summary>What a Docker Compose project holds as the Solution view shows it, by path: the files its ItemGroups name, and
    /// those its runs read that it names not, as Visual Studio's SDK adds them: its compose files, the override under the
    /// file it overrides, and its launch profiles' file; none for one that is not there or does not read.</summary>
    public static IReadOnlyList<EvaluatedItem> ItemsOf(string dcproj)
    {
        if (Text(dcproj) is not { } text || ComposeProjectFile.Read(text) is not { } read)
        {
            return [];
        }
        string folder = Path.GetDirectoryName(dcproj)!;
        List<EvaluatedItem> items = [.. read.Items.Select(i => new EvaluatedItem(i.Type, PathNames.Canonical(Path.Combine(folder, i.Include.Replace('\\', '/'))), i.Link, i.DependentUpon))];
        List<string> files = ComposeFilesOf(folder, read);
        string? overriding = Override(files[0]) is { } beside ? PathNames.Canonical(beside) : null;
        foreach (string file in files.Append(LaunchProfile.SettingsIn(folder)).Where(File.Exists))
        {
            if (!items.Any(i => PathNames.Same(i.Path, file)))
            {
                bool over = overriding is not null && PathNames.Same(file, overriding);
                items.Add(new EvaluatedItem("None", PathNames.Canonical(file), DependentUpon: over ? Path.GetFileName(files[0]) : null));
            }
        }
        return items;
    }

    /// <summary>The name a Docker Compose project's configuration gives its Compose project (its DockerComposeProjectName),
    /// read as it runs; null for any other configuration, and for one whose .dcproj names none.</summary>
    public static string? ProjectNameOf(RunConfiguration configuration) =>
        configuration.Kind == RunKind.Compose && IsComposeProject(configuration.Project) && Text(configuration.Project) is { } text
            ? ComposeProjectFile.Read(text)?.ProjectName
            : null;

    /// <summary>A Docker Compose project's override beside its base file, as Visual Studio pairs them: the base's name
    /// with <c>.override.yml</c> (docker-compose.override.yml for docker-compose.yml), else <c>.override.yaml</c>; null
    /// where it has neither.</summary>
    private static string? Override(string file)
    {
        string stem = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
        return new[] { ".override.yml", ".override.yaml" }.Select(extension => stem + extension).FirstOrDefault(File.Exists);
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
