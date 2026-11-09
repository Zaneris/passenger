using Passenger.Workspace.GDScript;

namespace Passenger.Workspace;

/// <summary>The cache of a project's global classes Godot's editor writes as it scans, where a run without the editor finds
/// each <c>class_name</c>'s script: a ConfigFile whose <c>list</c> holds a dictionary for each class (its <c>class</c>,
/// <c>base</c>, <c>path</c>, <c>language</c>, <c>icon</c>, <c>is_abstract</c> and <c>is_tool</c>, as Godot 4.7 writes them),
/// read through <see cref="GodotFile"/>; and whether it still lists what a project's GDScript files declare, each read as
/// Godot's editor reads one for the list. Pure.</summary>
public static class GodotClassCache
{
    /// <summary>The file's path from its project's folder.</summary>
    public static readonly string RelativePath = Path.Combine(".godot", "global_script_class_cache.cfg");

    /// <summary>The base Godot lists for a class that extends nothing.</summary>
    private const string Unextended = "RefCounted";

    /// <summary>The most scripts a class's base is followed through by path, past which a chain is taken to loop.</summary>
    private const int Deepest = 64;

    /// <summary>A class as the cache lists it: its name, the base Godot read for it (null where Passenger cannot tell what
    /// Godot would), and its script's res:// path.</summary>
    public sealed record Entry(string Class, string? Base, string Path);

    /// <summary>The classes a cache's text lists in its order; an entry without its class or path passed over.</summary>
    public static IReadOnlyList<Entry> Read(string text)
    {
        GodotFile file = GodotFile.Parse(text);
        if (file.Untagged.LastOrDefault(e => e.Key == "list") is not { } list)
        {
            return [];
        }
        List<Entry> entries = [];
        foreach (GodotValue item in file.ItemsOf(list.Value))
        {
            IReadOnlyList<GodotEntry> fields = file.EntriesOf(item);
            if (Field("class") is { Length: > 0 } named && Field("path") is { Length: > 0 } path)
            {
                entries.Add(new Entry(named, Field("base") ?? string.Empty, path));
            }

            string? Field(string key) => fields.LastOrDefault(f => f.Key == key)?.Value.Text;
        }
        return entries;
    }

    /// <summary>The class a GDScript file at a res:// path declares, as Godot's editor lists it; null where it declares no
    /// <c>class_name</c>. <paramref name="read"/> reads another script it extends by path (null where it cannot).</summary>
    public static Entry? Declared(string path, string text, Func<string, string?> read)
    {
        GDScriptClass head = GDScriptTree.Preamble(text);
        return head.Identifier is { } named ? new Entry(named.Name, BaseOf(head, read, 0), path) : null;
    }

    /// <summary>Whether a project's global classes need Godot's import before a run without its editor reads them. With no
    /// cache (<paramref name="cache"/> null), where one of <paramref name="scripts"/> (every GDScript file Godot's editor
    /// scans) declares a <c>class_name</c>; else where one of them (those newer than the cache) declares a class other than
    /// the cache lists for its path (its name, its base, or none), or the cache lists a script <paramref name="exists"/>
    /// finds gone. A body's edit asks for none. Scripts are named by res:// path, read by <paramref name="read"/>.</summary>
    public static bool Stale(string? cache, IEnumerable<string> scripts, Func<string, string?> read, Func<string, bool> exists)
    {
        if (cache is null)
        {
            return scripts.Any(script => read(script) is { } text && GDScriptTree.Preamble(text).Identifier is not null);
        }
        IReadOnlyList<Entry> listed = Read(cache);
        Dictionary<string, Entry> byPath = new(StringComparer.Ordinal);
        foreach (Entry entry in listed)
        {
            byPath[entry.Path] = entry;
        }
        foreach (string script in scripts)
        {
            Entry? declared = read(script) is { } text ? Declared(script, text, read) : null;
            if (declared != byPath.GetValueOrDefault(script) || declared is { Base: null })
            {
                return true;
            }
        }
        return listed.Any(entry => !exists(entry.Path));
    }

    /// <summary>The base Godot lists for a class (its GDScript's get_global_class_name): RefCounted where it extends nothing,
    /// the one name it extends, else, extending a script by path, what that script extends, or an inner class of it found by
    /// name; empty where Godot finds none, null where a chain of scripts goes on past <see cref="Deepest"/>.</summary>
    private static string? BaseOf(GDScriptClass declared, Func<string, string?> read, int depth)
    {
        GDScriptClass? at = declared;
        while (at is not null)
        {
            if (depth > Deepest)
            {
                return null;
            }
            if (!at.ExtendsUsed)
            {
                return Unextended;
            }
            if (at.ExtendsPath is not { } written)
            {
                return at.Extends.Count == 1 ? at.Extends[0].Name : string.Empty;
            }
            // Godot opens the path as written, so a relative one from the project's folder, where its editor runs.
            string target = GodotResPaths.Resolved(written, ProjectTop);
            if (at.Extends.Count == 0)
            {
                return read(target) is { } extended ? BaseOf(GDScriptTree.Preamble(extended), read, depth + 1) : string.Empty;
            }
            if (read(target) is not { Length: > 0 } holding || GDScriptTree.Parse(holding) is not { Errors.Count: 0 } tree)
            {
                return string.Empty;
            }
            at = InnerOf(tree, at.Extends);
            depth++;
        }
        return string.Empty;
    }

    /// <summary>A file at a project's top, from which a relative path names what it names from the project's folder.</summary>
    private static readonly string ProjectTop = GodotResPaths.Scheme + GodotProject.FileName;

    /// <summary>The inner class a chain of names reaches in a script, each among the members of the one before; null where
    /// one is not there.</summary>
    private static GDScriptClass? InnerOf(GDScriptTree tree, IReadOnlyList<GDScriptIdentifier> names)
    {
        IEnumerable<GDScriptNode> members = tree.Members.Select(m => m.Node);
        GDScriptClass? found = null;
        foreach (GDScriptIdentifier name in names)
        {
            found = members.OfType<GDScriptClass>().FirstOrDefault(c => c.Identifier?.Name == name.Name);
            if (found is null)
            {
                return null;
            }
            members = found.Members;
        }
        return found;
    }
}
