using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Passenger.Tests;

/// <summary>Where a built assembly lets go of a task a call returned: the IL pops it (a discard, a call as a statement, a
/// lambda for a delegate that returns nothing) or stores it in a lambda's parameter named _, placed by the PDB. Read
/// from the IL, which knows each call's type, as the source alone does not. Not counted: an awaited result (the task
/// Task.WhenAny finished with), and what a static Observe(Task) lets go of, the observers being where others' tasks go.</summary>
internal static class DroppedTasks
{
    public static IReadOnlyList<(string File, int Line)> In(string assembly)
    {
        using PEReader pe = new(File.OpenRead(assembly));
        MetadataReader metadata = pe.GetMetadataReader();
        using MetadataReaderProvider symbols = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(assembly, ".pdb")));
        MetadataReader debug = symbols.GetMetadataReader();
        Signatures signatures = new(metadata);
        List<(string, int)> found = [];
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0 || metadata.GetString(method.Name) == "Observe" && signatures.TakesOnlyATask(method))
            {
                continue;
            }
            SequencePoint[] points = [.. debug.GetMethodDebugInformation(handle.ToDebugInformationHandle()).GetSequencePoints().Where(p => !p.IsHidden)];
            bool instance = metadata.GetBlobReader(method.Signature).ReadSignatureHeader().IsInstance;
            string[] parameters = [.. method.GetParameters().Select(metadata.GetParameter).Where(p => p.SequenceNumber > 0)
                .OrderBy(p => p.SequenceNumber).Select(p => metadata.GetString(p.Name))];
            BlobReader il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            int? task = null;
            while (il.RemainingBytes > 0)
            {
                int offset = il.Offset;
                int code = il.ReadByte();
                code = code == 0xFE ? 0xFE00 | il.ReadByte() : code;
                int stored = -1;
                bool returnsTask = false;
                if (code is 0x28 or 0x6F)
                {
                    returnsTask = signatures.ReturnsTask(MetadataTokens.EntityHandle(il.ReadInt32()));
                }
                else if (code is 0x10 or 0xFE0B)
                {
                    stored = (code == 0x10 ? il.ReadByte() : il.ReadUInt16()) - (instance ? 1 : 0);
                }
                else
                {
                    int operand = OperandSize(ref il, code);
                    il.Offset += operand;
                }
                bool dropped = code == 0x26 || stored >= 0 && stored < parameters.Length && parameters[stored] == "_";
                if (task is { } called && dropped && points.LastOrDefault(p => p.Offset <= called) is { Document.IsNil: false } at)
                {
                    found.Add((debug.GetString(debug.GetDocument(at.Document).Name), at.StartLine));
                }
                task = returnsTask ? offset : null;
            }
        }
        return [.. found.Distinct()];
    }

    /// <summary>The bytes of an instruction's operand after its opcode; a switch's, its targets after their count.</summary>
    private static int OperandSize(ref BlobReader il, int code) => code switch
    {
        0x0E or 0x0F or 0x11 or 0x12 or 0x13 or 0x1F or (>= 0x2B and <= 0x37) or 0xDE or 0xFE12 or 0xFE19 => 1,
        0xFE09 or 0xFE0A or 0xFE0C or 0xFE0D or 0xFE0E => 2,
        0x20 or 0x22 or 0x27 or 0x29 or (>= 0x38 and <= 0x44) or (>= 0x70 and <= 0x75) or 0x79 or (>= 0x7B and <= 0x81) or 0x8C or 0x8D
            or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 or 0xDD or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C => 4,
        0x21 or 0x23 => 8,
        0x45 => il.ReadInt32() * 4,
        _ => 0,
    };

    private sealed record TypeName(string Name, ImmutableArray<TypeName> Arguments);

    /// <summary>A called method's return type, a generic one's parameter replaced by what its type or call gives it.</summary>
    private sealed class Signatures(MetadataReader metadata) : ISignatureTypeProvider<TypeName, object?>
    {
        private static readonly TypeName Other = new("?", []);

        /// <summary>A static method whose one parameter is a Task: an observer's shape.</summary>
        public bool TakesOnlyATask(MethodDefinition method)
        {
            MethodSignature<TypeName> signature = method.DecodeSignature(this, null);
            return !signature.Header.IsInstance && signature.ParameterTypes is [{ Name: "System.Threading.Tasks.Task" }];
        }

        public bool ReturnsTask(EntityHandle called)
        {
            ImmutableArray<TypeName> methodArguments = [];
            if (called.Kind == HandleKind.MethodSpecification)
            {
                MethodSpecification generic = metadata.GetMethodSpecification((MethodSpecificationHandle)called);
                methodArguments = generic.DecodeSignature(this, null);
                called = generic.Method;
            }
            ImmutableArray<TypeName> typeArguments = [];
            string name;
            BlobHandle signature;
            if (called.Kind == HandleKind.MethodDefinition)
            {
                MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)called);
                (name, signature) = (metadata.GetString(method.Name), method.Signature);
            }
            else if (called.Kind == HandleKind.MemberReference)
            {
                MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)called);
                (name, signature) = (metadata.GetString(member.Name), member.Signature);
                if (member.Parent.Kind == HandleKind.TypeSpecification)
                {
                    typeArguments = metadata.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(this, null).Arguments;
                }
            }
            else
            {
                return false;
            }
            BlobReader reader = metadata.GetBlobReader(signature);
            if (name == "GetResult" || reader.ReadSignatureHeader().Kind != SignatureKind.Method)
            {
                return false;
            }
            reader.Reset();
            TypeName returned = new SignatureDecoder<TypeName, object?>(this, metadata, null).DecodeMethodSignature(ref reader).ReturnType;
            returned = returned.Name switch
            {
                ['!', '!', .. string index] when int.Parse(index) < methodArguments.Length => methodArguments[int.Parse(index)],
                ['!', .. string index] when !index.StartsWith('!') && int.Parse(index) < typeArguments.Length => typeArguments[int.Parse(index)],
                _ => returned,
            };
            return returned.Name is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask"
                or "System.Threading.Tasks.ValueTask`1";
        }

        public TypeName GetArrayType(TypeName elementType, ArrayShape shape) => Other;

        public TypeName GetByReferenceType(TypeName elementType) => Other;

        public TypeName GetFunctionPointerType(MethodSignature<TypeName> signature) => Other;

        public TypeName GetGenericInstantiation(TypeName genericType, ImmutableArray<TypeName> typeArguments) => genericType with { Arguments = typeArguments };

        public TypeName GetGenericMethodParameter(object? genericContext, int index) => new($"!!{index}", []);

        public TypeName GetGenericTypeParameter(object? genericContext, int index) => new($"!{index}", []);

        public TypeName GetModifiedType(TypeName modifier, TypeName unmodifiedType, bool isRequired) => unmodifiedType;

        public TypeName GetPinnedType(TypeName elementType) => elementType;

        public TypeName GetPointerType(TypeName elementType) => Other;

        public TypeName GetPrimitiveType(PrimitiveTypeCode typeCode) => Other;

        public TypeName GetSZArrayType(TypeName elementType) => Other;

        public TypeName GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new($"{reader.GetString(reader.GetTypeDefinition(handle).Namespace)}.{reader.GetString(reader.GetTypeDefinition(handle).Name)}", []);

        public TypeName GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            new($"{reader.GetString(reader.GetTypeReference(handle).Namespace)}.{reader.GetString(reader.GetTypeReference(handle).Name)}", []);

        public TypeName GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
