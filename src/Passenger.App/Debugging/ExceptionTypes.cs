using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>The exception types Add CLR Exception Breakpoints offers as a name is typed: every
/// public exception of the newest .NET runtime the IDE's dotnet has, read once from its assemblies' metadata, and the
/// solution's own, its classes named for exceptions as the language server finds them.</summary>
public static class ExceptionTypes
{
    /// <summary>The runtime's, read once for the process: the machine's dotnet is every window's.</summary>
    private static Task<IReadOnlyList<string>>? runtime;

    /// <summary>The runtime's exception types by full name, in order; none when no runtime can be read.</summary>
    public static Task<IReadOnlyList<string>> Runtime() => runtime ??= BackgroundWork.Run(ReadRuntime);

    /// <summary>The full names of the solution's classes a workspace symbol search for "Exception" finds whose names end so.</summary>
    public static IReadOnlyList<string> FromSymbols(IEnumerable<Lsp.SymbolInformation> symbols) =>
        [.. symbols.Where(s => s.Kind == Lsp.SymbolKind.Class && s.Name.EndsWith("Exception", StringComparison.Ordinal))
            .Select(s => s.ContainerName is { Length: > 0 } container ? $"{container}.{s.Name}" : s.Name)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The types that match what is typed, best first: those whose name starts with it, then those that contain it,
    /// each by name; a name with dots is matched against the full name.</summary>
    public static IReadOnlyList<string> Matching(IEnumerable<string> types, string typed)
    {
        string query = typed.Trim();
        if (query.Length == 0)
        {
            return [.. types.OrderBy(Name, StringComparer.OrdinalIgnoreCase)];
        }
        bool full = query.Contains('.');
        return [.. types
            .Select(type => (Type: type, Text: full ? type : Name(type)))
            .Select(t => (t.Type, At: t.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t.At >= 0)
            .OrderBy(t => t.At == 0 ? 0 : 1)
            .ThenBy(t => Name(t.Type), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Type, StringComparer.Ordinal)
            .Select(t => t.Type)];
    }

    private static string Name(string type) => type[(type.LastIndexOf('.') + 1)..];

    private static IReadOnlyList<string> ReadRuntime()
    {
        try
        {
            string shared = Path.Combine(Path.GetDirectoryName(Dotnet.Host())!, "shared", "Microsoft.NETCore.App");
            string? newest = Directory.Exists(shared)
                ? Directory.GetDirectories(shared).OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out Version? v) ? v : new Version()).FirstOrDefault()
                : null;
            return newest is null ? [] : Exceptions(Directory.GetFiles(newest, "*.dll"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeLog.Write(IdeLog.Debugger, $"The runtime's exception types could not be read: {error.Message}");
            return [];
        }
    }

    /// <summary>The public types of these assemblies that derive from System.Exception, by their base types' names.</summary>
    private static IReadOnlyList<string> Exceptions(IEnumerable<string> assemblies)
    {
        Dictionary<string, string?> bases = new(StringComparer.Ordinal);
        foreach (string assembly in assemblies)
        {
            try
            {
                using FileStream stream = File.OpenRead(assembly);
                using PEReader pe = new(stream);
                if (!pe.HasMetadata)
                {
                    continue;
                }
                MetadataReader metadata = pe.GetMetadataReader();
                foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
                {
                    TypeDefinition type = metadata.GetTypeDefinition(handle);
                    if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                    {
                        continue;
                    }
                    bases[$"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}"] = BaseName(metadata, type.BaseType);
                }
            }
            catch (BadImageFormatException)
            {
                // A native library among the managed ones.
            }
        }
        Dictionary<string, bool> known = new(StringComparer.Ordinal) { ["System.Exception"] = true };
        return [.. bases.Keys.Where(IsException).Order(StringComparer.Ordinal)];

        bool IsException(string name)
        {
            if (known.TryGetValue(name, out bool found))
            {
                return found;
            }
            known[name] = false;
            bool derives = bases.TryGetValue(name, out string? baseName) && baseName is not null && IsException(baseName);
            known[name] = derives;
            return derives;
        }
    }

    /// <summary>A base type's full name; none for System.Object or an interface (whose base is a nil definition), nor a
    /// generic instance.</summary>
    private static string? BaseName(MetadataReader metadata, EntityHandle handle) => handle.IsNil ? null : handle.Kind switch
    {
        HandleKind.TypeReference when metadata.GetTypeReference((TypeReferenceHandle)handle) is var reference =>
            $"{metadata.GetString(reference.Namespace)}.{metadata.GetString(reference.Name)}",
        HandleKind.TypeDefinition when metadata.GetTypeDefinition((TypeDefinitionHandle)handle) is var definition =>
            $"{metadata.GetString(definition.Namespace)}.{metadata.GetString(definition.Name)}",
        _ => null,
    };
}
