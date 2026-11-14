using System.Globalization;
using System.Text;

namespace Passenger.Vcs;

/// <summary>Moves of files and folders made in an index as git mv makes them, planned from what the index holds: each
/// entry at a moved path, or below a moved folder, goes where the move takes it with its mode and object, so its staged
/// content goes with it and what the disk has beyond that stays unstaged. Moves are taken in order, a later one moving
/// what an earlier one brought; one over a path in conflict leaves its entries where they are.</summary>
internal static class IndexMoves
{
    /// <summary>What <c>git update-index -z --index-info</c> reads to make <paramref name="moves"/> (paths from the tree's
    /// top, as the index names them) in an index <c>git ls-files --stage -z</c> wrote as <paramref name="index"/>: each path
    /// that goes taken out, then each that comes given its entry. Empty where no move takes an entry.</summary>
    public static string Plan(ReadOnlySpan<byte> index, IReadOnlyList<(string From, string To)> moves)
    {
        HashSet<string> sources = new(moves.Select(m => m.From), StringComparer.Ordinal);
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> moved = sources.GetAlternateLookup<ReadOnlySpan<char>>();
        // The entries the moves may take, by path, as the index has them; and the paths held in conflict.
        Dictionary<string, (string Mode, string Object)> before = new(StringComparer.Ordinal);
        List<string> conflicted = [];
        char[] chars = new char[256];
        while (!index.IsEmpty)
        {
            int end = index.IndexOf((byte)0);
            ReadOnlySpan<byte> entry = end < 0 ? index : index[..end];
            index = end < 0 ? [] : index[(end + 1)..];
            // "<mode> <object> <stage>\t<path>"
            int tab = entry.IndexOf((byte)'\t');
            if (tab < 0)
            {
                continue;
            }
            ReadOnlySpan<byte> name = entry[(tab + 1)..];
            if (chars.Length < name.Length)
            {
                chars = new char[name.Length * 2];
            }
            int length = Encoding.UTF8.GetChars(name, chars);
            if (!Under(chars.AsSpan(0, length), moved))
            {
                continue;
            }
            string[] fields = Encoding.ASCII.GetString(entry[..tab]).Split(' ');
            if (fields.Length != 3)
            {
                continue;
            }
            string path = new(chars, 0, length);
            if (fields[2] != "0")
            {
                conflicted.Add(path);
                continue;
            }
            before[path] = (fields[0], fields[1]);
        }
        Dictionary<string, (string Mode, string Object)> after = new(before, StringComparer.Ordinal);
        // In order, so a folder's entries are found together however many moves there are.
        SortedSet<string> held = new(before.Keys, StringComparer.Ordinal);
        foreach ((string from, string to) in moves)
        {
            if (conflicted.Any(path => Within(path, from)))
            {
                continue;
            }
            List<string> going = [.. held.GetViewBetween(from + "/", from + "/\uffff").Where(path => Within(path, from))];
            if (held.Contains(from))
            {
                going.Add(from);
            }
            List<(string Path, (string Mode, string Object) Entry)> coming = [.. going.Select(path => (to + path[from.Length..], after[path]))];
            foreach (string path in going)
            {
                after.Remove(path);
                held.Remove(path);
            }
            foreach ((string path, (string Mode, string Object) entry) in coming)
            {
                after[path] = entry;
                held.Add(path);
            }
        }
        StringBuilder plan = new();
        foreach ((string path, (string mode, string id)) in before.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            // Mode 0 takes a path out of the index, as update-index reads it.
            if (!after.ContainsKey(path))
            {
                plan.Append(CultureInfo.InvariantCulture, $"0 {new string('0', id.Length)}\t{path}\0");
            }
        }
        foreach ((string path, (string mode, string id)) in after.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(path, out (string Mode, string Object) was) || was != (mode, id))
            {
                plan.Append(CultureInfo.InvariantCulture, $"{mode} {id}\t{path}\0");
            }
        }
        return plan.ToString();
    }

    /// <summary>Whether a path is one of the moved paths or lies below one, each folder above it looked up once.</summary>
    private static bool Under(ReadOnlySpan<char> path, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> moved)
    {
        for (int end = path.Length; end > 0; end = path[..end].LastIndexOf('/'))
        {
            if (moved.Contains(path[..end]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a path is <paramref name="folder"/> itself or lies below it, both named from the tree's top.</summary>
    private static bool Within(string path, string folder) =>
        path.Length == folder.Length ? path == folder : path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, StringComparison.Ordinal);
}
