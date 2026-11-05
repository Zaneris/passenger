using System.Buffers.Binary;
using System.Text;

namespace Passenger.Core;

/// <summary>What a macOS program links, read from its load commands alone: a thin Mach-O or each slice of a universal one.
/// Sizes are the file's word, so each is held to what it can be; a file cut short gives what was read of it.</summary>
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

    /// <summary>The load commands that name a library a program links (LC_LOAD_DYLIB and its weak, re-exported, lazy and upward kinds).</summary>
    private static readonly uint[] Linking = [0x0C, 0x80000018, 0x8000001F, 0x20, 0x80000023];

    /// <summary>The libraries a program links, in the order its commands name them, a universal program's each once; null for
    /// a file that is no Mach-O.</summary>
    public static IReadOnlyList<string>? Libraries(Stream program)
    {
        if (Read(program, 0, 8) is not { } start)
        {
            return null;
        }
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(start);
        if (magic is Universal32 or Universal64)
        {
            uint slices = BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(4));
            return slices is 0 or > MostSlices ? null : [.. Slices(program, slices, magic == Universal64).SelectMany(offset => Linked(program, offset) ?? []).Distinct(StringComparer.Ordinal)];
        }
        return Linked(program, 0);
    }

    /// <summary>Where each slice of a universal program begins, by the table after its header, which is big-endian.</summary>
    private static IEnumerable<long> Slices(Stream program, uint slices, bool wide)
    {
        int entry = wide ? 32 : 20;
        for (int slice = 0; slice < slices; slice++)
        {
            if (Read(program, 8 + (slice * entry), entry) is { } read)
            {
                yield return wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(read.AsSpan(8)) : BinaryPrimitives.ReadUInt32BigEndian(read.AsSpan(8));
            }
        }
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
