using System.Runtime.Serialization;
using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A trace's read takes what TraceEvent throws for a trace it cannot read, its FastSerialization's plain Exception
/// among them, and leaves every other exception, a fault of Passenger's own, to go on to the boundary that says it.</summary>
public sealed class TraceFailuresTests
{
    [Fact]
    public void TraceEvents_failures_for_a_broken_trace_are_taken_and_its_files_too()
    {
        Assert.True(TraceFailures.Broken(new FormatException("Read past end of stream.")));
        Assert.True(TraceFailures.Broken(new Exception("Read past end of stream.")));
        Assert.True(TraceFailures.Broken(new SerializationException("Invalid string length.")));
        Assert.True(TraceFailures.Of(new IOException("The disk is full")));
        Assert.True(TraceFailures.Of(new UnauthorizedAccessException("Access denied")));
        Assert.False(TraceFailures.Broken(new IOException("The disk is full")), "A file's failure was taken for a trace cut short");
    }

    [Fact]
    public void Any_other_exception_goes_on_to_the_boundary()
    {
        Assert.False(TraceFailures.Of(new NullReferenceException()), "A fault of Passenger's own was taken as a trace's failure");
        Assert.False(TraceFailures.Of(new InvalidCastException()), "A fault of Passenger's own was taken as a trace's failure");
        Assert.False(TraceFailures.Of(new ArgumentOutOfRangeException("index")), "A fault of Passenger's own was taken as a trace's failure");
    }
}
