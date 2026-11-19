using System.Globalization;

namespace Passenger.Core;

/// <summary>What Linux's <c>/proc/ID/stat</c> says of a process: its state (<c>Z</c> a zombie), its parent, and its start
/// in clock ticks since boot. Read into the stack and parsed where it lies, allocating nothing.</summary>
internal readonly record struct ProcStat(char State, int Parent, long Started)
{
    /// <summary>The longest of a stat's text read: the name, at most 64 bytes, and the 20 fields up to the start, each at
    /// most 20 digits, fit well within it.</summary>
    private const int Longest = 1024;

    /// <summary>Process <paramref name="id"/>'s stat; null for one gone or unreadable, and off Linux.</summary>
    public static unsafe ProcStat? Of(int id)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        Span<byte> path = stackalloc byte[32];
        "/proc/"u8.CopyTo(path);
        if (!id.TryFormat(path[6..], out int digits, default, CultureInfo.InvariantCulture))
        {
            return null;
        }
        "/stat\0"u8.CopyTo(path[(6 + digits)..]);
        int file;
        fixed (byte* named = path)
        {
            file = Libc.Open(named, Libc.ReadOnly | Libc.ClosedOnExec);
        }
        if (file < 0)
        {
            return null;
        }
        byte* text = stackalloc byte[Longest];
        nint read = Libc.Read(file, text, Longest);
        _ = Libc.Close(file);
        return read > 0 ? Read(new ReadOnlySpan<byte>(text, (int)read)) : null;
    }

    /// <summary>A stat's text parsed, or null where it is cut short before the start or misread. Its name, the 2nd field,
    /// may hold spaces and parentheses itself, so the fields are read after its last parenthesis.</summary>
    public static ProcStat? Read(ReadOnlySpan<byte> stat)
    {
        int named = stat.LastIndexOf((byte)')');
        if (named < 0 || named + 2 >= stat.Length)
        {
            return null;
        }
        ReadOnlySpan<byte> rest = stat[(named + 2)..];
        char state = (char)rest[0];
        int parent = 0;
        // The state is the 3rd field, the parent the 4th and the start the 22nd.
        for (int field = 3; ; field++)
        {
            int end = rest.IndexOfAny((byte)' ', (byte)'\n');
            ReadOnlySpan<byte> value = end < 0 ? rest : rest[..end];
            if (field == 4 && !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parent))
            {
                return null;
            }
            if (field == 22)
            {
                return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long started) ? new ProcStat(state, parent, started) : null;
            }
            if (end < 0 || rest[end] == (byte)'\n')
            {
                return null;
            }
            rest = rest[(end + 1)..];
        }
    }
}
