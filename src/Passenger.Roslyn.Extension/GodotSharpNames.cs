using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Passenger.Workspace;

namespace Passenger.Roslyn.Extension;

/// <summary>The names GodotSharp gives its engine classes' members in Godot's own words (<c>Node2D.PropertyName.Position</c>
/// is <c>"position"</c>), read from the IL of the assembly's own file (<see cref="GodotSharpIl"/>), once for each GodotSharp
/// the projects reference.</summary>
internal static class GodotSharpNames
{
    private static readonly ConditionalWeakTable<IAssemblySymbol, IReadOnlyDictionary<(string Owner, string Nested, string Member), string>> Kept = [];

    /// <summary>Godot's name of a member of an engine class, by the class's nested <paramref name="nested"/>
    /// (<c>PropertyName</c>, <c>MethodName</c>, <c>SignalName</c>); null where GodotSharp names none (a member of C#'s alone).</summary>
    public static string? Of(Compilation compilation, INamedTypeSymbol owner, string nested, string member)
    {
        IAssemblySymbol assembly = owner.ContainingAssembly;
        IReadOnlyDictionary<(string, string, string), string> names = Kept.GetValue(assembly,
            a => compilation.GetMetadataReference(a) is PortableExecutableReference { FilePath: { } path } ? Read(path) : new Dictionary<(string, string, string), string>());
        return names.GetValueOrDefault((owner.MetadataName, nested, member));
    }

    /// <summary>Every name the nested classes' static constructors set in an assembly's file (<see cref="GodotSharpIl"/>); none
    /// for a file that cannot be read.</summary>
    private static Dictionary<(string Owner, string Nested, string Member), string> Read(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            return GodotSharpIl.NamesOf(pe, pe.GetMetadataReader(), [GodotSharpIl.PropertyName, GodotSharpIl.MethodName, GodotSharpIl.SignalName]);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // An assembly that cannot be read names nothing: its members' pages are their classes'.
            return [];
        }
    }
}
