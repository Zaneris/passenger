using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Interop.Extensions;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    /// <summary>A frame's locals by slot, each null where it is not read, <c>Why</c> saying why; null with why where none is.
    /// The runtime's, but of a frame its interpreter runs in code an update changed, which it lays out by the build's code
    /// (<see cref="ModuleMetadataReader.VersionOf(ICorDebugFrame)"/>): those are read as the code the frame runs keeps them
    /// (<see cref="RunningLocals"/>).</summary>
    internal (ICorDebugValue?[]? Locals, string? Why) LocalsOf(ICorDebugILFrame frame)
    {
        try
        {
            if (RunningLocalsOf(frame) is not { } running)
            {
                return (frame.LocalVariables, null);
            }
            ICorDebugValue?[] locals = new ICorDebugValue?[running.Count];
            string? why = null;
            for (int slot = 0; slot < locals.Length; slot++)
            {
                try
                {
                    locals[slot] = running.Read(slot);
                }
                catch (Exception error) when (Failures.OfRuntime(error))
                {
                    why ??= $"Not read: {error.Message}";
                }
            }
            return (locals, why);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return (null, $"Not read: {error.Message}");
        }
    }

    /// <summary>A frame's local in a slot, read as <see cref="LocalsOf"/> reads it; a failure thrown.</summary>
    internal ICorDebugValue LocalOf(ICorDebugILFrame frame, int slot) =>
        RunningLocalsOf(frame) is { } running ? running.Read(slot) : frame.GetLocalVariable(slot);

    /// <summary>A frame's local in a slot, read as <see cref="LocalsOf"/> reads it, or null where it is not.</summary>
    internal ICorDebugValue? TryLocalOf(ICorDebugILFrame frame, int slot)
    {
        try
        {
            return RunningLocalsOf(frame) is { } running ? running.Read(slot)
                : frame.TryGetLocalVariable(checked((uint)slot), out ICorDebugValue? value) is Cor.S_OK ? value : null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return null;
        }
    }

    /// <summary>How many locals a frame has: as the runtime reads them, else as the code it runs declares them.</summary>
    internal int LocalCountOf(ICorDebugILFrame frame) =>
        LocalsOf(frame).Locals?.Length
            ?? (FindModule(frame.Function.Module)?.MetadataReader is { } reader ? reader.LocalTypes(frame.Function.Token, reader.VersionOf(frame)).Length : 0);

    /// <summary>Where a frame keeps its locals, of one the runtime says runs other code than it does (its interpreter's, in
    /// code an update changed: <see cref="ModuleMetadataReader.VersionOf(ICorDebugFrame)"/>); null for any other frame.</summary>
    private RunningLocals? RunningLocalsOf(ICorDebugILFrame frame)
    {
        if (FindModule(frame.Function.Module) is not { } module || module.MetadataReader.VersionOf(frame) is not int version
            || frame.TryGetCode(out ICorDebugCode said) is not Cor.S_OK || said.TryGetVersionNumber(out uint saidVersion) is not Cor.S_OK || version == saidVersion)
        {
            return null;
        }
        if (frame is not ICorDebugNativeFrame native)
        {
            throw new InvalidOperationException("The runtime gives this frame no compiled code to read its locals in");
        }
        Marshal.ThrowExceptionForHR(native.TryGetIP(out uint ip));
        Marshal.ThrowExceptionForHR(native.TryGetCode(out ICorDebugCode compiled));
        if (compiled is not ICorDebugCode4 code)
        {
            throw new InvalidOperationException("The runtime does not say where the code this frame runs keeps its locals");
        }
        List<RunningLocals.Home> homes = [];
        foreach (ICorDebugVariableHome home in code.EnumerateVariableHomes())
        {
            // An argument's home has no slot.
            if (home.TryGetSlotIndex(out uint slot) is Cor.S_OK && home.TryGetLiveRange(out uint start, out uint end) is Cor.S_OK
                && home.TryGetLocationType(out VariableLocationType kind) is Cor.S_OK)
            {
                CorDebugRegister register = home.TryGetRegister(out CorDebugRegister held) is Cor.S_OK ? held : default;
                homes.Add(new RunningLocals.Home((int)slot, start, end, kind, register, home.TryGetOffset(out int offset) is Cor.S_OK ? offset : 0));
            }
        }
        return new RunningLocals(native, ip, module.MetadataReader.LocalTypeSignatures(frame.Function.Token, version), homes, Instantiating(frame, module));
    }

    /// <summary>A local's signature as the frame's instantiation types it: each type parameter it names written as the frame's
    /// type argument for it, as the frame's module writes that type (<see cref="SignatureIn"/>), the arguments read once.</summary>
    private Func<byte[], byte[]> Instantiating(ICorDebugILFrame frame, ModuleInfo module)
    {
        (ICorDebugType[] OfClass, ICorDebugType[] OfMethod)? arguments = null;
        return type => InstantiatedSignature.Of(type, (method, number) =>
        {
            arguments ??= CilInterpreter.SplitFrameTypeParameters(this, frame);
            ICorDebugType[] given = method ? arguments.Value.OfMethod : arguments.Value.OfClass;
            if (number >= given.Length)
            {
                throw new InvalidOperationException("The runtime does not say the frame's type arguments, which the local's type names");
            }
            return SignatureIn(given[number], module)
                ?? throw new InvalidOperationException($"The local's type argument {GetCorDebugTypeFriendlyName(given[number])} has no name in the frame's module");
        });
    }

    /// <summary>A type as a signature in a module writes it, its classes by the module's names for them (<see
    /// cref="TypeIn"/>); a reference type the module has no name for as object, its value's own type the object's to say. Null
    /// for a value type the module has no name for.</summary>
    private byte[]? SignatureIn(ICorDebugType type, ModuleInfo module)
    {
        CorElementType kind = type.Type;
        byte[] reference = [(byte)CorElementType.OBJECT];
        switch (kind)
        {
            case CorElementType.CLASS or CorElementType.VALUETYPE:
            {
                bool isClass = kind is CorElementType.CLASS;
                if (TypeIn(type.Class, module) is not { } named)
                {
                    return isClass ? reference : null;
                }
                ICorDebugType[] arguments = type.TypeParameters;
                BlobBuilder written = new();
                if (arguments.Length > 0)
                {
                    written.WriteByte((byte)CorElementType.GENERICINST);
                }
                written.WriteByte((byte)kind);
                written.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(named));
                if (arguments.Length > 0)
                {
                    written.WriteCompressedInteger(arguments.Length);
                    foreach (ICorDebugType argument in arguments)
                    {
                        if (SignatureIn(argument, module) is not { } inner)
                        {
                            return isClass ? reference : null;
                        }
                        written.WriteBytes(inner);
                    }
                }
                return written.ToArray();
            }
            case CorElementType.SZARRAY or CorElementType.ARRAY:
            {
                if (SignatureIn(type.FirstTypeParameter, module) is not { } element)
                {
                    return reference;
                }
                BlobBuilder written = new();
                written.WriteByte((byte)kind);
                written.WriteBytes(element);
                if (kind is CorElementType.ARRAY)
                {
                    // Its rank, then no sizes and no lower bounds.
                    written.WriteCompressedInteger(type.Rank);
                    written.WriteCompressedInteger(0);
                    written.WriteCompressedInteger(0);
                }
                return written.ToArray();
            }
            case CorElementType.PTR or CorElementType.BYREF:
                return SignatureIn(type.FirstTypeParameter, module) is { } pointed ? [(byte)kind, .. pointed] : null;
            case CorElementType.FNPTR:
                return null;
            default:
                return [(byte)kind];
        }
    }

    /// <summary>The name a module has for a class: its definition where the class is the module's own, else the module's
    /// reference to a type of the same full name, which the runtime resolves; null where it has neither.</summary>
    private EntityHandle? TypeIn(ICorDebugClass of, ModuleInfo module)
    {
        if (FindModule(of.Module) is not { } home)
        {
            return null;
        }
        TypeDefinitionHandle definition = (TypeDefinitionHandle)MetadataTokens.Handle(of.Token);
        if (ReferenceEquals(home, module))
        {
            return definition;
        }
        MetadataReader ours = module.MetadataReader.PeMetadataReader;
        MetadataReader theirs = home.MetadataReader.PeMetadataReader;
        foreach (TypeReferenceHandle reference in ours.TypeReferences)
        {
            if (Names(reference, definition))
            {
                return reference;
            }
        }
        return null;

        bool Names(TypeReferenceHandle reference, TypeDefinitionHandle type)
        {
            TypeReference written = ours.GetTypeReference(reference);
            TypeDefinition defined = theirs.GetTypeDefinition(type);
            if (!ours.StringComparer.Equals(written.Name, theirs.GetString(defined.Name)))
            {
                return false;
            }
            TypeDefinitionHandle outer = defined.GetDeclaringType();
            return outer.IsNil
                ? written.ResolutionScope.Kind is not HandleKind.TypeReference && ours.StringComparer.Equals(written.Namespace, theirs.GetString(defined.Namespace))
                : written.ResolutionScope.Kind is HandleKind.TypeReference && Names((TypeReferenceHandle)written.ResolutionScope, outer);
        }
    }

    /// <summary>The locals of a frame at native offset <paramref name="ip"/> of the code it runs: each read from its home there,
    /// as that code's variable information gives it, typed as the frame's version declares it (<paramref name="types"/>,
    /// as its signature writes each, <paramref name="instantiated"/> with the frame's type arguments), the way the runtime
    /// reads a local but for the code it takes the frame to run.</summary>
    private sealed class RunningLocals(ICorDebugNativeFrame frame, uint ip, ImmutableArray<byte[]> types, IReadOnlyList<RunningLocals.Home> homes,
        Func<byte[], byte[]> instantiated)
    {
        /// <summary>Where a local lives from one native offset to before another: a register, or memory at an offset from one.</summary>
        public sealed record Home(int Slot, uint Start, uint End, VariableLocationType Kind, CorDebugRegister Register, int Offset);

        public int Count => types.Length;

        public unsafe ICorDebugValue Read(int slot)
        {
            if (slot < 0 || slot >= types.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "The code this frame runs has no local in that slot");
            }
            Home home = HomeOf(slot);
            byte[] type = instantiated(types[slot]);
            ICorDebugValue value;
            fixed (byte* signature = type)
            {
                if (home.Kind is VariableLocationType.VLT_REGISTER)
                {
                    Marshal.ThrowExceptionForHR(frame.TryGetLocalRegisterValue(home.Register, (uint)type.Length, (nuint)signature, out value));
                }
                else if (home.Kind is VariableLocationType.VLT_REGISTER_RELATIVE)
                {
                    CORDB_ADDRESS address = new(unchecked(ValueOf(home.Register) + (ulong)(long)home.Offset));
                    Marshal.ThrowExceptionForHR(frame.TryGetLocalMemoryValue(address, (uint)type.Length, (nuint)signature, out value));
                }
                else
                {
                    throw new NotSupportedException("The code this frame runs keeps the local where the runtime does not say");
                }
            }
            return value;
        }

        /// <summary>The home a local has at the frame's place: the one live there, else the last that ends there, as the
        /// runtime takes it.</summary>
        private Home HomeOf(int slot)
        {
            Home? last = null;
            foreach (Home home in homes.Where(h => h.Slot == slot))
            {
                if (home.Start <= ip && ip < home.End)
                {
                    return home;
                }
                if (last is null || home.Start > last.Start)
                {
                    last = home;
                }
            }
            return last is not null && last.End == ip
                ? last
                : throw new COMException("The code this frame runs keeps the local nowhere here", Cor.CORDBG_E_IL_VAR_NOT_AVAILABLE);
        }

        private ulong ValueOf(CorDebugRegister register)
        {
            if ((int)register is < 0 or >= 64)
            {
                throw new NotSupportedException($"The runtime reads no register {register} of a frame by its mask");
            }
            Marshal.ThrowExceptionForHR(frame.TryGetRegisterSet(out ICorDebugRegisterSet registers));
            ulong[] value = new ulong[1];
            Marshal.ThrowExceptionForHR(registers.TryGetRegisters(1UL << (int)register, 1, value));
            return value[0];
        }
    }
}
