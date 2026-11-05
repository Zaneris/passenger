using System.Buffers.Binary;
using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What a macOS program links, read from its load commands: a thin program's, each slice's of a universal one, and
/// nothing past what the file holds, whatever its sizes say.</summary>
public class MachOFileTests
{
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private static IReadOnlyList<string>? Linked(byte[] program)
    {
        using MemoryStream stream = new(program);
        return MachOFile.Libraries(stream);
    }

    [Fact]
    public void A_thin_program_names_what_it_links_of_every_kind_in_order_and_nothing_else_its_commands_hold()
    {
        Assert.Equal([LibSystem, AppKit, "@rpath/libSDL2-2.0.0.dylib"], Linked(MachOBytes.Commands((0x0C, LibSystem), (0x1B, "a uuid"), (0x80000018, AppKit), (0x8000001F, "@rpath/libSDL2-2.0.0.dylib"))));
        Assert.Equal([LibSystem], Linked(MachOBytes.Thin(LibSystem)));
        Assert.Empty(Linked(MachOBytes.Thin())!);
    }

    [Fact]
    public void A_universal_program_names_what_each_slice_links_once()
    {
        Assert.Equal([LibSystem, AppKit], Linked(MachOBytes.Universal(MachOBytes.Thin(LibSystem, AppKit), MachOBytes.Thin(LibSystem))));
        Assert.Equal([LibSystem, AppKit], Linked(MachOBytes.Universal(MachOBytes.Thin(LibSystem), MachOBytes.Thin(AppKit))));
    }

    [Fact]
    public void Files_of_no_mach_o_are_none_a_java_class_that_begins_as_a_universal_program_too()
    {
        Assert.Null(Linked([]));
        Assert.Null(Linked([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0]));
        Assert.Null(Linked(Encoding.ASCII.GetBytes("#!/bin/sh\nexec app\n")));
        Assert.Null(Linked([(byte)'M', (byte)'Z', 0, 0, 0, 0, 0, 0]));
        Assert.Null(Linked([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 65, 0, 0, 0, 0]));
    }

    [Fact]
    public void A_program_cut_short_gives_what_is_there_and_a_size_past_its_end_reads_no_further()
    {
        byte[] whole = MachOBytes.Thin(LibSystem, AppKit);
        Assert.Equal([LibSystem], Linked(whole[..^40]));
        Assert.Null(Linked(whole[..20]));

        byte[] lying = MachOBytes.Thin(LibSystem);
        BinaryPrimitives.WriteUInt32LittleEndian(lying.AsSpan(36), 1_000_000);
        Assert.Empty(Linked(lying)!);

        byte[] zero = MachOBytes.Thin(LibSystem);
        BinaryPrimitives.WriteUInt32LittleEndian(zero.AsSpan(36), 0);
        Assert.Empty(Linked(zero)!);

        byte[] pointing = MachOBytes.Thin(LibSystem);
        BinaryPrimitives.WriteUInt32LittleEndian(pointing.AsSpan(40), 1_000_000);
        Assert.Empty(Linked(pointing)!);

        byte[] commands = MachOBytes.Thin(LibSystem);
        BinaryPrimitives.WriteUInt32LittleEndian(commands.AsSpan(20), uint.MaxValue);
        Assert.Empty(Linked(commands)!);
    }

    [Fact]
    public void A_slice_past_the_programs_end_adds_nothing_to_those_that_are_there()
    {
        byte[] program = MachOBytes.Universal(MachOBytes.Thin(LibSystem), MachOBytes.Thin(AppKit));
        BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(8 + 20 + 8), int.MaxValue);
        Assert.Equal([LibSystem], Linked(program));
    }

    /// <summary>The system's own builds, thin or universal, are the reader's real input: read, never run.</summary>
    [Fact]
    public void A_macs_own_tool_is_read_for_what_it_links()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Only a Mac has macOS's programs to read");
        using FileStream open = File.OpenRead("/usr/bin/open");
        using FileStream list = File.OpenRead("/bin/ls");

        Assert.Contains(MachOFile.Libraries(open)!, library => library.EndsWith("/AppKit", StringComparison.Ordinal));
        Assert.DoesNotContain(MachOFile.Libraries(list)!, library => library.EndsWith("/AppKit", StringComparison.Ordinal));
    }
}

/// <summary>macOS programs as bytes, their header and load commands as the linker writes them: a command of a kind and a text.</summary>
internal static class MachOBytes
{
    private const uint LoadLibrary = 0x0C;

    public static byte[] Thin(params string[] libraries) => Commands([.. libraries.Select(library => (LoadLibrary, library))]);

    public static byte[] Commands(params (uint Kind, string Text)[] commands)
    {
        using MemoryStream body = new();
        foreach ((uint kind, string text) in commands)
        {
            byte[] name = Encoding.UTF8.GetBytes(text + "\0");
            int size = (24 + name.Length + 7) / 8 * 8;
            byte[] command = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(command, kind);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), 24);
            name.CopyTo(command, 24);
            body.Write(command);
        }
        byte[] header = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x0100000C);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)commands.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)body.Length);
        return [.. header, .. body.ToArray()];
    }

    public static byte[] Universal(params byte[][] slices)
    {
        using MemoryStream program = new();
        byte[] header = new byte[8 + (20 * slices.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)slices.Length);
        uint at = (uint)header.Length;
        for (int slice = 0; slice < slices.Length; slice++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + (slice * 20) + 8), at);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + (slice * 20) + 12), (uint)slices[slice].Length);
            at += (uint)slices[slice].Length;
        }
        program.Write(header);
        foreach (byte[] slice in slices)
        {
            program.Write(slice);
        }
        return program.ToArray();
    }
}
