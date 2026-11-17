using System.Security.Cryptography;
using System.Text;
using Passenger.Core;

namespace Passenger.App.Chat;

/// <summary>A file the agent asks to make, shown before it is written: a copy of what it would hold, under Passenger's
/// cache, opened read-only in the editor with every line marked added, as Cursor shows a new file; Accept has the agent
/// write the file itself.</summary>
public static class ChatProposals
{
    private static (string Root, string Folder)? known;

    /// <summary>The folder the copies are written in, as the file system names it.</summary>
    public static string Folder
    {
        get
        {
            string root = State.StateStore.DefaultRoot;
            if (known is not { } kept || kept.Root != root)
            {
                known = kept = (root, PathNames.Canonical(Path.Combine(root, "cache", "proposals")));
            }
            return kept.Folder;
        }
    }

    /// <summary>Whether a file is a copy of a file the agent asks to make, which the editor shows read-only.</summary>
    public static bool IsCopy(string path) => PathNames.IsUnder(path, Folder);

    /// <summary>Writes what a file the agent asks to make would hold to its copy, named as the file, in a folder of the
    /// file's path's own so two of one name stay apart; the copy's path.</summary>
    public static string Write(string path, string text)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PathNames.Canonical(path))))[..12];
        string copy = Path.Combine(Folder, key, Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        AtomicFile.Write(copy, Encoding.UTF8.GetBytes(text));
        return PathNames.Canonical(copy);
    }
}
