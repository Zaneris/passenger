using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>What a part of a run of several parts runs: a .NET project, a Node package's script by its own package manager,
/// that package's dependencies installed by it, or a command.</summary>
public enum RunPartKind
{
    Project,
    Script,
    Install,
    Command,
}

/// <summary>How a part of a run is known to be ready, which every part after it waits for: once it has started, once it has
/// ended with exit code 0 (a step), once a line of its output matches a pattern, or once something listens at an address
/// (for a .NET part naming none, once its host says it has started).</summary>
public enum ReadyWhen
{
    Started,
    Exited,
    Line,
    Listening,
}

/// <summary>One part of a run of several parts, in the order they start: what it runs (<see cref="Kind"/>), what Run and
/// Debug do with it (<see cref="Action"/>: a .NET project Start debugs under Debug, any other part runs either way; None
/// leaves it), and for a .NET project the launch profile of its own it starts in, empty for its default. Paths are
/// canonical: a project's file, a script's or install's package.json, a command's folder.</summary>
public sealed record RunPart(string Project, ProjectLaunchAction? Action, string LaunchProfile)
{
    public RunPartKind Kind { get; init; }

    /// <summary>A script part's script of its package.json.</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>A command part's command line: its program, found as a terminal finds a command, and its arguments.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Where a command part runs (canonical); empty for the open folder.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>A script part's arguments after its script, as one command line.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>Variables its program is given, over its own configuration's for a .NET project.</summary>
    public Dictionary<string, string> Environment { get; init; } = [];

    public ReadyWhen Ready { get; init; }

    /// <summary>What <see cref="Ready"/> waits for: a line's pattern, or the address listened at (empty for a .NET part's host's own).</summary>
    public string ReadyText { get; init; } = string.Empty;

    /// <summary>A page opened in the browser once something answers there; empty for none beyond a .NET project's own.</summary>
    public string Browser { get; init; } = string.Empty;

    /// <summary>Its own name, which its console's tab is called; empty for its kind's (<see cref="RunsFile.NameOf"/>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>It as Visual Studio's file held it when read, which a change is written over; null for one not read from one.</summary>
    [JsonIgnore]
    public SolutionLaunchProject? Launch { get; init; }

    /// <summary>It as passenger-runs.json held it when read, whose members Passenger does not model a change keeps.</summary>
    [JsonIgnore]
    public JsonObject? Read { get; init; }

    /// <summary>Why it cannot run as its file says it (a kind or readiness this Passenger does not know); null when it can.</summary>
    [JsonIgnore]
    public string? Unread { get; init; }

    /// <summary>Whether Visual Studio's multi-project launch profile can hold it: a .NET project with nothing of its own but
    /// its action and launch profile.</summary>
    [JsonIgnore]
    public bool FitsSolutionLaunch => Kind == RunPartKind.Project && Environment.Count == 0 && Ready == ReadyWhen.Started && Browser.Length == 0
        && Name.Length == 0 && Unread is null;
}

/// <summary>A run of several parts as a folder's passenger-runs.json holds it: its name and its parts in order, with its place
/// in the file (-1 for one made here) and what the file held of it, whose members Passenger does not model are kept.</summary>
public sealed record RunsFileRun(string Name, IReadOnlyList<RunPart> Parts)
{
    public JsonObject? Read { get; init; }

    public int Index { get; init; } = -1;
}

/// <summary>A folder's <c>passenger-runs.json</c> at its top (docs/runs.md): the runs of several parts its team shares that
/// Visual Studio's <c>.slnLaunch</c> cannot hold, each a <c>name</c> and its <c>parts</c> in the order they start, a part's
/// kind by the member naming what it runs (<c>project</c>, <c>script</c> with its <c>package</c>, <c>install</c>,
/// <c>command</c>), its paths from the folder in forward slashes. Read as JSON with comments, and written back as text where
/// each run stands (<see cref="JsonText.ItemsInPlace"/>), so a run unchanged keeps its every character.</summary>
public static class RunsFile
{
    public const string FileName = "passenger-runs.json";

    /// <summary>The format a Passenger writes and reads; a later one is refused, saying so.</summary>
    public const int Format = 1;

    private const string FormatKey = "passengerRuns";
    private const string RunsKey = "runs";
    private const string PartsKey = "parts";

    /// <summary>The file of a folder's runs.</summary>
    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>Whether a path is a folder's file of runs, by its name.</summary>
    public static bool IsFile(ReadOnlySpan<char> path) => Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A folder's runs as its file holds them, with why where it cannot be read; none for no file. On any thread.</summary>
    public static (IReadOnlyList<RunsFileRun> Runs, string? Problem) Read(string folder)
    {
        string file = PathIn(folder);
        string text;
        try
        {
            if (!File.Exists(file))
            {
                return ([], null);
            }
            text = FileText.Load(file)?.Text ?? throw new IOException("it is not a text file");
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return ([], $"{FileName} could not be read: {unread.Message}");
        }
        return Parse(text, folder);
    }

    /// <summary>The runs a file's text holds, their paths read from the <paramref name="folder"/>; none with why for a text
    /// that is no object of runs, or of a later format. A run without a name, or an item that is no object, is passed over;
    /// a part this Passenger cannot read keeps its run, which says why it cannot start.</summary>
    public static (IReadOnlyList<RunsFileRun> Runs, string? Problem) Parse(string text, string folder)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(text.TrimStart('\uFEFF'), LenientJson.Document);
            JsonElement root = read.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ([], $"{FileName} does not hold an object of runs");
            }
            if (JsonMembers.Number(root, FormatKey) is > Format)
            {
                return ([], $"{FileName} is of a later format than this Passenger reads");
            }
            List<RunsFileRun> runs = [];
            IReadOnlyList<JsonElement> items = JsonMembers.Items(root, RunsKey);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].ValueKind != JsonValueKind.Object || JsonMembers.Text(items[i], "name") is not { Length: > 0 } name)
                {
                    continue;
                }
                List<RunPart> parts = [.. JsonMembers.Items(items[i], PartsKey).Where(p => p.ValueKind == JsonValueKind.Object).Select(p => PartOf(p, folder))];
                runs.Add(new RunsFileRun(name, parts) { Read = JsonObject.Create(items[i].Clone()), Index = i });
            }
            return (runs, null);
        }
        catch (JsonException unreadable)
        {
            return ([], $"{FileName} does not read as JSON: {unreadable.Message}");
        }
    }

    /// <summary>A part as its file writes it, its kind by the member naming what it runs.</summary>
    private static RunPart PartOf(JsonElement part, string folder)
    {
        string? action = JsonMembers.Text(part, "action");
        ProjectLaunchAction? known = action is null ? ProjectLaunchAction.Start : ActionOf(action);
        (ReadyWhen ready, string readyText, string? unready) = ReadyOf(JsonMembers.Value(part, "readyWhen"), JsonMembers.Text(part, "install") is { Length: > 0 });
        RunPart read = new(string.Empty, known, JsonMembers.Text(part, "launchProfile") ?? string.Empty)
        {
            Name = JsonMembers.Text(part, "name") ?? string.Empty,
            Arguments = JsonMembers.Text(part, "arguments") ?? string.Empty,
            Environment = VariablesOf(JsonMembers.Object(part, "environment")),
            Ready = ready,
            ReadyText = readyText,
            Browser = JsonMembers.Text(part, "browser") ?? string.Empty,
            Read = JsonObject.Create(part.Clone()),
        };
        string? unread = known is null ? $"its action {action} is not one Passenger knows" : unready;
        if (JsonMembers.Text(part, "project") is { Length: > 0 } project)
        {
            return read with { Kind = RunPartKind.Project, Project = Named(project, folder), Unread = unread };
        }
        if (JsonMembers.Text(part, "script") is { Length: > 0 } script)
        {
            return JsonMembers.Text(part, "package") is { Length: > 0 } package
                ? read with { Kind = RunPartKind.Script, Project = Named(package, folder), Script = script, Unread = unread }
                : read with { Kind = RunPartKind.Script, Script = script, Unread = $"its script {script} names no package" };
        }
        if (JsonMembers.Text(part, "install") is { Length: > 0 } install)
        {
            return read with { Kind = RunPartKind.Install, Project = Named(install, folder), Unread = unread };
        }
        if (JsonMembers.Text(part, "command") is { Length: > 0 } command)
        {
            return read with
            {
                Kind = RunPartKind.Command,
                Command = command,
                Folder = JsonMembers.Text(part, "folder") is { Length: > 0 } at ? Named(at, folder) : string.Empty,
                Unread = unread,
            };
        }
        return read with { Kind = RunPartKind.Command, Unread = "it names no project, script, install or command this Passenger runs" };
    }

    /// <summary>An object of variables, each a string, number or truth as written; the last of a name, as a reader takes it.</summary>
    private static Dictionary<string, string> VariablesOf(JsonElement? variables)
    {
        Dictionary<string, string> read = new(StringComparer.Ordinal);
        if (variables is not { } given)
        {
            return read;
        }
        foreach (JsonProperty variable in given.EnumerateObject())
        {
            if (variable.Name.Length > 0 && JsonMembers.Scalar(given, variable.Name) is { } value)
            {
                read[variable.Name] = value;
            }
        }
        return read;
    }

    /// <summary>A part's readiness as written: a word, or an object of the one thing it waits for; for none, the default (an
    /// install's once it has ended, being a step), and why for one this Passenger does not know.</summary>
    private static (ReadyWhen Ready, string Text, string? Problem) ReadyOf(JsonElement? written, bool install)
    {
        if (written is not { } given || given.ValueKind == JsonValueKind.Null)
        {
            return (install ? ReadyWhen.Exited : ReadyWhen.Started, string.Empty, null);
        }
        if (given.ValueKind == JsonValueKind.String)
        {
            return given.GetString() switch
            {
                "started" => (ReadyWhen.Started, string.Empty, null),
                "exited" => (ReadyWhen.Exited, string.Empty, null),
                "listening" => (ReadyWhen.Listening, string.Empty, null),
                string said => (ReadyWhen.Started, string.Empty, $"its readyWhen {said} is not one Passenger knows"),
                null => (ReadyWhen.Started, string.Empty, "its readyWhen is not one Passenger knows"),
            };
        }
        return JsonMembers.Text(given, "line") is { Length: > 0 } line ? (ReadyWhen.Line, line, null)
            : JsonMembers.Text(given, "listening") is { } address ? (ReadyWhen.Listening, address, null)
            : (ReadyWhen.Started, string.Empty, "its readyWhen is not one Passenger knows");
    }

    /// <summary>An action as written, by the file's words; null for one Passenger does not know.</summary>
    private static ProjectLaunchAction? ActionOf(string written) => written switch
    {
        "start" => ProjectLaunchAction.Start,
        "startWithoutDebugging" => ProjectLaunchAction.StartWithoutDebugging,
        "none" => ProjectLaunchAction.None,
        _ => null,
    };

    private static string ActionWord(ProjectLaunchAction action) => action switch
    {
        ProjectLaunchAction.StartWithoutDebugging => "startWithoutDebugging",
        ProjectLaunchAction.None => "none",
        _ => "start",
    };

    /// <summary>The name a part goes by where it names none of its own: a .NET project's file name and launch profile, a
    /// script's package folder and script, an install's package folder, a command's program.</summary>
    public static string NameOf(RunPart part) => part.Name.Length > 0 ? part.Name : part.Kind switch
    {
        RunPartKind.Project => part.LaunchProfile.Length > 0 ? $"{Path.GetFileNameWithoutExtension(part.Project)}: {part.LaunchProfile}" : Path.GetFileNameWithoutExtension(part.Project),
        RunPartKind.Script => $"{PackageName(part.Project)}: {part.Script}",
        RunPartKind.Install => $"{PackageName(part.Project)}: install",
        _ => ProgramOf(part.Command) is { Length: > 0 } program ? Path.GetFileNameWithoutExtension(program) : "command",
    };

    /// <summary>A command line's program: its first word, or what its opening quotes hold.</summary>
    private static string ProgramOf(string command)
    {
        ReadOnlySpan<char> line = command.AsSpan().Trim();
        if (line.StartsWith('"'))
        {
            int close = line[1..].IndexOf('"');
            return (close < 0 ? line[1..] : line[1..(close + 1)]).ToString();
        }
        int space = line.IndexOfAny(' ', '\t');
        return (space < 0 ? line : line[..space]).ToString();
    }

    /// <summary>A package by its folder's name, its package.json's path being the part's.</summary>
    private static string PackageName(string manifest) => Path.GetFileName(Path.GetDirectoryName(manifest)) is { Length: > 0 } name ? name : "package";

    /// <summary>The text of a folder's file holding these runs in this order (<paramref name="text"/>, null for none yet), paths
    /// from the <paramref name="folder"/>: a run read unchanged kept, one changed written in its place, one gone taken out,
    /// one new put last; written anew, as the format's first file, where there is none. Null with why for a text that is no
    /// object of runs, left for the person to mend.</summary>
    public static (string? Text, string? Problem) Written(string? text, IReadOnlyList<RunsFileRun> runs, string folder)
    {
        List<JsonObject> values = [.. runs.Select(r => ValueOf(r, folder))];
        if (text is null || text.Trim('﻿').Trim().Length == 0)
        {
            JsonObject made = new() { [FormatKey] = Format, [RunsKey] = new JsonArray([.. values]) };
            return (JsonText.Laid(made) + "\n", null);
        }
        (string? written, string? problem) = JsonText.ItemsInPlace(text, [RunsKey], [.. runs.Select((r, i) => (r.Read is not null ? r.Index : -1, values[i]))], IsRun, Holds);
        return problem is null ? (written, null) : (null, $"{FileName} {problem}");
    }

    /// <summary>A folder's file of runs following files and folders moved or renamed, as a save writes it: each path a part
    /// names (its project, package, install or folder) that moved named where it is now (<see cref="Moved"/>). Whether it
    /// changed; a file that does not read as runs is left as it is.</summary>
    public static bool Move(string file, IReadOnlyList<(string From, string To)> moves)
    {
        FileText read = FileText.Load(file) ?? throw new IOException($"{FileName} is not a text file");
        if (Moved(read.Text, Path.GetDirectoryName(file)!, moves) is not { } text || text == read.Text)
        {
            return false;
        }
        FileText.SaveOver(file, read, text);
        return true;
    }

    /// <summary>The text with each path its parts name that names a file or folder moved written where it is now, from the
    /// <paramref name="folder"/>, every other character as it was. Null for a text that does not read as an object of runs.</summary>
    public static string? Moved(string text, string folder, IReadOnlyList<(string From, string To)> moves)
    {
        string Now(string path) => moves.FirstOrDefault(m => PathNames.Within(path, m.From)) is { From: not null } move ? PathNames.Moved(path, move.From, move.To) : path;
        JsonText.Node root;
        try
        {
            root = new JsonText.Reader(text).Document();
        }
        catch (FormatException)
        {
            return null;
        }
        if (root.Kind != JsonText.Kind.Object)
        {
            return null;
        }
        List<(int Start, int End, string Written)> changes = [];
        foreach (JsonText.Node run in root.Member(RunsKey)?.Value is { Kind: JsonText.Kind.Array } runs ? runs.Items : [])
        {
            foreach (JsonText.Node part in run.Member(PartsKey)?.Value is { Kind: JsonText.Kind.Array } parts ? parts.Items : [])
            {
                foreach (string member in (string[])["project", "package", "install", "folder"])
                {
                    if (part.Member(member)?.Value is not { } value || ItemOf(text[value.Start..value.End]) is not JsonValue literal
                        || !literal.TryGetValue(out string? written) || written.Length == 0)
                    {
                        continue;
                    }
                    string was = Named(written, folder);
                    string now = Now(was);
                    if (!PathNames.Same(now, was))
                    {
                        changes.Add((value.Start, value.End, JsonSerializer.Serialize(Written(now, folder), JsonText.Written)));
                    }
                }
            }
        }
        foreach ((int start, int end, string value) in changes.OrderByDescending(c => c.Start))
        {
            text = string.Concat(text.AsSpan(0, start), value, text.AsSpan(end));
        }
        return text;
    }

    /// <summary>A run as its file is to hold it: what it was read as, with its name and parts as they are now.</summary>
    private static JsonObject ValueOf(RunsFileRun run, string folder)
    {
        JsonObject value = run.Read is { } read ? (JsonObject)read.DeepClone() : [];
        value["name"] = run.Name;
        value[PartsKey] = new JsonArray([.. run.Parts.Select(p => (JsonNode)PartValue(p, folder))]);
        return value;
    }

    /// <summary>A part's entry: what it was read as, each member Passenger models set as it is now (one at its default left
    /// out), the rest kept; one this Passenger could not read kept as written.</summary>
    private static JsonObject PartValue(RunPart part, string folder)
    {
        if (part is { Unread: not null, Read: { } kept })
        {
            return (JsonObject)kept.DeepClone();
        }
        JsonObject value = part.Read is { } read ? (JsonObject)read.DeepClone() : [];
        // A member that reads as the part holds it now stays as written, its default spelled out included.
        RunPart? was = part.Read is { } held ? PartOf(JsonSerializer.SerializeToElement(held), folder) : null;
        JsonObject what = part.Kind switch
        {
            RunPartKind.Project => new() { ["project"] = Written(part.Project, folder) },
            RunPartKind.Script => new() { ["script"] = part.Script, ["package"] = Written(part.Project, folder) },
            RunPartKind.Install => new() { ["install"] = Written(part.Project, folder) },
            _ => part.Folder.Length > 0 ? new() { ["command"] = part.Command, ["folder"] = Written(part.Folder, folder) } : new() { ["command"] = part.Command },
        };
        if (KindMembers.All(k => what.ContainsKey(k) == value.ContainsKey(k)))
        {
            // The same kind as read: each set where it stands.
            foreach ((string name, JsonNode? member) in what)
            {
                bool same = was is not null && name switch
                {
                    "script" => was.Script == part.Script,
                    "command" => was.Command == part.Command,
                    "folder" => PathNames.Same(was.Folder, part.Folder),
                    _ => PathNames.Same(was.Project, part.Project),
                };
                if (!same)
                {
                    value[name] = member?.DeepClone();
                }
            }
        }
        else
        {
            // What it runs first, as a person reads a part, then the rest as they stood.
            JsonObject ordered = [];
            foreach ((string name, JsonNode? member) in what)
            {
                ordered[name] = member?.DeepClone();
            }
            foreach ((string name, JsonNode? member) in value.Where(m => !KindMembers.Contains(m.Key)).ToList())
            {
                value.Remove(name);
                ordered[name] = member;
            }
            value = ordered;
        }
        void Keep(string name, bool same, JsonNode? now)
        {
            if (!(same && value.ContainsKey(name)))
            {
                Set(value, name, now);
            }
        }
        Keep("name", was?.Name == part.Name, part.Name.Length > 0 ? JsonValue.Create(part.Name) : null);
        Keep("action", was?.Action == part.Action, part.Action is { } action and not ProjectLaunchAction.Start ? JsonValue.Create(ActionWord(action)) : null);
        Keep("launchProfile", was?.LaunchProfile == part.LaunchProfile,
            part.Kind == RunPartKind.Project && part.LaunchProfile.Length > 0 ? JsonValue.Create(part.LaunchProfile) : null);
        Keep("arguments", was?.Arguments == part.Arguments, part.Kind == RunPartKind.Script && part.Arguments.Length > 0 ? JsonValue.Create(part.Arguments) : null);
        Keep("environment", was is not null && was.Environment.Count == part.Environment.Count && !was.Environment.Except(part.Environment).Any(),
            part.Environment.Count == 0 ? null : new JsonObject(part.Environment.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value))));
        Keep("readyWhen", was is not null && was.Ready == part.Ready && was.ReadyText == part.ReadyText, (part.Ready, part.Kind) switch
        {
            (ReadyWhen.Exited, RunPartKind.Install) or (ReadyWhen.Started, not RunPartKind.Install) => null,
            (ReadyWhen.Started, _) => JsonValue.Create("started"),
            (ReadyWhen.Exited, _) => JsonValue.Create("exited"),
            (ReadyWhen.Line, _) => new JsonObject { ["line"] = part.ReadyText },
            _ => part.ReadyText.Length > 0 ? new JsonObject { ["listening"] = part.ReadyText } : JsonValue.Create("listening"),
        });
        Keep("browser", was?.Browser == part.Browser, part.Browser.Length > 0 ? JsonValue.Create(part.Browser) : null);
        return value;
    }

    /// <summary>The members naming what a part runs, one kind's or another's.</summary>
    private static readonly string[] KindMembers = ["project", "script", "package", "install", "command", "folder"];

    /// <summary>A path as the file writes it: from its folder in forward slashes.</summary>
    private static string Written(string path, string folder) => PathNames.Route(path, folder);

    private static string Named(string written, string folder) => SolutionFilter.Named(written, folder);

    private static void Set(JsonObject value, string name, JsonNode? now)
    {
        if (now is null)
        {
            value.Remove(name);
        }
        else
        {
            value[name] = now;
        }
    }

    private static void Set(JsonObject value, string name, string? now) => Set(value, name, now is null ? null : JsonValue.Create(now));

    /// <summary>Whether an item is one the file's reader takes for a run: an object with a name.</summary>
    private static bool IsRun(string itemText) => ItemOf(itemText) is JsonObject item && item["name"] is JsonValue name && name.TryGetValue(out string? named) && named.Length > 0;

    private static bool Holds(string itemText, JsonObject value) => ItemOf(itemText) is { } read && JsonNode.DeepEquals(read, value);

    private static JsonNode? ItemOf(string itemText)
    {
        try
        {
            return JsonNode.Parse(itemText, documentOptions: LenientJson.Document);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
