using System.Buffers.Binary;
using System.Security.Principal;
using System.Text;

namespace Passenger.Core;

/// <summary>Windows' Recycle Bin as it lies on a volume: under <c>$Recycle.Bin</c>, a folder for each user, where each
/// item deleted is renamed <c>$R…</c> beside a record <c>$I…</c> of the same ending, which holds the path it was deleted
/// from. The shell's delete says nothing of where it put an item, so it is found among the records made meanwhile.</summary>
public static class RecycleBin
{
    private const string RecordStart = "$I";

    private const string ItemStart = "$R";

    /// <summary>How the user's own folder of the bin is listed: every entry, hidden and system ones too.</summary>
    private static readonly EnumerationOptions Listing = new() { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };

    /// <summary>The names of the records in the user's folder of the Recycle Bin on a path's volume; none where it has no
    /// bin or it does not read.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static HashSet<string> Records(string path)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (FolderOf(path) is not { } bin)
        {
            return names;
        }
        try
        {
            foreach (string record in Directory.EnumerateFiles(bin, RecordStart + "*", Listing))
            {
                names.Add(Path.GetFileName(record));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A bin that does not read keeps nothing Passenger can find.
        }
        return names;
    }

    /// <summary>Where the Recycle Bin put an item deleted from a path: the item and its record, among those not in
    /// <paramref name="before"/> (<see cref="Records"/> as the delete began); null where it made none (an item deleted
    /// outright, a volume without a bin).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static (string Item, string Record)? Find(string path, IReadOnlySet<string> before)
    {
        if (FolderOf(path) is not { } bin)
        {
            return null;
        }
        foreach (string name in Records(path).Where(n => !before.Contains(n)))
        {
            string record = Path.Join(bin, name);
            string item = Path.Join(bin, ItemStart + name[RecordStart.Length..]);
            try
            {
                if (DeletedFrom(File.ReadAllBytes(record)) is { } from && PathNames.Same(from, path)
                    && (File.Exists(item) || Directory.Exists(item)))
                {
                    return (item, record);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Another program's record, taken as it was read: never the item this delete put there.
            }
        }
        return null;
    }

    /// <summary>The path a record says its item was deleted from: the second form (Windows 10 on) a count of its characters
    /// after the item's size and the time it went, the first a field of 260; null for a record not written so.</summary>
    public static string? DeletedFrom(ReadOnlySpan<byte> record)
    {
        const int Named = 24;
        if (record.Length < Named)
        {
            return null;
        }
        ReadOnlySpan<byte> written;
        switch (BinaryPrimitives.ReadInt64LittleEndian(record))
        {
            case 2 when record.Length >= Named + sizeof(int):
                int characters = BinaryPrimitives.ReadInt32LittleEndian(record[Named..]);
                int start = Named + sizeof(int);
                if (characters <= 0 || characters > (record.Length - start) / 2)
                {
                    return null;
                }
                written = record.Slice(start, characters * 2);
                break;
            case 1:
                written = record[Named..Math.Min(record.Length, Named + 260 * 2)];
                break;
            default:
                return null;
        }
        string text = Encoding.Unicode.GetString(written);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        string path = end >= 0 ? text[..end] : text;
        return path.Length == 0 ? null : path;
    }

    /// <summary>The user's own folder of the bin on a path's volume; null where it is not there.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FolderOf(string path)
    {
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        if (Path.GetPathRoot(Path.GetFullPath(path)) is not { Length: > 0 } root || user.User?.Value is not { } id)
        {
            return null;
        }
        string bin = Path.Join(root, "$Recycle.Bin", id);
        return Directory.Exists(bin) ? bin : null;
    }
}
