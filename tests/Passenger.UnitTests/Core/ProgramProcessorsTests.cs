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
    public void A_programs_processors_are_read_from_its_header_on_every_system() => Cases.All(
        ("a thin mac programs processor is its cpu type", A_thin_mac_programs_processor_is_its_cpu_type),
        ("a universal mac programs processors are its slices", A_universal_mac_programs_processors_are_its_slices),
        ("a universal table of more slices than a program has is a java class built for no processor", A_universal_table_of_more_slices_than_a_program_has_is_a_java_class_built_for_no_processor),
        ("a linux programs processor is its elf machine in the byte order its header names", A_linux_programs_processor_is_its_elf_machine_in_the_byte_order_its_header_names),
        ("a windows programs processor is its machine", A_windows_programs_processor_is_its_machine),
        ("a dotnet assembly is built for no processor its machine names and a native x86 program for x86", A_dotnet_assembly_is_built_for_no_processor_its_machine_names_and_a_native_x86_program_for_x86),
        ("a file that is no program or is cut short has none", A_file_that_is_no_program_or_is_cut_short_has_none));

    private void A_thin_mac_programs_processor_is_its_cpu_type()
    {
        Assert.Equal([Architecture.X64], Of(Thin(0x01000007)));
        Assert.Equal([Architecture.Arm64], Of(Thin(0x0100000C)));
    }

    private void A_universal_mac_programs_processors_are_its_slices()
    {
        byte[] program = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(program, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(8), 0x01000007);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(28), 0x0100000C);
        Assert.Equal([Architecture.X64, Architecture.Arm64], Of(program));
    }

    /// <summary>A Java class file's magic is a universal program's, its version (65 for Java 21) where the count of slices stands.</summary>
    private void A_universal_table_of_more_slices_than_a_program_has_is_a_java_class_built_for_no_processor()
    {
        byte[] program = new byte[8 + (65 * 20)];
        BinaryPrimitives.WriteUInt32BigEndian(program, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(4), 65);
        BinaryPrimitives.WriteInt32BigEndian(program.AsSpan(8), 0x01000007);
        IReadOnlyList<Architecture> read = Of(program);
        Assert.True(read.Count == 0, $"A Java class file reads as a program built for [{string.Join(", ", read)}], where what it links reads as no Mach-O's");
    }

    private void A_linux_programs_processor_is_its_elf_machine_in_the_byte_order_its_header_names()
    {
        Assert.Equal([Architecture.X64], Of(Linux(62)));
        Assert.Equal([Architecture.Arm64], Of(Linux(183)));
        Assert.Equal([Architecture.Arm64], Of(Linux(183, bigEndian: true)));
        Assert.Equal([Architecture.Arm], Of(Linux(40)));
        Assert.Equal([Architecture.RiscV64], Of(Linux(243)));
        Assert.Empty(Of(Linux(62)[..19]));
        Assert.Empty(Of(Linux(0x1234)));
    }

    private void A_windows_programs_processor_is_its_machine()
    {
        Assert.Equal([Architecture.X64], Of(Windows(0x8664)));
        Assert.Equal([Architecture.Arm64], Of(Windows(0xAA64)));
    }

    /// <summary>A Windows program with a whole PE32 optional header, its CLR header's data directory set where it is a .NET assembly.</summary>
    private static byte[] WindowsWithOptionalHeader(ushort machine, bool managed)
    {
        byte[] program = new byte[0x200];
        Windows(machine).CopyTo(program, 0);
        const int Optional = 0x80 + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(program.AsSpan(Optional), 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(Optional + 92), 16);
        if (managed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(Optional + 96 + (14 * 8)), 0x2008);
            BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(Optional + 96 + (14 * 8) + 4), 0x48);
        }
        return program;
    }

    private void A_dotnet_assembly_is_built_for_no_processor_its_machine_names_and_a_native_x86_program_for_x86()
    {
        IReadOnlyList<Architecture> assembly = Of(WindowsWithOptionalHeader(0x014C, managed: true));
        Assert.True(assembly.Count == 0,
            $"An AnyCPU .NET assembly, whose PE machine is x86's, read as built for [{string.Join(", ", assembly)}], so Debug would refuse a 64-bit program as a 32-bit one");
        Assert.Equal([Architecture.X86], Of(WindowsWithOptionalHeader(0x014C, managed: false)));
        IReadOnlyList<Architecture> built = ProgramProcessors.Of(typeof(ProgramProcessorsTests).Assembly.Location);
        Assert.True(built.Count == 0, $"This AnyCPU test assembly read as built for [{string.Join(", ", built)}]");
    }

    private void A_file_that_is_no_program_or_is_cut_short_has_none()
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
