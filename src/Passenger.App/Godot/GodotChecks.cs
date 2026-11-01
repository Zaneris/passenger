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

    /// <summary>What to ask of the extension for a scene's own connections, through a C# file of its project
    /// (<paramref name="path"/>): every engine class and C# script of its tree, and each connection's signal and method.</summary>
    public static GodotCodeRequest SceneAsks(string path, GodotScenes scenes, string scene)
    {
        IReadOnlyList<GodotTreeNode> nodes = scenes.Tree(scene) is { } root ? [.. root.Below()] : [];
        List<Connected> connections = [.. Connections(scenes, scene)];
        return new GodotCodeRequest(path, [.. nodes.Select(n => n.Type).OfType<string>().Distinct(StringComparer.Ordinal)],
            [.. nodes.Select(n => n.Script).Where(IsCSharp).OfType<string>().Distinct(StringComparer.Ordinal)],
            [.. connections.Select(SignalAsked).Distinct()], [.. connections.Select(MethodAsked).Distinct()]);
    }

    /// <summary>One finding in a scene's own text: where (offsets of the file), whether it is an error, and its words.</summary>
    public sealed record SceneFinding(int Start, int End, bool Error, string Message);

    /// <summary>What is wrong in a scene or resource as Godot would load it, by what the extension answered of its connections
    /// (<see cref="SceneAsks"/>): an external resource whose file is not there, an ExtResource no external resource of the
    /// file has, a node whose parent is not in the scene (which Godot drops), a connection's end no node answers, and a
    /// connection to a method its receiving node lacks, from a signal its sending node lacks, or of other arguments than the
    /// method takes; a node whose script is not C# is never said to lack a member, which its script may give it.</summary>
    public static IReadOnlyList<SceneFinding> SceneFindings(GodotScenes scenes, string scene, GodotCodeRequest asked, GodotCodeFound found, Func<string, bool> exists)
    {
        List<SceneFinding> findings = [];
        if (!scenes.Files.TryGetValue(scene, out GodotFile? file))
        {
            return findings;
        }
        foreach (GodotExternal external in file.Externals)
        {
            string? byUid = external.Uid is { } uid && scenes.ResOfUid(uid) is { } res && scenes.FileOf(res) is { } loaded && exists(loaded) ? res : null;
            if (external.Path is { } path && scenes.FileOf(path) is { } named && !exists(named) && external.Section.Attribute("path")?.Value is { } value)
            {
                // Godot loads an external resource by its uid where one names a file, by its path only where none does.
                findings.Add(byUid is null ? new SceneFinding(value.TextStart, value.TextEnd, true, $"No file {path} is in the Godot project")
                    : new SceneFinding(value.TextStart, value.TextEnd, false, $"No file {path} is in the Godot project: Godot loads {external.Uid}, {byUid}, by its uid"));
            }
            else if (byUid is not null && external.Path is { } written && written != byUid && external.Section.Attribute("uid")?.Value is { } uidValue)
            {
                findings.Add(new SceneFinding(uidValue.TextStart, uidValue.TextEnd, false, $"{external.Uid} is {byUid}, which Godot loads in place of {written}"));
            }
        }
        foreach (GodotSection section in file.Sections)
        {
            foreach (GodotEntry entry in section.Attributes.Concat(section.Properties))
            {
                if (entry.Value is { Call: "ExtResource", Text: { } id } value && file.External(id) is null && section.Tag != "ext_resource")
                {
                    findings.Add(new SceneFinding(value.TextStart, value.TextEnd, true, $"No ext_resource of this file has the id \"{id}\""));
                }
            }
        }
        if (scenes.Tree(scene) is not { } root)
        {
            return findings;
        }
        foreach (Passenger.Workspace.GodotNode node in file.Nodes)
        {
            if (node.Parent is { } parent && GodotScenes.Resolve(root, parent) is { Found: null, Unknown: false } && node.Section.Attribute("parent")?.Value is { } value)
            {
                findings.Add(new SceneFinding(value.TextStart, value.TextEnd, true, $"No node '{parent}' is in the scene, so Godot leaves {node.Name} out"));
            }
        }
        foreach (Connected connected in Connections(scenes, scene))
        {
            GodotConnection connection = connected.Connection;
            foreach ((string key, string path, GodotTreeNode? end) in new[] { ("from", connection.From, connected.From), ("to", connection.To, connected.To) })
            {
                if (end is null && !GodotScenes.Resolve(root, path).Unknown && connection.Section.Attribute(key)?.Value is { } value)
                {
                    findings.Add(new SceneFinding(value.TextStart, value.TextEnd, true, $"No node '{path}' is in the scene"));
                }
            }
            int methodAt = Known(connected.To) ? IndexOf(asked.Methods, MethodAsked(connected)) : -1;
            int signalAt = Known(connected.From) ? IndexOf(asked.Signals, SignalAsked(connected)) : -1;
            bool methodKnown = methodAt >= 0 && methodAt < found.Methods.Count;
            bool signalKnown = signalAt >= 0 && signalAt < found.Signals.Count;
            GodotSignature? method = methodKnown ? found.Methods[methodAt] : null;
            GodotSignature? signal = signalKnown ? found.Signals[signalAt] : null;
            if (methodKnown && method is null && connection.Section.Attribute("method")?.Value is { } named)
            {
                findings.Add(new SceneFinding(named.TextStart, named.TextEnd, true, $"{connected.To!.Name}, {Article(Shown(connected.To))}, has no method {connection.Method}"));
            }
            if (signalKnown && signal is null && connection.Section.Attribute("signal")?.Value is { } emitted)
            {
                findings.Add(new SceneFinding(emitted.TextStart, emitted.TextEnd, true, $"{connected.From!.Name}, {Article(Shown(connected.From))}, has no signal {connection.Signal}"));
            }
            int given = (signal?.Parameters.Count ?? 0) + connection.Binds - connection.Unbinds;
            if (method is not null && signal is not null && (given < method.Required || given > method.Parameters.Count) && connection.Section.Attribute("method")?.Value is { } called)
            {
                findings.Add(new SceneFinding(called.TextStart, called.TextEnd, true,
                    $"{connection.Signal} {Gives(given, signal.Parameters.Concat(Enumerable.Repeat("bound", Math.Max(0, connection.Binds))))}, but {connection.Method} {Takes(method.Parameters)}"));
            }
        }
        return findings;

        // A node whose members the code says: one with no script, of a known engine class, or one with a C# script.
        static bool Known(GodotTreeNode? node) => node is not null && (node.Script is null ? node.Type is not null : IsCSharp(node.Script));
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
            if (named.StartsWith(GodotUids.Scheme, StringComparison.Ordinal))
            {
                // A uid none of the project's files has is known to name none only once Godot's editor has kept their cache.
                if (scenes.ResOfUid(named) is { } res ? scenes.FileOf(res) is { } loaded && !exists(loaded) : scenes.KeepsUids)
                {
                    findings.Add(new Finding(read.At, read.End, true, scenes.ResOfUid(named) is { } gone ? $"{named} is {gone}, which is not in the Godot project"
                        : $"No file of the Godot project has {named}"));
                }
                continue;
            }
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
        foreach (GodotClassFound type in found.Classes)
        {
            findings.AddRange(Exports(scenes, asked, found, type));
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

    /// <summary>Each scene's value of an exported member that names a node by its path (a NodePath, a node's type) that no
    /// node answers from the node the script runs on, or that names a node not of the member's type: at the member's name
    /// where this file declares it (else at its class's), naming the scene and line; a value set in one scene, read from each
    /// tree that holds it, said once.</summary>
    private static IEnumerable<Finding> Exports(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found, GodotClassFound type)
    {
        HashSet<(string, int)> said = [];
        foreach (GodotMember export in type.Exports.Where(e => e.Node is not null))
        {
            bool here = SameFile(export.Path, asked.Path);
            foreach ((string scene, GodotTreeNode node) in type.Scripts.SelectMany(scenes.RunsOf))
            {
                foreach ((string written, Passenger.Workspace.GodotNode section) in node.Written)
                {
                    if (section.Section.Property(export.Name) is not { Value: { Call: "NodePath", Text: { Length: > 0 } path } } property
                        || GodotScenes.Resolve(node, path) is not { Unknown: false } resolved || !scenes.Files.TryGetValue(written, out GodotFile? file))
                    {
                        continue;
                    }
                    string where = $"{System.IO.Path.GetFileName(written)}:{file.LineOf(property.KeyStart) + 1}";
                    string? message = null;
                    if (resolved.Found is not { } target)
                    {
                        (int start, int end) = resolved.Missing!.Value;
                        string name = path[start..end];
                        string under = resolved.Reached?.Name ?? node.Name;
                        message = $"{where} sets {export.Name} to '{path}', which names no node: {under} has no {(name.StartsWith('%') ? $"unique node {name[1..]}" : $"child {name}")}";
                    }
                    else if (Fits(export.Node!, target, found) == false)
                    {
                        message = $"{where} sets {export.Name} to '{path}', {Article(Shown(target))}, not {Article(export.Node!.Shown)}";
                    }
                    if (message is not null && said.Add((written, property.KeyStart)))
                    {
                        yield return new Finding(here ? export.At : type.At, here ? export.End : type.End, true, message);
                    }
                }
            }
        }
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
