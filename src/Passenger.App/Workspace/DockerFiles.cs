using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.Core;
using Passenger.Host;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>A project as Add > Docker Support and Add > Docker Compose write for it: its file, the assembly its image runs,
/// its newest .NET target, whether it is an ASP.NET Core app (the aspnet image, ports), the HTTP ports its launch profiles
/// listen on, and the project files a restore of it needs (its own and those it references, through them all).</summary>
public sealed record DockerProject(string Path, string Name, string Assembly, string? Framework, bool Web, IReadOnlyList<int> HttpPorts, IReadOnlyList<string> Restores)
{
    public string Folder => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>Its Dockerfile, where Add > Docker Support writes it: beside the project.</summary>
    public string Dockerfile => System.IO.Path.Combine(Folder, "Dockerfile");

    /// <summary>The Dockerfile it has (Dockerfile or Containerfile beside it), or null.</summary>
    public string? ExistingDockerfile => new[] { "Dockerfile", "Containerfile" }.Select(n => System.IO.Path.Combine(Folder, n)).FirstOrDefault(File.Exists);

    /// <summary>Its name as a compose service and an image's repository: lowercase letters, digits and dashes.</summary>
    public string Service => DockerFiles.ServiceName(Name);
}

/// <summary>The files Add > Docker Support and Add > Docker Compose write: a project's Dockerfile (its stages base,
/// build, publish and final), the .dockerignore at the build's context, and a solution's compose.yaml. Text alone: the
/// window writes them as one command (MainWindow.DockerSupport.cs).</summary>
public static class DockerFiles
{
    /// <summary>A project as its Dockerfile needs it, from its evaluation and the solution's other projects.</summary>
    public static DockerProject Describe(ProjectModel project, IReadOnlyList<ProjectModel> all)
    {
        EvaluatedProject? evaluated = project.Evaluated;
        string bare = System.IO.Path.GetFileNameWithoutExtension(project.Path);
        string assembly = evaluated?.AssemblyName is { Length: > 0 } named ? named : bare;
        List<string> restores = [project.Path];
        for (int i = 0; i < restores.Count; i++)
        {
            ProjectModel? at = all.FirstOrDefault(p => PathNames.Same(p.Path, restores[i]));
            foreach (string referenced in at?.ProjectReferences ?? [])
            {
                if (!restores.Any(r => PathNames.Same(r, referenced)))
                {
                    restores.Add(referenced);
                }
            }
        }
        bool web = AspNetCore(restores, all);
        return new DockerProject(project.Path, bare, assembly, Newest(project.TargetFrameworks), web, web ? Run.LaunchProfile.HttpPorts(Run.LaunchProfile.Read(project)) : [], restores);
    }

    private const string AspNetCoreApp = "Microsoft.AspNetCore.App";

    /// <summary>Whether a project runs on ASP.NET Core's shared framework, so its image is aspnet's: it or a project it
    /// references (through them all) references the framework as evaluated (the Web SDK's own reference among them), or
    /// its restore says a package or a reference brings it (<c>project.assets.json</c>, as the build's runtimeconfig.json
    /// will name it).</summary>
    private static bool AspNetCore(IReadOnlyList<string> restores, IReadOnlyList<ProjectModel> all)
    {
        if (restores.Select(r => all.FirstOrDefault(p => PathNames.Same(p.Path, r))).Any(p =>
            p?.Evaluated?.Dependencies.Any(d => d.FrameworkReferences.Contains(AspNetCoreApp, StringComparer.OrdinalIgnoreCase)) == true))
        {
            return true;
        }
        if (Passenger.Xaml.Projects.ProjectInputs.FindAssets(restores[0]) is not { } assets)
        {
            return false;
        }
        try
        {
            using FileStream stream = File.OpenRead(assets);
            using JsonDocument read = JsonDocument.Parse(stream);
            JsonElement root = read.RootElement;
            bool own = root.TryGetProperty("project", out JsonElement project) && project.TryGetProperty("frameworks", out JsonElement frameworks)
                && frameworks.ValueKind == JsonValueKind.Object
                && frameworks.EnumerateObject().Any(f => f.Value.TryGetProperty("frameworkReferences", out JsonElement named)
                    && named.ValueKind == JsonValueKind.Object && named.EnumerateObject().Any(n => n.Name.Equals(AspNetCoreApp, StringComparison.OrdinalIgnoreCase)));
            // What a package or a project it references brings: each library's frameworkReferences in the restore's targets.
            bool brought = root.TryGetProperty("targets", out JsonElement targets) && targets.ValueKind == JsonValueKind.Object
                && targets.EnumerateObject().Any(t => t.Value.ValueKind == JsonValueKind.Object && t.Value.EnumerateObject().Any(l =>
                    l.Value.ValueKind == JsonValueKind.Object && l.Value.TryGetProperty("frameworkReferences", out JsonElement references)
                    && references.ValueKind == JsonValueKind.Array && references.EnumerateArray().Any(r => r.GetString()?.Equals(AspNetCoreApp, StringComparison.OrdinalIgnoreCase) == true)));
            return own || brought;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A restore being written, or one that does not read: the evaluation's word stands.
            return false;
        }
    }

    /// <summary>The newest .NET a project targets (net10.0 of net8.0;net10.0), its platform left off; null for none.</summary>
    private static string? Newest(IReadOnlyList<string> frameworks) =>
        frameworks.Select(f => f.Split('-')[0])
            .Where(f => f.StartsWith("net", StringComparison.OrdinalIgnoreCase) && Version.TryParse(f[3..], out Version? v) && v.Major >= 5)
            .OrderByDescending(f => Version.Parse(f[3..]))
            .FirstOrDefault();

    /// <summary>The image tag of a target framework: 10.0 of net10.0; null for one that is not .NET 5 or later.</summary>
    private static string? Tag(string? framework) => framework is { Length: > 3 } && Version.TryParse(framework[3..], out Version? version)
        ? $"{version.Major}.{Math.Max(0, version.Minor)}" : null;

    /// <summary>A project's Dockerfile, its build's context the folder given (the solution's): the runtime stage it runs in
    /// (aspnet for an ASP.NET Core app, else runtime, at its framework's version, as its own unprivileged user, listening
    /// on its launch profiles' HTTP ports), the SDK stage that restores from the project files alone first (so a change of
    /// code reuses the restore's layer) and builds, a publish stage, and the final one. Each nuget.config is a secret
    /// mounted where it stands for the commands that restore (<see cref="NuGetSecrets"/>), never copied into a layer.</summary>
    public static string Dockerfile(DockerProject project, string context)
    {
        string tag = Tag(project.Framework) ?? "10.0";
        int major = int.Parse(tag.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        string relative = Relative(context, project.Path);
        string folder = Relative(context, project.Folder);
        string file = System.IO.Path.GetFileName(project.Path);
        IReadOnlyList<string> shared = Shared(context, project);
        IReadOnlyList<(string Id, string File)> secrets = NuGetSecrets(context, shared);
        string mounts = string.Concat(secrets.Select(s => $"--mount=type=secret,id={s.Id},target=/src/{Relative(context, s.File)},required=true "));
        StringBuilder text = new();
        text.Append("# Built from the solution's folder: docker build -f ").Append(Relative(context, project.Dockerfile));
        foreach ((string id, string config) in secrets)
        {
            text.Append(" --secret id=").Append(id).Append(",src=").Append(Relative(context, config));
        }
        text.Append(" .\n");
        text.Append("FROM mcr.microsoft.com/dotnet/").Append(project.Web ? "aspnet" : "runtime").Append(':').Append(tag).Append(" AS base\n");
        if (major >= 8)
        {
            // .NET 8's images have an unprivileged user, which the app runs as.
            text.Append("USER $APP_UID\n");
        }
        text.Append("WORKDIR /app\n");
        if (project.Web && project.HttpPorts.Count > 0)
        {
            text.Append("ENV ASPNETCORE_HTTP_PORTS=").Append(string.Join(';', project.HttpPorts)).Append('\n');
            foreach (int port in project.HttpPorts)
            {
                text.Append("EXPOSE ").Append(port).Append('\n');
            }
        }
        else if (project.Web)
        {
            text.Append("EXPOSE 8080\n");
        }
        text.Append("\nFROM mcr.microsoft.com/dotnet/sdk:").Append(tag).Append(" AS build\n");
        text.Append("ARG BUILD_CONFIGURATION=Release\n");
        text.Append("WORKDIR /src\n");
        foreach (string copied in shared.Where(f => !IsNuGetConfig(f)).Concat(project.Restores))
        {
            text.Append("COPY [\"").Append(Relative(context, copied)).Append("\", \"").Append(Into(Relative(context, System.IO.Path.GetDirectoryName(copied)!))).Append("\"]\n");
        }
        if (secrets.Count > 0)
        {
            text.Append("# nuget.config, which may hold a feed's password, is mounted for each restore as a secret, so no layer keeps it.\n");
        }
        text.Append("RUN ").Append(mounts).Append("dotnet restore \"./").Append(relative).Append("\"\n");
        text.Append("COPY . .\n");
        text.Append("WORKDIR \"/src/").Append(folder).Append("\"\n");
        text.Append("RUN ").Append(mounts).Append("dotnet build \"./").Append(file).Append("\" -c $BUILD_CONFIGURATION -o /app/build\n");
        text.Append("\nFROM build AS publish\n");
        text.Append("ARG BUILD_CONFIGURATION=Release\n");
        text.Append("RUN ").Append(mounts).Append("dotnet publish \"./").Append(file).Append("\" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false\n");
        text.Append("\nFROM base AS final\n");
        text.Append("WORKDIR /app\n");
        text.Append("COPY --from=publish /app/publish .\n");
        text.Append("ENTRYPOINT [\"dotnet\", \"").Append(project.Assembly).Append(".dll\"]\n");
        return text.ToString();
    }

    /// <summary>Where a COPY puts a file of a folder of the context into the build's work folder: the same folder under it.</summary>
    private static string Into(string folder) => folder.Length == 0 ? "./" : folder + "/";

    /// <summary>The files a restore reads besides the projects, where the context holds them: each Directory.Build.props,
    /// Directory.Packages.props, nuget.config and global.json from the context down to each project's folder, named as
    /// the disk names them (NuGet.Config, nuget.config), since the image's file system tells letter case apart.</summary>
    private static IReadOnlyList<string> Shared(string context, DockerProject project)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "global.json", "nuget.config", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" };
        List<string> found = [];
        foreach (string restored in project.Restores)
        {
            for (string? folder = System.IO.Path.GetDirectoryName(restored); folder is not null && (PathNames.Same(folder, context) || PathNames.IsUnder(folder, context));
                folder = System.IO.Path.GetDirectoryName(folder))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder).Where(f => names.Contains(System.IO.Path.GetFileName(f))).ToList();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    files = [];
                }
                foreach (string file in files)
                {
                    if (!found.Contains(file, StringComparer.Ordinal))
                    {
                        found.Add(file);
                    }
                }
                if (PathNames.Same(folder, context))
                {
                    break;
                }
            }
        }
        // The context's first, then deeper, as MSBuild finds them.
        return [.. found.OrderBy(f => f.Count(c => c is '/' or '\\')).ThenBy(f => f, StringComparer.Ordinal)];
    }

    /// <summary>The nuget.configs a project's restore reads in its build's context (<see cref="Shared"/>), each with the id
    /// of the build secret Passenger's Dockerfile mounts it as: <c>nugetconfig</c> for the context's own, and after it the
    /// folder it is in for a deeper one (<c>nugetconfig-web</c>).</summary>
    public static IReadOnlyList<(string Id, string File)> NuGetSecrets(string context, DockerProject project) => NuGetSecrets(context, Shared(context, project));

    private static IReadOnlyList<(string Id, string File)> NuGetSecrets(string context, IReadOnlyList<string> shared) =>
        [.. shared.Where(IsNuGetConfig).Select(f => (Relative(context, System.IO.Path.GetDirectoryName(f)!) is { Length: > 0 } folder
            ? $"nugetconfig-{ServiceName(folder)}" : "nugetconfig", f))];

    /// <summary>The secrets of those given a Dockerfile there mounts (one Passenger wrote before).</summary>
    public static IReadOnlyList<(string Id, string File)> Mounted(string dockerfile, IReadOnlyList<(string Id, string File)> secrets)
    {
        if (secrets.Count == 0)
        {
            return [];
        }
        string text;
        try
        {
            text = FileText.Load(dockerfile)?.Text ?? string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            text = string.Empty;
        }
        return [.. secrets.Where(s => text.Contains($"type=secret,id={s.Id},", StringComparison.Ordinal))];
    }

    private static bool IsNuGetConfig(string file) => System.IO.Path.GetFileName(file).Equals("nuget.config", StringComparison.OrdinalIgnoreCase);

    /// <summary>A path from the build's context, with '/' as the Dockerfile writes it.</summary>
    public static string Relative(string context, string path)
    {
        string relative = System.IO.Path.GetRelativePath(context, path).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }

    /// <summary>What a build's context leaves out of the image: build output, the IDEs' and Git's folders, packages a
    /// restore makes again, secrets (nuget.config in any letter case, which the Dockerfile mounts as one), and the Docker
    /// files themselves.</summary>
    public const string Ignore =
        "# Kept out of the build's context, so a change to them never rebuilds an image.\n"
        + "**/bin\n**/obj\nartifacts/\n**/.git\n**/.gitignore\n**/.vs\n**/.vscode\n**/.idea\n**/*.user\n**/node_modules\n**/npm-debug.log\n"
        + "**/.env\n**/secrets.json\n" + NuGetConfigIgnored + "\n"
        + "**/.dockerignore\n**/Dockerfile*\n**/compose*.y*ml\n**/docker-compose*\nLICENSE\nREADME.md\n";

    /// <summary>The line of a .dockerignore that leaves nuget.config out, in any letter case, wherever it stands.</summary>
    public const string NuGetConfigIgnored = "**/[Nn][Uu][Gg][Ee][Tt].[Cc][Oo][Nn][Ff][Ii][Gg]";

    /// <summary>A .dockerignore's text with <see cref="NuGetConfigIgnored"/> added at its end, in its line breaks, where
    /// it keeps a nuget.config in the build's context (by a name NuGet reads, at the root or in a folder, as Docker reads
    /// its patterns: <see cref="LeavesOut"/>); null where it leaves every one out already.</summary>
    public static string? IgnoringNuGetConfig(string text)
    {
        string[] names = ["nuget.config", "NuGet.config", "NuGet.Config"];
        if (names.All(n => LeavesOut(text, n) && LeavesOut(text, $"Project/{n}") && LeavesOut(text, $"Project/Folder/{n}")))
        {
            return null;
        }
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return $"{text}{(text.Length == 0 || text.EndsWith('\n') ? string.Empty : newline)}{NuGetConfigIgnored}{newline}";
    }

    /// <summary>Whether a .dockerignore leaves a path of the build's context ('/' between its folders) out, as Docker reads
    /// it: a line a pattern (one starting '#' a comment), cleaned as a path, '*' and '?' within a name, '**' across folders,
    /// a '!' line taking back what came before; the last line matching the path or a folder holding it decides.</summary>
    public static bool LeavesOut(string ignore, string path)
    {
        bool left = false;
        string[] folders = path.Split('/');
        foreach (string line in ignore.TrimStart('\uFEFF').Split('\n'))
        {
            string pattern = line.Trim();
            if (line.StartsWith('#') || pattern.Length == 0)
            {
                continue;
            }
            bool back = pattern[0] == '!';
            // A line taking back is read only of what is left out, any other only of what is not.
            if (back != left)
            {
                continue;
            }
            Regex matching = IgnorePattern(Cleaned(back ? pattern[1..].Trim() : pattern));
            if (matching.IsMatch(path) || Enumerable.Range(1, folders.Length - 1).Any(n => matching.IsMatch(string.Join('/', folders[..n]))))
            {
                left = !back;
            }
        }
        return left;
    }

    /// <summary>A pattern as a path cleaned (its '.' parts and empty ones gone, a '..' taking the part before it) and its
    /// leading '/' taken off.</summary>
    private static string Cleaned(string pattern)
    {
        List<string> parts = [];
        foreach (string part in pattern.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!pattern.StartsWith('/'))
                {
                    parts.Add(part);
                }
            }
            else if (part is not ("" or "."))
            {
                parts.Add(part);
            }
        }
        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    /// <summary>A cleaned pattern as Docker makes it a regular expression of a whole path: '**' any folders (none too), '*'
    /// and '?' within a name, '\' escaping the character after it, the characters a path pattern gives no meaning
    /// escaped, the rest ('[…]' classes among them) as written.</summary>
    private static Regex IgnorePattern(string pattern)
    {
        StringBuilder made = new("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                }
                made.Append(i + 1 == pattern.Length ? ".*" : "(.*/)?");
            }
            else if (c == '*')
            {
                made.Append("[^/]*");
            }
            else if (c == '?')
            {
                made.Append("[^/]");
            }
            else if (c == '\\')
            {
                made.Append(i + 1 < pattern.Length ? Regex.Escape(pattern[++i].ToString()) : @"\\");
            }
            else
            {
                made.Append(c is '.' or '+' or '(' or ')' or '|' or '{' or '}' or '$' ? $"\\{c}" : c.ToString());
            }
        }
        try
        {
            return new Regex(made.Append('$').ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // A class left open, which Docker refuses too: it matches nothing here.
            return new Regex("(?!)", RegexOptions.None, TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>A compose service's name from a project's: lowercase, every run of other characters a dash.</summary>
    public static string ServiceName(string name)
    {
        StringBuilder made = new();
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                made.Append(c);
            }
            else if (made.Length > 0 && made[^1] != '-')
            {
                made.Append('-');
            }
        }
        return made.ToString().Trim('-') is { Length: > 0 } service ? service : "app";
    }

    /// <summary>How compose.yaml builds a project's image, from the solution's folder (<paramref name="context"/>, where
    /// compose.yaml is), and the ports it publishes: Passenger's own Dockerfile (<paramref name="existing"/> null) from
    /// that folder on the launch profiles' HTTP ports; a Dockerfile the project had from the folder its COPY lines read
    /// (the project's, unless more of what they copy is found from the solution's), on the ports it sets
    /// (ASPNETCORE_HTTP_PORTS, else ASPNETCORE_URLS' HTTP ones, else what it EXPOSEs, else ASP.NET Core's 8080).</summary>
    public static (string Context, string Dockerfile, IReadOnlyList<int> Ports) BuildFor(DockerProject project, string? existing, string context)
    {
        if (existing is null)
        {
            return (".", Relative(context, project.Dockerfile), project.Web ? project.HttpPorts.Count > 0 ? project.HttpPorts : [8080] : []);
        }
        DockerfileStages stages;
        try
        {
            stages = DockerfileStages.Read(FileText.Load(existing)?.Text ?? string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stages = DockerfileStages.Read(string.Empty);
        }
        List<string> sources = [.. stages.Stages.SelectMany(s => s.Copies.Select(c => c.Source))];
        string from = Found(sources, context) > Found(sources, project.Folder) ? context : project.Folder;
        string folder = Relative(context, from);
        List<int> ports = [];
        if (stages.Final is { } final)
        {
            DockerfileStage[] lineage = [.. stages.Lineage(final)];
            string? Variable(string name) => lineage.Select(s => s.Environment.GetValueOrDefault(name)).FirstOrDefault(v => v is not null);
            if (Variable("ASPNETCORE_HTTP_PORTS") is { } http)
            {
                ports.AddRange(http.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0).Where(n => n > 0));
            }
            else if (Variable("ASPNETCORE_URLS") is { } urls)
            {
                ports.AddRange(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(u => Uri.TryCreate(u.Replace("*", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal), UriKind.Absolute, out Uri? url)
                        && url.Scheme == Uri.UriSchemeHttp ? url.Port : 0).Where(n => n > 0));
            }
            if (ports.Count == 0)
            {
                ports.AddRange(lineage.SelectMany(s => s.Exposed));
            }
            if (ports.Count == 0 && (project.Web || lineage[^1].From.Contains("/dotnet/aspnet", StringComparison.OrdinalIgnoreCase)))
            {
                ports.Add(8080);
            }
        }
        return (folder.Length == 0 ? "." : folder, Relative(from, existing), [.. ports.Distinct()]);
    }

    /// <summary>How many of a Dockerfile's COPY sources a folder holds as written (a glob's folder holding a match).</summary>
    private static int Found(IEnumerable<string> sources, string folder) => sources.Count(source =>
    {
        string path = source;
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        if (path is "" or "." || source.StartsWith('/') || source.Contains("://", StringComparison.Ordinal) || source.Contains('$', StringComparison.Ordinal))
        {
            // The context itself, which every folder is, an absolute path, a URL or a variable: no sign either way.
            return false;
        }
        string full = System.IO.Path.Combine(folder, path);
        if (path.IndexOfAny(['*', '?', '[']) < 0)
        {
            return File.Exists(full) || Directory.Exists(full);
        }
        string under = System.IO.Path.GetDirectoryName(full)!;
        try
        {
            return Directory.Exists(under) && Directory.EnumerateFileSystemEntries(under, System.IO.Path.GetFileName(full)).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    });

    /// <summary>A solution's compose.yaml: a service for each project (named apart, <see cref="ServiceNames"/>), built from
    /// its Dockerfile (<see cref="BuildFor"/>: <c>Existing</c> the one it had, null for Passenger's own) given the
    /// nuget.configs it mounts (<see cref="NuGetSecrets"/>), published on its HTTP ports, given its connection strings to the
    /// backing services its packages point to (<see cref="BackingService.Needs"/>, the databases kept), which it waits for
    /// until their healthchecks pass; each backing service with its healthcheck and data volume, its first database made.</summary>
    public static string Compose(string context, IReadOnlyList<(DockerProject Project, string? Existing, BackingNeeds Needs)> projects)
    {
        List<BackingService> backing = [.. BackingService.Known.Where(k => projects.Any(p => p.Needs.Services.Contains(k)))];
        IReadOnlyList<string> names = ServiceNames([.. projects.Select(p => p.Project)], backing);
        List<(DockerProject Project, string? Existing, IReadOnlyList<BackingService> Uses, IReadOnlyList<BackingConnection> Reached)> made =
            [.. projects.Select(p => (p.Project, p.Existing, p.Needs.Services, p.Needs.Connections))];
        Dictionary<BackingService, List<string>> databases = backing.ToDictionary(b => b, b => made.SelectMany(m => m.Reached)
            .Where(r => r.To == b).Select(r => b.Database(r.Name, r.Value)).OfType<string>().Distinct(StringComparer.Ordinal).ToList());
        Dictionary<string, string> secrets = new(StringComparer.Ordinal);
        StringBuilder text = new();
        foreach (BackingService service in backing.Where(b => b.Note is not null))
        {
            text.Append("# ").Append(service.Note).Append('\n');
        }
        text.Append("services:\n");
        foreach (((DockerProject project, string? existing, IReadOnlyList<BackingService> uses, IReadOnlyList<BackingConnection> reached), string named) in made.Zip(names))
        {
            (string from, string dockerfile, IReadOnlyList<int> ports) = BuildFor(project, existing, context);
            text.Append("  ").Append(named).Append(":\n");
            text.Append("    build:\n      context: ").Append(from).Append("\n      dockerfile: ").Append(dockerfile).Append('\n');
            IReadOnlyList<(string Id, string File)> mounted = existing is null ? NuGetSecrets(context, project) : Mounted(existing, NuGetSecrets(context, project));
            if (mounted.Count > 0)
            {
                text.Append("      secrets:\n");
                foreach ((string id, string config) in mounted)
                {
                    text.Append("        - ").Append(id).Append('\n');
                    secrets[id] = Relative(context, config);
                }
            }
            text.Append("    image: ").Append(named).Append('\n');
            if (ports.Count > 0)
            {
                text.Append("    ports:\n");
                foreach (int port in ports)
                {
                    text.Append("      - \"").Append(port).Append(':').Append(port).Append("\"\n");
                }
            }
            if (reached.Count > 0)
            {
                text.Append("    environment:\n");
                foreach ((string name, string value, BackingService to) in reached)
                {
                    text.Append("      ConnectionStrings__").Append(name).Append(": \"").Append(to.ConnectionString(name, value)).Append("\"\n");
                }
            }
            if (uses.Count > 0)
            {
                text.Append("    depends_on:\n");
                foreach (BackingService service in uses)
                {
                    text.Append("      ").Append(service.Service).Append(":\n        condition: service_healthy\n");
                }
            }
        }
        foreach (BackingService service in backing)
        {
            text.Append("  ").Append(service.Service).Append(":\n");
            foreach (string comment in service.Comments(databases[service]))
            {
                text.Append("    # ").Append(comment).Append('\n');
            }
            text.Append("    image: ").Append(service.Image).Append('\n');
            foreach (string line in service.Body(databases[service]))
            {
                text.Append("    ").Append(line).Append('\n');
            }
            text.Append("    healthcheck:\n      test: ").Append(service.Test).Append('\n');
            text.Append("      interval: 5s\n      timeout: 5s\n      retries: 10\n");
            if (service.Service == "sqlserver")
            {
                text.Append("      start_period: 20s\n");
            }
        }
        string[] volumes = [.. backing.Select(b => b.Volume).OfType<string>()];
        if (volumes.Length > 0)
        {
            text.Append("\nvolumes:\n");
            foreach (string volume in volumes)
            {
                text.Append("  ").Append(volume).Append(":\n");
            }
        }
        if (secrets.Count > 0)
        {
            text.Append('\n').Append(Secrets(secrets));
        }
        return text.ToString();
    }

    /// <summary>The projects' services' names in a compose file, in their order: each its project's (<see
    /// cref="DockerProject.Service"/>), or where a backing service (which connection strings and depends_on name) or a
    /// project before it has that name already, the name with the first number from 2 after a dash that none has.</summary>
    public static IReadOnlyList<string> ServiceNames(IReadOnlyList<DockerProject> projects, IEnumerable<BackingService> backing)
    {
        HashSet<string> taken = new(backing.Select(b => b.Service), StringComparer.Ordinal);
        List<string> names = [];
        foreach (DockerProject project in projects)
        {
            string name = project.Service;
            for (int n = 2; !taken.Add(name); n++)
            {
                name = $"{project.Service}-{n}";
            }
            names.Add(name);
        }
        return names;
    }

    /// <summary>A compose file's top-level secrets: each by its id, read from its file.</summary>
    private static string Secrets(IEnumerable<KeyValuePair<string, string>> secrets) =>
        "secrets:\n" + string.Concat(secrets.Select(s => $"  {s.Key}:\n    file: {s.Value}\n"));

    /// <summary>A compose file's text with a service's lines (those under <c>services:</c> of a file <see cref="Compose"/>
    /// wrote for it) after the last line of its services, indented as its services are, and the build secrets they name
    /// that it does not declare after the last of its own (a block of them begun at its end where it has none), every
    /// other character as it was; null with why when it has no block of services or one of that name.</summary>
    public static (string? Text, string? Problem) WithService(string text, string service, string made)
    {
        if (ComposeText.Of(text).Entries.Any(e => e.Path.Count == 2 && e.Path[0] == "services" && e.Path[1] == service))
        {
            return (null, $"it has a service named {service} already");
        }
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (Inserted(text, "services", Block(made, "services"), newline) is not { } added)
        {
            return (null, "it has no block of services");
        }
        HashSet<string> declared = [.. ComposeText.Of(text).Entries.Where(e => e.Path.Count == 2 && e.Path[0] == "secrets").Select(e => e.Path[1])];
        string[] secrets = Block(made, "secrets");
        // Each secret is its id's line and its file's after it.
        string[] lacking = [.. secrets.Chunk(2).Where(s => !declared.Contains(s[0].Trim().TrimEnd(':'))).SelectMany(s => s)];
        if (lacking.Length == 0)
        {
            return (added, null);
        }
        return (Inserted(added, "secrets", lacking, newline)
            ?? $"{added}{(added.EndsWith('\n') ? string.Empty : newline)}{newline}secrets:{newline}{string.Concat(lacking.Select(l => l + newline))}", null);
    }

    /// <summary>The lines of a top-level block of a file <see cref="Compose"/> wrote (<c>services:</c>, <c>secrets:</c>),
    /// as written there; none where it has no such block.</summary>
    private static string[] Block(string made, string name)
    {
        string header = $"{name}:\n";
        int begins = made.StartsWith(header, StringComparison.Ordinal) ? 0
            : made.IndexOf($"\n{header}", StringComparison.Ordinal) is int found and >= 0 ? found + 1 : -1;
        return begins < 0 ? []
            : [.. made[(begins + header.Length)..].Split('\n').TakeWhile(l => l.Length == 0 || char.IsWhiteSpace(l[0])).Where(l => l.Length > 0)];
    }

    /// <summary>A compose file's text with lines added after the last line of a top-level block, indented as its entries
    /// are; null when it has no such block.</summary>
    private static string? Inserted(string text, string name, IEnumerable<string> lines, string newline)
    {
        string[] have = text.Split('\n');
        int at = Array.FindIndex(have, l => l.TrimEnd('\r').TrimEnd() == $"{name}:");
        if (at < 0)
        {
            return null;
        }
        int last = at;
        int indent = -1;
        for (int i = at + 1; i < have.Length; i++)
        {
            string line = have[i].TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }
            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }
            // A comment indented under the block is its own, though its column sets none.
            indent = indent < 0 && !line.TrimStart().StartsWith('#') ? line.Length - line.TrimStart().Length : indent;
            last = i;
        }
        string shift = new(' ', Math.Max(0, (indent < 0 ? 2 : indent) - 2));
        int end = 0;
        for (int i = 0; i <= last; i++)
        {
            end += have[i].Length + 1;
        }
        string before = end > text.Length ? text + newline : text[..end];
        return before + string.Concat(lines.Select(l => shift + l + newline)) + (end > text.Length ? string.Empty : text[end..]);
    }
}
