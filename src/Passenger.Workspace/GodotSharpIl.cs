using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Passenger.Workspace;

/// <summary>The names GodotSharp gives its engine classes' members in Godot's own words, read from its IL: each class's
/// nested <c>PropertyName</c>, <c>MethodName</c> and <c>SignalName</c> hold a <c>StringName</c> field of each member's C#
/// name, its value Godot's name (<c>Node2D.MethodName.GetTree</c> is <c>"get_tree"</c>), which their static constructors
/// set and metadata never says. Framework-free, since Passenger's extension inside Roslyn's server compiles it in.</summary>
internal static class GodotSharpIl
{
    /// <summary>The nested class naming an engine class's properties.</summary>
    public const string PropertyName = "PropertyName";

    /// <summary>The nested class naming an engine class's methods.</summary>
    public const string MethodName = "MethodName";

    /// <summary>The nested class naming an engine class's signals.</summary>
    public const string SignalName = "SignalName";

    /// <summary>Every name the asked nested classes' static constructors set: each field stored with the string last loaded
    /// before it (<c>ldstr</c>, the <c>StringName</c> conversion's call, <c>stsfld</c>), by its owner's C# name, the nested
    /// class and the field; a constructor's fields up to an instruction other than those.</summary>
    public static Dictionary<(string Owner, string Nested, string Member), string> NamesOf(PEReader pe, MetadataReader metadata, IReadOnlyCollection<string> asked)
    {
        Dictionary<(string, string, string), string> names = [];
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            string nested = metadata.GetString(type.Name);
            if (!asked.Contains(nested) || type.GetDeclaringType().IsNil)
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
        return names;
    }
}
