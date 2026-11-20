using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Passenger.Xaml.Metadata;

/// <summary>The index of Avalonia's resources as an assembly embeds them (<c>!AvaloniaResources</c>), as Avalonia 11 and
/// later write it: the resources' length, the index's size, its version (2) and each entry's path, offset and size, the
/// entries' bytes after it. Read from where a stream stands at the resources' length, the entries themselves left unread.</summary>
/// <param name="Start">Where the resources start in the stream, past their length.</param>
public sealed record AvaloniaResourceIndex(long Start, long Length, int IndexSize, IReadOnlyList<AvaloniaResourceEntry> Entries)
{
    /// <summary>The name an assembly gives Avalonia's resources among its own.</summary>
    public const string ResourceName = "!AvaloniaResources";

    /// <summary>Where an assembly's file holds Avalonia's resources, at their length; null for one that embeds none.</summary>
    public static long? At(PEReader pe, MetadataReader reader)
    {
        if (!reader.IsAssembly || pe.PEHeaders.CorHeader is not { } cor || !pe.PEHeaders.TryGetDirectoryOffset(cor.ResourcesDirectory, out int resources))
        {
            return null;
        }
        foreach (ManifestResourceHandle handle in reader.ManifestResources)
        {
            ManifestResource resource = reader.GetManifestResource(handle);
            if (resource.Implementation.IsNil && reader.StringComparer.Equals(resource.Name, ResourceName))
            {
                return resources + resource.Offset;
            }
        }
        return null;
    }

    /// <summary>The index read from where a stream stands; null where it is not as Avalonia 11 and later write it.</summary>
    public static AvaloniaResourceIndex? Read(Stream resources)
    {
        using BinaryReader read = new(resources, System.Text.Encoding.UTF8, leaveOpen: true);
        long length = read.ReadUInt32();
        long start = resources.Position;
        int indexSize = read.ReadInt32();
        if (indexSize < 8 || indexSize > length - 4 || read.ReadInt32() != 2)
        {
            return null;
        }
        int count = read.ReadInt32();
        List<AvaloniaResourceEntry> entries = [];
        for (int i = 0; i < count && resources.Position < start + 4 + indexSize; i++)
        {
            entries.Add(new AvaloniaResourceEntry(read.ReadString(), read.ReadInt32(), read.ReadInt32()));
        }
        return new AvaloniaResourceIndex(start, length, indexSize, entries);
    }

    /// <summary>Where an entry's bytes start in the stream; null where they do not lie within the resources, or are more than
    /// <paramref name="most"/> bytes.</summary>
    public long? DataOf(AvaloniaResourceEntry entry, int most) =>
        entry.Offset < 0 || entry.Size < 0 || entry.Size > most || 4L + IndexSize + entry.Offset + entry.Size > Length ? null : Start + 4 + IndexSize + entry.Offset;
}

/// <summary>An entry of Avalonia's resources: its path in the project (<c>/Views/MainWindow.axaml</c>), and where its bytes
/// are past the index and how many.</summary>
public sealed record AvaloniaResourceEntry(string Path, int Offset, int Size);
