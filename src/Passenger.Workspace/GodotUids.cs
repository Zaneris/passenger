using System.Buffers.Binary;
using System.Text;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>Godot's uid:// names of a project's files as Godot keeps them: in a scene's or resource's header
/// (<c>uid="uid://…"</c>), in the <c>.uid</c> file beside a file that has no header of its own (a script, a shader), in the
/// <c>.import</c> file beside an imported one, and in the cache Godot's editor writes of them all
/// (<c>.godot/uid_cache.bin</c>: a count, then each entry's 64-bit id, its path's length and its UTF-8 path). An id is
/// written as Godot writes it, in base 34 of the letters a to y and the digits 0 to 8. Pure.</summary>
public static partial class GodotUids
{
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

    /// <summary>A new id as Godot's editor makes one, 63 random bits, written as it writes it.</summary>
    public static string Made(Random random) => Text(random.NextInt64())!;

    /// <summary>The texts each file of a copy in a Godot project takes as Godot's own duplicate leaves them, no copy giving its
    /// original's uid: each uid made anew in a header, a .uid and an .import's [remap] (but one Godot keeps or skips a file by);
    /// in a folder's copy within its project (<paramref name="within"/>), each external resource naming a file copied with it,
    /// by a uid that file gave (one <paramref name="named"/> by another file never) or else by its res:// path, written to name
    /// the copy and its uid. Each comes back with the texts it takes.</summary>
    public static IReadOnlyList<GodotCopy> Copied(IReadOnlyList<GodotCopy> files, bool within, Func<string, bool> named, Random random)
    {
        // Where each file keeps its uid, as Godot looks: an .import first, else its own header, else its .uid.
        List<(GodotCopy File, string? Uid, string? Made)> uids = [];
        foreach (GodotCopy file in files)
        {
            GodotSection? remap = file.Import is { } import ? GodotFile.Parse(import).Sections.FirstOrDefault(s => s.Tag == "remap") : null;
            string? uid = file.Import is not null ? remap?.Property("uid")?.Value.Text
                : file.Text is { } text ? GodotFile.Parse(text).Header?.Text("uid")
                : file.Uid is { } beside ? OfBeside(file.From + ".uid", beside)
                : null;
            bool kept = remap?.Text("importer") is "keep" or "skip";
            uids.Add((file, uid, uid is not null && uid.StartsWith(Scheme, StringComparison.Ordinal) && !kept ? Made(random) : null));
        }
        Dictionary<string, (string To, string? Uid)> copies = new(StringComparer.Ordinal);
        Dictionary<string, string> gave = new(StringComparer.Ordinal);
        foreach ((GodotCopy file, string? uid, string? made) in uids)
        {
            copies[file.From] = (file.To, made);
            if (made is not null)
            {
                gave.TryAdd(uid!, file.From);
            }
        }
        return [.. uids.Select(u => u.File with
        {
            Text = u.File.Text is { } text ? Edited(text, [.. Given(GodotFile.Parse(text).Header?.Attribute("uid")?.Value, u.File.Import is null ? u.Made : null),
                .. within ? Naming(GodotFile.Parse(text), copies, gave, named) : []]) : null,
            Uid = u.File.Uid is { } beside && u.File.Import is null && u.File.Text is null && u.Made is { } made ? Edited(beside, [.. Beside(beside, u.Uid!, made)]) : u.File.Uid,
            Import = u.File.Import is { } import && u.Made is { } given
                ? Edited(import, [.. Given(GodotFile.Parse(import).Sections.FirstOrDefault(s => s.Tag == "remap")?.Property("uid")?.Value, given)]) : u.File.Import,
        })];

        static IEnumerable<TextEdit> Given(GodotValue? value, string? made) =>
            made is not null && value is { Plain: true } ? [new TextEdit(value.TextStart, value.TextEnd - value.TextStart, made)] : [];

        static IEnumerable<TextEdit> Beside(string text, string uid, string made) => [new TextEdit(text.IndexOf(uid, StringComparison.Ordinal), uid.Length, made)];

        static string Edited(string text, IReadOnlyList<TextEdit> edits) => edits.Count == 0 ? text : TextSnapshot.From(text).Apply([.. edits.OrderBy(e => e.Start)]).ToString();
    }

    /// <summary>The edits a copied scene or resource takes for each external resource naming a file copied with it, as Godot
    /// resolves one: by its uid where a file copied gave it, not at all where another file of the project gives it, else by
    /// its res:// path (a relative one names a file beside the copy already).</summary>
    private static IEnumerable<TextEdit> Naming(GodotFile file, IReadOnlyDictionary<string, (string To, string? Uid)> copies,
        IReadOnlyDictionary<string, string> gave, Func<string, bool> named)
    {
        foreach (GodotExternal external in file.Externals)
        {
            GodotValue? path = external.Section.Attribute("path")?.Value;
            GodotValue? uid = external.Section.Attribute("uid")?.Value;
            string? from = uid?.Text is { } written && gave.TryGetValue(written, out string? copied) ? copied
                : uid?.Text is { } other && named(other) ? null
                : path?.Text is { } at && at.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal) ? GodotResPaths.Resolved(at, null)
                : null;
            if (from is null || !copies.TryGetValue(from, out (string To, string? Uid) copy))
            {
                continue;
            }
            if (path is { Plain: true })
            {
                yield return new TextEdit(path.TextStart, path.TextEnd - path.TextStart, GodotWrites.Escaped(copy.To));
            }
            if (uid is { Plain: true } && copy.Uid is { } made)
            {
                yield return new TextEdit(uid.TextStart, uid.TextEnd - uid.TextStart, made);
            }
        }
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

/// <summary>A file a copy made in a Godot project, as Godot's own duplicate takes it: its res:// path before
/// (<paramref name="From"/>) and as copied (<paramref name="To"/>), and the texts its uid is kept in, each null where it has
/// none: its own (a scene's or resource's), its .uid file's and its .import file's.</summary>
public sealed record GodotCopy(string From, string To, string? Text, string? Uid, string? Import);
