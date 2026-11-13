using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>The processors a program's code is built for, read from its header alone: a Mach-O's CPU type, each slice's of
/// a universal one, or a Windows program's machine. Empty for a file that is neither, or is cut short.</summary>
public static class ProgramProcessors
{
    /// <summary>The most slices a universal program is taken to have: a Java class file begins as one does.</summary>
    private const int MostSlices = 16;

    /// <summary>The processors of the program at a path; none where it cannot be read.</summary>
    public static IReadOnlyList<Architecture> Of(string path)
    {
        try
        {
            using FileStream program = File.OpenRead(path);
            return Of(program);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<Architecture> Of(Stream program)
    {
        Span<byte> head = stackalloc byte[8];
        if (!Read(program, 0, head))
        {
            return [];
        }
        // A thin 64-bit Mach-O is little-endian, as every Mac's since Intel; a universal one's table big-endian.
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) == 0xFEEDFACF)
        {
            return Cpu(BinaryPrimitives.ReadInt32LittleEndian(head[4..])) is { } thin ? [thin] : [];
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0xCAFEBABE)
        {
            List<Architecture> slices = [];
            Span<byte> slice = stackalloc byte[4];
            uint count = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(head[4..]), MostSlices);
            for (int at = 0; at < count && Read(program, 8 + (at * 20), slice); at++)
            {
                if (Cpu(BinaryPrimitives.ReadInt32BigEndian(slice)) is { } cpu)
                {
                    slices.Add(cpu);
                }
            }
            return slices;
        }
        Span<byte> machine = stackalloc byte[6];
        if (head[0] == 'M' && head[1] == 'Z' && Read(program, 0x3C, head[..4])
            && Read(program, BinaryPrimitives.ReadInt32LittleEndian(head), machine) && machine[..4].SequenceEqual("PE\0\0"u8))
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(machine[4..]) switch
            {
                0x8664 => [Architecture.X64],
                0xAA64 => [Architecture.Arm64],
                0x014C => [Architecture.X86],
                _ => [],
            };
        }
        return [];
    }

    /// <summary>A Mach-O CPU type's processor: x86-64 or arm64 (each the 64-bit flag over its family), or i386.</summary>
    private static Architecture? Cpu(int type) => type switch
    {
        0x01000007 => Architecture.X64,
        0x0100000C => Architecture.Arm64,
        7 => Architecture.X86,
        _ => null,
    };

    /// <summary>The bytes at an offset, false where the file ends first.</summary>
    private static bool Read(Stream program, long offset, Span<byte> into)
    {
        if (offset < 0 || offset + into.Length > program.Length)
        {
            return false;
        }
        program.Position = offset;
        program.ReadExactly(into);
        return true;
    }
}
