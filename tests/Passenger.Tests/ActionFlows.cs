using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>What a method of the App does at once and what only after an await, read from its built IL: the works it
/// begins, the dialogs and popups it opens and what it tells, through which of the App's methods. A call is followed into
/// the App's own methods; an async method's state machine is walked from its first entry, what no path reaches without
/// passing an await's check of its awaiter taken as after it. A delegate made or invoked, an override of a virtual call and
/// another assembly's code are not followed.</summary>
internal sealed class ActionFlows
{
    /// <summary>Each instruction by its first byte, and those beginning 0xFE by their second.</summary>
    private static readonly (OpCode[] OneByte, OpCode[] TwoByte) Codes = ReadCodes();

    /// <summary>A state machine's state as it is first entered.</summary>
    private static readonly int FirstState = -1;

    private readonly Assembly app = typeof(ActionWork).Assembly;
    private readonly Dictionary<MethodBase, Flow> read = [];
    private readonly HashSet<MethodBase> reading = [];

    /// <summary>What a method does that an agent's action hears, or should.</summary>
    public enum Kind
    {
        /// <summary>Begins a Working (ActionWork.Begin).</summary>
        Begins,

        /// <summary>Shows a modal dialog.</summary>
        Dialog,

        /// <summary>Opens a popup, a menu or a flyout.</summary>
        Popup,

        /// <summary>Tells the action heard in its flow what it came to (ActionWork.Tell).</summary>
        Tells,

        /// <summary>Tells what a Working came to (Working.Tell).</summary>
        Told,
    }

    /// <summary>One thing done, and the App's method whose code does it.</summary>
    public sealed record Effect(Kind Kind, string Where);

    /// <summary>What is done before the first await and what only after one, each with the calls that lead to it; and what
    /// the delegates it makes tell when they run (an event's handler, a continuation).</summary>
    public sealed record Flow(IReadOnlyDictionary<Effect, string> AtOnce, IReadOnlyDictionary<Effect, string> Later, IReadOnlySet<Effect> Going)
    {
        public static readonly Flow None = new(new Dictionary<Effect, string>(), new Dictionary<Effect, string>(), new HashSet<Effect>());
    }

    /// <summary>One instruction: where it is, what it is, the method it calls (call, callvirt, newobj) or makes a delegate
    /// of (ldftn), the int it loads or the local it reads, and where it may go next besides the next instruction.</summary>
    private sealed record Instruction(int At, OpCode Code, MethodBase? Called, int? Value, int[] Targets);

    /// <summary>What a method does at once and later, the App's methods it calls included.</summary>
    public Flow Of(MethodBase method)
    {
        if (read.TryGetValue(method, out Flow? known))
        {
            return known;
        }
        if (method.Module.Assembly != app || !reading.Add(method))
        {
            return Flow.None;
        }
        Dictionary<Effect, string> atOnce = [];
        Dictionary<Effect, string> later = [];
        HashSet<Effect> going = [];
        MethodBase? machine = method.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType
            .GetMethod(nameof(IAsyncStateMachine.MoveNext), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodBase body = machine ?? method;
        List<Instruction> code = Read(body);
        HashSet<int> first = machine is null ? [.. code.Select(i => i.At)] : FirstEntry(code, body.GetMethodBody()!);
        foreach (Instruction instruction in code)
        {
            if (instruction.Called is not { } called)
            {
                continue;
            }
            if (instruction.Code == OpCodes.Ldftn || instruction.Code == OpCodes.Ldvirtftn)
            {
                Flow made = Of(called);
                going.UnionWith(made.AtOnce.Keys.Concat(made.Later.Keys).Concat(made.Going).Where(e => e.Kind is not Kind.Begins));
                continue;
            }
            bool now = first.Contains(instruction.At);
            if (KindOf(called, body) is { } kind)
            {
                (now ? atOnce : later).TryAdd(new Effect(kind, Named(method)), Named(method));
            }
            else if (called.Module.Assembly == app)
            {
                Flow flow = Of(called);
                foreach ((Effect effect, string path) in flow.AtOnce)
                {
                    (now ? atOnce : later).TryAdd(effect, $"{Named(method)} > {path}");
                }
                foreach ((Effect effect, string path) in flow.Later)
                {
                    later.TryAdd(effect, $"{Named(method)} > {path}");
                }
                going.UnionWith(flow.Going);
            }
        }
        reading.Remove(method);
        Flow flowing = new(atOnce, later, going);
        read[method] = flowing;
        return flowing;
    }

    /// <summary>A method's name as its code is written: an async method's for its state machine, the method a lambda or a
    /// local function is written in for it.</summary>
    public static string Named(MethodBase method)
    {
        Type? type = method.DeclaringType;
        string name = method.Name;
        while (type is { DeclaringType: { } outer } && type.IsDefined(typeof(CompilerGeneratedAttribute)))
        {
            // A state machine (<ShowAsync>d__40) is named by its method; a closure's class (<>c__DisplayClass12_0) names none.
            name = type.Name.StartsWith('<') && !type.Name.StartsWith("<>", StringComparison.Ordinal) ? type.Name : name;
            type = outer;
        }
        // A lambda (<Add>b__12_0), a local function (<Add>g__Show|12_0) or a state machine (<ShowAsync>d__40).
        if (name.StartsWith('<') && name.IndexOf('>') is > 1 and int end)
        {
            string within = name[1..end];
            name = name.Length > end + 1 && name[end + 1] == 'g' && name.IndexOf('|') is > 0 and int bar ? $"{within}'s {name[(end + 4)..bar]}"
                : name.Length > end + 1 && name[end + 1] == 'b' ? $"{within}'s lambda"
                : within;
        }
        return $"{type?.Name}.{name}";
    }

    /// <summary>The instructions a state machine's first entry reaches before any await: from its start, its dispatch on
    /// the state read as the first entry's (-1), never past an await's check of its awaiter, into a handler from anywhere
    /// in its try.</summary>
    private static HashSet<int> FirstEntry(List<Instruction> code, MethodBody body)
    {
        Dictionary<int, int> index = code.Select((instruction, i) => (instruction.At, i)).ToDictionary();
        // The machine copies its state into a local first (ldarg.0, ldfld <>1__state, stloc).
        int? state = code.Count > 2 && code[1].Code == OpCodes.Ldfld && IsStore(code[2].Code) ? code[2].Value : null;
        HashSet<int> reached = [];
        Queue<int> next = new([0]);
        while (next.TryDequeue(out int i))
        {
            if (i >= code.Count || !reached.Add(code[i].At))
            {
                continue;
            }
            Instruction instruction = code[i];
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                if (instruction.At >= clause.TryOffset && instruction.At < clause.TryOffset + clause.TryLength)
                {
                    next.Enqueue(index[clause.Flags == ExceptionHandlingClauseOptions.Filter ? clause.FilterOffset : clause.HandlerOffset]);
                }
            }
            if (instruction.Called is { } called && Awaits(called))
            {
                continue;
            }
            FlowControl control = instruction.Code.FlowControl;
            if (control is FlowControl.Return or FlowControl.Throw)
            {
                continue;
            }
            if (control == FlowControl.Branch)
            {
                next.Enqueue(index[instruction.Targets[0]]);
                continue;
            }
            if (control == FlowControl.Cond_Branch)
            {
                if (Dispatched(code, i, state) is { } taken)
                {
                    next.Enqueue(taken < 0 ? i + 1 : index[taken]);
                    continue;
                }
                foreach (int target in instruction.Targets)
                {
                    next.Enqueue(index[target]);
                }
            }
            next.Enqueue(i + 1);
        }
        return reached;
    }

    /// <summary>Where the state machine's dispatch at <paramref name="i"/> goes on its first entry, its state -1: a target,
    /// or -1 for the next instruction; null for a branch that is not on the state alone.</summary>
    private static int? Dispatched(List<Instruction> code, int i, int? state)
    {
        Instruction branch = code[i];
        bool Loads(int at) => at >= 0 && IsLoad(code[at].Code) && code[at].Value == state;
        int? Constant(int at) => at >= 0 && IsConstant(code[at].Code) ? code[at].Value : null;
        if (state is null)
        {
            return null;
        }
        if (Loads(i - 1))
        {
            return branch.Code == OpCodes.Switch ? (unchecked((uint)FirstState) < branch.Targets.Length ? branch.Targets[unchecked((uint)FirstState)] : -1)
                : branch.Code == OpCodes.Brfalse || branch.Code == OpCodes.Brfalse_S ? (FirstState == 0 ? branch.Targets[0] : -1)
                : branch.Code == OpCodes.Brtrue || branch.Code == OpCodes.Brtrue_S ? (FirstState != 0 ? branch.Targets[0] : -1)
                : null;
        }
        if (Loads(i - 3) && Constant(i - 2) is { } minus && code[i - 1].Code == OpCodes.Sub && branch.Code == OpCodes.Switch)
        {
            uint at = unchecked((uint)(FirstState - minus));
            return at < branch.Targets.Length ? branch.Targets[at] : -1;
        }
        if (Loads(i - 2) && Constant(i - 1) is { } k)
        {
            string name = branch.Code.Name!.Replace(".s", string.Empty, StringComparison.Ordinal);
            bool? holds = name switch
            {
                "beq" => FirstState == k,
                "bne.un" => FirstState != k,
                "bge" => FirstState >= k,
                "bgt" => FirstState > k,
                "ble" => FirstState <= k,
                "blt" => FirstState < k,
                "bge.un" => unchecked((uint)FirstState >= (uint)k),
                "bgt.un" => unchecked((uint)FirstState > (uint)k),
                "ble.un" => unchecked((uint)FirstState <= (uint)k),
                "blt.un" => unchecked((uint)FirstState < (uint)k),
                _ => null,
            };
            return holds is { } taken ? (taken ? branch.Targets[0] : -1) : null;
        }
        return null;
    }

    private static bool IsStore(OpCode code) => code == OpCodes.Stloc_0 || code == OpCodes.Stloc_1 || code == OpCodes.Stloc_2 || code == OpCodes.Stloc_3
        || code == OpCodes.Stloc_S || code == OpCodes.Stloc;

    private static bool IsLoad(OpCode code) => code == OpCodes.Ldloc_0 || code == OpCodes.Ldloc_1 || code == OpCodes.Ldloc_2 || code == OpCodes.Ldloc_3
        || code == OpCodes.Ldloc_S || code == OpCodes.Ldloc;

    private static bool IsConstant(OpCode code) => code.Name!.StartsWith("ldc.i4", StringComparison.Ordinal);

    /// <summary>Whether a call is an await's check that its awaiter has finished: what follows it runs only after an await.</summary>
    private static bool Awaits(MethodBase called) =>
        called.Name == "get_IsCompleted" && called.DeclaringType is { } awaiter && typeof(INotifyCompletion).IsAssignableFrom(awaiter);

    /// <summary>What a call does that an action is heard by, or null; <paramref name="caller"/> is the method calling it. The
    /// window's writing of closed files (CommandHistory) is one work for every change queued, no action's own, and an agent's
    /// action refused what it asks is told so (AgentAsks) wherever it asks.</summary>
    private static Kind? KindOf(MethodBase called, MethodBase caller) => called.DeclaringType switch
    {
        { } type when type == typeof(ActionWork) && called.Name == nameof(ActionWork.Begin) && caller.DeclaringType == typeof(Passenger.App.Editors.CommandHistory) => null,
        { } type when type == typeof(ActionWork) && called.Name == nameof(ActionWork.Tell) && caller.DeclaringType == typeof(AgentAsks) => null,
        { } type when type == typeof(ActionWork) => called.Name switch
        {
            nameof(ActionWork.Begin) or nameof(ActionWork.Unseen) => Kind.Begins,
            nameof(ActionWork.Tell) => Kind.Tells,
            _ => null,
        },
        // The window's questions to the person, a dialog and the system's picker of files, each given as a delegate to call.
        { } type when type == typeof(MainWindow) && called.Name is "get_" + nameof(MainWindow.Confirm) or "get_" + nameof(MainWindow.PickFiles) => Kind.Dialog,
        { } type when type == typeof(Working) && called.Name == nameof(Working.Tell) => Kind.Told,
        { } type when type == typeof(Window) && called.Name == nameof(Window.ShowDialog) => Kind.Dialog,
        { } type when type == typeof(PopupHost) && called.Name == nameof(PopupHost.Show) => Kind.Popup,
        { } type when type == typeof(ContextMenu) && called.Name == nameof(ContextMenu.Open) => Kind.Popup,
        { } type when typeof(FlyoutBase).IsAssignableFrom(type) && called.Name == nameof(FlyoutBase.ShowAt) => Kind.Popup,
        _ => null,
    };

    /// <summary>A method's instructions in the order its IL holds them.</summary>
    private static List<Instruction> Read(MethodBase method)
    {
        List<Instruction> read = [];
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } code)
        {
            return read;
        }
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (int at = 0; at < code.Length;)
        {
            int start = at;
            OpCode instruction = code[at] == 0xFE ? Codes.TwoByte[code[at + 1]] : Codes.OneByte[code[at]];
            at += instruction.Size;
            int size = instruction.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(code, at)),
                _ => 4,
            };
            int next = at + size;
            MethodBase? called = instruction.OperandType == OperandType.InlineMethod
                ? method.Module.ResolveMethod(BitConverter.ToInt32(code, at), typeArguments, methodArguments)
                : null;
            int[] targets = instruction.OperandType switch
            {
                OperandType.ShortInlineBrTarget => [next + (sbyte)code[at]],
                OperandType.InlineBrTarget => [next + BitConverter.ToInt32(code, at)],
                OperandType.InlineSwitch => [.. Enumerable.Range(0, BitConverter.ToInt32(code, at)).Select(i => next + BitConverter.ToInt32(code, at + 4 + (4 * i)))],
                _ => [],
            };
            read.Add(new Instruction(start, instruction, called, ValueOf(instruction, code, at), targets));
            at = next;
        }
        return read;
    }

    /// <summary>The int a constant loads, or the local a load or store names.</summary>
    private static int? ValueOf(OpCode instruction, byte[] code, int at)
    {
        string name = instruction.Name!;
        if (name.StartsWith("ldc.i4", StringComparison.Ordinal))
        {
            return instruction.OperandType switch
            {
                OperandType.ShortInlineI => (sbyte)code[at],
                OperandType.InlineI => BitConverter.ToInt32(code, at),
                _ => name == "ldc.i4.m1" ? -1 : name[^1] - '0',
            };
        }
        if (name.StartsWith("ldloc", StringComparison.Ordinal) || name.StartsWith("stloc", StringComparison.Ordinal))
        {
            return instruction.OperandType switch
            {
                OperandType.ShortInlineVar => code[at],
                OperandType.InlineVar => BitConverter.ToUInt16(code, at),
                _ => name[^1] - '0',
            };
        }
        return null;
    }

    private static (OpCode[] OneByte, OpCode[] TwoByte) ReadCodes()
    {
        (OpCode[] OneByte, OpCode[] TwoByte) read = (new OpCode[0x100], new OpCode[0x100]);
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode instruction = (OpCode)field.GetValue(null)!;
            (instruction.Size == 1 ? read.OneByte : read.TwoByte)[(ushort)instruction.Value & 0xFF] = instruction;
        }
        return read;
    }
}
