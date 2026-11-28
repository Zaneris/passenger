using System.IO.Compression;
using System.Reflection.Metadata;
using System.Text;

namespace Passenger.Core;

/// <summary>What a build's portable PDB embeds of its sources: the code a source generator wrote is embedded whole, so the
/// code a program runs is read there as its build made it. Framework-free, since Passenger's extension inside Roslyn's
/// server compiles it in.</summary>
public static class PortablePdbs
{
    /// <summary>The kind of custom debug information that holds a document's embedded text.</summary>
    private static readonly Guid EmbeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    /// <summary>Each document of the PDB at <paramref name="pdb"/> whose name <paramref name="named"/> takes and whose text it
    /// embeds, by its name as the PDB records it; none where the PDB is not there, is being written, or is no portable one.</summary>
    public static IReadOnlyList<(string Document, string Text)> EmbeddedSources(string pdb, Func<string, bool> named)
    {
        List<(string, string)> found = [];
        try
        {
            using FileStream stream = File.OpenRead(pdb);
            using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            MetadataReader reader = provider.GetMetadataReader();
            foreach (DocumentHandle handle in reader.Documents)
            {
                string name = reader.GetString(reader.GetDocument(handle).Name);
                if (!named(name))
                {
                    continue;
                }
                foreach (CustomDebugInformationHandle information in reader.GetCustomDebugInformation(handle))
                {
                    CustomDebugInformation embedded = reader.GetCustomDebugInformation(information);
                    if (reader.GetGuid(embedded.Kind) == EmbeddedSource)
                    {
                        byte[] blob = reader.GetBlobBytes(embedded.Value);
                        found.Add((name, Encoding.UTF8.GetString(BitConverter.ToInt32(blob, 0) == 0 ? blob[4..] : Inflated(blob[4..]))));
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException)
        {
            // No PDB a build finished writing: nothing of it is read.
        }
        return found;
    }

    private static byte[] Inflated(byte[] compressed)
    {
        using MemoryStream inflated = new();
        using (DeflateStream deflate = new(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            deflate.CopyTo(inflated);
        }
        return inflated.ToArray();
    }
}
