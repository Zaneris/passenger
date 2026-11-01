using Passenger.Roslyn.Extension;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>A Godot project's C# file read against its scenes: the project's folder and scenes as they stood, what the
/// extension was asked of the code, and what it answered.</summary>
internal sealed record GodotRead(string Folder, GodotScenes Scenes, GodotCodeRequest Asked, GodotCodeFound Found);

/// <summary>What a Godot project's C# file and its scenes say of each other, its code read by Passenger's extension
/// (<see cref="GodotSource"/>) and its scenes by Passenger's own reader (<see cref="GodotScenes"/>): what to ask the
/// extension of the project's types for the scenes the file's scripts run in (<see cref="Asks"/>), and what is wrong
/// (<see cref="Findings"/>): a GetNode path no node answers in any scene a script runs in, one whose node is not of the
/// type asked for, a res:// path no file answers, a scene's connection to a method the script lacks, or that takes other
/// parameters than its signal gives, and a string Godot's calls take naming a member a script lacks or connecting one of
/// other arguments. Pure: it reads only what it is given (and whether a file is there).</summary>
internal static class GodotChecks
{
    /// <summary>One finding: where it is in the file (the server's places), whether it is an error, and its words.</summary>
    public sealed record Finding(Place At, Place End, bool Error, string Message);

    /// <summary>A connection a scene writes, with the nodes its tree finds for its ends; null for an end it finds none of.</summary>
    public sealed record Connected(string Scene, GodotConnection Connection, GodotTreeNode? From, GodotTreeNode? To);

    /// <summary>What to ask of the extension for a file whose code runs as some scripts: every engine class and C# script of
    /// the scenes they run in, and the signals and methods their connections to those scripts' nodes name.</summary>
    public static GodotCodeRequest Asks(string path, GodotScenes scenes, IEnumerable<string> scripts)
    {
        HashSet<string> engine = new(StringComparer.Ordinal);
        HashSet<string> written = new(StringComparer.Ordinal);
        List<GodotMemberAsked> signals = [];
        List<GodotMemberAsked> methods = [];
        HashSet<string> runs = new(StringComparer.Ordinal);
        foreach (string script in scripts.Distinct(StringComparer.Ordinal))
        {
            foreach ((string scene, GodotTreeNode node) in scenes.RunsOf(script))
            {
                if (!runs.Add(scene))
                {
                    continue;
                }
                foreach (GodotTreeNode below in node.Root.Below())
                {
                    if (below.Type is { } type)
                    {
                        engine.Add(type);
                    }
                    if (IsCSharp(below.Script))
                    {
                        written.Add(below.Script!);
                    }
                }
            }
            foreach (Connected connected in runs.SelectMany(scene => Connections(scenes, scene)).Where(c => c.To?.Script == script))
            {
                signals.Add(SignalAsked(connected));
                methods.Add(MethodAsked(connected));
            }
        }
        return new GodotCodeRequest(path, [.. engine], [.. written], [.. signals.Distinct()], [.. methods.Distinct()]);
    }

    public static GodotMemberAsked SignalAsked(Connected connected) =>
        new(connected.From?.Type, IsCSharp(connected.From?.Script) ? connected.From!.Script : null, connected.Connection.Signal);

    public static GodotMemberAsked MethodAsked(Connected connected) =>
        new(connected.To?.Type, IsCSharp(connected.To?.Script) ? connected.To!.Script : null, connected.Connection.Method);

    /// <summary>A scene's own connections, each with the nodes its tree finds for its ends.</summary>
    public static IEnumerable<Connected> Connections(GodotScenes scenes, string scene)
    {
        if (!scenes.Files.TryGetValue(scene, out GodotFile? file) || scenes.Tree(scene) is not { } root)
        {
            yield break;
        }
        foreach (GodotConnection connection in file.Connections)
        {
            yield return new Connected(scene, connection, GodotScenes.Resolve(root, connection.From).Found, GodotScenes.Resolve(root, connection.To).Found);
        }
    }

    /// <summary>Whether a script is C#'s, which the extension reads.</summary>
    public static bool IsCSharp(string? script) => script?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>What is wrong between a file and the scenes its scripts run in, by what the extension answered of it.</summary>
    public static IReadOnlyList<Finding> Findings(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, Func<string, bool> exists)
    {
        List<Finding> findings = [];
        foreach (GodotNodeCall call in found.Calls)
        {
            if (Call(scenes, found, call) is { } finding)
            {
                findings.Add(finding);
            }
        }
        foreach (GodotPathRead read in found.Resources)
        {
            string last = read.Path.TrimEnd('/').Split('/')[^1];
            string named = read.Path.Split("::")[0];
            if ((read.Path.EndsWith('/') || last.Contains('.')) && scenes.FileOf(named) is { } file && !exists(file))
            {
                findings.Add(new Finding(read.At, read.End, true, $"No {(named.EndsWith('/') ? "folder" : "file")} {named} is in the Godot project"));
            }
        }
        foreach (GodotStringRead read in found.Strings)
        {
            if (StringFinding(found, read) is { } finding)
            {
                findings.Add(finding);
            }
        }
        for (int i = 0; i < found.Classes.Count; i++)
        {
            GodotClassFound type = found.Classes[i];
            HashSet<string> runs = new(StringComparer.Ordinal);
            foreach (string script in type.Scripts)
            {
                foreach (string scene in scenes.RunsOf(script).Select(r => r.Scene).Where(runs.Add))
                {
                    foreach (Connected connected in Connections(scenes, scene).Where(c => c.To?.Script == script))
                    {
                        if (Connection(asked, found, type, connected) is { } finding)
                        {
                            findings.Add(finding);
                        }
                    }
                }
            }
        }
        return findings;
    }

    /// <summary>A GetNode call's finding: no node answers its path in any scene it runs in that says (a GetNodeOrNull's never
    /// counts), or a node it names in one is not of the type asked for; null where every scene's node fits or none says.</summary>
    private static Finding? Call(GodotScenes scenes, GodotCodeFound found, GodotNodeCall call)
    {
        if (call.Class >= found.Classes.Count)
        {
            return null;
        }
        GodotClassFound type = found.Classes[call.Class];
        List<(string Scene, GodotTreeNode Node, GodotResolved Resolved)> said = [.. type.Scripts.SelectMany(scenes.RunsOf)
            .Select(r => (r.Scene, r.Node, Resolved: GodotScenes.Resolve(r.Node, call.Path))).Where(r => !r.Resolved.Unknown)];
        if (said.Count == 0)
        {
            return null;
        }
        if (said.All(s => s.Resolved.Found is null))
        {
            if (call.OrNull)
            {
                return null;
            }
            (string scene, GodotTreeNode node, GodotResolved resolved) = said.OrderByDescending(s => Attached(scenes, s.Scene, s.Node)).First();
            (int start, int end) = resolved.Missing!.Value;
            string name = call.Path[start..end];
            string under = resolved.Reached?.Name ?? node.Name;
            string lacking = start == 0 && end == call.Path.Split(':')[0].Length ? string.Empty
                : $": {under} has no {(name.StartsWith('%') ? $"unique node {name[1..]}" : $"child {name}")}";
            return new Finding(call.At, call.End, true, $"{Place(scenes, scene, node)} has no node '{call.Path}' under {node.Name}{lacking}");
        }
        if (call.Type is not { } wanted)
        {
            return null;
        }
        foreach ((string scene, GodotTreeNode node, GodotResolved resolved) in said.Where(s => s.Resolved.Found is not null))
        {
            GodotTreeNode target = resolved.Found!;
            if (Fits(wanted, target, found) == false)
            {
                string shown = $"'{call.Path}' is {Article(Shown(target))} in {Place(scenes, target.Declared.Scene, target)}, not {Article(wanted.Shown)}";
                return new Finding(call.At, call.End, !call.OrNull, call.OrNull ? $"GetNodeOrNull<{wanted.Shown}> gives null: {shown}" : shown);
            }
        }
        return null;
    }

    /// <summary>A connection's finding in its receiving script's file: a method the script lacks (at the class's name), one
    /// whose parameters are not as many as the signal gives with its binds (at the method's name, where this file declares
    /// it), or a signal its sending node does not have; null where it holds, or what it needs is not known.</summary>
    private static Finding? Connection(GodotCodeRequest asked, GodotCodeFound found, GodotClassFound type, Connected connected)
    {
        GodotConnection connection = connected.Connection;
        string where = $"{System.IO.Path.GetFileName(connected.Scene)}:{connection.Section.Line + 1}";
        string from = connection.From == "." ? connected.From?.Name ?? connection.From : connection.From;
        int methodAt = IndexOf(asked.Methods, MethodAsked(connected));
        int signalAt = IndexOf(asked.Signals, SignalAsked(connected));
        GodotSignature? method = methodAt >= 0 && methodAt < found.Methods.Count ? found.Methods[methodAt] : null;
        GodotSignature? signal = signalAt >= 0 && signalAt < found.Signals.Count ? found.Signals[signalAt] : null;
        if (methodAt < 0 || methodAt >= found.Methods.Count)
        {
            return null;
        }
        if (method is null)
        {
            return new Finding(type.At, type.End, true, $"{where} connects {from}'s {connection.Signal} to {connection.Method}, which {type.Name} has no method of");
        }
        if (signal is null)
        {
            bool known = connected.From is { } sender && (sender.Script is null || IsCSharp(sender.Script)) && sender.Type is not null;
            return known && signalAt >= 0 && signalAt < found.Signals.Count
                ? new Finding(type.At, type.End, true, $"{where} connects {from}'s {connection.Signal}, a signal {from} ({Shown(connected.From!)}) has none of, to {connection.Method}")
                : null;
        }
        int given = signal.Parameters.Count + connection.Binds - connection.Unbinds;
        if (given >= method.Required && given <= method.Parameters.Count)
        {
            return null;
        }
        bool here = method.At is not null && method.Path is not null && SameFile(method.Path, asked.Path);
        string takes = Takes(method.Parameters);
        string gives = Gives(given, signal.Parameters.Concat(Enumerable.Repeat("bound", Math.Max(0, connection.Binds))));
        return new Finding(here ? method.At! : type.At, here ? method.End! : type.End, true, $"{where} connects {from}'s {connection.Signal}, which {gives}, to {connection.Method}, which {takes}");
    }

    /// <summary>A string Godot's calls take naming a member: one a script's class lacks, a signal an EmitSignal gives other
    /// arguments than it carries, or a method connected by a Connect to a signal named by a string that takes other
    /// arguments than the signal gives; null where it holds, or what it names is not known (an engine class's object, which
    /// a script may give more).</summary>
    private static Finding? StringFinding(GodotCodeFound found, GodotStringRead read)
    {
        if (read.Found is not { } member)
        {
            string what = read.Kind switch { GodotNameKind.Signal => "signal", GodotNameKind.Method => "method", _ => "property" };
            return read.Script ? new Finding(read.At, read.End, true, $"{read.On} has no {what} '{read.Name}'") : null;
        }
        if (read.Given is { } given && (given < member.Required || given > member.Parameters.Count))
        {
            return new Finding(read.At, read.End, true, $"'{read.Name}' {Takes(member.Parameters)}, but this EmitSignal gives it {(given == 0 ? "none" : given.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }
        if (read.Signal is { } index && index < found.Strings.Count && found.Strings[index].Found is { } signal
            && (signal.Parameters.Count < member.Required || signal.Parameters.Count > member.Parameters.Count))
        {
            return new Finding(read.At, read.End, true, $"'{found.Strings[index].Name}', which {Gives(signal.Parameters.Count, signal.Parameters)}, is connected to '{read.Name}', which {Takes(member.Parameters)}");
        }
        return null;
    }

    private static string Gives(int given, IEnumerable<string> types) => given == 0 ? "gives no arguments" : $"gives {given} ({string.Join(", ", types.Take(given))})";

    private static string Takes(IReadOnlyList<string> parameters) => parameters.Count == 0 ? "takes none" : $"takes {parameters.Count} ({string.Join(", ", parameters)})";

    private static int IndexOf(IReadOnlyList<GodotMemberAsked> asked, GodotMemberAsked one)
    {
        for (int i = 0; i < asked.Count; i++)
        {
            if (asked[i] == one)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool SameFile(string left, string right) => Core.PathNames.Same(left, right) || Core.PathNames.Same(Core.PathNames.Canonical(left), Core.PathNames.Canonical(right));

    /// <summary>Whether a node is of a type: its engine class's chain holds an engine type, its C# script's class's chain a
    /// type of the code; null where what it is is not known.</summary>
    public static bool? Fits(GodotTypeRef wanted, GodotTreeNode node, GodotCodeFound found)
    {
        if (wanted.Engine)
        {
            return node.Type is { } type && found.Engine.FirstOrDefault(e => e.Name == type) is { } chain ? chain.Chain.Contains(wanted.Name) : null;
        }
        if (node.Script is null)
        {
            return node.Type is null ? null : false;
        }
        if (!IsCSharp(node.Script))
        {
            return false;
        }
        return found.Scripts.FirstOrDefault(s => s?.Name == node.Script) is { } script ? script.Chain.Contains(wanted.Name) : null;
    }

    /// <summary>A node as its words name it: its engine class, with its script's file beside it.</summary>
    public static string Shown(GodotTreeNode node) =>
        node.Script is { } script ? $"{node.Type ?? "node"} ({script.Split('/')[^1]})" : node.Type ?? "node";

    private static string Article(string name) => (name.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(name[0])) ? "an " : "a ") + name;

    /// <summary>Where a node is written in a scene, as its words name it: the scene's file and the line its section starts on.</summary>
    public static string Place(GodotScenes scenes, string scene, GodotTreeNode node)
    {
        (string declaring, Passenger.Workspace.GodotNode declared) = node.Written.LastOrDefault(w => w.Scene == scene) is { Node: not null } own ? own : node.Declared;
        return $"{System.IO.Path.GetFileName(declaring)}:{declared.Section.Line + 1}";
    }

    /// <summary>Whether a scene's own sections give a node its script, rather than an instance bringing it in.</summary>
    private static bool Attached(GodotScenes scenes, string scene, GodotTreeNode node) =>
        node.Written.Any(w => w.Scene == scene && scenes.Files.TryGetValue(scene, out GodotFile? file) && file.External(w.Node.Script)?.Path == node.Script);
}
