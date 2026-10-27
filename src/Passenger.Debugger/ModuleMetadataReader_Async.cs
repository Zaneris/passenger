using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.Debugger;

public partial class ModuleMetadataReader
{
    private (int MethodToken, int ILOffset)? TryResolveAsyncMainEntryPoint(TypeDefinitionHandle declaringTypeHandle)
    {
        TypeDefinition declaringType = _peMetadataReader.GetTypeDefinition(declaringTypeHandle);
        foreach (MethodDefinitionHandle kickoffMethodHandle in declaringType.GetMethods())
        {
            MethodDefinition kickoffMethod = _peMetadataReader.GetMethodDefinition(kickoffMethodHandle);
            if (_peMetadataReader.GetString(kickoffMethod.Name) is not ("Main" or "<Main>$"))
            {
                continue;
            }

            foreach (TypeDefinitionHandle stateMachineHandle in declaringType.GetNestedTypes())
            {
                if (!kickoffMethod.GetCustomAttributes().Any(attribute => StateMachineAttributeMatches(attribute, stateMachineHandle)))
                {
                    continue;
                }

                TypeDefinition stateMachine = _peMetadataReader.GetTypeDefinition(stateMachineHandle);
                foreach (MethodDefinitionHandle methodHandle in stateMachine.GetMethods())
                {
                    MethodDefinition method = _peMetadataReader.GetMethodDefinition(methodHandle);
                    if (_peMetadataReader.GetString(method.Name) is not "MoveNext")
                    {
                        continue;
                    }

                    int methodToken = MetadataTokens.GetToken(methodHandle);
                    return (methodToken, GetNextUserCodeIlOffset(methodToken, 0) ?? 0);
                }
            }
        }

        return null;
    }

    /// <summary>An await of a runtime-async method: the instruction before it (the call it awaits, when it awaits a call), the
    /// call to the runtime's await helper, and the IL after it, where the method goes on once what it awaited is done.</summary>
    internal readonly record struct RuntimeAwait(int Before, int Call, int After);

    /// <summary>Whether a method is compiled with runtime async (MethodImplAttributes.Async): no state machine, its awaits
    /// the runtime's, its run left and later called again at each await that suspends.</summary>
    internal bool IsRuntimeAsync(int methodToken) =>
        MetadataTokens.EntityHandle(methodToken).Kind == HandleKind.MethodDefinition
        && ((int)_peMetadataReader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(methodToken)).ImplAttributes & RuntimeAsyncFlag) != 0;

    /// <summary>MethodImplAttributes.Async, which the metadata reader's enum does not name.</summary>
    private const int RuntimeAsyncFlag = 0x2000;

    /// <summary>A runtime-async method's awaits in IL order: its calls to AsyncHelpers' Await methods. None for any other
    /// method.</summary>
    internal IReadOnlyList<RuntimeAwait> RuntimeAwaits(int methodToken, int? version = null)
    {
        if (!IsRuntimeAsync(methodToken) || MethodIL(methodToken, version) is not { } il)
        {
            return [];
        }
        IReadOnlyList<CilInstruction> instructions = CilInstructionDecoder.Decode(il);
        List<RuntimeAwait> awaits = [];
        for (int i = 0; i + 1 < instructions.Count; i++)
        {
            if (IsRuntimeAwait(instructions[i]))
            {
                awaits.Add(new RuntimeAwait(i > 0 ? instructions[i - 1].Offset : -1, instructions[i].Offset, instructions[i + 1].Offset));
            }
        }
        return awaits;
    }

    /// <summary>The await of the call a runtime-async method's frame is in, its IP at or before that call (the start of the
    /// compiled code's place around it): the first call from there awaited at once, or an await itself. None when the
    /// first call is not awaited at once, made through its callee's task-returning thunk.</summary>
    internal RuntimeAwait? AwaitOfCallAt(int methodToken, int? version, int ip)
    {
        if (RuntimeAwaits(methodToken, version).FirstOrDefault(awaited => awaited.Call >= ip) is not { Call: > 0 } next || MethodIL(methodToken, version) is not { } il)
        {
            return null;
        }
        bool calledFirst = CilInstructionDecoder.Decode(il).Any(instruction => instruction.Offset >= ip && instruction.Offset < next.Before
            && instruction.Code is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Calli or ILOpCode.Newobj);
        return calledFirst ? null : next;
    }

    /// <summary>Where a runtime-async method goes on after an await: the first IL offset its compiled code maps
    /// (<paramref name="mapped"/>) on the way on from the await, straight on or through an unconditional branch or leave.
    /// <see cref="Returns"/> when the method returns first; null when the way on branches or throws first.</summary>
    internal int? ResumeAfter(int methodToken, int? version, RuntimeAwait awaited, IReadOnlySet<int> mapped)
    {
        if (MethodIL(methodToken, version) is not { } il)
        {
            return null;
        }
        IReadOnlyList<CilInstruction> instructions = CilInstructionDecoder.Decode(il);
        Dictionary<int, int> index = instructions.Select((instruction, at) => (instruction.Offset, at)).ToDictionary();
        HashSet<int> passed = [];
        int offset = awaited.After;
        while (passed.Add(offset) && index.TryGetValue(offset, out int at))
        {
            if (mapped.Contains(offset))
            {
                return offset;
            }
            CilInstruction instruction = instructions[at];
            switch (instruction.OpCode.FlowControl)
            {
                case FlowControl.Return when instruction.Code is ILOpCode.Ret:
                    return Returns;
                case FlowControl.Branch:
                    offset = instruction.Target;
                    break;
                case FlowControl.Next or FlowControl.Call or FlowControl.Meta when at + 1 < instructions.Count:
                    offset = instructions[at + 1].Offset;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    /// <summary>What <see cref="ResumeAfter"/> answers for an await its method returns right after.</summary>
    internal const int Returns = -1;

    /// <summary>Whether an instruction calls one of AsyncHelpers' Await methods, through which a runtime-async method awaits.</summary>
    private bool IsRuntimeAwait(CilInstruction instruction) =>
        instruction.Code is ILOpCode.Call && DescribeCall(instruction.Token) is { DeclaringType: "AsyncHelpers" } called
        && (called.Name.StartsWith("Await", StringComparison.Ordinal) || called.Name.StartsWith("UnsafeAwait", StringComparison.Ordinal));

    public int? GetStateMachineKickoffMethodToken(int moveNextMethodToken)
    {
        MethodDefinitionHandle moveNextHandle = MetadataTokens.MethodDefinitionHandle(moveNextMethodToken);
        if (_pdbMetadataReader is not null)
        {
            MethodDefinitionHandle kickoffHandle = _pdbMetadataReader.GetMethodDebugInformation(moveNextHandle).GetStateMachineKickoffMethod();
            if (kickoffHandle.IsNil is false)
            {
                return MetadataTokens.GetToken(kickoffHandle);
            }
        }

        // From metadata alone: the type the kickoff method's state machine attribute names, so overloads stay apart.
        MethodDefinition moveNext = _peMetadataReader.GetMethodDefinition(moveNextHandle);
        TypeDefinitionHandle stateMachineHandle = moveNext.GetDeclaringType();
        TypeDefinition stateMachineType = _peMetadataReader.GetTypeDefinition(stateMachineHandle);
        TypeDefinitionHandle declaringTypeHandle = stateMachineType.GetDeclaringType();
        if (declaringTypeHandle.IsNil)
        {
            return null;
        }

        foreach (MethodDefinitionHandle candidateHandle in _peMetadataReader.GetTypeDefinition(declaringTypeHandle).GetMethods())
        {
            MethodDefinition candidate = _peMetadataReader.GetMethodDefinition(candidateHandle);
            if (candidate.GetCustomAttributes().Any(attribute => StateMachineAttributeMatches(attribute, stateMachineHandle)))
            {
                return MetadataTokens.GetToken(candidateHandle);
            }
        }

        return null;
    }

    private bool StateMachineAttributeMatches(CustomAttributeHandle attributeHandle,
        TypeDefinitionHandle stateMachineHandle)
    {
        CustomAttribute attribute = _peMetadataReader.GetCustomAttribute(attributeHandle);
        EntityHandle attributeType = attribute.Constructor.Kind switch
        {
            HandleKind.MethodDefinition => _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            HandleKind.MemberReference => _peMetadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            _ => default
        };
        if (attributeType.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference))
        {
            return false;
        }

        (string? attributeNamespace, string? attributeName) = attributeType.Kind is HandleKind.TypeDefinition
            ? GetTypeName((TypeDefinitionHandle)attributeType)
            : GetTypeName((TypeReferenceHandle)attributeType);
        if (attributeNamespace is not "System.Runtime.CompilerServices" || attributeName is not
                ("AsyncStateMachineAttribute" or "IteratorStateMachineAttribute" or "AsyncIteratorStateMachineAttribute"))
        {
            return false;
        }

        BlobReader value = _peMetadataReader.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1)
        {
            return false;
        }

        string? serializedTypeName = value.ReadSerializedString();
        string stateMachineName = GetSerializedTypeName(stateMachineHandle);
        return serializedTypeName == stateMachineName || serializedTypeName?.StartsWith(stateMachineName + ",", StringComparison.Ordinal) is true;
    }

    private (string Namespace, string Name) GetTypeName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = _peMetadataReader.GetTypeDefinition(handle);
        return (_peMetadataReader.GetString(type.Namespace), _peMetadataReader.GetString(type.Name));
    }

    private (string Namespace, string Name) GetTypeName(TypeReferenceHandle handle)
    {
        TypeReference type = _peMetadataReader.GetTypeReference(handle);
        return (_peMetadataReader.GetString(type.Namespace), _peMetadataReader.GetString(type.Name));
    }

    private string GetSerializedTypeName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = _peMetadataReader.GetTypeDefinition(handle);
        string name = _peMetadataReader.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (declaringType.IsNil is false)
        {
            return $"{GetSerializedTypeName(declaringType)}+{name}";
        }

        string @namespace = _peMetadataReader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }
}
