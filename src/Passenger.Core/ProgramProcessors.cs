using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>The processors a program's code is built for, read from its header alone: a Mach-O's CPU type, each slice's of
/// a universal one (<see cref="MachOFile.Slices"/>), a Linux program's ELF machine, or a Windows program's machine. Empty
/// for a file that is none of these, is cut short, or is for a processor .NET does not run on, and for a .NET assembly,
/// whose machine (x86's for AnyCPU) is not the processor it runs as.</summary>
public static class ProgramProcessors
{
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
        if (MachOFile.Slices(program) is { } slices)
        {
            return [.. slices.Select(slice => Cpu(slice.Cpu)).OfType<Architecture>()];
        }
        Span<byte> head = stackalloc byte[20];
        if (!Read(program, 0, head[..8]))
        {
            return [];
        }
        // An ELF header names its byte order (EI_DATA: 1 little-endian, 2 big-endian) before its machine.
        if (head[..4].SequenceEqual("\u007fELF"u8) && head[5] is 1 or 2 && Read(program, 0, head))
        {
            return Machine(head[5] == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(head[18..]) : BinaryPrimitives.ReadUInt16BigEndian(head[18..])) is { } processor ? [processor] : [];
        }
        Span<byte> machine = stackalloc byte[6];
        if (head[0] == 'M' && head[1] == 'Z' && Read(program, 0x3C, head[..4])
            && Read(program, BinaryPrimitives.ReadInt32LittleEndian(head), machine) && machine[..4].SequenceEqual("PE\0\0"u8))
        {
            if (Managed(program, BinaryPrimitives.ReadInt32LittleEndian(head)))
            {
                return [];
            }
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

    /// <summary>An ELF e_machine's processor, each that .NET runs Linux on.</summary>
    private static Architecture? Machine(ushort machine) => machine switch
    {
        62 => Architecture.X64,
        183 => Architecture.Arm64,
        40 => Architecture.Arm,
        3 => Architecture.X86,
        243 => Architecture.RiscV64,
        258 => Architecture.LoongArch64,
        22 => Architecture.S390x,
        21 => Architecture.Ppc64le,
        _ => null,
    };

    /// <summary>Whether a Windows program whose PE header is at <paramref name="pe"/> is a .NET assembly: its optional header
    /// (PE32's or PE32+'s) names a CLR header among its data directories, the fifteenth. False where it cannot be read.</summary>
    private static bool Managed(Stream program, long pe)
    {
        const int Clr = 14;
        Span<byte> magic = stackalloc byte[2];
        long optional = pe + 24;
        if (!Read(program, optional, magic))
        {
            return false;
        }
        // NumberOfRvaAndSizes stands just before the data directories, 96 bytes into a PE32 optional header, 112 into a PE32+'s.
        long directories = optional + (BinaryPrimitives.ReadUInt16LittleEndian(magic) switch { 0x10B => 96, 0x20B => 112, _ => -1 });
        Span<byte> entry = stackalloc byte[8];
        if (directories <= optional || !Read(program, directories - 4, entry[..4]) || BinaryPrimitives.ReadUInt32LittleEndian(entry) <= Clr
            || !Read(program, directories + (Clr * 8), entry))
        {
            return false;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(entry) != 0 && BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]) != 0;
    }

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
