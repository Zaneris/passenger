using Passenger.Core;

namespace Passenger.Text;

/// <summary>What the .editorconfig files over a folder's files set, key by key: those in the folder and below it, as the
/// caller lists them, then those above it in the person's folders (<see cref="FoldersUp"/>) up to the first marked
/// <c>root = true</c> (none where the folder's own is), each read through <see cref="EditorConfig"/>, each file's keys in
/// its own order. Which files a key is set for is its section's glob's to say (<see cref="SetKey.Concerns"/>).</summary>
public sealed class EditorConfigFolder
{
    /// <summary>Nothing set: no folder, or no .editorconfig over it.</summary>
    public static readonly EditorConfigFolder None = new(string.Empty, []);

    private EditorConfigFolder(string folder, IReadOnlyList<SetKey> keys)
    {
        Folder = folder;
        Keys = keys;
    }

    /// <summary>The folder, canonical.</summary>
    public string Folder { get; }

    public IReadOnlyList<SetKey> Keys { get; }

    /// <summary>Reads the .editorconfig files at the canonical paths <paramref name="under"/> the folder lists, then those
    /// above it.</summary>
    public static EditorConfigFolder Read(string folder, IEnumerable<string> under)
    {
        List<SetKey> keys = [];
        bool rooted = false;
        foreach (string file in under)
        {
            if (EditorConfig.WrittenIn(file) is { } written)
            {
                keys.AddRange(written.Pairs.Select(p => new SetKey(file, p.Glob, p.Line, p.Key, p.Value)));
                rooted |= written.Root && PathNames.Same(Path.GetDirectoryName(file) ?? file, folder);
            }
        }
        foreach (string above in !rooted && Path.GetDirectoryName(folder) is { } parent ? FoldersUp.From(parent) : [])
        {
            string file = Path.Join(above, EditorConfig.FileName);
            if (EditorConfig.WrittenIn(file) is { } written)
            {
                keys.AddRange(written.Pairs.Select(p => new SetKey(file, p.Glob, p.Line, p.Key, p.Value)));
                if (written.Root)
                {
                    break;
                }
            }
        }
        return new EditorConfigFolder(folder, keys);
    }
}

/// <summary>A key a section of an .editorconfig sets: the file's canonical path, the section's glob and the key's value as
/// written, the line the key is on (from 1) and the key in lower case.</summary>
public sealed record SetKey(string File, string Glob, int Line, string Key, string Value)
{
    /// <summary>Whether its section says anything of the files a setting is for: one of theirs by its canonical path under
    /// the .editorconfig's folder, or a name of theirs standing in that folder (<c>file.gd</c>), which says it for a folder
    /// with none of them yet.</summary>
    public bool Concerns(IEnumerable<string> names, IEnumerable<string> files)
    {
        string folder = Path.GetDirectoryName(File) ?? File;
        return names.Any(name => EditorConfig.Matches(Glob, "/" + name))
            || files.Any(path => PathNames.IsUnder(path, folder) && EditorConfig.Matches(Glob, "/" + PathNames.Relative(path, folder)));
    }

    /// <summary>What it sets as a person reads it, the file by its way from <paramref name="folder"/>:
    /// "./.editorconfig sets max_line_length = 120 for *.gd"; one taking the key back unsets it.</summary>
    public string Said(string folder)
    {
        string way = PathNames.Route(File, folder);
        string file = way.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(way) ? way : "./" + way;
        return EditorConfig.Word(Value) == "unset" ? $"{file} unsets {Key} for {Glob}" : $"{file} sets {Key} = {Value} for {Glob}";
    }
}
