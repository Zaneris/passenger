using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Debugger.Relay;

namespace Passenger.UnitTests.Debugger;

/// <summary>A module's metadata an app on a device sends, opened on the host only as a module's metadata is laid out: a
/// compiler's passes, and metadata whose root, stream headers, streams or tables run past what was sent, whose storage
/// flags move its stream headers, or whose streams the host's reader would find otherwise than the check read them, is
/// refused before the host's reader sees it.</summary>
public class DeviceMetadataTests
{
    [Fact]
    public void A_compilers_metadata_is_opened_and_one_whose_layout_runs_past_what_was_sent_or_reads_otherwise_is_refused() => Cases.All(
        ("a compiler's metadata is opened", () => Assert.True(DeviceMetadata.Refused(Metadata()) is null, $"A compiler's metadata was refused: {DeviceMetadata.Refused(Metadata())}")),
        ("metadata whose layout runs past what was sent or reads otherwise is refused", () => Cases.Each(
        [
            "its storage flags say it has extra data before its stream headers",
            "its #GUID stream is named #JTD, a minimal delta's",
            "its #Blob stream is named #blob",
            "its #US stream is named #~, a second stream of tables",
            "its #Blob stream runs past its end",
            "it says it has one stream more than its headers",
            "its version string has no end",
            "its TypeDef table has more rows than its stream holds",
            "it is cut short in its tables",
        ], how => Assert.True(DeviceMetadata.Refused(Malformed(Metadata(), how)) is not null, $"Metadata where {how} was opened by the host's reader"))));

    /// <summary><paramref name="metadata"/> changed as <paramref name="how"/> says.</summary>
    private static byte[] Malformed(byte[] metadata, string how)
    {
        int version = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(12));
        int flags = 16 + version;
        switch (how)
        {
            case "its storage flags say it has extra data before its stream headers":
                metadata[flags] = 1;
                return metadata;
            case "its #GUID stream is named #JTD, a minimal delta's":
                Encoding.ASCII.GetBytes("#JTD\0\0\0\0").CopyTo(metadata, NameOf(metadata, "#GUID"));
                return metadata;
            case "its #Blob stream is named #blob":
                Encoding.ASCII.GetBytes("#blob").CopyTo(metadata, NameOf(metadata, "#Blob"));
                return metadata;
            case "its #US stream is named #~, a second stream of tables":
                Encoding.ASCII.GetBytes("#~\0\0").CopyTo(metadata, NameOf(metadata, "#US"));
                return metadata;
            case "its #Blob stream runs past its end":
                BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(NameOf(metadata, "#Blob") - 4), metadata.Length);
                return metadata;
            case "it says it has one stream more than its headers":
                metadata[flags + 2]++;
                return metadata;
            case "its version string has no end":
                metadata.AsSpan(16, version).Fill((byte)'v');
                return metadata;
            case "its TypeDef table has more rows than its stream holds":
                // Past the stream's 24-byte header, the row counts of Module, TypeRef and TypeDef.
                int tables = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(NameOf(metadata, "#~") - 8));
                BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(tables + 24 + 8), 0xFFFFFF);
                return metadata;
            case "it is cut short in its tables":
                return metadata[..(BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(NameOf(metadata, "#~") - 8)) + 40)];
            default:
                throw new ArgumentOutOfRangeException(nameof(how), how, "No such change");
        }
    }

    /// <summary>Where the name of the stream <paramref name="name"/> lies among <paramref name="metadata"/>'s stream headers.</summary>
    private static int NameOf(byte[] metadata, string name)
    {
        int at = 16 + BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(12)) + 4;
        int streams = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(at - 2));
        for (int i = 0; i < streams; i++)
        {
            int end = Array.IndexOf(metadata, (byte)0, at + 8);
            if (Encoding.ASCII.GetString(metadata, at + 8, end - at - 8) == name)
            {
                return at + 8;
            }
            at += 8 + ((end - at - 8 + 4) & ~3);
        }
        throw new InvalidOperationException($"The metadata has no {name} stream");
    }

    /// <summary>A small module's metadata as a compiler writes it.</summary>
    private static byte[] Metadata()
    {
        CSharpCompilation compilation = CSharpCompilation.Create("Shop", [CSharpSyntaxTree.ParseText("public static class Prices { public const int Tax = 20; public static string Name = \"Shop\"; }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream image = new();
        Assert.True(compilation.Emit(image).Success, "The module did not build");
        image.Position = 0;
        using PEReader reader = new(image);
        return [.. reader.GetMetadata().GetContent()];
    }
}
