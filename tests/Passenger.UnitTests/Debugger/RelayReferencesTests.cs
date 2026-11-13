using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Relay;

namespace Passenger.UnitTests.Debugger;

/// <summary>The engine's hold on an app's objects through the relay: each object let go in the app once the engine holds
/// none of it, the objects the app's debugger keeps anyway held for the session, and what an object said asked of the
/// app once, for as long as it holds.</summary>
public class RelayReferencesTests
{
    [Fact]
    public void A_stops_objects_are_let_go_in_the_app_once_the_engine_lets_go_of_them()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint thread = session.Take(new RemotePointer(0x1000, 0x1000), RemoteInterfaces.Of(typeof(ICorDebugThread)));
        nint value = session.Take(new RemotePointer(0x2000, 0x2000), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        Marshal.AddRef(thread);
        Marshal.Release(value);
        Marshal.Release(thread);
        Assert.True(app.Released.SequenceEqual([0x2000ul]), $"With a value let go and a thread still held, the app was told to let go of {string.Join(", ", app.Released)}");
        Marshal.Release(thread);
        Assert.True(app.Released.SequenceEqual([0x2000ul, 0x1000ul]), "The thread was not let go in the app once the engine let go of it");
        Assert.True(session.ObjectsHeld == 0, $"{session.ObjectsHeld} objects are still held once the engine let go of all of them");
    }

    [Fact]
    public void A_second_pointer_to_an_interface_held_lets_its_reference_go_at_once()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint first = session.Take(new RemotePointer(0x2000, 0x2008), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        nint second = session.Take(new RemotePointer(0x2000, 0x2008), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        Assert.True(first == second && app.Released.SequenceEqual([0x2000ul]), "A second pointer to a value held kept its reference in the app");
        Marshal.Release(first);
        Marshal.Release(second);
        Assert.True(app.Released.SequenceEqual([0x2000ul, 0x2000ul]), "The value's own reference was not let go");
    }

    [Fact]
    public void A_class_is_held_for_the_session_and_its_token_asked_of_the_app_once()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x3000, 0x3000), RemoteInterfaces.Of(typeof(ICorDebugClass)));
        ICorDebugClass @class = (ICorDebugClass)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.Equal(0, @class.TryGetToken(out mdTypeDef first));
        session.EndStop();
        Assert.Equal(0, @class.TryGetToken(out mdTypeDef second));
        Assert.True(first == second && app.Asked(typeof(ICorDebugClass), nameof(ICorDebugClass.TryGetToken)) == 1, $"A class's token was asked of the app {app.Asked(typeof(ICorDebugClass), nameof(ICorDebugClass.TryGetToken))} times");
        Marshal.Release(block);
        Assert.True(app.Released.Count == 0, "A class the app's debugger keeps for its module was let go, losing what it said");
    }

    [Fact]
    public void What_a_value_said_holds_until_the_program_runs()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x4000, 0x4000), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        ICorDebugValue value = (ICorDebugValue)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.Equal(0, value.TryGetType(out _));
        Assert.Equal(0, value.TryGetType(out _));
        Assert.True(app.Asked(typeof(ICorDebugValue), nameof(ICorDebugValue.TryGetType)) == 1, "A value's type was asked of the app again within one stop");
        session.EndStop();
        Assert.Equal(0, value.TryGetType(out _));
        Assert.True(app.Asked(typeof(ICorDebugValue), nameof(ICorDebugValue.TryGetType)) == 2, "A value's type was not asked again once the program ran");
    }

    [Fact]
    public void Two_methods_at_one_slot_of_two_interfaces_of_a_value_are_remembered_apart()
    {
        App app = new();
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x4000, 0x4000), RemoteInterfaces.Of(typeof(ICorDebugValue)));
        ICorDebugValue value = (ICorDebugValue)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        Assert.Equal(0, value.TryGetType(out _));
        int exact = ((ICorDebugValue2)value).TryGetExactType(out ICorDebugType type);
        Assert.True(exact == 0 && type is not null, $"A value's exact type, at the slot its type takes in another interface, answered {exact:X8}");
    }

    [Fact]
    public void An_enumerators_elements_are_read_in_one_round_trip_and_each_let_go_with_the_engines_hold()
    {
        App app = new() { Frames = 3 };
        RelaySession session = new(app, new DeviceModules(null), null);
        nint block = session.Take(new RemotePointer(0x5000, 0x5000), RemoteInterfaces.Of(typeof(ICorDebugFrameEnum)));
        ICorDebugFrameEnum frames = (ICorDebugFrameEnum)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(block, CreateObjectFlags.UniqueInstance);
        List<ICorDebugFrame> walked = [];
        while (true)
        {
            ICorDebugFrame[] one = new ICorDebugFrame[1];
            int hr = frames.TryNext(1, one, out uint fetched);
            if (fetched == 0)
            {
                Assert.Equal(1, hr);
                break;
            }
            walked.Add(one[0]);
        }
        Assert.True(walked.Count == 3 && app.Asked(typeof(ICorDebugFrameEnum), nameof(ICorDebugFrameEnum.TryNext)) == 1,
            $"Walking 3 frames gave {walked.Count} in {app.Asked(typeof(ICorDebugFrameEnum), nameof(ICorDebugFrameEnum.TryNext))} round trips, where one brings them all");
    }

    /// <summary>An app's debugger as the relay carries it: every call answered by its arguments' kinds, a frame
    /// enumerator's Next with <see cref="Frames"/> frames, every call's slot and every release heard.</summary>
    private sealed class App : IRelayLink
    {
        private readonly ConcurrentQueue<uint> _asked = new();
        private ulong _next = 0x10000;

        public int Frames { get; init; }

        public List<ulong> Released { get; } = [];

        public ulong Callback => 0;

        public BlockingCollection<RelayEvent> Events { get; } = [];

        public int Asked(Type type, string method) => _asked.Count(each => each == RelaySession.SlotOf(type, method));

        public RelayReply Call(ulong obj, uint slot, IReadOnlyList<RelayArg> args)
        {
            _asked.Enqueue(slot);
            if (slot == 0)
            {
                return new RelayReply(0, [], [[new RemotePointer(obj + 8, obj)]]);
            }
            if (slot == RelaySession.SlotOf(typeof(ICorDebugFrameEnum), nameof(ICorDebugFrameEnum.TryNext)) && args.Count == 3)
            {
                int count = (int)args[0].Value;
                int given = Math.Min(count, Frames);
                RemotePointer[] frames = [.. Enumerable.Range(0, count).Select(i => i < given ? new RemotePointer(_next, _next++) : default)];
                return new RelayReply(given < count ? 1 : 0, [BitConverter.GetBytes(given)], [frames]);
            }
            List<byte[]> outs = [];
            List<RemotePointer[]> objects = [];
            foreach (RelayArg arg in args)
            {
                if (arg.Kind is RelayArgKind.Out or RelayArgKind.InOut)
                {
                    byte[] written = new byte[arg.Size];
                    written.AsSpan().Fill(7);
                    outs.Add(written);
                }
                else if (arg.Kind == RelayArgKind.Objects)
                {
                    objects.Add([.. Enumerable.Range(0, arg.Size).Select(_ => new RemotePointer(_next, _next++))]);
                }
            }
            return new RelayReply(0, outs, objects);
        }

        public void Release(ulong pointer)
        {
            if (pointer != 0)
            {
                Released.Add(pointer);
            }
        }

        public (int Trips, double WaitedMs) TakeTally() => (0, 0);
    }
}
