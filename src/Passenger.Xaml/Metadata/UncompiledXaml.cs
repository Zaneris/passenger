using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Passenger.Xaml.Metadata;

/// <summary>Built assemblies holding Avalonia XAML that was never compiled. Avalonia's XAML compiler, run on the assembly as
/// the C# compiler wrote it, takes the XAML files' text out of Avalonia's resources and adds the loader Avalonia's runtime
/// looks for; an assembly whose resources still hold XAML text and that has no such loader is one it never finished,
/// whose classes the runtime refuses to load (<see cref="RuntimeRefusal"/>). Read from the file, which is never held.</summary>
public static class UncompiledXaml
{
    /// <summary>What Avalonia's runtime throws on loading a class whose XAML was never compiled, before the class's name.</summary>
    public const string RuntimeRefusal = "No precompiled XAML found for ";

    /// <summary>The namespace and name of the loader Avalonia's XAML compiler adds.</summary>
    private const string LoaderNamespace = "CompiledAvaloniaXaml";
    private const string LoaderName = "!XamlLoader";

    /// <summary>The extensions of the files Avalonia's XAML compiler compiles.</summary>
    private static readonly string[] Extensions = [".axaml", ".xaml", ".paml"];

    /// <summary>Whether an assembly holds Avalonia XAML never compiled; false for one holding none, or that does not read.</summary>
    public static bool In(string assembly)
    {
        try
        {
            using FileStream file = new(assembly, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using PEReader pe = new(file, PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
            {
                return false;
            }
            MetadataReader reader = pe.GetMetadataReader();
            if (AvaloniaResourceIndex.At(pe, reader) is not { } resources)
            {
                return false;
            }
            file.Position = resources;
            return AvaloniaResourceIndex.Read(file) is { } index && index.Entries.Any(e => Extensions.Any(x => e.Path.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                && !reader.TypeDefinitions.Any(handle => reader.GetTypeDefinition(handle) is var type
                    && reader.StringComparer.Equals(type.Namespace, LoaderNamespace) && reader.StringComparer.Equals(type.Name, LoaderName));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The assemblies directly in a folder that hold Avalonia XAML never compiled (<see cref="In"/>), by name; none
    /// for a folder that does not read.</summary>
    public static IReadOnlyList<string> AssembliesIn(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.dll").Where(In).Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
