using System.Security.Cryptography;
using System.Text;

namespace Passenger.Core;

/// <summary>Names Passenger makes for the files and folders it keeps: a text's short hash, which every process and version
/// works out alike (never HashCode's, seeded anew in each process), and a name every file system takes.</summary>
public static class FileNames
{
    /// <summary>The first <paramref name="length"/> lower-case hex digits of the SHA-256 of <paramref name="text"/>'s UTF-8.
    /// A name kept on disk keeps its length, or what was kept under it is lost.</summary>
    public static string Hash(string text, int length) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];

    /// <summary>A name made one every file system takes: each character a file name may not hold on one made '_', and "."
    /// and ".." too.</summary>
    public static string Safe(string name)
    {
        string made = string.Concat(name.Select(c => c < ' ' || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c));
        return made is "" or "." or ".." ? "_" : made;
    }
}
