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
/// type asked for, and a res:// path no file answers. Pure: it reads only what it is given (and whether a file is there).</summary>
internal static class GodotChecks
{
    /// <summary>One finding: where it is in the file (the server's places), whether it is an error, and its words.</summary>
    public sealed record Finding(Place At, Place End, bool Error, string Message);

    /// <summary>What to ask of the extension for a file whose code runs as some scripts: every engine class and C# script of
    /// the scenes they run in.</summary>
    public static GodotCodeRequest Asks(string path, GodotScenes scenes, IEnumerable<string> scripts)
    {
        HashSet<string> engine = new(StringComparer.Ordinal);
        HashSet<string> written = new(StringComparer.Ordinal);
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
        }
        return new GodotCodeRequest(path, [.. engine], [.. written]);
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
