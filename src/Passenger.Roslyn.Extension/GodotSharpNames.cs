using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Passenger.Roslyn.Extension;

/// <summary>The names GodotSharp gives its engine classes' members in Godot's own words: each class's nested
/// <c>PropertyName</c>, <c>MethodName</c> and <c>SignalName</c> hold a <c>StringName</c> field of each member's C# name, its
/// value Godot's name (<c>Node2D.PropertyName.Position</c> is <c>"position"</c>), which their static constructors set and
/// metadata never says: read from the IL of the assembly's own file, once for each GodotSharp the projects reference.</summary>
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

    /// <summary>Every name the nested classes' static constructors set in an assembly's file: each field stored with the
    /// string last loaded before it (<c>ldstr</c>, the <c>StringName</c> conversion's call, <c>stsfld</c>); none for a file
    /// that cannot be read, and a constructor's fields up to an instruction other than those.</summary>
    private static Dictionary<(string Owner, string Nested, string Member), string> Read(string path)
    {
        Dictionary<(string, string, string), string> names = [];
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                string nested = metadata.GetString(type.Name);
                if (nested is not ("PropertyName" or "MethodName" or "SignalName") || type.GetDeclaringType().IsNil)
                {
                    continue;
                }
                string owner = metadata.GetString(metadata.GetTypeDefinition(type.GetDeclaringType()).Name);
                foreach (MethodDefinition method in type.GetMethods().Select(metadata.GetMethodDefinition).Where(m => metadata.GetString(m.Name) == ".cctor" && m.RelativeVirtualAddress != 0))
                {
                    BlobReader il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                    string? loaded = null;
                    while (il.RemainingBytes > 0)
                    {
                        byte code = il.ReadByte();
                        if (code == (byte)ILOpCode.Ldstr)
                        {
                            loaded = metadata.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32() & 0xFFFFFF));
                        }
                        else if (code is (byte)ILOpCode.Call or (byte)ILOpCode.Newobj)
                        {
                            il.ReadInt32();
                        }
                        else if (code == (byte)ILOpCode.Stsfld && MetadataTokens.EntityHandle(il.ReadInt32()) is { Kind: HandleKind.FieldDefinition } field && loaded is not null)
                        {
                            names[(owner, nested, metadata.GetString(metadata.GetFieldDefinition((FieldDefinitionHandle)field).Name))] = loaded;
                        }
                        else if (code != (byte)ILOpCode.Nop)
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // An assembly that cannot be read names nothing: its members' pages are their classes'.
        }
        return names;
    }
}
