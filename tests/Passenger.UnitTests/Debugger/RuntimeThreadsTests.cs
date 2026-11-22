using System.Buffers.Binary;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A thread's stack, read up from the runtime's frame at its base, tells the routine it was started at by a frame
/// record whose return lies in it: a saved frame pointer to a record above, then the return beside it.</summary>
public class RuntimeThreadsTests
{
    private const ulong From = 0x16BB4AE78;
    private static readonly (ulong Start, ulong End) Routine = (0x10A15C4EC, 0x10A15C58C);

    /// <summary>A stack of 512 bytes from <see cref="From"/> up, with the words given at their offsets.</summary>
    private static byte[] Stack(params (int At, ulong Word)[] words)
    {
        byte[] stack = new byte[512];
        foreach ((int at, ulong word) in words)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(at), word);
        }
        return stack;
    }

    [Fact]
    public void A_frame_record_returning_into_the_routine_and_only_one_tells_the_thread_started_there() => Cases.All(
        ("a frame record returning into the routine tells the thread started there", () =>
        {
            Assert.True(RuntimeThreads.ReturnsInto(Stack((0x118, From + 0x148), (0x120, Routine.Start + 0x28)), From, Routine));
        }),
        ("a return outside the routine or beside no saved frame pointer above it tells nothing", () =>
        {
            Assert.False(RuntimeThreads.ReturnsInto(Stack((0x118, From + 0x148), (0x120, Routine.End)), From, Routine));
            Assert.False(RuntimeThreads.ReturnsInto(Stack((0x118, From + 0x118), (0x120, Routine.Start)), From, Routine));
            Assert.False(RuntimeThreads.ReturnsInto(Stack((0x120, Routine.Start + 0x28)), From, Routine));
            Assert.False(RuntimeThreads.ReturnsInto([], From, Routine));
        }),
        ("a stack read from an address off eight bytes is read by the words the stack keeps", () =>
        {
            byte[] stack = new byte[516];
            BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(0x11C), From + 0x148);
            BinaryPrimitives.WriteUInt64LittleEndian(stack.AsSpan(0x124), Routine.Start);
            Assert.True(RuntimeThreads.ReturnsInto(stack, From - 4, Routine));
            Assert.False(RuntimeThreads.ReturnsInto(stack, From - 8, Routine));
        }));

}
