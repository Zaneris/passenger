using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>A place Go to Declaration goes to from a compose file: a file and the editor's position in it, named for the
/// list it is chosen from when there are several.</summary>
public sealed record ComposePlace(string Path, TextPosition Position, string Name);

/// <summary>What Go to Declaration finds in a compose file: places, or an image's registry page, or neither and why.</summary>
public sealed record ComposeAnswer(IReadOnlyList<ComposePlace> Places, Uri? Page, string Said)
{
    public static ComposeAnswer None(string said) => new([], null, said);
}

/// <summary>Passenger's own Go to Declaration in a compose file, read from the YAML server's outline of it (each key's path
/// from the top) and the text under the caret: a service's build to its Dockerfile and to the project that Dockerfile
/// builds; depends_on, extends (another file's too), links, volumes_from and network_mode's service: to the service; a
/// named volume, network, config or secret to its declaration; a ${VAR} or $VAR to its line in the .env beside the file
/// (one an env_file alone sets is said not set for Compose; $$VAR is no variable); an env_file to its file; an image to its
/// registry's page. Compose's own model needs an engine; this reads only the file, as it is typed.</summary>
public static partial class ComposeDeclarations
{
    /// <summary>Whether a file is one Docker Compose reads (<see cref="JsonSchemas.ComposeFiles"/>): compose.yaml,
    /// docker-compose.override.yml and the like.</summary>
    public static bool IsCompose(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        string extension = Path.GetExtension(name);
        if (extension is not (".yaml" or ".yml"))
        {
            return false;
        }
        string stem = name[..^extension.Length];
        return stem is "compose" or "docker-compose" || stem.StartsWith("compose.", StringComparison.Ordinal) || stem.StartsWith("docker-compose.", StringComparison.Ordinal);
    }

    /// <summary>Where Go to Declaration goes from an offset of a compose file whose outline the server gave for
    /// <paramref name="text"/>: <paramref name="texts"/> reads another file (the editor's text, else the disk's), and
    /// <paramref name="projects"/> are the solution's, a Dockerfile's project found among them.</summary>
    public static ComposeAnswer Find(string path, TextSnapshot text, IReadOnlyList<DocumentSymbol> outline, int offset,
        Func<string, TextSnapshot?> texts, IReadOnlyList<ProjectModel> projects)
    {
        ServerLines lines = ServerLines.Of(text);
        LspPosition at = lines.PositionOf(Math.Clamp(offset, 0, text.Length));
        List<DocumentSymbol> chain = DocumentSymbol.PathTo(outline, at);
        string folder = Path.GetDirectoryName(path)!;
        int line = text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length));
        int column = Math.Clamp(offset, 0, text.Length) - text.GetLineStart(line);
        string written = text.GetLineText(line);
        if (VariableAt(written, column) is { } variable)
        {
            return Variable(variable, folder, chain, outline, lines, texts);
        }
        if (chain is not [{ Name: "services" }, _, { } under, ..])
        {
            return ComposeAnswer.None(Refusals.NoDeclaration);
        }
        ComposeAnswer answer = under.Name switch
        {
            "build" => Build(chain, folder, texts, projects),
            "image" when under.Detail is { Length: > 0 } image => Image(image),
            "env_file" => EnvFile(chain, lines) is { Length: > 0 } file ? File(file, folder) : ComposeAnswer.None(Refusals.NoDeclaration),
            "extends" => Extends(chain, outline, lines, folder, texts),
            _ => Named(chain, at, written, column) switch
            {
                { Kind: "service" } service => Service(outline, lines, service.Name),
                { } named => Declared(outline, lines, named.Kind + "s", named.Name),
                null => ComposeAnswer.None(Refusals.NoDeclaration),
            },
        };
        return answer with { Places = [.. answer.Places.Select(p => p.Path.Length == 0 ? p with { Path = path } : p)] };
    }

    /// <summary>The name of the file's own that a place under a service names, read from the server's outline of the file
    /// (<paramref name="chain"/>, the symbols holding <paramref name="at"/>) and the line written there: a service its
    /// depends_on (an item, or a mapping's key with the place on it), links, volumes_from, network_mode's <c>service:</c>
    /// or extends names (with the other file an extends names), or a named volume, network, config or secret it uses (a short
    /// entry, or a long one's source); null where it names none. Go to Declaration and Rename read a place alike.</summary>
    internal static ComposeName? Named(List<DocumentSymbol> chain, LspPosition at, string written, int column)
    {
        string[] names = [.. chain.Select(s => s.Name)];
        if (names is not ["services", _, _, ..])
        {
            return null;
        }
        string key = names[2];
        // An entry under the key: a list's item, or a mapping's key (depends_on's db:) with the caret on its name.
        bool entry = chain.Count == 4 && (IsIndex(names[3]) || chain[3].SelectionRange.Holds(at));
        string? value = chain.Count > 3 && !IsIndex(names[3]) && chain[3].SelectionRange.Holds(at) ? names[3]
            : WordAt(written, column).Word is { Length: > 0 } word ? word : null;
        // A long entry's source (a volume's, a secret's, a config's), which names what is declared.
        bool source = chain.Count == 5 && IsIndex(names[3]) && names[4] == "source";
        string? named = key switch
        {
            "depends_on" or "links" or "volumes_from" when entry && value is not null => value.Split(':')[0] is var name && name != "container" ? name : null,
            "network_mode" when chain[2].Detail is { } mode && mode.StartsWith("service:", StringComparison.Ordinal) => mode["service:".Length..],
            "extends" when chain[2].Children is not { Count: > 0 } => chain[2].Detail,
            "extends" when !(chain.Count > 3 && chain[3].Name == "file") => chain[2].Children!.FirstOrDefault(p => p.Name == "service")?.Detail,
            "volumes" when (entry || source) && value is not null => VolumeSource(source, value),
            "networks" or "configs" or "secrets" when (entry || source) && value is not null => value,
            _ => null,
        };
        if (named is not { Length: > 0 })
        {
            return null;
        }
        string? file = key == "extends" ? chain[2].Children?.FirstOrDefault(p => p.Name == "file")?.Detail : null;
        return new ComposeName(named, key is "depends_on" or "links" or "volumes_from" or "network_mode" or "extends" ? "service" : key[..^1], file);
    }

    /// <summary>A list item's name in the server's outline: its index.</summary>
    private static bool IsIndex(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);

    /// <summary>The YAML scalar around a column of a line, without its quotes, a key's colon or a list's brackets.</summary>
    private static (string Word, int Start) WordAt(string line, int column)
    {
        static bool Ends(string line, int i) =>
            char.IsWhiteSpace(line[i]) || line[i] is '[' or ']' or '{' or '}' or ',' or '"' or '\'' or '#'
            || (line[i] == ':' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])));
        int start = Math.Min(column, line.Length);
        while (start > 0 && !Ends(line, start - 1))
        {
            start--;
        }
        int end = Math.Min(column, line.Length);
        while (end < line.Length && !Ends(line, end))
        {
            end++;
        }
        string word = line[start..end];
        return (word is "-" ? string.Empty : word, start);
    }

    /// <summary>The variable a ${VAR}, ${VAR:-default} or $VAR names around a column; null where there is none, as after
    /// <c>$$</c>, Compose's dollar sign written whole (<c>$$VAR</c> is the text $VAR, which Compose leaves to the container).</summary>
    private static string? VariableAt(string line, int column)
    {
        foreach (Match found in Interpolation().Matches(line))
        {
            if (column >= found.Index && column <= found.Index + found.Length)
            {
                int dollars = 0;
                while (found.Index - dollars - 1 >= 0 && line[found.Index - dollars - 1] == '$')
                {
                    dollars++;
                }
                if (dollars % 2 == 1)
                {
                    return null;
                }
                return found.Groups["braced"].Success ? found.Groups["braced"].Value : found.Groups["bare"].Value;
            }
        }
        return null;
    }

    /// <summary>A variable's line in the .env beside the compose file, which Compose interpolates from; one an env_file sets
    /// alone is said not to be set for Compose, since an env_file is the service's container's environment, never read
    /// for the file's own <c>${VAR}</c>s.</summary>
    private static ComposeAnswer Variable(string name, string folder, List<DocumentSymbol> chain, IReadOnlyList<DocumentSymbol> outline,
        ServerLines lines, Func<string, TextSnapshot?> texts)
    {
        string env = Path.Combine(folder, ".env");
        if (SetIn(env, name, texts) is { } set)
        {
            return new ComposeAnswer([new ComposePlace(PathNames.Canonical(env), set, $"{name} in .env")], null, $"The variable {name}");
        }
        IEnumerable<DocumentSymbol> services = outline.FirstOrDefault(s => s.Name == "services")?.Children ?? [];
        foreach (DocumentSymbol service in services.OrderBy(s => chain.Count > 1 && chain[1] == s ? 0 : 1))
        {
            if (service.Children?.FirstOrDefault(c => c.Name == "env_file") is not { } envFile)
            {
                continue;
            }
            foreach (string file in EnvFiles(envFile, lines))
            {
                if (SetIn(Path.GetFullPath(Path.Combine(folder, file)), name, texts) is not null)
                {
                    return ComposeAnswer.None($"{name} is not set for Compose: {file} sets it in the {service.Name} service's container, while Compose "
                        + "fills in the file's own variables from the .env beside it and the environment it runs in");
                }
            }
        }
        return ComposeAnswer.None($"{name} is not set in the .env beside the file: Compose takes it from the environment it runs in");
    }

    /// <summary>Where an env file sets a variable, or null when it does not (or is not there).</summary>
    private static TextPosition? SetIn(string file, string name, Func<string, TextSnapshot?> texts)
    {
        if (texts(file) is not { } env)
        {
            return null;
        }
        for (int i = 0; i < env.LineCount; i++)
        {
            if (EnvLine(name).Match(env.GetLineText(i)) is { Success: true } set)
            {
                return new TextPosition(i, set.Groups["name"].Index);
            }
        }
        return null;
    }

    /// <summary>The files an env_file names: one path, a list of paths, or a list of entries with a path each.</summary>
    private static IEnumerable<string> EnvFiles(DocumentSymbol envFile, ServerLines lines)
    {
        if (envFile.Children is not { Count: > 0 } items)
        {
            return envFile.Detail is { Length: > 0 } one ? [one] : [];
        }
        return items.Select(item => item.Children?.FirstOrDefault(c => c.Name == "path")?.Detail ?? Scalar(lines, item)).OfType<string>().Where(p => p.Length > 0);
    }

    /// <summary>The env_file a place is in: the one path, or the list's entry (a long entry's path); null on a list's key.</summary>
    private static string? EnvFile(List<DocumentSymbol> chain, ServerLines lines) =>
        chain.Count == 3 ? chain[2].Detail
        : chain[3].Children?.FirstOrDefault(c => c.Name == "path") is { Detail: { Length: > 0 } path } ? path
        : chain.Count == 4 && IsIndex(chain[3].Name) ? Scalar(lines, chain[3])
        : null;

    /// <summary>A list item's text, as written, its quotes taken off.</summary>
    private static string? Scalar(ServerLines lines, DocumentSymbol item)
    {
        int start = lines.OffsetOf(item.Range.Start);
        int end = lines.OffsetOf(item.Range.End);
        return end > start ? lines.Text.GetText(start, end - start).Trim().Trim('"', '\'') : null;
    }

    private static ComposeAnswer File(string relative, string folder)
    {
        string full = Path.GetFullPath(Path.Combine(folder, relative));
        return System.IO.File.Exists(full)
            ? new ComposeAnswer([new ComposePlace(PathNames.Canonical(full), new TextPosition(0, 0), Path.GetFileName(full))], null, relative)
            : ComposeAnswer.None($"{relative} is not there: there is no {full}");
    }

    /// <summary>A service of this file, by its name under services.</summary>
    private static ComposeAnswer Service(IReadOnlyList<DocumentSymbol> outline, ServerLines lines, string? name)
    {
        if (name is not { Length: > 0 })
        {
            return ComposeAnswer.None(Refusals.NoDeclaration);
        }
        return outline.FirstOrDefault(s => s.Name == "services")?.Children?.FirstOrDefault(s => s.Name == name) is { } service
            ? new ComposeAnswer([Here(lines, service, $"The service {name}")], null, $"The service {name}")
            : ComposeAnswer.None($"There is no service {name} in this file");
    }

    /// <summary>A volume, network, config or secret declared at the file's top.</summary>
    private static ComposeAnswer Declared(IReadOnlyList<DocumentSymbol> outline, ServerLines lines, string kind, string? name)
    {
        if (name is not { Length: > 0 })
        {
            return ComposeAnswer.None(Refusals.NoDeclaration);
        }
        string what = kind[..^1];
        return outline.FirstOrDefault(s => s.Name == kind)?.Children?.FirstOrDefault(s => s.Name == name) is { } declared
            ? new ComposeAnswer([Here(lines, declared, $"The {what} {name}")], null, $"The {what} {name}")
            : ComposeAnswer.None($"The {what} {name} is not declared under the file's {kind}");
    }

    /// <summary>The place of a key of the file itself, at its name.</summary>
    private static ComposePlace Here(ServerLines lines, DocumentSymbol symbol, string name) =>
        new(string.Empty, lines.Text.GetPosition(lines.OffsetOf(symbol.SelectionRange.Start)), name);

    /// <summary>The named volume a service's volume mounts: a short entry's source before its colon, or a long entry's
    /// source; null for a path on the host (./data, /srv, ~/x) or a variable.</summary>
    private static string? VolumeSource(bool longEntry, string value)
    {
        string source = longEntry ? value : value.Split(':')[0];
        return source.Length == 0 || source[0] is '.' or '/' or '~' or '$' || source.Contains('\\') || (source.Length > 1 && source[1] == ':') ? null : source;
    }

    /// <summary>The service an extends names, in this file or the file it names.</summary>
    private static ComposeAnswer Extends(List<DocumentSymbol> chain, IReadOnlyList<DocumentSymbol> outline, ServerLines lines, string folder,
        Func<string, TextSnapshot?> texts)
    {
        DocumentSymbol extends = chain[2];
        if (extends.Children is not { Count: > 0 } parts)
        {
            return Service(outline, lines, extends.Detail);
        }
        string? service = parts.FirstOrDefault(p => p.Name == "service")?.Detail;
        string? file = parts.FirstOrDefault(p => p.Name == "file")?.Detail;
        if (chain.Count > 3 && chain[3].Name == "file" && file is not null)
        {
            return File(file, folder);
        }
        if (file is null)
        {
            return Service(outline, lines, service);
        }
        string other = Path.GetFullPath(Path.Combine(folder, file));
        if (texts(other) is not { } read)
        {
            return ComposeAnswer.None($"{file} is not there: there is no {other}");
        }
        return ServiceIn(read, service) is { } position
            ? new ComposeAnswer([new ComposePlace(PathNames.Canonical(other), position, $"The service {service} in {file}")], null, $"The service {service}")
            : ComposeAnswer.None($"There is no service {service} in {file}");
    }

    /// <summary>A service's name in another compose file, read as the file is written (<see cref="ComposeText"/>): the first
    /// key under the top's services with that name.</summary>
    private static TextPosition? ServiceIn(TextSnapshot text, string? name) =>
        name is not null && ComposeText.Of(text.ToString()).At(["services", name]) is { } service ? text.GetPosition(service.KeyStart) : null;

    /// <summary>A service's build: its Dockerfile (the context's own, or the one it names), and the project the Dockerfile
    /// builds when it is one of the solution's.</summary>
    private static ComposeAnswer Build(List<DocumentSymbol> chain, string folder, Func<string, TextSnapshot?> texts, IReadOnlyList<ProjectModel> projects)
    {
        DocumentSymbol build = chain[2];
        string context = build.Children is { Count: > 0 } parts ? parts.FirstOrDefault(p => p.Name == "context")?.Detail ?? "." : build.Detail ?? ".";
        if (build.Children?.Any(p => p.Name == "dockerfile_inline") == true)
        {
            return ComposeAnswer.None("The build's Dockerfile is written inline, in this file");
        }
        if (context.Contains("://", StringComparison.Ordinal) || context.StartsWith("git@", StringComparison.Ordinal))
        {
            return ComposeAnswer.None($"The build's context is a repository ({context}), not a folder here");
        }
        string named = build.Children?.FirstOrDefault(p => p.Name == "dockerfile")?.Detail ?? "Dockerfile";
        string root = Path.GetFullPath(Path.Combine(folder, context));
        string dockerfile = Path.GetFullPath(Path.Combine(root, named));
        if (texts(dockerfile) is not { } read)
        {
            return ComposeAnswer.None($"The build's Dockerfile is not there: there is no {dockerfile}");
        }
        List<ComposePlace> found = [new ComposePlace(PathNames.Canonical(dockerfile), new TextPosition(0, 0), Path.GetFileName(dockerfile))];
        if (Built(read, root, projects) is { } project)
        {
            found.Add(new ComposePlace(project, new TextPosition(0, 0), Path.GetFileName(project)));
        }
        return new ComposeAnswer(found, null, "The build's Dockerfile");
    }

    /// <summary>The project a Dockerfile builds: the one whose assembly the final stage's ENTRYPOINT runs with dotnet (its
    /// own, else the one of the stage it is built FROM; never a comment's nor an earlier stage's alone), else the project
    /// file its dotnet publish (or build) names, from the build's context.</summary>
    public static string? Built(TextSnapshot dockerfile, string context, IReadOnlyList<ProjectModel> projects)
    {
        string text = dockerfile.ToString();
        DockerfileStages stages = DockerfileStages.Read(text);
        if (stages.Final is { } final && stages.Lineage(final).Select(s => s.EntryAssembly).FirstOrDefault(a => a is not null) is { } assembly
            && projects.FirstOrDefault(p => string.Equals(p.Evaluated?.AssemblyName ?? p.Name, assembly, StringComparison.OrdinalIgnoreCase)) is { } running)
        {
            return running.Path;
        }
        foreach (Match command in Publish().Matches(text))
        {
            string candidate = Path.GetFullPath(Path.Combine(context, command.Groups["project"].Value.Replace('\\', '/')));
            if (System.IO.File.Exists(candidate))
            {
                return PathNames.Canonical(candidate);
            }
            // Its path in the image's WORKDIR, which is the context's folders copied in: the project of that name.
            string name = Path.GetFileName(candidate);
            if (projects.FirstOrDefault(p => Path.GetFileName(p.Path).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } project)
            {
                return project.Path;
            }
        }
        return null;
    }

    /// <summary>An image's page on its registry: Docker Hub's for a name with no registry, else the registry's
    /// own.</summary>
    private static ComposeAnswer Image(string image)
    {
        if (RegistryPage(image) is not { } page)
        {
            return ComposeAnswer.None($"{image} names its registry through a variable, so it has no page to go to");
        }
        return new ComposeAnswer([], page, $"The image {image}");
    }

    public static Uri? RegistryPage(string image)
    {
        if (image.Contains('$', StringComparison.Ordinal) || image.Length == 0)
        {
            return null;
        }
        string name = image.Split('@')[0];
        int slash = name.IndexOf('/', StringComparison.Ordinal);
        string? registry = slash > 0 && (name[..slash].Contains('.') || name[..slash].Contains(':') || name[..slash] == "localhost") ? name[..slash] : null;
        string path = registry is null ? name : name[(slash + 1)..];
        int colon = path.LastIndexOf(':');
        if (colon > path.LastIndexOf('/'))
        {
            path = path[..colon];
        }
        return registry switch
        {
            null or "docker.io" or "index.docker.io" or "registry-1.docker.io" => new Uri(path.StartsWith("library/", StringComparison.Ordinal)
                ? $"https://hub.docker.com/_/{path["library/".Length..]}" : path.Contains('/') ? $"https://hub.docker.com/r/{path}" : $"https://hub.docker.com/_/{path}"),
            "mcr.microsoft.com" => new Uri($"https://mcr.microsoft.com/artifact/mar/{path}"),
            "ghcr.io" when path.Contains('/') => new Uri($"https://github.com/{path}/pkgs/container/{path[(path.LastIndexOf('/') + 1)..]}"),
            "quay.io" => new Uri($"https://quay.io/repository/{path}"),
            _ => Uri.TryCreate($"https://{registry}/", UriKind.Absolute, out Uri? host) ? host : null,
        };
    }

    [GeneratedRegex(@"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)[^}]*\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Interpolation();

    private static Regex EnvLine(string name) => new($@"^\s*(?:export\s+)?(?<name>{Regex.Escape(name)})\s*[=:]");

    /// <summary>A dotnet publish or build of a project on a line that is no comment.</summary>
    [GeneratedRegex(@"^(?![ \t]*#)[^\n]*?dotnet\s+(?:publish|build)\s+""?(?<project>[^""\s]+\.(?:cs|fs|vb)proj)""?", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Publish();
}
