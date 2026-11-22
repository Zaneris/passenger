using System.Runtime.Serialization;
using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A trace's read takes what TraceEvent throws for a trace it cannot read, its FastSerialization's plain Exception
/// among them, and leaves every other exception, a fault of Passenger's own, to go on to the boundary that says it.</summary>
public sealed class TraceFailuresTests
{
    [Fact]
    public void A_traces_read_takes_TraceEvents_failures_and_leaves_any_other_to_the_boundary() => Cases.All(
        ("traceEvents failures for a broken trace are taken and its files too", () =>
        {
            Assert.True(TraceFailures.Broken(new FormatException("Read past end of stream.")));
            Assert.True(TraceFailures.Broken(new Exception("Read past end of stream.")));
            Assert.True(TraceFailures.Broken(new SerializationException("Invalid string length.")));
            Assert.True(TraceFailures.Of(new IOException("The disk is full")));
            Assert.False(TraceFailures.Broken(new IOException("The disk is full")), "A file's failure was taken for a trace cut short");
        }),
        ("any other exception goes on to the boundary", () =>
        {
            Assert.False(TraceFailures.Of(new NullReferenceException()), "A fault of Passenger's own was taken as a trace's failure");
            Assert.False(TraceFailures.Of(new ArgumentOutOfRangeException("index")), "A fault of Passenger's own was taken as a trace's failure");
        }));

}
