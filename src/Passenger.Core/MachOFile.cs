using System.Buffers.Binary;
using System.Text;

namespace Passenger.Core;

/// <summary>A macOS program read from its header alone: its slices, a thin Mach-O's one or each of a universal one's, and
/// what it links, from each slice's load commands, and where a thin one's functions lie, from its symbol table. Sizes are
/// the file's word, so each is held to what it can be; a file cut short gives what was read of it.</summary>
public static class MachOFile
{
    private const uint Thin32 = 0xFEEDFACE;
    private const uint Thin64 = 0xFEEDFACF;
    private const uint Universal32 = 0xCAFEBABE;
    private const uint Universal64 = 0xCAFEBABF;

    /// <summary>The most slices a universal program is taken to have: a Java class file begins as one does, its version where the count is.</summary>
    private const uint MostSlices = 16;

    /// <summary>The most bytes of load commands read of a slice; a program's run to tens of kilobytes.</summary>
    private const uint MostCommands = 16 * 1024 * 1024;

    /// <summary>LC_SEGMENT_64 and LC_SYMTAB.</summary>
    private const uint Segment64 = 0x19;
    private const uint SymbolTable = 0x02;

    /// <summary>An nlist_64 entry's size, and its type's bits: a debugger's entry (N_STAB), and the kind, a section's symbol (N_SECT).</summary>
    private const int SymbolSize = 16;
    private const byte Debugging = 0xE0;
    private const byte Kind = 0x0E;
    private const byte InSection = 0x0E;

    /// <summary>The most bytes of a symbol table, or of its names, read: a large library's run to a few megabytes.</summary>
    private const uint MostSymbolBytes = 256 * 1024 * 1024;

    /// <summary>The load commands that name a library a program links (LC_LOAD_DYLIB and its weak, re-exported, lazy and upward kinds).</summary>
    private static readonly uint[] Linking = [0x0C, 0x80000018, 0x8000001F, 0x20, 0x80000023];

    /// <summary>Each slice of a program, its CPU type and where its own header begins: a thin Mach-O's one at its start
    /// (little-endian, as every Mac's since Intel), a universal one's by the big-endian table after its header, an entry the
    /// file ends within left out; null for a file that is no Mach-O, or a table of no slice or of more than a program has.</summary>
    public static IReadOnlyList<(int Cpu, long Offset)>? Slices(Stream program)
    {
        if (Read(program, 0, 8) is not { } start)
        {
            return null;
        }
        uint thin = BinaryPrimitives.ReadUInt32LittleEndian(start);
        if (thin is Thin32 or Thin64)
        {
            return Read(program, 0, thin == Thin64 ? 32 : 28) is null ? null : [(BinaryPrimitives.ReadInt32LittleEndian(start.AsSpan(4)), 0)];
        }
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(start);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(4));
        if (magic is not (Universal32 or Universal64) || count is 0 or > MostSlices)
        {
            return null;
        }
        bool wide = magic == Universal64;
        int entry = wide ? 32 : 20;
        List<(int Cpu, long Offset)> slices = [];
        for (int slice = 0; slice < count; slice++)
        {
            if (Read(program, 8 + (slice * entry), entry) is { } read)
            {
                slices.Add((BinaryPrimitives.ReadInt32BigEndian(read), wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(read.AsSpan(8)) : BinaryPrimitives.ReadUInt32BigEndian(read.AsSpan(8))));
            }
        }
        return slices;
    }

    /// <summary>The libraries a program links, in the order its commands name them, a universal program's each once; null for
    /// a file that is no Mach-O.</summary>
    public static IReadOnlyList<string>? Libraries(Stream program) =>
        Slices(program) is { } slices ? [.. slices.SelectMany(slice => Linked(program, slice.Offset) ?? []).Distinct(StringComparer.Ordinal)] : null;

    /// <summary>Where each function of <paramref name="names"/> a thin 64-bit Mach-O's symbol table names lies, as offsets
    /// from its image's start as loaded (its __TEXT segment's address taken off): from its symbol to the next of its section,
    /// the last one's to its __TEXT segment's end; a name it lacks left out, null for a file that is no thin 64-bit Mach-O or
    /// has no symbol table.</summary>
    public static IReadOnlyDictionary<string, (ulong Start, ulong End)>? Functions(Stream program, IReadOnlyCollection<string> names)
    {
        if (Read(program, 0, 32) is not { } header || BinaryPrimitives.ReadUInt32LittleEndian(header) != Thin64)
        {
            return null;
        }
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        if (size > MostCommands || Read(program, 32, (int)Math.Min(size, Math.Max(0, program.Length - 32))) is not { } commands)
        {
            return null;
        }
        ulong text = 0;
        ulong mapped = ulong.MaxValue;
        (uint At, uint Count, uint Names, uint NamesSize)? table = null;
        for (int at = 0; at + 8 <= commands.Length;)
        {
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(at));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(at + 4));
            if (length < 8 || length > commands.Length - at)
            {
                break;
            }
            ReadOnlySpan<byte> command = commands.AsSpan(at, (int)length);
            if (kind == Segment64 && length >= 40 && command[8..24].SequenceEqual("__TEXT\0\0\0\0\0\0\0\0\0\0"u8))
            {
                text = BinaryPrimitives.ReadUInt64LittleEndian(command[24..]);
                mapped = text + BinaryPrimitives.ReadUInt64LittleEndian(command[32..]);
            }
            else if (kind == SymbolTable && length >= 24)
            {
                table = (BinaryPrimitives.ReadUInt32LittleEndian(command[8..]), BinaryPrimitives.ReadUInt32LittleEndian(command[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(command[16..]), BinaryPrimitives.ReadUInt32LittleEndian(command[20..]));
            }
            at += (int)length;
        }
        if (table is not { } symbols || (ulong)symbols.Count * SymbolSize > MostSymbolBytes || symbols.NamesSize > MostSymbolBytes
            || Read(program, symbols.At, (int)symbols.Count * SymbolSize) is not { } entries || Read(program, symbols.Names, (int)symbols.NamesSize) is not { } named)
        {
            return null;
        }
        byte[][] wanted = [.. names.Select(Encoding.UTF8.GetBytes)];
        Dictionary<string, (byte Section, ulong Start)> found = [];
        Dictionary<byte, List<ulong>> starts = [];
        for (int at = 0; at + SymbolSize <= entries.Length; at += SymbolSize)
        {
            ReadOnlySpan<byte> entry = entries.AsSpan(at, SymbolSize);
            byte type = entry[4];
            if ((type & Debugging) != 0 || (type & Kind) != InSection)
            {
                continue;
            }
            byte section = entry[5];
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            (starts.TryGetValue(section, out List<ulong>? list) ? list : starts[section] = []).Add(value);
            uint name = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (name < named.Length && Wanted(named.AsSpan((int)name), wanted) is { } symbol)
            {
                found.TryAdd(symbol, (section, value));
            }
        }
        Dictionary<string, (ulong Start, ulong End)> functions = [];
        foreach ((string symbol, (byte section, ulong start)) in found)
        {
            ulong end = starts[section].Where(value => value > start).DefaultIfEmpty(mapped).Min();
            if (start >= text && end > start)
            {
                functions[symbol] = (start - text, end - text);
            }
        }
        return functions;
    }

    /// <summary>Which of the names wanted a symbol's is, its text ended by a zero; null for none.</summary>
    private static string? Wanted(ReadOnlySpan<byte> names, byte[][] wanted)
    {
        foreach (byte[] name in wanted)
        {
            if (names.Length > name.Length && names[name.Length] == 0 && names.StartsWith(name))
            {
                return Encoding.UTF8.GetString(name);
            }
        }
        return null;
    }

    /// <summary>The libraries a thin Mach-O at an offset names; null where it is none. Its fields are little-endian, as every
    /// Mac's since Intel.</summary>
    private static List<string>? Linked(Stream program, long offset)
    {
        if (offset < 0 || Read(program, offset, 24) is not { } header || BinaryPrimitives.ReadUInt32LittleEndian(header) is not (Thin32 or Thin64))
        {
            return null;
        }
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        long commands = offset + (BinaryPrimitives.ReadUInt32LittleEndian(header) == Thin64 ? 32 : 28);
        if (size > MostCommands || Read(program, commands, (int)Math.Min(size, Math.Max(0, program.Length - commands))) is not { } read)
        {
            return [];
        }
        List<string> libraries = [];
        for (int at = 0; at + 8 <= read.Length;)
        {
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(at));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(at + 4));
            if (length < 8 || length > read.Length - at)
            {
                break;
            }
            if (Linking.Contains(kind) && length >= 12 && Named(read.AsSpan(at, (int)length)) is { } library)
            {
                libraries.Add(library);
            }
            at += (int)length;
        }
        return libraries;
    }

    /// <summary>The library a command names: the text its third field points to, ended by a zero or the command's end.</summary>
    private static string? Named(ReadOnlySpan<byte> command)
    {
        uint at = BinaryPrimitives.ReadUInt32LittleEndian(command[8..]);
        if (at >= command.Length)
        {
            return null;
        }
        ReadOnlySpan<byte> rest = command[(int)at..];
        int end = rest.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
    }

    /// <summary>The bytes at an offset; null where the file ends first.</summary>
    private static byte[]? Read(Stream program, long offset, int count)
    {
        if (offset + count > program.Length)
        {
            return null;
        }
        byte[] read = new byte[count];
        program.Position = offset;
        program.ReadExactly(read);
        return read;
    }
}
