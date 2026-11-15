using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The processors a program is built for, read from its header: a thin Mach-O's CPU, each slice's of a universal
/// one (its table of 32-bit or 64-bit offsets), a Linux program's ELF machine, a Windows program's machine, and none for a
/// file that is no program or is cut short. An x64 Passenger the system translates loads an SDK's MSBuild only where its
/// dotnet reads as x64, and Linux builds Android apps where the workload's aapt2 reads as its own processor's.</summary>
public class ProgramProcessorsTests
{
    private static IReadOnlyList<Architecture> Of(byte[] program)
    {
        using MemoryStream stream = new(program);
        return ProgramProcessors.Of(stream);
    }

    private static byte[] Thin(int cpu)
    {
        byte[] program = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(program, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(program.AsSpan(4), cpu);
        return program;
    }

    /// <summary>A Linux program's ELF header to its machine, a 64-bit one in the byte order given.</summary>
    private static byte[] Linux(ushort machine, bool bigEndian = false)
    {
        byte[] program = new byte[64];
        "\u007fELF"u8.CopyTo(program);
        program[4] = 2;
        program[5] = (byte)(bigEndian ? 2 : 1);
        program[6] = 1;
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(program.AsSpan(18), machine);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(program.AsSpan(18), machine);
        }
        return program;
    }

    private static byte[] Windows(ushort machine)
    {
        byte[] program = new byte[0x100];
        program[0] = (byte)'M';
        program[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(program.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(program.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(program.AsSpan(0x84), machine);
        return program;
    }

    [Fact]
    public void A_thin_mac_programs_processor_is_its_cpu_type()
    {
        Assert.Equal([Architecture.X64], Of(Thin(0x01000007)));
        Assert.Equal([Architecture.Arm64], Of(Thin(0x0100000C)));
    }

    [Fact]
    public void A_universal_mac_programs_processors_are_its_slices()
    {
        byte[] program = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(program, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(8), 0x01000007);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(28), 0x0100000C);
        Assert.Equal([Architecture.X64, Architecture.Arm64], Of(program));
    }

    [Fact]
    public void A_universal_mac_program_of_64_bit_offsets_is_built_for_each_of_its_slices()
    {
        byte[] program = new byte[8 + (2 * 32)];
        BinaryPrimitives.WriteUInt32BigEndian(program, 0xCAFEBABF);
        BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(8), 0x01000007);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(8 + 32), 0x0100000C);
        IReadOnlyList<Architecture> read = Of(program);
        Assert.True(read.SequenceEqual([Architecture.X64, Architecture.Arm64]),
            $"A universal program whose table holds 64-bit offsets reads as built for [{string.Join(", ", read)}]: a dotnet shipped so is passed over by an x64 Passenger under Rosetta as no x64 program");
    }

    [Fact]
    public void A_thin_32_bit_mac_programs_processor_is_its_cpu_type()
    {
        byte[] program = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(program, 0xFEEDFACE);
        BinaryPrimitives.WriteInt32LittleEndian(program.AsSpan(4), 7);
        IReadOnlyList<Architecture> read = Of(program);
        Assert.True(read.SequenceEqual([Architecture.X86]), $"A thin 32-bit Intel program reads as built for [{string.Join(", ", read)}], where its linked libraries read as a Mach-O's");
    }

    /// <summary>A Java class file's magic is a universal program's, its version (65 for Java 21) where the count of slices stands.</summary>
    [Fact]
    public void A_universal_table_of_more_slices_than_a_program_has_is_a_java_class_built_for_no_processor()
    {
        byte[] program = new byte[8 + (65 * 20)];
        BinaryPrimitives.WriteUInt32BigEndian(program, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(4), 65);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(8), 0x01000007);
        IReadOnlyList<Architecture> read = Of(program);
        Assert.True(read.Count == 0, $"A Java class file reads as a program built for [{string.Join(", ", read)}], where what it links reads as no Mach-O's");
    }

    [Fact]
    public void A_linux_programs_processor_is_its_elf_machine_in_the_byte_order_its_header_names()
    {
        Assert.Equal([Architecture.X64], Of(Linux(62)));
        Assert.Equal([Architecture.Arm64], Of(Linux(183)));
        Assert.Equal([Architecture.Arm64], Of(Linux(183, bigEndian: true)));
        Assert.Equal([Architecture.Arm], Of(Linux(40)));
        Assert.Equal([Architecture.RiscV64], Of(Linux(243)));
        Assert.Empty(Of(Linux(62)[..19]));
        Assert.Empty(Of(Linux(0x1234)));
    }

    [Fact]
    public void A_windows_programs_processor_is_its_machine()
    {
        Assert.Equal([Architecture.X64], Of(Windows(0x8664)));
        Assert.Equal([Architecture.Arm64], Of(Windows(0xAA64)));
    }

    [Fact]
    public void A_file_that_is_no_program_or_is_cut_short_has_none()
    {
        Assert.Empty(Of("#!/bin/sh\nexec dotnet \"$@\"\n"u8.ToArray()));
        Assert.Empty(Of(Thin(0x01000007)[..6]));
        Assert.Empty(Of(Windows(0x8664)[..0x82]));
        byte[] universal = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(universal, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(universal.AsSpan(4), 3);
        Assert.Empty(Of(universal));
    }
}
