using Passenger.App.Editors;
using Passenger.Roslyn.Extension;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>What a Godot project's scenes say beside a C# file's code, read from what Passenger's extension answered of it
/// (<see cref="GodotRead"/>): the scenes each class runs in (<see cref="Classes"/>), the connections calling each of its
/// handlers (<see cref="Handlers"/>), and the node each GetNode path comes to (<see cref="Hints"/>). Places are the
/// server's; scenes' places the scenes' files and lines (from zero).</summary>
internal static class GodotBeside
{
    /// <summary>A mark beside the code: where its name is written (the server's place), and where it leads, in order.</summary>
    public sealed record Mark(Place At, IReadOnlyList<SceneLead> Leads);

    /// <summary>A hint after a GetNode path's string, whose closing quote is at <paramref name="Quote"/> (the server's
    /// place), of what the path comes to.</summary>
    public sealed record Hint(Place Quote, string Words);

    /// <summary>Each class's mark at its name: the scenes it runs in, one lead a scene, to the node whose script it is (where
    /// that scene writes it); none for a class no scene runs.</summary>
    public static IReadOnlyList<Mark> Classes(GodotScenes scenes, GodotCodeFound found)
    {
        List<Mark> marks = [];
        foreach (GodotClassFound type in found.Classes)
        {
            List<SceneLead> leads = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach ((string scene, GodotTreeNode node) in type.Scripts.SelectMany(scenes.RunsOf).OrderBy(r => r.Scene, StringComparer.Ordinal))
            {
                if (seen.Add(scene) && WrittenIn(scenes, scene, node) is { } written)
                {
                    leads.Add(new SceneLead($"{Path.GetFileName(scene)}: {(node.Parent is null ? node.Name : $"{node.Root.Name}/{node.Path}")}", written.File, written.Line));
                }
            }
            if (leads.Count > 0)
            {
                marks.Add(new Mark(type.At, leads));
            }
        }
        return marks;
    }

    /// <summary>Each handler's mark at its name, where the file declares it: the scenes' connections calling it, one lead a
    /// connection, to its line.</summary>
    public static IReadOnlyList<Mark> Handlers(GodotScenes scenes, GodotCodeRequest asked, GodotCodeFound found)
    {
        List<(Place At, List<SceneLead> Leads)> marks = [];
        foreach (GodotClassFound type in found.Classes)
        {
            HashSet<string> runs = new(StringComparer.Ordinal);
            foreach (string scene in type.Scripts.SelectMany(scenes.RunsOf).Select(r => r.Scene).Where(runs.Add).Order(StringComparer.Ordinal))
            {
                foreach (GodotChecks.Connected connected in GodotChecks.Connections(scenes, scene).Where(c => c.To?.Script is { } to && type.Scripts.Contains(to)))
                {
                    int index = IndexOf(asked.Methods, GodotChecks.MethodAsked(connected));
                    if (index < 0 || index >= found.Methods.Count || found.Methods[index] is not { Path: { } declaring, At: { } at } || !Core.PathNames.SameFile(declaring, asked.Path)
                        || scenes.FileOf(scene) is not { } file)
                    {
                        continue;
                    }
                    if (marks.FindIndex(m => m.At == at) is var known && known < 0)
                    {
                        marks.Add((at, []));
                        known = marks.Count - 1;
                    }
                    GodotConnection connection = connected.Connection;
                    marks[known].Leads.Add(new SceneLead($"{Path.GetFileName(scene)}: {connection.From}'s {connection.Signal} → {connection.Method}", file, connection.Section.Line));
                }
            }
        }
        return [.. marks.Select(m => new Mark(m.At, m.Leads))];
    }

    /// <summary>Each GetNode path written as it says, after its string: the node it comes to in the scenes its class runs in,
    /// as its words name it (an engine class, a script's file beside it), each kind once; none for a path no scene answers,
    /// which is a finding of its own.</summary>
    public static IReadOnlyList<Hint> Hints(GodotScenes scenes, GodotCodeFound found)
    {
        List<Hint> hints = [];
        foreach (GodotNodeCall call in found.Calls.Where(c => c.Exact && c.Class < found.Classes.Count))
        {
            List<string> shown = [];
            foreach ((_, GodotTreeNode node) in found.Classes[call.Class].Scripts.SelectMany(scenes.RunsOf))
            {
                if (GodotScenes.Resolve(node, call.Path).Found is { } target && GodotChecks.Shown(target) is var words && !shown.Contains(words))
                {
                    shown.Add(words);
                }
            }
            if (shown.Count > 0)
            {
                hints.Add(new Hint(call.End, string.Join(" | ", shown)));
            }
        }
        return hints;
    }

    /// <summary>Where a scene writes a node: its own section of it, else the one it was declared by (an instanced scene's).</summary>
    private static (string File, int Line)? WrittenIn(GodotScenes scenes, string scene, GodotTreeNode node)
    {
        (string declaring, Passenger.Workspace.GodotNode section) = node.Written.LastOrDefault(w => w.Scene == scene) is { Node: not null } own ? own : node.Declared;
        return scenes.FileOf(declaring) is { } file ? (file, section.Section.Line) : null;
    }

    private static int IndexOf(IReadOnlyList<GodotMemberAsked> asked, GodotMemberAsked wanted)
    {
        for (int i = 0; i < asked.Count; i++)
        {
            if (asked[i] == wanted)
            {
                return i;
            }
        }
        return -1;
    }
}
