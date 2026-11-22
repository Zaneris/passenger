using System.Buffers.Binary;
using System.Text;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A macOS program read from its header: its slices, a thin program's one or each a universal one's table names
/// (with 32-bit or 64-bit offsets), and what each links, read from its load commands, nothing past what the file holds,
/// whatever its sizes say.</summary>
public class MachOFileTests
{
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private const int Arm64 = 0x0100000C;

    private static IReadOnlyList<string>? Linked(byte[] program)
    {
        using MemoryStream stream = new(program);
        return MachOFile.Libraries(stream);
    }

    private static IReadOnlyList<(int Cpu, long Offset)>? Sliced(byte[] program)
    {
        using MemoryStream stream = new(program);
        return MachOFile.Slices(stream);
    }

    [Fact]
    public void A_mac_program_is_read_for_its_slices_and_what_each_links_and_a_file_cut_short_reads_no_further() => Cases.All(
        ("A thin program is one slice at its start and a universal one each its table names by 32 or 64 bit offsets", A_thin_program_is_one_slice_at_its_start_and_a_universal_one_each_its_table_names_by_32_or_64_bit_offsets),
        ("A header cut short or a table of no slice is no program", A_header_cut_short_or_a_table_of_no_slice_is_no_program),
        ("A thin program names what it links of every kind in order and nothing else its commands hold", A_thin_program_names_what_it_links_of_every_kind_in_order_and_nothing_else_its_commands_hold),
        ("A universal program names what each slice links once", A_universal_program_names_what_each_slice_links_once),
        ("Files of no mach o are none a java class that begins as a universal program too", Files_of_no_mach_o_are_none_a_java_class_that_begins_as_a_universal_program_too),
        ("A program cut short gives what is there and a size past its end reads no further", A_program_cut_short_gives_what_is_there_and_a_size_past_its_end_reads_no_further),
        ("A slice past the programs end adds nothing to those that are there", A_slice_past_the_programs_end_adds_nothing_to_those_that_are_there),
        ("A function lies from its symbol to the next of its section as offsets into its image", A_function_lies_from_its_symbol_to_the_next_of_its_section_as_offsets_into_its_image),
        ("A program with no symbol table or a table past its end names no function", A_program_with_no_symbol_table_or_a_table_past_its_end_names_no_function),
        ("The runtimes own library names where its tiering worker starts", The_runtimes_own_library_names_where_its_tiering_worker_starts),
        ("A macs own tool is read for what it links", A_macs_own_tool_is_read_for_what_it_links));

    private void A_thin_program_is_one_slice_at_its_start_and_a_universal_one_each_its_table_names_by_32_or_64_bit_offsets()
    {
        byte[] first = MachOBytes.Thin(LibSystem);
        byte[] second = MachOBytes.Thin(AppKit);

        Assert.Equal([(Arm64, 0L)], Sliced(first));
        Assert.Equal([(Arm64, 8L + (2 * 20)), (Arm64, 8L + (2 * 20) + first.Length)], Sliced(MachOBytes.Universal(first, second)));
        Assert.Equal([(Arm64, 8L + (2 * 32)), (Arm64, 8L + (2 * 32) + first.Length)], Sliced(MachOBytes.Universal(wide: true, first, second)));
        Assert.Equal([LibSystem, AppKit], Linked(MachOBytes.Universal(wide: true, first, second)));
    }

    private void A_header_cut_short_or_a_table_of_no_slice_is_no_program()
    {
        Assert.Null(Sliced(MachOBytes.Thin(LibSystem)[..31]));
        Assert.Null(Sliced([0xCA, 0xFE, 0xBA, 0xBF, 0, 0, 0, 0]));
        Assert.Null(Sliced([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 17]));
        Assert.Empty(Sliced([0xCA, 0xFE, 0xBA, 0xBF, 0, 0, 0, 1, .. new byte[31]])!);
    }

    private void A_thin_program_names_what_it_links_of_every_kind_in_order_and_nothing_else_its_commands_hold()
    {
        Assert.Equal([LibSystem, AppKit, "@rpath/libSDL2-2.0.0.dylib"], Linked(MachOBytes.Commands((0x0C, LibSystem), (0x1B, "a uuid"), (0x80000018, AppKit), (0x8000001F, "@rpath/libSDL2-2.0.0.dylib"))));
        Assert.Equal([LibSystem], Linked(MachOBytes.Thin(LibSystem)));
        Assert.Empty(Linked(MachOBytes.Thin())!);
    }

    private void A_universal_program_names_what_each_slice_links_once()
    {
        Assert.Equal([LibSystem, AppKit], Linked(MachOBytes.Universal(MachOBytes.Thin(LibSystem, AppKit), MachOBytes.Thin(LibSystem))));
        Assert.Equal([LibSystem, AppKit], Linked(MachOBytes.Universal(MachOBytes.Thin(LibSystem), MachOBytes.Thin(AppKit))));
    }

    private void Files_of_no_mach_o_are_none_a_java_class_that_begins_as_a_universal_program_too()
    {
        Assert.Null(Linked([]));
        Assert.Null(Linked([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0]));
        Assert.Null(Linked(Encoding.ASCII.GetBytes("#!/bin/sh\nexec app\n")));
        Assert.Null(Linked([(byte)'M', (byte)'Z', 0, 0, 0, 0, 0, 0]));
        Assert.Null(Linked([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 65, 0, 0, 0, 0]));
    }

    private void A_program_cut_short_gives_what_is_there_and_a_size_past_its_end_reads_no_further()
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

    private void A_slice_past_the_programs_end_adds_nothing_to_those_that_are_there()
    {
        byte[] program = MachOBytes.Universal(MachOBytes.Thin(LibSystem), MachOBytes.Thin(AppKit));
        BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(8 + 20 + 8), int.MaxValue);
        Assert.Equal([LibSystem], Linked(program));
    }

    private void A_function_lies_from_its_symbol_to_the_next_of_its_section_as_offsets_into_its_image()
    {
        byte[] program = MachOBytes.Symbols(text: 0x1000, mapped: 0x9000,
            ("_first", 0x0E, 1, 0x1100), ("_stab", 0x24, 1, 0x1140), ("_elsewhere", 0x0E, 2, 0x1160), ("_undefined", 0x01, 0, 0x1170),
            ("_second", 0x0F, 1, 0x1180), ("_last", 0x0E, 1, 0x1200));
        using MemoryStream stream = new(program);

        IReadOnlyDictionary<string, (ulong Start, ulong End)>? functions = MachOFile.Functions(stream, ["_first", "_last", "_missing"]);

        Assert.Equal(new Dictionary<string, (ulong Start, ulong End)> { ["_first"] = (0x100, 0x180), ["_last"] = (0x200, 0x9000) }, functions);
    }

    private void A_program_with_no_symbol_table_or_a_table_past_its_end_names_no_function()
    {
        using MemoryStream linking = new(MachOBytes.Thin(LibSystem));
        Assert.Null(MachOFile.Functions(linking, ["_first"]));

        byte[] cut = MachOBytes.Symbols(text: 0, mapped: 0x1000, ("_first", 0x0E, 1, 0x100));
        using MemoryStream cutShort = new(cut[..^8]);
        Assert.Null(MachOFile.Functions(cutShort, ["_first"]));
        using MemoryStream universal = new(MachOBytes.Universal(cut));
        Assert.Null(MachOFile.Functions(universal, ["_first"]));
    }

    /// <summary>The runtime's own library names the routine its tiering worker starts at, which the debugger tells that
    /// thread by on macOS.</summary>
    private void The_runtimes_own_library_names_where_its_tiering_worker_starts()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Only macOS's runtime is a Mach-O library");
        const string start = "__ZN24TieredCompilationManager29BackgroundWorkerBootstrapper0EPv";
        using FileStream runtime = File.OpenRead(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "libcoreclr.dylib"));

        (ulong Start, ulong End) routine = MachOFile.Functions(runtime, [start])![start];

        Assert.True(routine.Start > 0 && routine.End > routine.Start && routine.End - routine.Start < 4096, $"The tiering worker's start lies at {routine}");
    }

    /// <summary>The system's own builds, thin or universal, are the reader's real input: read, never run.</summary>
    private void A_macs_own_tool_is_read_for_what_it_links()
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

    /// <summary>A thin program of a __TEXT segment at an address and of a size, and a symbol table of the symbols given:
    /// each its name, its type's byte, its section and its address.</summary>
    public static byte[] Symbols(ulong text, ulong mapped, params (string Name, byte Type, byte Section, ulong Address)[] symbols)
    {
        const int Header = 32;
        const int Segment = 72;
        const int Table = 24;
        using MemoryStream names = new();
        names.WriteByte(0);
        byte[] entries = new byte[16 * symbols.Length];
        for (int symbol = 0; symbol < symbols.Length; symbol++)
        {
            Span<byte> entry = entries.AsSpan(16 * symbol);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)names.Length);
            entry[4] = symbols[symbol].Type;
            entry[5] = symbols[symbol].Section;
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], symbols[symbol].Address);
            names.Write(Encoding.UTF8.GetBytes(symbols[symbol].Name + "\0"));
        }
        byte[] program = new byte[Header + Segment + Table + entries.Length + names.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(program, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(4), 0x0100000C);
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(12), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(20), Segment + Table);
        Span<byte> segment = program.AsSpan(Header, Segment);
        BinaryPrimitives.WriteUInt32LittleEndian(segment, 0x19);
        BinaryPrimitives.WriteUInt32LittleEndian(segment[4..], Segment);
        Encoding.ASCII.GetBytes("__TEXT").CopyTo(segment[8..]);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[24..], text);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[32..], mapped);
        Span<byte> table = program.AsSpan(Header + Segment, Table);
        BinaryPrimitives.WriteUInt32LittleEndian(table, 0x02);
        BinaryPrimitives.WriteUInt32LittleEndian(table[4..], Table);
        BinaryPrimitives.WriteUInt32LittleEndian(table[8..], Header + Segment + Table);
        BinaryPrimitives.WriteUInt32LittleEndian(table[12..], (uint)symbols.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(table[16..], (uint)(Header + Segment + Table + entries.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(table[20..], (uint)names.Length);
        entries.CopyTo(program, Header + Segment + Table);
        names.ToArray().CopyTo(program, Header + Segment + Table + entries.Length);
        return program;
    }

    public static byte[] Universal(params byte[][] slices) => Universal(wide: false, slices);

    /// <summary>A universal program of the slices given, each named in its table by its CPU type, where it begins and its
    /// size: 20 bytes an entry, or 32 with 64-bit offsets and sizes (<paramref name="wide"/>).</summary>
    public static byte[] Universal(bool wide, params byte[][] slices)
    {
        using MemoryStream program = new();
        int entry = wide ? 32 : 20;
        byte[] header = new byte[8 + (entry * slices.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(header, wide ? 0xCAFEBABF : 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)slices.Length);
        uint at = (uint)header.Length;
        for (int slice = 0; slice < slices.Length; slice++)
        {
            Span<byte> named = header.AsSpan(8 + (slice * entry));
            BinaryPrimitives.WriteInt32BigEndian(named, BinaryPrimitives.ReadInt32LittleEndian(slices[slice].AsSpan(4)));
            if (wide)
            {
                BinaryPrimitives.WriteUInt64BigEndian(named[8..], at);
                BinaryPrimitives.WriteUInt64BigEndian(named[16..], (ulong)slices[slice].Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(named[8..], at);
                BinaryPrimitives.WriteUInt32BigEndian(named[12..], (uint)slices[slice].Length);
            }
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
