using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>A node of a scene as Godot builds it: the nodes the scene writes, with the scenes it instances built into it
/// (their nodes copied under the node that instances them, which a later section of the scene may change). Each knows
/// where it is written (<see cref="Declared"/>, the section that names it, and every section of a scene over it that
/// changes it), its engine class and script as the last of those give them, and its owner, whose unique names
/// (<c>%Score</c>) it finds.</summary>
public sealed class GodotTreeNode
{
    private readonly List<GodotTreeNode> children = [];

    internal GodotTreeNode(string name, GodotTreeNode? parent, (string Scene, GodotNode Node) declared)
    {
        Name = name;
        Parent = parent;
        Written = [declared];
    }

    public string Name { get; private set; }

    public GodotTreeNode? Parent { get; }

    public IReadOnlyList<GodotTreeNode> Children => children;

    /// <summary>Its engine class (<c>Label</c>); null where no scene says it (an instance of a scene not found).</summary>
    public string? Type { get; private set; }

    /// <summary>Its script's res:// path; null for none.</summary>
    public string? Script { get; private set; }

    public bool Unique { get; private set; }

    /// <summary>The node whose scene it is part of; null for the tree's root.</summary>
    public GodotTreeNode? Owner { get; private set; }

    /// <summary>The scene it instances (res://), where it is an instance, one no text says the nodes of too (an imported
    /// model); null for any other node.</summary>
    public string? Instance { get; internal set; }

    /// <summary>Whether it stands in for a scene loaded only when the game asks (<c>instance_placeholder</c>), whose
    /// nodes are not known.</summary>
    public bool Placeholder { get; private set; }

    /// <summary>Whether it instances a scene no text says the nodes of (an imported model, a binary scene, one not found),
    /// so a name below it that no section adds is not known.</summary>
    public bool NodesUnread { get; internal set; }

    /// <summary>The scene and section that name it, its declaration, then every later section that changes it.</summary>
    public IReadOnlyList<(string Scene, GodotNode Node)> Written { get; private set; }

    public (string Scene, GodotNode Node) Declared => Written[0];

    /// <summary>Its path from the tree's root: "." for the root, else its names below the root ("Hud/Score").</summary>
    public string Path => Parent is null ? "." : Parent.Parent is null ? Name : $"{Parent.Path}/{Name}";

    /// <summary>The root of its tree.</summary>
    public GodotTreeNode Root => Parent?.Root ?? this;

    public GodotTreeNode? Child(string name) => children.FirstOrDefault(c => c.Name == name);

    /// <summary>It and every node below it, in the scene's order.</summary>
    public IEnumerable<GodotTreeNode> Below()
    {
        yield return this;
        foreach (GodotTreeNode child in children)
        {
            foreach (GodotTreeNode below in child.Below())
            {
                yield return below;
            }
        }
    }

    /// <summary>The unique nodes it finds by <c>%Name</c>: those it owns, else its owner's.</summary>
    public GodotTreeNode? UniqueNamed(string name) =>
        Root.Below().FirstOrDefault(n => n.Unique && n.Owner == this && n.Name == name)
        ?? (Owner is { } owner ? Root.Below().FirstOrDefault(n => n.Unique && n.Owner == owner && n.Name == name) : null);

    /// <summary>The same node as another: declared by the same section of the same scene.</summary>
    public bool Same(GodotTreeNode other) => Declared.Scene == other.Declared.Scene && Declared.Node.Section.Start == other.Declared.Node.Section.Start;

    internal void Take(string scene, GodotNode node, GodotFile file, bool declares)
    {
        if (!declares)
        {
            Written = [.. Written, (scene, node)];
        }
        if (node.Type is { } type)
        {
            Type = type;
        }
        if (file.External(node.Script)?.Path is { } script)
        {
            Script = script;
        }
        if (node.Section.Property("unique_name_in_owner") is { } unique)
        {
            Unique = unique.Value.Word == "true";
        }
        if (node.Placeholder is not null)
        {
            Placeholder = true;
        }
    }

    internal void Add(GodotTreeNode child) => children.Add(child);

    /// <summary>A copy of a tree under a parent, its root named and declared by the section that instances it, owned by
    /// <paramref name="owner"/>, and the copied nodes owned by the copies of their owners.</summary>
    internal static GodotTreeNode Copy(GodotTreeNode root, GodotTreeNode? parent, GodotTreeNode? owner, string scene, GodotNode instancing, string instanced)
    {
        Dictionary<GodotTreeNode, GodotTreeNode> made = [];
        GodotTreeNode top = Copied(root, parent, made);
        top.Name = instancing.Name;
        top.Written = [(scene, instancing), .. root.Written];
        top.Instance = instanced;
        top.Owner = owner;
        foreach ((GodotTreeNode was, GodotTreeNode copy) in made)
        {
            if (was != root)
            {
                copy.Owner = was.Owner is { } wasOwned && made.TryGetValue(wasOwned, out GodotTreeNode? owned) ? owned : top;
            }
        }
        return top;
    }

    private static GodotTreeNode Copied(GodotTreeNode node, GodotTreeNode? parent, Dictionary<GodotTreeNode, GodotTreeNode> made)
    {
        GodotTreeNode copy = new(node.Name, parent, node.Declared)
        {
            Written = node.Written,
            Type = node.Type,
            Script = node.Script,
            Unique = node.Unique,
            Instance = node.Instance,
            Placeholder = node.Placeholder,
            NodesUnread = node.NodesUnread,
        };
        made[node] = copy;
        foreach (GodotTreeNode child in node.children)
        {
            copy.children.Add(Copied(child, copy, made));
        }
        return copy;
    }

    internal void Own(GodotTreeNode? owner) => Owner = owner;
}

/// <summary>Where a node path leads from a node: the nodes each of its names reached (<see cref="Steps"/>, with where the
/// name lies in the path: a name, a <c>%Name</c> or ".."), and the node it names; else where the first name no node answers
/// lies (<see cref="Missing"/>), or <see cref="Unknown"/> where the path leaves what the scene says (above its root, an
/// absolute path, a placeholder's nodes).</summary>
public sealed record GodotResolved(GodotTreeNode? Found, IReadOnlyList<(int Start, int End, GodotTreeNode Node)> Steps, (int Start, int End)? Missing, bool Unknown)
{
    /// <summary>The node the last step reached before the path went missing: the one with no child of the name.</summary>
    public GodotTreeNode? Reached => Steps.Count > 0 ? Steps[^1].Node : null;
}

/// <summary>A Godot project's text scenes and resources (<see cref="GodotFile"/>), by res:// path, and what they say of its
/// nodes: each scene's tree with the scenes it instances built in (<see cref="Tree"/>), every node a script runs on
/// (<see cref="RunsOf"/>), where a node path leads from a node (<see cref="Resolve"/>), and the edits a node's rename makes
/// (<see cref="Renames"/>). Pure: given the files, it reads nothing else, so it is used off the UI thread.</summary>
public sealed class GodotScenes
{
    /// <summary>How deep instances go into instances before the rest are taken for unknown.</summary>
    private const int Deepest = 24;

    /// <summary>The characters a node's name never holds, as Godot refuses them.</summary>
    public const string Unnamable = ".:@/\"%";

    private readonly Dictionary<string, GodotTreeNode?> trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<(string Scene, GodotTreeNode Node)>> runs = new(StringComparer.Ordinal);
    private readonly Lock building = new();
    private readonly Lazy<IReadOnlyDictionary<string, string>> named;
    private readonly Lazy<GodotDefinitions> defined;
    private readonly Dictionary<string, GodotScriptDeclarations?> scripts = new(StringComparer.Ordinal);
    private readonly Lazy<IReadOnlyDictionary<string, string>> classes;

    /// <summary>The scenes and resources by res:// path, and the uid:// names Godot keeps of the project's other files (its
    /// .uid and .import files' and its cache's, <see cref="GodotUids"/>, uid by uid) beside those the files' headers give;
    /// <paramref name="cached"/> when Godot's editor keeps a cache of them, so a uid none names is known to name no file;
    /// and the texts of its other files that define names (<see cref="GodotDefinitions.Reads"/>), by res:// path.</summary>
    public GodotScenes(string folder, IReadOnlyDictionary<string, GodotFile> files, IReadOnlyDictionary<string, string>? uids = null, bool cached = false,
        IReadOnlyDictionary<string, string>? texts = null)
    {
        Folder = folder;
        Files = files;
        Texts = texts ?? new Dictionary<string, string>(StringComparer.Ordinal);
        KeepsUids = cached;
        defined = new(() => new GodotDefinitions(this));
        classes = new(() =>
        {
            Dictionary<string, string> all = new(StringComparer.Ordinal);
            foreach (string res in Texts.Keys.Where(GodotScriptDeclarations.IsScript).Order(StringComparer.Ordinal))
            {
                if (ScriptOf(res)?.ClassName is { } name)
                {
                    all.TryAdd(name, res);
                }
            }
            return all;
        });
        named = new(() =>
        {
            Dictionary<string, string> all = new(uids ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            foreach ((string res, GodotFile file) in files)
            {
                if (file.Header?.Text("uid") is { } uid && uid.StartsWith(GodotUids.Scheme, StringComparison.Ordinal))
                {
                    all[uid] = res;
                }
            }
            return all;
        });
    }

    /// <summary>Whether Godot's editor keeps a cache of the project's uids, so one no file has is known to name none.</summary>
    public bool KeepsUids { get; }

    /// <summary>Every res:// path the project's files are known by: its scenes and resources, and the files it keeps a uid of.</summary>
    public IEnumerable<string> Known => Files.Keys.Concat(named.Value.Values).Distinct(StringComparer.Ordinal);

    /// <summary>The res:// path of the file a uid:// names, a scene's own header first; null for one no file has.</summary>
    public string? ResOfUid(string uid) => named.Value.GetValueOrDefault(uid);

    /// <summary>The res:// path of the file an autoload of project.godot loads, through its uid where it names one (as
    /// Godot 4.4 writes them); null for none, or a uid no file has.</summary>
    public string? AutoloadFile(string name) =>
        Defined.AutoloadFile(name) is { } written && written.StartsWith(GodotUids.Scheme, StringComparison.Ordinal) ? ResOfUid(written) : Defined.AutoloadFile(name);

    /// <summary>The Godot project's folder, where its project.godot is: what res:// names.</summary>
    public string Folder { get; }

    /// <summary>Its text scenes and resources by res:// path.</summary>
    public IReadOnlyDictionary<string, GodotFile> Files { get; }

    /// <summary>Its project.godot, shaders, translations and GDScripts by res:// path, as text.</summary>
    public IReadOnlyDictionary<string, string> Texts { get; }

    /// <summary>What its files define for the strings its code gives Godot's calls, read once.</summary>
    public GodotDefinitions Defined => defined.Value;

    /// <summary>A GDScript's own declarations, read once; null for a script the project's texts lack.</summary>
    public GodotScriptDeclarations? ScriptOf(string res)
    {
        lock (building)
        {
            if (scripts.TryGetValue(res, out GodotScriptDeclarations? kept))
            {
                return kept;
            }
        }
        GodotScriptDeclarations? read = GodotScriptDeclarations.IsScript(res) && Texts.TryGetValue(res, out string? text) ? GodotScriptDeclarations.Read(text) : null;
        lock (building)
        {
            scripts[res] = read;
        }
        return read;
    }

    /// <summary>A GDScript's class with the scripts it extends (<see cref="GodotScriptChain"/>): by a path (res://, or beside
    /// it), a uid:// name, or a <c>class_name</c> a script of the project gives; whole once one extends a class none names.</summary>
    public GodotScriptChain ChainOf(string script)
    {
        List<(string, GodotScriptDeclarations)> chain = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (string? at = script; at is not null && seen.Add(at);)
        {
            if (ScriptOf(at) is not { } declared)
            {
                return new GodotScriptChain(chain, false, null);
            }
            chain.Add((at, declared));
            if (declared.Extends is not { } extends)
            {
                return new GodotScriptChain(chain, true, "RefCounted");
            }
            at = declared.ExtendsPath ? Beside(at, extends) : classes.Value.GetValueOrDefault(extends);
            if (at is null)
            {
                // A class no script names is the engine's, but for an inner class of one (Base.Inner), which is not read.
                return extends.Contains('.') || declared.ExtendsPath ? new GodotScriptChain(chain, false, null) : new GodotScriptChain(chain, true, extends);
            }
        }
        return new GodotScriptChain(chain, false, null);
    }

    /// <summary>The res:// path a script's <c>extends</c> path names: a res:// or uid:// one as it is, another from the script's folder.</summary>
    private string? Beside(string script, string path)
    {
        if (path.StartsWith(GodotUids.Scheme, StringComparison.Ordinal))
        {
            return ResOfUid(path);
        }
        if (path.StartsWith("res://", StringComparison.Ordinal) || !script.StartsWith("res://", StringComparison.Ordinal))
        {
            return path.StartsWith("res://", StringComparison.Ordinal) ? path : null;
        }
        List<string> names = [.. script["res://".Length..].Split('/')[..^1]];
        foreach (string name in path.Split('/'))
        {
            if (name == "..")
            {
                if (names.Count == 0)
                {
                    return null;
                }
                names.RemoveAt(names.Count - 1);
            }
            else if (name is not ("." or ""))
            {
                names.Add(name);
            }
        }
        return "res://" + string.Join('/', names);
    }

    /// <summary>The res:// path of a file under a Godot project's folder; null outside it.</summary>
    public static string? ResPathOf(string folder, string path) =>
        PathNames.IsUnder(path, folder) && !PathNames.Same(path, folder) ? "res://" + System.IO.Path.GetRelativePath(folder, path).Replace('\\', '/') : null;

    /// <summary>The file a res:// path names under a Godot project's folder; null for a path not res://, or one that climbs out.</summary>
    public static string? FileOf(string folder, string res)
    {
        if (!res.StartsWith("res://", StringComparison.Ordinal))
        {
            return null;
        }
        string relative = res["res://".Length..].Split(':')[0];
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        return PathNames.IsUnder(full, folder) ? full : null;
    }

    /// <summary>The file a res:// path, or a uid:// name of one, names; null for neither, or a uid no file has.</summary>
    public string? FileOf(string res) => res.StartsWith(GodotUids.Scheme, StringComparison.Ordinal)
        ? ResOfUid(res) is { } path ? FileOf(Folder, path) : null
        : FileOf(Folder, res);

    public string? ResPathOf(string path) => ResPathOf(Folder, path);

    /// <summary>A scene's tree, built once; null for a file that is no scene, or has no root.</summary>
    public GodotTreeNode? Tree(string scene)
    {
        lock (building)
        {
            return Built(scene, []);
        }
    }

    /// <summary>Every node a script runs on: in each scene's tree, each node whose script it is, the scenes that instance
    /// another holding it among them. Each with the scene whose tree it is in.</summary>
    public IReadOnlyList<(string Scene, GodotTreeNode Node)> RunsOf(string script)
    {
        lock (building)
        {
            if (runs.TryGetValue(script, out IReadOnlyList<(string, GodotTreeNode)>? kept))
            {
                return kept;
            }
        }
        List<(string, GodotTreeNode)> found = [];
        foreach (string scene in Files.Keys.Order(StringComparer.Ordinal))
        {
            if (Files[scene].Header?.Tag == "gd_scene" && Tree(scene) is { } root)
            {
                found.AddRange(root.Below().Where(n => n.Script == script).Select(n => (scene, n)));
            }
        }
        lock (building)
        {
            runs[script] = found;
        }
        return found;
    }

    /// <summary>The scenes whose own sections give a node a script: where it is attached, rather than brought in by an instance.</summary>
    public IReadOnlyList<(string Scene, GodotTreeNode Node)> AttachedOf(string script) =>
        [.. RunsOf(script).Where(r => r.Node.Written.Any(w => w.Scene == r.Scene && Files[r.Scene].External(w.Node.Script)?.Path == script))];

    /// <summary>Each key setting a property on a resource a script runs in, which no node's section writes: a resource file's
    /// own <c>[resource]</c>, and any file's <c>[sub_resource]</c>, whose <c>script</c> is one <paramref name="runs"/> takes
    /// in; by res:// path, in the files' order.</summary>
    public IEnumerable<(string Res, GodotEntry Key)> ResourceKeys(Func<string, bool> runs, string property)
    {
        foreach ((string res, GodotFile file) in Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            foreach (GodotSection section in file.Sections.Where(s => s.Tag is "resource" or "sub_resource"))
            {
                if (file.External(section.Property("script")?.Value is { Call: "ExtResource", Text: { } id } ? id : null)?.Path is { } script && runs(script)
                    && section.Property(property) is { } key)
                {
                    yield return (res, key);
                }
            }
        }
    }

    /// <summary>Where a node path leads from a node: its names in turn, "." and ".." among them, a first <c>%Name</c> among the
    /// unique nodes it finds, what follows a ':' (a property's) left aside. Unknown for an absolute path, one above the
    /// tree's root, or one into a placeholder's nodes or those of a scene no text says (<see cref="GodotTreeNode.NodesUnread"/>).</summary>
    public static GodotResolved Resolve(GodotTreeNode from, string path)
    {
        List<(int, int, GodotTreeNode)> steps = [];
        int colon = path.IndexOf(':');
        string names = colon >= 0 ? path[..colon] : path;
        if (names.StartsWith('/'))
        {
            return new GodotResolved(null, steps, null, Unknown: true);
        }
        GodotTreeNode at = from;
        int start = 0;
        while (start <= names.Length)
        {
            int slash = names.IndexOf('/', start);
            int end = slash < 0 ? names.Length : slash;
            string name = names[start..end];
            if (name.Length == 0 || name == ".")
            {
                // An empty path or a "." is the node itself.
            }
            else if (name == "..")
            {
                if (at.Parent is not { } parent)
                {
                    return new GodotResolved(null, steps, null, Unknown: true);
                }
                at = parent;
                steps.Add((start, end, at));
            }
            else if (at.Placeholder)
            {
                return new GodotResolved(null, steps, null, Unknown: true);
            }
            else if ((name.StartsWith('%') ? at.UniqueNamed(name[1..]) : at.Child(name)) is { } next)
            {
                at = next;
                steps.Add((start, end, at));
            }
            else if (at.NodesUnread)
            {
                return new GodotResolved(null, steps, null, Unknown: true);
            }
            else
            {
                return new GodotResolved(null, steps, (start, end), Unknown: false);
            }
            if (slash < 0)
            {
                break;
            }
            start = slash + 1;
        }
        return new GodotResolved(at, steps, null, Unknown: false);
    }

    /// <summary>The paths a node can name from it, each with the node it names: every node below it by its path, and the
    /// unique nodes it finds by their <c>%Name</c>.</summary>
    public static IReadOnlyList<(string Path, GodotTreeNode Node)> PathsFrom(GodotTreeNode from)
    {
        List<(string, GodotTreeNode)> paths = [];
        foreach (GodotTreeNode below in from.Below().Skip(1))
        {
            paths.Add((RelativePath(from, below), below));
        }
        foreach (GodotTreeNode node in from.Root.Below().Where(n => n.Unique && (n.Owner == from || n.Owner == from.Owner && from.Owner is not null)))
        {
            paths.Add(("%" + node.Name, node));
        }
        return paths;
    }

    /// <summary>The path of a node below another, from it.</summary>
    public static string RelativePath(GodotTreeNode from, GodotTreeNode below)
    {
        List<string> names = [];
        for (GodotTreeNode? at = below; at is not null && at != from; at = at.Parent)
        {
            names.Add(at.Name);
        }
        names.Reverse();
        return string.Join('/', names);
    }

    /// <summary>Why a node may not take a name, else null: an empty one, one Godot refuses a character of, or one a node beside
    /// it in any scene that holds it has already.</summary>
    public string? RenameProblem(GodotTreeNode node, string name)
    {
        if (name.Length == 0 || name.Trim() != name)
        {
            return $"'{name}' is no node's name";
        }
        if (name.FirstOrDefault(c => Unnamable.Contains(c)) is var refused and not '\0')
        {
            return $"A node's name never holds '{refused}', so {node.Name} keeps its name";
        }
        foreach ((string scene, GodotTreeNode copy) in CopiesOf(node))
        {
            if (copy.Parent?.Children.FirstOrDefault(c => c != copy && c.Name == name) is { } beside)
            {
                return $"{copy.Parent.Name} has a node named {name} already, in {System.IO.Path.GetFileName(beside.Declared.Scene)}";
            }
        }
        return null;
    }

    /// <summary>Every tree's copy of a node: the scene of each tree and the node there declared by the same section.</summary>
    public IReadOnlyList<(string Scene, GodotTreeNode Node)> CopiesOf(GodotTreeNode node)
    {
        List<(string, GodotTreeNode)> found = [];
        foreach (string scene in Files.Keys.Order(StringComparer.Ordinal))
        {
            if (Files[scene].Header?.Tag == "gd_scene" && Tree(scene) is { } root)
            {
                found.AddRange(root.Below().Where(n => n.Same(node)).Select(n => (scene, n)));
            }
        }
        return found;
    }

    /// <summary>The edits each scene takes as a node is renamed: its <c>name</c> where it is declared and where a scene over
    /// that one changes it, and in every scene holding it the paths through it (a node's <c>parent</c>, a connection's <c>from</c> and <c>to</c>,
    /// an <c>[editable]</c> path and a NodePath value relative to the node it is set on). By res:// path.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Renames(GodotTreeNode node, string name)
    {
        Dictionary<string, List<TextEdit>> edits = new(StringComparer.Ordinal);
        (string declaring, GodotNode declared) = node.Declared;
        if (declared.Section.Attribute("name")?.Value is { Plain: true } written)
        {
            Edit(declaring, new TextEdit(written.TextStart, written.TextEnd - written.TextStart, name));
        }
        foreach ((string scene, GodotTreeNode copy) in CopiesOf(node))
        {
            // A scene over the one declaring it names it again where it changes it (an instance's editable children).
            foreach ((string over, GodotNode changing) in copy.Written.Skip(1).Where(w => w.Scene == scene))
            {
                if (changing.Section.Attribute("name")?.Value is { Plain: true } again)
                {
                    Edit(over, new TextEdit(again.TextStart, again.TextEnd - again.TextStart, name));
                }
            }
            string through = copy.Path;
            GodotFile file = Files[scene];
            foreach (GodotSection section in file.Sections)
            {
                foreach (string key in section.Tag switch { "node" => ["parent"], "connection" => ["from", "to"], "editable" => ["path"], _ => Array.Empty<string>() })
                {
                    if (section.Attribute(key)?.Value is { Plain: true } value && Segment(value.Text!, through) is { } at)
                    {
                        Edit(scene, new TextEdit(value.TextStart + at.Start, at.Length, name));
                    }
                }
                if (section.Tag == "node" && new GodotNode(section) is var holder)
                {
                    foreach (GodotEntry property in section.Properties)
                    {
                        if (property.Value is { Call: "NodePath", Plain: true } path && Through(holder.PathInScene, path.Text!, through) is { } at)
                        {
                            Edit(scene, new TextEdit(path.TextStart + at.Start, at.Length, name));
                        }
                    }
                }
            }
        }
        return edits.ToDictionary(e => e.Key, e => (IReadOnlyList<TextEdit>)[.. e.Value.Distinct().OrderBy(t => t.Start)], StringComparer.Ordinal);

        void Edit(string scene, TextEdit edit)
        {
            if (!edits.TryGetValue(scene, out List<TextEdit>? list))
            {
                edits[scene] = list = [];
            }
            list.Add(edit);
        }
    }

    /// <summary>A res:// path once files and folders moved (each by its res:// path, a folder's ending in '/'): a moved file's
    /// new path, a path inside a moved folder under its new one, a sub-resource's "::" kept; null for a path none moved.</summary>
    public static string? Moved(IReadOnlyList<(string From, string To)> moves, string res)
    {
        int sub = res.IndexOf("::", StringComparison.Ordinal);
        string file = sub < 0 ? res : res[..sub];
        foreach ((string from, string to) in moves)
        {
            if (from.EndsWith('/') ? file.StartsWith(from, StringComparison.Ordinal) : file == from)
            {
                return (from.EndsWith('/') ? to + file[from.Length..] : to) + (sub < 0 ? string.Empty : res[sub..]);
            }
        }
        return null;
    }

    /// <summary>The edits a Godot text file (a scene, a resource, project.godot, an .import) takes as files and folders move
    /// (<see cref="Moved"/>): each string it gives a res:// path in that names one moved, an external resource's path, a
    /// property's, an autoload's after its '*'.</summary>
    public static IReadOnlyList<TextEdit> MovedEdits(GodotFile file, IReadOnlyList<(string From, string To)> moves)
    {
        List<TextEdit> edits = [];
        foreach (GodotSection section in file.Sections)
        {
            foreach (GodotEntry entry in section.Attributes.Concat(section.Properties))
            {
                if (entry.Value is not { Call: null, Plain: true, Text: { } text } value)
                {
                    continue;
                }
                int star = text.StartsWith("*res://", StringComparison.Ordinal) ? 1 : 0;
                if ((star == 1 || text.StartsWith("res://", StringComparison.Ordinal)) && Moved(moves, text[star..]) is { } now)
                {
                    edits.Add(new TextEdit(value.TextStart + star, value.TextEnd - value.TextStart - star, now));
                }
            }
        }
        return edits;
    }

    /// <summary>Where a path from the scene's root ("Hud/Score/Label", "." for the root) names the last name of another
    /// (<paramref name="through"/>, "Hud/Score"): that name's place in it; null where it does not lead through it.</summary>
    internal static (int Start, int Length)? Segment(string path, string through)
    {
        if (through == "." || path == ".")
        {
            return null;
        }
        if (path == through || path.StartsWith(through + "/", StringComparison.Ordinal))
        {
            int last = through.LastIndexOf('/') + 1;
            return (last, through.Length - last);
        }
        return null;
    }

    /// <summary>Where a NodePath set on a node (at <paramref name="holder"/> from the root) names the last name of a path
    /// from the root: that name's place in it; null where it does not lead through it.</summary>
    internal static (int Start, int Length)? Through(string holder, string path, string through)
    {
        int colon = path.IndexOf(':');
        string names = colon >= 0 ? path[..colon] : path;
        if (names.StartsWith('/') || through == ".")
        {
            return null;
        }
        List<string> at = holder == "." ? [] : [.. holder.Split('/')];
        string[] target = through.Split('/');
        int start = 0;
        foreach (string name in names.Split('/'))
        {
            if (name == "..")
            {
                if (at.Count == 0)
                {
                    return null;
                }
                at.RemoveAt(at.Count - 1);
            }
            else if (name.Length > 0 && name != ".")
            {
                at.Add(name);
                if (at.Count == target.Length && at.SequenceEqual(target))
                {
                    return (start, name.Length);
                }
            }
            start += name.Length + 1;
        }
        return null;
    }

    private GodotTreeNode? Built(string scene, List<string> building)
    {
        if (trees.TryGetValue(scene, out GodotTreeNode? kept))
        {
            return kept;
        }
        if (!Files.TryGetValue(scene, out GodotFile? file) || building.Contains(scene) || building.Count >= Deepest)
        {
            return null;
        }
        building.Add(scene);
        GodotTreeNode? root = null;
        foreach (GodotNode node in file.Nodes)
        {
            GodotTreeNode? parent = node.Parent is { } path ? root is null ? null : Resolve(root, path).Found : null;
            if (node.Parent is not null && parent is null)
            {
                // A node under one the scene does not have: Godot drops it too.
                continue;
            }
            if (node.Parent is null && root is not null)
            {
                continue;
            }
            GodotTreeNode? existing = parent?.Child(node.Name);
            if (existing is not null && node.Instance is null && node.Type is null)
            {
                existing.Take(scene, node, file, declares: false);
                continue;
            }
            GodotTreeNode made;
            if (file.External(node.Instance)?.Path is { } instanced && Built(instanced, building) is { } sub)
            {
                made = GodotTreeNode.Copy(sub, parent, root, scene, node, instanced);
            }
            else
            {
                made = new GodotTreeNode(node.Name, parent, (scene, node)) { NodesUnread = node.Instance is not null, Instance = file.External(node.Instance)?.Path };
                made.Own(root);
            }
            made.Take(scene, node, file, declares: true);
            if (parent is null)
            {
                root = made;
            }
            else
            {
                parent.Add(made);
            }
        }
        building.Remove(scene);
        trees[scene] = root;
        return root;
    }
}
