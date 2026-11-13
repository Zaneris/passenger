using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The processors a program is built for, read from its header: a thin Mach-O's CPU, each slice's of a universal
/// one, a Windows program's machine, and none for a file that is no program or is cut short. An x64 Passenger the system
/// translates loads an SDK's MSBuild only where its dotnet reads as x64.</summary>
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
