using System.Text.RegularExpressions;

namespace Passenger.App.Containers;

/// <summary>A stage of a Dockerfile: its name (or its index), the image or stage it is FROM, the ARGs it declares, its last
/// WORKDIR, the context's folders its COPY lines put in the image (each with where it went), its EXPOSEd ports, the
/// assembly its ENTRYPOINT runs with dotnet (its last ENTRYPOINT's) and the variables its ENV lines set.</summary>
public sealed record DockerfileStage(string Name, string From, IReadOnlyList<string> Arguments, string? WorkDir, IReadOnlyList<(string Source, string Target)> Copies,
    IReadOnlyList<int> Exposed, string? EntryAssembly)
{
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>A Dockerfile's stages, read as Docker reads its instructions (line continuations joined, comments and parser
/// directives passed over), enough to choose fast mode's runtime stage, map the container's paths back to the context's
/// files (<see cref="ContainerPaths"/>), and know which ARGs a build may be given. Text alone; never the engine.</summary>
public sealed partial record DockerfileStages(IReadOnlyList<DockerfileStage> Stages, IReadOnlyList<string> GlobalArguments)
{
    public DockerfileStage? Final => Stages.Count > 0 ? Stages[^1] : null;

    /// <summary>Whether any stage (or the file's top, before the first FROM) declares the ARG.</summary>
    public bool Declares(string argument) =>
        GlobalArguments.Contains(argument, StringComparer.Ordinal) || Stages.Any(s => s.Arguments.Contains(argument, StringComparer.Ordinal));

    /// <summary>The stage fast mode runs a .NET service in: the one named as the project's <c>DockerfileFastModeStage</c>
    /// says, else one named <c>base</c>, else the stage the final stage is FROM; null when the final stage builds FROM an
    /// image of its own (no stage to stop at).</summary>
    public DockerfileStage? FastStage(string? named)
    {
        if (named is { Length: > 0 } && Stages.FirstOrDefault(s => string.Equals(s.Name, named, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            return chosen;
        }
        if (Stages.FirstOrDefault(s => string.Equals(s.Name, "base", StringComparison.OrdinalIgnoreCase)) is { } basis)
        {
            return basis;
        }
        return Final is { } last ? Stages.FirstOrDefault(s => string.Equals(s.Name, last.From, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>The stages a stage is built on through FROM, itself first: where its WORKDIR and copies come from.</summary>
    public IEnumerable<DockerfileStage> Lineage(DockerfileStage stage)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (DockerfileStage? at = stage; at is not null && seen.Add(at.Name);
            at = Stages.FirstOrDefault(s => string.Equals(s.Name, at.From, StringComparison.OrdinalIgnoreCase)))
        {
            yield return at;
        }
    }

    /// <summary>Reads a Dockerfile's text.</summary>
    public static DockerfileStages Read(string text)
    {
        List<DockerfileStage> stages = [];
        List<string> global = [];
        string? name = null;
        string from = string.Empty;
        List<string> arguments = [];
        string? workDir = null;
        List<(string, string)> copies = [];
        List<int> exposed = [];
        string? entry = null;
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        foreach (string instruction in Instructions(text))
        {
            Match parts = Instruction().Match(instruction);
            if (!parts.Success)
            {
                continue;
            }
            string verb = parts.Groups["verb"].Value.ToUpperInvariant();
            string rest = parts.Groups["rest"].Value.Trim();
            switch (verb)
            {
                case "FROM":
                    Close();
                    string[] words = [.. Words(rest).Where(w => !w.StartsWith("--", StringComparison.Ordinal))];
                    from = words.Length > 0 ? words[0] : string.Empty;
                    name = words.Length >= 3 && words[1].Equals("AS", StringComparison.OrdinalIgnoreCase) ? words[2] : null;
                    arguments = [];
                    copies = [];
                    exposed = [];
                    entry = null;
                    environment = new(StringComparer.Ordinal);
                    // A stage built on another starts where that one's work folder is.
                    workDir = stages.FirstOrDefault(s => string.Equals(s.Name, from, StringComparison.OrdinalIgnoreCase))?.WorkDir;
                    break;
                case "ARG":
                    string argument = rest.Split('=', 2)[0].Trim();
                    (name is null && stages.Count == 0 && from.Length == 0 ? global : arguments).Add(argument);
                    break;
                case "WORKDIR":
                    string folder = Unquoted(rest);
                    workDir = folder.StartsWith('/') || workDir is null ? folder : $"{workDir.TrimEnd('/')}/{folder}";
                    break;
                case "COPY" or "ADD":
                    List<string> copied = Copied(rest);
                    if (copied.Count >= 2 && !rest.Contains("--from", StringComparison.OrdinalIgnoreCase))
                    {
                        string target = copied[^1];
                        string into = target.StartsWith('/') ? target : $"{(workDir ?? "/").TrimEnd('/')}/{(target is "." or "./" ? string.Empty : target)}";
                        foreach (string source in copied.Take(copied.Count - 1))
                        {
                            copies.Add((source, into));
                        }
                    }
                    break;
                case "EXPOSE":
                    foreach (string port in Words(rest))
                    {
                        if (int.TryParse(port.Split('/')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
                        {
                            exposed.Add(number);
                        }
                    }
                    break;
                case "ENTRYPOINT":
                    // The last one is the one that runs; one of another form names no assembly.
                    entry = EntryPoint().Match(instruction) is { Success: true } dll ? dll.Groups["assembly"].Value : null;
                    break;
                case "ENV":
                    foreach ((string key, string value) in Variables(rest))
                    {
                        environment[key] = value;
                    }
                    break;
            }
        }
        Close();
        return new DockerfileStages(stages, global);

        void Close()
        {
            if (from.Length > 0)
            {
                stages.Add(new DockerfileStage(name ?? stages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), from, arguments, workDir, copies, exposed, entry)
                {
                    Environment = environment,
                });
            }
        }
    }

    /// <summary>A Dockerfile's text with each variable its ARG and ENV lines set ($NAME, ${NAME}, ${NAME:-default}) put in
    /// as its build would have it: the build's arguments given first, else an ARG's own default, else what an ENV set it
    /// to; one none of them sets is left as written, but for a default it carries. For finding what a build names (the
    /// project it publishes, the assembly it runs), never for running it.</summary>
    public static string Expanded(string text, IReadOnlyDictionary<string, string> given)
    {
        Dictionary<string, string> values = new(given, StringComparer.Ordinal);
        foreach (string instruction in Instructions(text))
        {
            Match parts = Instruction().Match(instruction);
            string verb = parts.Success ? parts.Groups["verb"].Value.ToUpperInvariant() : string.Empty;
            string rest = parts.Groups["rest"].Value.Trim();
            if (verb == "ARG" && rest.Split('=', 2) is [{ } name, { } fallback] && !given.ContainsKey(name.Trim()))
            {
                values[name.Trim()] = Put(Unquoted(fallback), values);
            }
            else if (verb == "ENV")
            {
                foreach ((string key, string value) in Variables(rest))
                {
                    values[key] = Put(value, values);
                }
            }
        }
        return Put(text, values);

        static string Put(string text, Dictionary<string, string> values) => Variable().Replace(text, found =>
        {
            string name = found.Groups["braced"].Success ? found.Groups["braced"].Value : found.Groups["bare"].Value;
            return values.TryGetValue(name, out string? value) ? value : found.Groups["default"].Success ? found.Groups["default"].Value : found.Value;
        });
    }

    /// <summary>A file's instructions: each line with its continuations ('\' at a line's end) joined, comments left out.</summary>
    private static IEnumerable<string> Instructions(string text)
    {
        System.Text.StringBuilder joined = new();
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }
            if (line.EndsWith('\\'))
            {
                joined.Append(line, 0, line.Length - 1).Append(' ');
                continue;
            }
            joined.Append(line);
            if (joined.ToString().Trim() is { Length: > 0 } whole)
            {
                yield return whole;
            }
            joined.Clear();
        }
        if (joined.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }

    /// <summary>A COPY's sources and its destination, from its JSON form or its words, its flags left out.</summary>
    private static List<string> Copied(string rest)
    {
        string trimmed = rest;
        while (trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            trimmed = space < 0 ? string.Empty : trimmed[(space + 1)..].TrimStart();
        }
        if (trimmed.StartsWith('['))
        {
            return [.. JsonWords().Matches(trimmed).Select(m => m.Groups[1].Value)];
        }
        return [.. Words(trimmed)];
    }

    /// <summary>An ENV line's variables: <c>KEY=value</c> pairs (a value quoted to hold spaces), or the older <c>KEY value</c>.</summary>
    private static IEnumerable<(string Key, string Value)> Variables(string rest)
    {
        if (!Pair().IsMatch(rest))
        {
            string[] split = rest.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            return split.Length == 2 ? [(split[0], Unquoted(split[1]))] : [];
        }
        return Pairs().Matches(rest).Select(pair => (pair.Groups["key"].Value,
            pair.Groups["value"].Value is { Length: > 1 } quoted && quoted[0] is '"' or '\'' ? quoted[1..^1] : pair.Groups["value"].Value));
    }

    private static IEnumerable<string> Words(string text) => text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Unquoted);

    private static string Unquoted(string word) => word.Trim().Trim('"');

    [GeneratedRegex(@"^\s*(?<verb>[A-Za-z]+)\s+(?<rest>.*)$")]
    private static partial Regex Instruction();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*=")]
    private static partial Regex Pair();

    [GeneratedRegex(@"(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)=(?<value>""(?:[^""\\]|\\.)*""|'[^']*'|\S*)")]
    private static partial Regex Pairs();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex JsonWords();

    /// <summary>An ENTRYPOINT that runs an assembly with dotnet: <c>["dotnet", "X.dll"]</c>, a shell's
    /// <c>["sh", "-c", "dotnet X.dll"]</c>, or the shell form <c>dotnet X.dll</c>, an <c>exec</c> before dotnet or not.</summary>
    [GeneratedRegex(@"ENTRYPOINT\s*(?:\[\s*""dotnet""\s*,\s*""(?:[^""]*/)?(?<assembly>[^""/]+)\.dll""|\[\s*""(?:/bin/)?(?:ba)?sh""\s*,\s*""-c""\s*,\s*""(?:exec\s+)?dotnet\s+(?:\\"")?(?:[^""\s\\]*/)?(?<assembly>[^""\s/\\]+)\.dll|(?!\[)(?:exec\s+)?dotnet\s+""?(?:[^""\s]*/)?(?<assembly>[^""\s/]+)\.dll)", RegexOptions.IgnoreCase)]
    private static partial Regex EntryPoint();

    [GeneratedRegex(@"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)(?::?-(?<default>[^}]*))?\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Variable();
}
