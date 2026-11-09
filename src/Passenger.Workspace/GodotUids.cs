using System.Buffers.Binary;
using System.Text;

namespace Passenger.Workspace;

/// <summary>Godot's uid:// names of a project's files as Godot keeps them: in a scene's or resource's header
/// (<c>uid="uid://…"</c>), in the <c>.uid</c> file beside a file that has no header of its own (a script, a shader), in the
/// <c>.import</c> file beside an imported one, and in the cache Godot's editor writes of them all
/// (<c>.godot/uid_cache.bin</c>: a count, then each entry's 64-bit id, its path's length and its UTF-8 path). An id is
/// written as Godot writes it, in base 34 of the letters a to y and the digits 0 to 8. Pure.</summary>
public static class GodotUids
{
    public const string Scheme = "uid://";

    /// <summary>The cache's file under a project's folder.</summary>
    public static readonly string CacheFile = Path.Combine(".godot", "uid_cache.bin");

    private const int Letters = 'z' - 'a';
    private const int Base = Letters + ('9' - '0');

    /// <summary>Whether a file is one Godot keeps beside another for its uid: a <c>.uid</c> or an <c>.import</c>.</summary>
    public static bool IsBeside(string path) =>
        path.EndsWith(".uid", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".import", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file a <c>.uid</c> or <c>.import</c> file stands beside.</summary>
    public static string BesideOf(string path) => path[..path.LastIndexOf('.')];

    /// <summary>An id as Godot writes it (uid://…); null for one Godot takes for none.</summary>
    public static string? Text(long id)
    {
        if (id < 0)
        {
            return null;
        }
        StringBuilder written = new();
        ulong left = (ulong)id;
        do
        {
            int digit = (int)(left % Base);
            written.Insert(0, digit < Letters ? (char)('a' + digit) : (char)('0' + digit - Letters));
            left /= Base;
        }
        while (left > 0);
        return Scheme + written;
    }

    /// <summary>The uid a <c>.uid</c> file or an <c>.import</c> file gives the file beside it; null where it gives none.</summary>
    public static string? OfBeside(string path, string text)
    {
        if (path.EndsWith(".uid", StringComparison.OrdinalIgnoreCase))
        {
            string first = text.Trim().Split('\n')[0].Trim();
            return first.StartsWith(Scheme, StringComparison.Ordinal) ? first : null;
        }
        return GodotFile.Parse(text).Sections.FirstOrDefault(s => s.Tag == "remap")?.Property("uid")?.Value.Text is { } uid && uid.StartsWith(Scheme, StringComparison.Ordinal)
            ? uid : null;
    }

    /// <summary>The cache's entries, each uid with the res:// path it names; those read before a cut short end are kept.</summary>
    public static IReadOnlyList<(string Uid, string Res)> ReadCache(ReadOnlySpan<byte> bytes)
    {
        List<(string, string)> entries = [];
        if (bytes.Length < 4)
        {
            return entries;
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        int at = 4;
        for (uint i = 0; i < count && at + 12 <= bytes.Length; i++)
        {
            long id = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]), int.MaxValue);
            at += 12;
            if (length > bytes.Length - at)
            {
                break;
            }
            string res = Encoding.UTF8.GetString(bytes.Slice(at, length)).TrimEnd('\0');
            at += length;
            if (Text(id) is { } uid && res.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal))
            {
                entries.Add((uid, res));
            }
        }
        return entries;
    }
}
