using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace Passenger.WpfPreview;

/// <summary>The assemblies the project references, as the app has them: one asked for by name that the runtime's own
/// list lacks (a project reference's, copied beside the project's assembly, which the dependencies name in a folder the
/// previewer does not run from) is loaded from the project's folder; and every one declaring XAML namespaces
/// (<c>XmlnsDefinitionAttribute</c>: Microsoft.Xaml.Behaviors' <c>http://schemas.microsoft.com/xaml/behaviors</c>) is
/// loaded before any XAML is read, as WPF maps a namespace's URI only among the assemblies already loaded, where the app's
/// compiled XAML names them itself.</summary>
internal static class ProjectAssemblies
{
    /// <summary>XmlnsDefinitionAttribute's namespace and name, in System.Xaml and WindowsBase alike.</summary>
    private const string MarkupNamespace = "System.Windows.Markup";

    private const string Definition = "XmlnsDefinitionAttribute";

    public static void Take(Assembly project, string folder)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) => Beside(folder, name) is { } file ? context.LoadFromAssemblyPath(file) : null;
        HashSet<string> framework = new(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty,
            Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location) ?? string.Empty,
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
        };
        IEnumerable<string> listed = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> beside = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.dll") : [];
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase) { project.GetName().Name ?? string.Empty };
        foreach (string file in listed.Where(f => !framework.Contains(Path.GetDirectoryName(f) ?? string.Empty)).Concat(beside))
        {
            if (!named.Add(Path.GetFileNameWithoutExtension(file)) || !DeclaresXamlNamespaces(file))
            {
                continue;
            }
            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName.GetAssemblyName(file));
            }
            catch (Exception error) when (error is IOException or BadImageFormatException)
            {
                Console.Error.WriteLine($"{file}, which declares XAML namespaces, did not load: {error.Message}");
            }
        }
    }

    /// <summary>The file of an assembly asked for by name in the project's folder, or null.</summary>
    private static string? Beside(string folder, AssemblyName name) =>
        name.Name is { } asked && Path.Combine(folder, asked + ".dll") is { } file && File.Exists(file) ? file : null;

    /// <summary>Whether a file is an assembly with an XmlnsDefinitionAttribute of its own, read from its metadata alone,
    /// nothing of it loaded.</summary>
    private static bool DeclaresXamlNamespaces(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            using PEReader image = new(stream);
            if (!image.HasMetadata)
            {
                return false;
            }
            MetadataReader metadata = image.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return false;
            }
            foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                EntityHandle constructor = metadata.GetCustomAttribute(handle).Constructor;
                EntityHandle type = constructor.Kind == HandleKind.MemberReference ? metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent : default;
                if (type.Kind != HandleKind.TypeReference)
                {
                    continue;
                }
                TypeReference reference = metadata.GetTypeReference((TypeReferenceHandle)type);
                if (metadata.StringComparer.Equals(reference.Name, Definition) && metadata.StringComparer.Equals(reference.Namespace, MarkupNamespace))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
