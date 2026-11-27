using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

/// <summary>The code ahead of a stop: its method's IL is followed from where the stop stands with the values the frame holds
/// (locals, arguments, fields, constants and operators, read as a snapshot reads them, running nothing), each branch taken
/// as those values decide, until the method returns or a value can only be known by running code (a call's result, a branch
/// on one). What that path never reaches, nor any path on from where it could no longer tell (handlers included), won't
/// run; the values its locals take on the way are kept by line.</summary>
public partial class ManagedDebugger
{
    /// <summary>How many instructions the path is followed for, a loop's turns included, before it is no longer told.</summary>
    internal const int MostAheadSteps = 20_000;

    /// <summary>What the stopped frame's method will do from where it stands, as far as its values tell; null for a frame
    /// that is not the thread's top one at a statement (its code stands part way through one), or one without symbols.</summary>
    public AheadRead? Ahead(int frameId)
    {
        if (_frameReferenceManager.GetFrameInfoById(frameId) is not var (threadId, depth) || depth.Value != 0
            || GetFrameForThreadIdAndStackDepth(threadId, depth) is not ICorDebugILFrame frame || FindModule(frame.Function.Module) is not { } module)
        {
            return null;
        }
        try
        {
            return new AheadRun(this, frame, module).Read();
        }
        catch (Exception error) when (Failures.OfRuntime(error) || error is NotSupportedException or InvalidOperationException)
        {
            _logger?.Invoke($"The code ahead of frame {frameId} is not read: {error.Message}");
            return null;
        }
    }

    private enum AheadKind
    {
        Unknown,
        Integer,
        Long,
        Real,
        Null,
        Text,
        Reference,
    }

    /// <summary>A value the path holds: a number, null, a string, or an object (by its reference, and where it lies, 0 for
    /// a struct or a new object), or one not known.</summary>
    private readonly record struct Held(AheadKind Kind, long Integer = 0, double Real = 0, string? Text = null, ICorDebugValue? Value = null, ulong Address = 0)
    {
        public static Held Unknown => default;

        public bool Known => Kind != AheadKind.Unknown;

        public static Held Of(bool value) => new(AheadKind.Integer, value ? 1 : 0);
    }

    private sealed class AheadRun(ManagedDebugger debugger, ICorDebugILFrame frame, ModuleInfo module)
    {
        private readonly int token = frame.Function.Token;
        private readonly int? version = module.MetadataReader.VersionOf(frame);
        private readonly Dictionary<int, Held> locals = [];
        private readonly Dictionary<int, Held> arguments = [];
        private readonly HashSet<int> addressed = [];
        private readonly HashSet<int> addressedArguments = [];
        private readonly List<Held> stack = [];
        /// <summary>The fields the path set, by where their object lies (0 for a static one) and their name.</summary>
        private readonly Dictionary<(ulong Address, string Field), Held> written = [];
        /// <summary>Whether code the path could not follow ran, which may have changed any field.</summary>
        private bool heapMoved;
        private readonly List<(int Offset, int Local, Held Value)> stored = [];

        public AheadRead? Read()
        {
            (int ip, CorDebugMappingResult mapping) = frame.IP;
            ModuleMetadataReader reader = module.MetadataReader;
            // Read only from a statement's start, where nothing the frame computed waits on its stack.
            if (!mapping.HasFlag(CorDebugMappingResult.MAPPING_EXACT) || reader.MethodIL(token, version) is not { } il || reader.Regions(token, version) is not { } regions
                || reader.Points(token, version) is not { Count: > 0 } points || points.FirstOrDefault(p => !p.IsHidden).Document is not { } path
                || !points.Any(p => p.Offset == ip))
            {
                return null;
            }
            IReadOnlyList<CilInstruction> code = CilInstructionDecoder.Decode(il);
            Dictionary<int, int> index = [];
            for (int i = 0; i < code.Count; i++)
            {
                index[code[i].Offset] = i;
            }
            if (!index.TryGetValue(ip, out int at))
            {
                return null;
            }
            HashSet<int> visited = [];
            int? unsure = null;
            for (int steps = 0; ; steps++)
            {
                CilInstruction instruction = code[at];
                visited.Add(instruction.Offset);
                int? next = steps < MostAheadSteps ? Step(instruction, code, at, regions) : null;
                if (next is not int goes)
                {
                    unsure = instruction.Offset;
                    break;
                }
                if (goes < 0)
                {
                    break;
                }
                if (!index.TryGetValue(goes, out at))
                {
                    unsure = instruction.Offset;
                    break;
                }
            }
            HashSet<int> ahead = Reached([ip], code, index, regions, []);
            // From where it could no longer tell, anything reachable may run; a handler may run wherever the path went.
            HashSet<int> mayRun = Reached(unsure is int from ? [from] : [], code, index, regions, visited);
            List<AheadSpan> wontRun = WontRun(points, code, ahead, visited, mayRun);
            return new AheadRead(path, wontRun, ValuesAhead(points), unsure is int stoppedAt && LineOf(points, stoppedAt) is int line ? line : null);
        }

        /// <summary>Runs one instruction: the offset the path goes on at, -1 where the method returns, null where it can no
        /// longer tell.</summary>
        private int? Step(CilInstruction instruction, IReadOnlyList<CilInstruction> code, int at, ImmutableArray<ExceptionRegion> regions)
        {
            int next = at + 1 < code.Count ? code[at + 1].Offset : -2;
            switch (instruction.Code)
            {
                case ILOpCode.Nop or ILOpCode.Constrained or ILOpCode.Readonly or ILOpCode.Volatile or ILOpCode.Tail or ILOpCode.Unaligned:
                    return next;
                case ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3:
                    stack.Add(Argument((int)instruction.Code - (int)ILOpCode.Ldarg_0));
                    return next;
                case ILOpCode.Ldarg_s or ILOpCode.Ldarg:
                    stack.Add(Argument(instruction.Index));
                    return next;
                case ILOpCode.Starg_s or ILOpCode.Starg:
                    arguments[instruction.Index] = Pop();
                    return next;
                case ILOpCode.Ldloc_0 or ILOpCode.Ldloc_1 or ILOpCode.Ldloc_2 or ILOpCode.Ldloc_3:
                    stack.Add(Local((int)instruction.Code - (int)ILOpCode.Ldloc_0));
                    return next;
                case ILOpCode.Ldloc_s or ILOpCode.Ldloc:
                    stack.Add(Local(instruction.Index));
                    return next;
                case ILOpCode.Stloc_0 or ILOpCode.Stloc_1 or ILOpCode.Stloc_2 or ILOpCode.Stloc_3:
                    Store(instruction.Offset, (int)instruction.Code - (int)ILOpCode.Stloc_0);
                    return next;
                case ILOpCode.Stloc_s or ILOpCode.Stloc:
                    Store(instruction.Offset, instruction.Index);
                    return next;
                case ILOpCode.Ldloca_s or ILOpCode.Ldloca:
                    // Its address may be written through: it is not known from here.
                    addressed.Add(instruction.Index);
                    stack.Add(Held.Unknown);
                    return next;
                case ILOpCode.Ldarga_s or ILOpCode.Ldarga:
                    addressedArguments.Add(instruction.Index);
                    stack.Add(Held.Unknown);
                    return next;
                case ILOpCode.Ldnull:
                    stack.Add(new Held(AheadKind.Null));
                    return next;
                case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8:
                    stack.Add(new Held(AheadKind.Integer, (int)instruction.Code - (int)ILOpCode.Ldc_i4_0));
                    return next;
                case ILOpCode.Ldc_i4_s or ILOpCode.Ldc_i4:
                    stack.Add(new Held(AheadKind.Integer, Convert.ToInt64(instruction.Operand, CultureInfo.InvariantCulture)));
                    return next;
                case ILOpCode.Ldc_i8:
                    stack.Add(new Held(AheadKind.Long, (long)instruction.Operand!));
                    return next;
                case ILOpCode.Ldc_r4:
                    stack.Add(new Held(AheadKind.Real, Real: (float)instruction.Operand!));
                    return next;
                case ILOpCode.Ldc_r8:
                    stack.Add(new Held(AheadKind.Real, Real: (double)instruction.Operand!));
                    return next;
                case ILOpCode.Ldstr:
                    stack.Add(new Held(AheadKind.Text, Text: module.MetadataReader.UserString(instruction.Token)));
                    return next;
                case ILOpCode.Dup:
                    Held top = Pop();
                    stack.Add(top);
                    stack.Add(top);
                    return next;
                case ILOpCode.Pop:
                    Pop();
                    return next;
                case ILOpCode.Br_s or ILOpCode.Br:
                    return instruction.Target;
                case ILOpCode.Leave_s or ILOpCode.Leave:
                    // Leaving a try a finally or fault guards runs that block first, which the path does not follow.
                    stack.Clear();
                    return regions.Any(r => r.Kind is ExceptionRegionKind.Finally or ExceptionRegionKind.Fault && Inside(r.TryOffset, r.TryLength, instruction.Offset)
                        && !Inside(r.TryOffset, r.TryLength, instruction.Target)) ? null : instruction.Target;
                case ILOpCode.Brtrue_s or ILOpCode.Brtrue or ILOpCode.Brfalse_s or ILOpCode.Brfalse:
                    return Truth(Pop()) is bool truth ? (truth == (instruction.Code is ILOpCode.Brtrue_s or ILOpCode.Brtrue) ? instruction.Target : next) : null;
                case ILOpCode.Beq_s or ILOpCode.Beq or ILOpCode.Bne_un_s or ILOpCode.Bne_un or ILOpCode.Bge_s or ILOpCode.Bge or ILOpCode.Bge_un_s or ILOpCode.Bge_un
                    or ILOpCode.Bgt_s or ILOpCode.Bgt or ILOpCode.Bgt_un_s or ILOpCode.Bgt_un or ILOpCode.Ble_s or ILOpCode.Ble or ILOpCode.Ble_un_s or ILOpCode.Ble_un
                    or ILOpCode.Blt_s or ILOpCode.Blt or ILOpCode.Blt_un_s or ILOpCode.Blt_un:
                {
                    Held right = Pop();
                    Held left = Pop();
                    return Branches(instruction.Code, left, right) is bool taken ? (taken ? instruction.Target : next) : null;
                }
                case ILOpCode.Switch:
                {
                    int[] targets = (int[])instruction.Operand!;
                    return Pop() is { Kind: AheadKind.Integer } chosen ? (chosen.Integer >= 0 && chosen.Integer < targets.Length ? targets[chosen.Integer] : next) : null;
                }
                case ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Cgt_un or ILOpCode.Clt or ILOpCode.Clt_un:
                {
                    Held right = Pop();
                    Held left = Pop();
                    ILOpCode asked = instruction.Code switch
                    {
                        ILOpCode.Ceq => ILOpCode.Beq,
                        ILOpCode.Cgt => ILOpCode.Bgt,
                        ILOpCode.Cgt_un => ILOpCode.Bgt_un,
                        ILOpCode.Clt => ILOpCode.Blt,
                        _ => ILOpCode.Blt_un,
                    };
                    stack.Add(Branches(asked, left, right) is bool holds ? Held.Of(holds) : Held.Unknown);
                    return next;
                }
                case ILOpCode.Add or ILOpCode.Sub or ILOpCode.Mul or ILOpCode.Div or ILOpCode.Rem or ILOpCode.And or ILOpCode.Or or ILOpCode.Xor
                    or ILOpCode.Shl or ILOpCode.Shr or ILOpCode.Shr_un or ILOpCode.Add_ovf or ILOpCode.Sub_ovf or ILOpCode.Mul_ovf:
                {
                    Held right = Pop();
                    Held left = Pop();
                    Held result = Arithmetic(instruction.Code, left, right);
                    stack.Add(result);
                    // Dividing by zero or overflowing throws, where the path can no longer tell where it goes.
                    return result.Known || !left.Known || !right.Known ? next : null;
                }
                case ILOpCode.Neg or ILOpCode.Not:
                {
                    Held value = Pop();
                    stack.Add(value.Kind switch
                    {
                        AheadKind.Integer => new Held(AheadKind.Integer, instruction.Code == ILOpCode.Neg ? unchecked(-(int)value.Integer) : ~(int)value.Integer),
                        AheadKind.Long => new Held(AheadKind.Long, instruction.Code == ILOpCode.Neg ? unchecked(-value.Integer) : ~value.Integer),
                        AheadKind.Real when instruction.Code == ILOpCode.Neg => new Held(AheadKind.Real, Real: -value.Real),
                        _ => Held.Unknown,
                    });
                    return next;
                }
                case ILOpCode.Conv_i1 or ILOpCode.Conv_i2 or ILOpCode.Conv_i4 or ILOpCode.Conv_i8 or ILOpCode.Conv_u1 or ILOpCode.Conv_u2 or ILOpCode.Conv_u4
                    or ILOpCode.Conv_u8 or ILOpCode.Conv_r4 or ILOpCode.Conv_r8 or ILOpCode.Conv_i or ILOpCode.Conv_u or ILOpCode.Conv_r_un:
                    stack.Add(Converted(instruction.Code, Pop()));
                    return next;
                case ILOpCode.Box:
                    // A boxed value is an object that is never null; it is not read on.
                    stack.Add(Pop().Known ? new Held(AheadKind.Reference) : Held.Unknown);
                    return next;
                case ILOpCode.Ldfld:
                    stack.Add(Field(Pop(), instruction.Token));
                    return next;
                case ILOpCode.Ldsfld:
                    stack.Add(StaticField(instruction.Token));
                    return next;
                case ILOpCode.Stfld:
                {
                    Held value = Pop();
                    Held owner = Pop();
                    Write(owner, instruction.Token, value);
                    return next;
                }
                case ILOpCode.Stsfld:
                    Write(new Held(AheadKind.Reference), instruction.Token, Pop());
                    return next;
                case ILOpCode.Ldlen:
                {
                    Held array = Pop();
                    stack.Add(array.Value is ICorDebugReferenceValue reference && reference.Dereference() is ICorDebugArrayValue values
                        ? new Held(AheadKind.Integer, values.Count) : Held.Unknown);
                    return next;
                }
                case ILOpCode.Call or ILOpCode.Callvirt:
                    return Call(instruction.Token) ? next : null;
                case ILOpCode.Newobj:
                    if (module.MetadataReader.ShapeOf(instruction.Token) is not { } made)
                    {
                        return null;
                    }
                    PopMany(made.Parameters);
                    stack.Add(new Held(AheadKind.Reference));
                    CodeRan();
                    return next;
                case ILOpCode.Ret:
                    return -1;
                case ILOpCode.Isinst or ILOpCode.Castclass or ILOpCode.Unbox_any or ILOpCode.Ldflda or ILOpCode.Ldsflda or ILOpCode.Ldftn or ILOpCode.Ldtoken
                    or ILOpCode.Ldind_i1 or ILOpCode.Ldind_u1 or ILOpCode.Ldind_i2 or ILOpCode.Ldind_u2 or ILOpCode.Ldind_i4 or ILOpCode.Ldind_u4 or ILOpCode.Ldind_i8
                    or ILOpCode.Ldind_i or ILOpCode.Ldind_r4 or ILOpCode.Ldind_r8 or ILOpCode.Ldind_ref or ILOpCode.Ldobj or ILOpCode.Newarr:
                {
                    // What these give is not read: one value in, one unknown out (ldftn and ldtoken take none).
                    if (instruction.Code is not (ILOpCode.Ldftn or ILOpCode.Ldtoken or ILOpCode.Ldsflda))
                    {
                        Pop();
                    }
                    stack.Add(Held.Unknown);
                    return next;
                }
                case ILOpCode.Initobj:
                    Pop();
                    return next;
                default:
                    // Anything else (a throw, a store through an address, an element) is where the path can no longer tell.
                    return null;
            }
        }

        private Held Pop()
        {
            if (stack.Count == 0)
            {
                throw new InvalidOperationException("The stack the code ahead was read with ran out");
            }
            Held top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return top;
        }

        private void PopMany(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Pop();
            }
        }

        /// <summary>Code ran the path could not follow: any field it read may now hold anything.</summary>
        private void CodeRan()
        {
            heapMoved = true;
            written.Clear();
        }

        private Held Argument(int index)
        {
            if (addressedArguments.Contains(index))
            {
                return Held.Unknown;
            }
            if (!arguments.TryGetValue(index, out Held value))
            {
                value = frame.TryGetArgument(checked((uint)index), out ICorDebugValue? read) is Cor.S_OK ? HeldOf(read) : Held.Unknown;
                arguments[index] = value;
            }
            return value;
        }

        private Held Local(int index)
        {
            if (addressed.Contains(index))
            {
                return Held.Unknown;
            }
            if (!locals.TryGetValue(index, out Held value))
            {
                value = debugger.TryLocalOf(frame, index) is { } read ? HeldOf(read) : Held.Unknown;
                locals[index] = value;
            }
            return value;
        }

        private void Store(int offset, int index)
        {
            Held value = Pop();
            locals[index] = value;
            if (value.Known)
            {
                stored.Add((offset, index, value));
            }
        }

        /// <summary>A value of the program read as the path holds it: a primitive by its bits, a string, null, an enum by its
        /// number, an object by its reference; anything else not known.</summary>
        private static Held HeldOf(ICorDebugValue? value)
        {
            switch (value)
            {
                case null:
                    return Held.Unknown;
                case ICorDebugReferenceValue reference when reference.Type is CorElementType.CLASS or CorElementType.STRING or CorElementType.OBJECT
                    or CorElementType.SZARRAY or CorElementType.ARRAY:
                    if (reference.IsNull)
                    {
                        return new Held(AheadKind.Null);
                    }
                    ulong address = reference.TryGetValue(out CORDB_ADDRESS at) is Cor.S_OK ? at.Value : 0;
                    return reference.Dereference() is ICorDebugStringValue text
                        ? new Held(AheadKind.Text, Text: text.String, Value: reference, Address: address)
                        : new Held(AheadKind.Reference, Value: reference, Address: address);
                case ICorDebugReferenceValue:
                    return Held.Unknown;
                case ICorDebugGenericValue primitive when primitive.Type is not CorElementType.VALUETYPE:
                    byte[] bytes = primitive.GetValueAsBytes();
                    return primitive.Type switch
                    {
                        CorElementType.BOOLEAN or CorElementType.U1 => new Held(AheadKind.Integer, bytes[0]),
                        CorElementType.I1 => new Held(AheadKind.Integer, (sbyte)bytes[0]),
                        CorElementType.CHAR or CorElementType.U2 => new Held(AheadKind.Integer, BitConverter.ToUInt16(bytes)),
                        CorElementType.I2 => new Held(AheadKind.Integer, BitConverter.ToInt16(bytes)),
                        CorElementType.I4 => new Held(AheadKind.Integer, BitConverter.ToInt32(bytes)),
                        CorElementType.U4 => new Held(AheadKind.Integer, (int)BitConverter.ToUInt32(bytes)),
                        CorElementType.I8 or CorElementType.U8 => new Held(AheadKind.Long, BitConverter.ToInt64(bytes)),
                        CorElementType.R4 => new Held(AheadKind.Real, Real: BitConverter.ToSingle(bytes)),
                        CorElementType.R8 => new Held(AheadKind.Real, Real: BitConverter.ToDouble(bytes)),
                        _ => Held.Unknown,
                    };
                case ICorDebugObjectValue structure:
                    // An enum is its number; another struct is read by its fields.
                    return StoredField(structure, "value__") is { } number ? HeldOf(number) : new Held(AheadKind.Reference, Value: structure);
                default:
                    return Held.Unknown;
            }
        }

        private static bool? Truth(Held value) => value.Kind switch
        {
            AheadKind.Integer or AheadKind.Long => value.Integer != 0,
            AheadKind.Null => false,
            AheadKind.Text or AheadKind.Reference => true,
            _ => null,
        };

        /// <summary>Whether a comparing branch is taken: numbers by value (an unordered real not told), references by where
        /// they lie, null against anything known.</summary>
        private static bool? Branches(ILOpCode asked, Held left, Held right)
        {
            bool unsigned = asked is ILOpCode.Bge_un or ILOpCode.Bge_un_s or ILOpCode.Bgt_un or ILOpCode.Bgt_un_s or ILOpCode.Ble_un or ILOpCode.Ble_un_s
                or ILOpCode.Blt_un or ILOpCode.Blt_un_s;
            int? order = (left.Kind, right.Kind) switch
            {
                (AheadKind.Integer or AheadKind.Long, AheadKind.Integer or AheadKind.Long) => unsigned
                    ? ((ulong)left.Integer).CompareTo((ulong)right.Integer)
                    : left.Integer.CompareTo(right.Integer),
                (AheadKind.Real, AheadKind.Real) when !double.IsNaN(left.Real) && !double.IsNaN(right.Real) => left.Real.CompareTo(right.Real),
                _ => null,
            };
            bool? equal = order is int ordered ? ordered == 0 : (left.Kind, right.Kind) switch
            {
                (AheadKind.Null, AheadKind.Null) => true,
                (AheadKind.Null, AheadKind.Text or AheadKind.Reference) or (AheadKind.Text or AheadKind.Reference, AheadKind.Null) => false,
                (AheadKind.Text or AheadKind.Reference, AheadKind.Text or AheadKind.Reference) when left.Address != 0 && right.Address != 0 => left.Address == right.Address,
                _ => null,
            };
            return asked switch
            {
                ILOpCode.Beq or ILOpCode.Beq_s => equal,
                ILOpCode.Bne_un or ILOpCode.Bne_un_s => !equal,
                ILOpCode.Bge or ILOpCode.Bge_s or ILOpCode.Bge_un or ILOpCode.Bge_un_s => order >= 0,
                ILOpCode.Bgt or ILOpCode.Bgt_s or ILOpCode.Bgt_un or ILOpCode.Bgt_un_s => order > 0,
                ILOpCode.Ble or ILOpCode.Ble_s or ILOpCode.Ble_un or ILOpCode.Ble_un_s => order <= 0,
                _ => order < 0,
            };
        }

        /// <summary>Two numbers' result as the runtime computes it, 32-bit ones wrapping; not known for a division by zero, an
        /// overflow a checked operation throws for, or anything but numbers of a kind.</summary>
        private static Held Arithmetic(ILOpCode asked, Held left, Held right)
        {
            if (left.Kind == AheadKind.Real && right.Kind == AheadKind.Real)
            {
                double? real = asked switch
                {
                    ILOpCode.Add or ILOpCode.Add_ovf => left.Real + right.Real,
                    ILOpCode.Sub or ILOpCode.Sub_ovf => left.Real - right.Real,
                    ILOpCode.Mul or ILOpCode.Mul_ovf => left.Real * right.Real,
                    ILOpCode.Div => left.Real / right.Real,
                    ILOpCode.Rem => left.Real % right.Real,
                    _ => null,
                };
                return real is double done ? new Held(AheadKind.Real, Real: done) : Held.Unknown;
            }
            if (left.Kind is not (AheadKind.Integer or AheadKind.Long) || right.Kind is not (AheadKind.Integer or AheadKind.Long))
            {
                return Held.Unknown;
            }
            bool wide = left.Kind == AheadKind.Long || right.Kind == AheadKind.Long;
            long a = left.Integer;
            long b = right.Integer;
            if (asked is ILOpCode.Div or ILOpCode.Rem && (b == 0 || !wide && (int)a == int.MinValue && b == -1 || wide && a == long.MinValue && b == -1))
            {
                return Held.Unknown;
            }
            try
            {
                long result = asked switch
                {
                    ILOpCode.Add => unchecked(a + b),
                    ILOpCode.Sub => unchecked(a - b),
                    ILOpCode.Mul => unchecked(a * b),
                    ILOpCode.Div => a / b,
                    ILOpCode.Rem => a % b,
                    ILOpCode.And => a & b,
                    ILOpCode.Or => a | b,
                    ILOpCode.Xor => a ^ b,
                    ILOpCode.Shl => wide ? a << (int)b : (int)a << (int)b,
                    ILOpCode.Shr => wide ? a >> (int)b : (int)a >> (int)b,
                    ILOpCode.Shr_un => wide ? (long)((ulong)a >> (int)b) : (uint)a >> (int)b,
                    ILOpCode.Add_ovf => wide ? checked(a + b) : checked((int)a + (int)b),
                    ILOpCode.Sub_ovf => wide ? checked(a - b) : checked((int)a - (int)b),
                    _ => wide ? checked(a * b) : checked((int)a * (int)b),
                };
                return wide ? new Held(AheadKind.Long, result) : new Held(AheadKind.Integer, unchecked((int)result));
            }
            catch (OverflowException)
            {
                return Held.Unknown;
            }
        }

        private static Held Converted(ILOpCode asked, Held value)
        {
            if (value.Kind is AheadKind.Integer or AheadKind.Long)
            {
                long a = value.Integer;
                return asked switch
                {
                    ILOpCode.Conv_i1 => new Held(AheadKind.Integer, unchecked((sbyte)a)),
                    ILOpCode.Conv_i2 => new Held(AheadKind.Integer, unchecked((short)a)),
                    ILOpCode.Conv_i4 => new Held(AheadKind.Integer, unchecked((int)a)),
                    ILOpCode.Conv_u1 => new Held(AheadKind.Integer, unchecked((byte)a)),
                    ILOpCode.Conv_u2 => new Held(AheadKind.Integer, unchecked((ushort)a)),
                    ILOpCode.Conv_u4 => new Held(AheadKind.Integer, unchecked((int)(uint)a)),
                    ILOpCode.Conv_i8 or ILOpCode.Conv_i => new Held(AheadKind.Long, value.Kind == AheadKind.Integer ? (int)a : a),
                    ILOpCode.Conv_u8 or ILOpCode.Conv_u => new Held(AheadKind.Long, value.Kind == AheadKind.Integer ? (uint)(int)a : a),
                    ILOpCode.Conv_r4 => new Held(AheadKind.Real, Real: (float)a),
                    ILOpCode.Conv_r8 => new Held(AheadKind.Real, Real: a),
                    _ => new Held(AheadKind.Real, Real: (ulong)a),
                };
            }
            if (value.Kind == AheadKind.Real)
            {
                return asked switch
                {
                    ILOpCode.Conv_r4 => new Held(AheadKind.Real, Real: (float)value.Real),
                    ILOpCode.Conv_r8 => value,
                    // A real out of a whole number's range converts to what the processor gives: not told.
                    ILOpCode.Conv_i4 when value.Real is > int.MinValue - 1.0 and < int.MaxValue + 1.0 => new Held(AheadKind.Integer, (int)value.Real),
                    ILOpCode.Conv_i8 when value.Real is > -9.2e18 and < 9.2e18 => new Held(AheadKind.Long, (long)value.Real),
                    _ => Held.Unknown,
                };
            }
            return Held.Unknown;
        }

        /// <summary>A field of an object as the path holds it: one it set, else as the program holds it, unless code it could
        /// not follow has run since.</summary>
        private Held Field(Held owner, int fieldToken)
        {
            if (module.MetadataReader.FieldName(fieldToken) is not { } name || owner.Kind is not AheadKind.Reference || owner.Value is null)
            {
                return Held.Unknown;
            }
            if (owner.Address != 0 && written.TryGetValue((owner.Address, name), out Held set))
            {
                return set;
            }
            if (heapMoved)
            {
                return Held.Unknown;
            }
            ICorDebugObjectValue? held = owner.Value switch
            {
                ICorDebugReferenceValue reference => reference.Dereference() as ICorDebugObjectValue,
                ICorDebugObjectValue structure => structure,
                _ => null,
            };
            for (ICorDebugType? type = held?.ExactType; type is not null && held is not null;)
            {
                ICorDebugClass declaring = type.Class;
                mdFieldDef field = declaring.Module.GetMetaDataInterface<IMetaDataImport>().EnumFieldsWithName(declaring.Token, name).FirstOrDefault();
                if (!field.IsNil)
                {
                    return HeldOf(held.GetFieldValue(declaring, field));
                }
                type = type.TryGetBase(out ICorDebugType? next) is Cor.S_OK ? next : null;
            }
            return Held.Unknown;
        }

        private Held StaticField(int fieldToken)
        {
            if (module.MetadataReader.FieldName(fieldToken) is not { } name)
            {
                return Held.Unknown;
            }
            if (written.TryGetValue((0, $"{fieldToken}:{name}"), out Held set))
            {
                return set;
            }
            if (heapMoved || module.MetadataReader.FieldOwner(fieldToken) is not int owner)
            {
                return Held.Unknown;
            }
            ICorDebugClass declaring = module.Module.GetClassFromToken(new mdTypeDef(checked((uint)owner)));
            return declaring.TryGetStaticFieldValue(new mdFieldDef(checked((uint)fieldToken)), frame, out ICorDebugValue? value) is Cor.S_OK ? HeldOf(value) : Held.Unknown;
        }

        /// <summary>A field the path sets: kept by where its object lies; one of an object it cannot place may be any, so no
        /// field read is told from then on.</summary>
        private void Write(Held owner, int fieldToken, Held value)
        {
            string? name = module.MetadataReader.FieldName(fieldToken);
            if (name is null)
            {
                CodeRan();
                return;
            }
            if (owner.Kind == AheadKind.Reference && owner.Value is null)
            {
                // A static field's (or a new object's, which nothing else reads).
                written[(0, $"{fieldToken}:{name}")] = value;
                return;
            }
            if (owner.Address == 0)
            {
                CodeRan();
                return;
            }
            written[(owner.Address, name)] = value;
        }

        /// <summary>A call: a string's equality, length or emptiness answered from the strings, a method that only reads a
        /// field read as that field, and any other run nowhere, its result not known and every field from then on with it.
        /// False for a token of no method.</summary>
        private bool Call(int callToken)
        {
            if (module.MetadataReader.ShapeOf(callToken) is not { } shape)
            {
                return false;
            }
            if (shape.DeclaringType == "System.String" && Strings(shape) is { } answered)
            {
                stack.Add(answered);
                return true;
            }
            if (module.MetadataReader.FieldReadBy(callToken) is var (field, isStatic))
            {
                stack.Add(isStatic ? StaticField(field) : Field(Pop(), field));
                return true;
            }
            PopMany(shape.Parameters + (shape.HasThis ? 1 : 0));
            if (shape.ReturnsValue)
            {
                stack.Add(Held.Unknown);
            }
            CodeRan();
            return true;
        }

        /// <summary>What string.op_Equality, op_Inequality, Length, IsNullOrEmpty answer of strings the path knows, the
        /// operands taken off the stack; null, taking nothing, for any other call or a string not known.</summary>
        private Held? Strings(ModuleMetadataReader.CallShape shape)
        {
            int taken = shape.Parameters + (shape.HasThis ? 1 : 0);
            if (stack.Count < taken || stack.Skip(stack.Count - taken).Any(v => v.Kind is not (AheadKind.Text or AheadKind.Null)))
            {
                return null;
            }
            Held[] operands = [.. stack.Skip(stack.Count - taken)];
            Held? answer = (shape.Name, operands) switch
            {
                ("op_Equality", [var a, var b]) => Held.Of(a.Text == b.Text),
                ("op_Inequality", [var a, var b]) => Held.Of(a.Text != b.Text),
                ("get_Length", [{ Kind: AheadKind.Text } s]) => new Held(AheadKind.Integer, s.Text!.Length),
                ("IsNullOrEmpty", [var s]) => Held.Of(string.IsNullOrEmpty(s.Text)),
                _ => null,
            };
            if (answer is not null)
            {
                PopMany(taken);
            }
            return answer;
        }

        private static bool Inside(int start, int length, int offset) => offset >= start && offset < start + length;

        /// <summary>The offsets every way on from <paramref name="from"/> may reach, each branch both ways, and a handler
        /// wherever any instruction of its try block is reached (<paramref name="also"/>'s too).</summary>
        private static HashSet<int> Reached(IReadOnlyList<int> from, IReadOnlyList<CilInstruction> code, Dictionary<int, int> index, ImmutableArray<ExceptionRegion> regions,
            HashSet<int> also)
        {
            HashSet<int> reached = [];
            Stack<int> pending = new(from);
            bool grew = true;
            while (grew)
            {
                while (pending.TryPop(out int offset))
                {
                    if (!index.TryGetValue(offset, out int at) || !reached.Add(offset))
                    {
                        continue;
                    }
                    CilInstruction instruction = code[at];
                    int? next = at + 1 < code.Count ? code[at + 1].Offset : null;
                    switch (instruction.Code)
                    {
                        case ILOpCode.Br or ILOpCode.Br_s or ILOpCode.Leave or ILOpCode.Leave_s:
                            pending.Push(instruction.Target);
                            break;
                        case ILOpCode.Ret or ILOpCode.Throw or ILOpCode.Rethrow or ILOpCode.Endfinally or ILOpCode.Endfilter:
                            break;
                        case ILOpCode.Switch:
                            foreach (int target in (int[])instruction.Operand!)
                            {
                                pending.Push(target);
                            }
                            if (next is int after)
                            {
                                pending.Push(after);
                            }
                            break;
                        default:
                            if (instruction.OpCode.FlowControl == System.Reflection.Emit.FlowControl.Cond_Branch)
                            {
                                pending.Push(instruction.Target);
                            }
                            if (next is int on)
                            {
                                pending.Push(on);
                            }
                            break;
                    }
                }
                grew = false;
                foreach (ExceptionRegion region in regions)
                {
                    int entry = region.Kind == ExceptionRegionKind.Filter ? region.FilterOffset : region.HandlerOffset;
                    if (!reached.Contains(entry) && reached.Concat(also).Any(offset => Inside(region.TryOffset, region.TryLength, offset)))
                    {
                        pending.Push(entry);
                        if (region.Kind == ExceptionRegionKind.Filter)
                        {
                            pending.Push(region.HandlerOffset);
                        }
                        grew = true;
                    }
                }
            }
            return reached;
        }

        /// <summary>The statements ahead none of whose code the path runs or may run, as their symbols place them.</summary>
        private static List<AheadSpan> WontRun(IReadOnlyList<ModuleMetadataReader.Point> points, IReadOnlyList<CilInstruction> code, HashSet<int> ahead, HashSet<int> visited,
            HashSet<int> mayRun)
        {
            List<AheadSpan> spans = [];
            for (int i = 0; i < points.Count; i++)
            {
                ModuleMetadataReader.Point point = points[i];
                if (point.IsHidden)
                {
                    continue;
                }
                int end = i + 1 < points.Count ? points[i + 1].Offset : int.MaxValue;
                List<int> its = [.. code.Select(c => c.Offset).Where(offset => offset >= point.Offset && offset < end && ahead.Contains(offset))];
                if (its.Count > 0 && its.All(offset => !visited.Contains(offset) && !mayRun.Contains(offset)))
                {
                    spans.Add(new AheadSpan(point.StartLine, point.StartColumn, point.EndLine, point.EndColumn));
                }
            }
            return spans;
        }

        /// <summary>The line (from one) of the statement an IL offset is in.</summary>
        private static int? LineOf(IReadOnlyList<ModuleMetadataReader.Point> points, int offset) =>
            points.LastOrDefault(p => !p.IsHidden && p.Offset <= offset) is { Document: not null } point ? point.StartLine : null;

        /// <summary>The last value each named local of a number, a bool, a char or a string takes on each line the path runs.</summary>
        private List<AheadValue> ValuesAhead(IReadOnlyList<ModuleMetadataReader.Point> points)
        {
            Dictionary<(int Line, string Name), string> shown = [];
            foreach ((int offset, int local, Held value) in stored)
            {
                if (LineOf(points, offset) is not int line || module.MetadataReader.GetLocalVariableName(token, local, offset, version) is not { } name
                    || debugger.TryLocalOf(frame, local) is not { } declared || Said(declared.Type, value) is not { } said)
                {
                    continue;
                }
                shown[(line, name)] = said;
            }
            return [.. shown.Select(entry => new AheadValue(entry.Key.Line, entry.Key.Name, entry.Value))];
        }

        /// <summary>A value as Variables writes one of its type: a bool, a char, a number or a string; null for any other.</summary>
        private static string? Said(CorElementType type, Held value) => (type, value.Kind) switch
        {
            (CorElementType.BOOLEAN, AheadKind.Integer) => value.Integer != 0 ? "true" : "false",
            (CorElementType.CHAR, AheadKind.Integer) => $"'{(char)value.Integer}'",
            (CorElementType.I1 or CorElementType.U1 or CorElementType.I2 or CorElementType.U2 or CorElementType.I4, AheadKind.Integer)
                => value.Integer.ToString(CultureInfo.InvariantCulture),
            (CorElementType.U4, AheadKind.Integer) => ((uint)value.Integer).ToString(CultureInfo.InvariantCulture),
            (CorElementType.I8, AheadKind.Long) => value.Integer.ToString(CultureInfo.InvariantCulture),
            (CorElementType.U8, AheadKind.Long) => ((ulong)value.Integer).ToString(CultureInfo.InvariantCulture),
            (CorElementType.R4 or CorElementType.R8, AheadKind.Real) => value.Real.ToString("R", CultureInfo.InvariantCulture),
            (CorElementType.STRING or CorElementType.CLASS, AheadKind.Text) => $"\"{value.Text}\"",
            (CorElementType.STRING or CorElementType.CLASS, AheadKind.Null) => "null",
            _ => null,
        };
    }
}
