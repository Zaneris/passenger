using System.Text.Json;
using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Containers;

/// <summary>What a compose file's check reads of the folder's code: its projects, and its C# language server once the
/// projects are loaded (null before), which reads what each project's code reads.</summary>
public sealed record ComposeCode(IReadOnlyList<ProjectModel> Projects, RoslynSession? Roslyn);

/// <summary>A connection string a .NET service's project reads that its environment never sets: the service, the name its
/// code reads it by, the project, and the value its fix sets (the backing service's connection string where the file
/// runs the one it reaches, else null, the person's to fill in) with what that value is.</summary>
internal sealed record ComposeConnection(string Service, string Name, string Project, string? Value, string Detail)
{
    public string Variable => $"ConnectionStrings__{Name}";
}

/// <summary>A connection string a .NET service of an open compose file reads that its environment does not set, as the
/// file's check last found it, which the service's code is marked with where it reads it: the compose file, the service, the
/// name, the service's project and the files of its code that read the name (the extension's ConnectionReads answer), what
/// the check says of it and whether that is the weaker warning (its settings give it at localhost).</summary>
public sealed record ComposeGap(string Compose, string Service, string Name, string Project, IReadOnlyList<string> ReadIn, string Said, bool Weak)
{
    public string Variable => $"ConnectionStrings__{Name}";

    /// <summary>What its fix sets the variable to (null for a value the person fills in) and what that value is; none where
    /// it has no fix (a localhost one of a kind the file does not run).</summary>
    public (string? Value, string Detail)? Fix { get; init; }

    /// <summary>Whether two say the same, the files they are read in compared as paths.</summary>
    public bool Same(ComposeGap other) => this with { ReadIn = [] } == other with { ReadIn = [] } && ReadIn.SequenceEqual(other.ReadIn, PathNames.Comparer);
}

/// <summary>The connection strings a compose file's .NET services read that their environments do not set, by Compose's
/// own model of their environments (environment, env_file, what it interpolated) and the reads
/// <see cref="Handlers.ConnectionReads"/> finds in each service's project, each marked on the service's name where the file
/// declares it: one the project's own settings never give either (appsettings.json, or appsettings.{environment}.json for
/// the environment the service runs in, Development by default), a warning; one they give at this machine's own host
/// (<see cref="BackingService.LocalHost"/>), which in the container is the container itself, a weaker one; one they give
/// elsewhere, nothing. Its fix sets the variable in the service's environment (<see cref="ComposeFixes.SetVariable"/>).
/// Each is also a <see cref="ComposeGap"/> for the code that reads it. A service is .NET when its build's Dockerfile
/// builds one of the solution's projects (<see cref="Web.ComposeDeclarations.Built"/>).</summary>
internal static class ComposeConnections
{
    /// <summary>The missing connection strings of the services the file declares, from Compose's model of it
    /// (<c>config --format json --no-path-resolution</c>, paths the folder's); none while the language server has not
    /// loaded the projects, or when it cannot answer: the file's findings, and the same as gaps for the code.</summary>
    public static async Task<(IReadOnlyList<Finding> Findings, IReadOnlyList<ComposeGap> Gaps)> FindAsync(ComposeCode code, string folder, string path, TextSnapshot text, string model, CancellationToken cancel)
    {
        if (code.Roslyn is not { } roslyn || code.Projects.Count == 0)
        {
            return ([], []);
        }
        List<(string Name, string Project, IReadOnlyDictionary<string, string?> Environment)> services = [];
        List<(string Name, BackingService Kind)> backing = [];
        try
        {
            using JsonDocument read = JsonDocument.Parse(model);
            if (!read.RootElement.TryGetProperty("services", out JsonElement all) || all.ValueKind != JsonValueKind.Object)
            {
                return ([], []);
            }
            foreach (JsonProperty service in all.EnumerateObject())
            {
                if (BackingService.OfImage(ContainerEngine.Text(service.Value, "image")) is { } kind)
                {
                    backing.Add((service.Name, kind));
                }
                if (ProjectOf(service.Value, folder, code.Projects) is { } project)
                {
                    services.Add((service.Name, project, Environment(service.Value)));
                }
            }
        }
        catch (JsonException)
        {
            return ([], []);
        }
        if (services.Count == 0)
        {
            return ([], []);
        }
        ConnectionReadsFound reads;
        try
        {
            reads = await roslyn.ExtensionAsync<ConnectionReadsFound>(Handlers.ConnectionReads,
                new ConnectionReadsRequest([.. services.Select(s => s.Project).Distinct(StringComparer.Ordinal)]), cancel).ConfigureAwait(false);
        }
        catch (LanguageServerFailed failed)
        {
            IdeErrors.Write($"The connection strings the projects of {path} read could not be asked of the C# language server: {failed.Reason}");
            return ([], []);
        }
        string written = text.ToString();
        ComposeText declared = ComposeText.Of(written);
        List<Finding> found = [];
        List<ComposeGap> gaps = [];
        foreach ((string service, string project, IReadOnlyDictionary<string, string?> environment) in services)
        {
            if (declared.At(["services", service]) is not { } entry)
            {
                // Declared by the file read with it: its own check marks it there.
                continue;
            }
            ProjectModel? runs = code.Projects.FirstOrDefault(p => PathNames.Same(p.Path, project));
            // The settings files the app reads in its container, by the environment it runs in there.
            string environmentName = new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" }
                .Select(key => environment.GetValueOrDefault(key)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "Development";
            TextRange range = ComposeFixes.KeyName(written, entry);
            string projectName = Path.GetFileNameWithoutExtension(project);
            foreach (string name in reads.Reads.Where(r => PathNames.Same(r.Project, project)).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Sets(environment, name))
                {
                    continue;
                }
                string variable = $"ConnectionStrings__{name}";
                string[] readIn = [.. reads.Reads.Where(r => PathNames.Same(r.Project, project) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Path).Distinct(PathNames.Comparer)];
                if (runs is not null && BackingService.Configured(runs.Folder, name, environmentName) is { } given)
                {
                    // Given by its settings: worth saying only when it names this machine, in the container the container itself.
                    if (BackingService.LocalHost(given.Value) is not { } local)
                    {
                        continue;
                    }
                    ComposeConnection? to = Reach(service, name, project, given.Value, runs, backing);
                    string said = $"{service} runs {projectName}, whose {given.File} gives the connection string {name} at {local}, which in its container is the container itself: set {variable} in the service's environment to "
                        + (to is null ? "where it runs (a service of this file by its name, host.docker.internal for this machine)" : $"{to.Detail[3..]}");
                    found.Add(new Finding(ProblemSeverity.Warning, said, path, text.GetPosition(range.Start), range)
                    {
                        Weak = true,
                        Fixes = to is null ? null : now => ComposeFixes.SetVariable(now, to.Service, to.Variable, to.Value, to.Detail),
                    });
                    gaps.Add(new ComposeGap(path, service, name, project, readIn, said, Weak: true) { Fix = to is null ? null : (to.Value, to.Detail) });
                    continue;
                }
                ComposeConnection missing = Reach(service, name, project, string.Empty, runs, backing) ?? new ComposeConnection(service, name, project, null, "to a value you fill in");
                string never = $"{service} runs {projectName}, which reads the connection string {name}: the service's environment never sets {missing.Variable}";
                found.Add(new Finding(ProblemSeverity.Warning, never, path, text.GetPosition(range.Start), range)
                {
                    Fixes = now => ComposeFixes.SetVariable(now, missing.Service, missing.Variable, missing.Value, missing.Detail),
                });
                gaps.Add(new ComposeGap(path, service, name, project, readIn, never, Weak: false) { Fix = (missing.Value, missing.Detail) });
            }
        }
        return (found, gaps);
    }

    /// <summary>The project a service runs: the one its build's Dockerfile builds, its context and Dockerfile the folder's.</summary>
    private static string? ProjectOf(JsonElement service, string folder, IReadOnlyList<ProjectModel> projects)
    {
        if (!service.TryGetProperty("build", out JsonElement build) || build.ValueKind != JsonValueKind.Object
            || ContainerEngine.Text(build, "context") is not { } context || context.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            string root = Path.GetFullPath(Path.Combine(folder, context));
            string dockerfile = Path.GetFullPath(Path.Combine(root, ContainerEngine.Text(build, "dockerfile") ?? "Dockerfile"));
            return File.Exists(dockerfile) ? Web.ComposeDeclarations.Built(TextSnapshot.From(File.ReadAllText(dockerfile)), root, projects) : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A service's environment as Compose's model has it, a variable passed on unset as null.</summary>
    private static Dictionary<string, string?> Environment(JsonElement service)
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);
        if (service.TryGetProperty("environment", out JsonElement variables) && variables.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty variable in variables.EnumerateObject())
            {
                environment[variable.Name] = variable.Value.ValueKind switch
                {
                    JsonValueKind.String => variable.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => variable.Value.GetRawText(),
                };
            }
        }
        return environment;
    }

    /// <summary>Whether an environment sets a connection string, as .NET reads it there (<c>ConnectionStrings__name</c>, or
    /// with a colon; letter case aside), to something.</summary>
    private static bool Sets(IReadOnlyDictionary<string, string?> environment, string name) =>
        new[] { $"ConnectionStrings__{name}", $"ConnectionStrings:{name}" }.Any(key => environment.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value));

    /// <summary>Where the file runs the backing service a connection string reaches, its connection string there, the value
    /// its fix sets: the service of the name read, else the one of the kind the value its settings give (empty for none),
    /// its name or its packages point to, when the file runs one alone; null where the file runs none it can tell.</summary>
    private static ComposeConnection? Reach(string service, string name, string project, string configured, ProjectModel? model,
        IReadOnlyList<(string Name, BackingService Kind)> backing)
    {
        (string Name, BackingService Kind)? to = backing.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } same ? same : null;
        if (to is null)
        {
            IReadOnlyList<BackingService> kinds = model is null ? [] : BackingService.For(model.Packages.Select(p => p.Name));
            IReadOnlyList<BackingService> running = [.. backing.Select(b => b.Kind).Distinct()];
            BackingService? reached = BackingService.Reached(name, configured, [.. running.Where(r => kinds.Count == 0 || kinds.Contains(r))])
                ?? (running.Where(kinds.Contains).ToList() is [{ } only] ? only : null);
            to = reached is null ? null : backing.Where(b => b.Kind == reached).ToList() is [{ } one] ? one : null;
        }
        return to is { } reach
            ? new ComposeConnection(service, name, project, reach.Kind.ConnectionString(name, configured, reach.Name), $"to {reach.Name}'s connection string")
            : null;
    }
}
