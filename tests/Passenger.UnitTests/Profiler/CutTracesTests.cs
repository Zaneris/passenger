using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A trace's read lets go of its file however the read ends: a trace cut within its header is refused as no
/// snapshot, one cut at or past its header's end opens cut short, and either is written over and deleted at once after.</summary>
public sealed class CutTracesTests
{
    /// <summary>The start of a trace .NET's EventPipe wrote: its magic and FastSerialization header, its Trace object's
    /// header, its Trace block (when it began, its clock, pointer size, process, processors and sampling rate), the end of
    /// that object, where its header ends at 102 bytes, and the first bytes of the block after.</summary>
    private static readonly byte[] Written = Convert.FromHexString(
        "4E6574747261636514000000214661737453657269616C697A6174696F6E2E31"
        + "0505010400000004000000050000005472616365" + "06"
        + "EA070A0005001E0000000A0005000500B59CD33506F6000000CA9A3B00000000080000003E7A01000C00000040420F00"
        + "06"
        + "05050102000000020000");

    private const int HeaderLength = 102;

    [Fact]
    public void A_cut_trace_is_refused_or_opened_cut_short_and_its_file_let_go_at_once() => Cases.All(
        ("a trace cut within its header is refused and its file written over and deleted at once", () =>
        {
            string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;
            try
            {
                string cut = Path.Combine(folder, "cut.nettrace");
                foreach (int length in new[] { 4, 53, 100 })
                {
                    File.WriteAllBytes(cut, Written[..length]);
                    Exception refused = Assert.ThrowsAny<Exception>(() => Snapshot.Read(cut));
                    Assert.True(TraceFailures.Broken(refused), $"The trace cut at {length} bytes, within its header, failed otherwise than as no trace: {refused}");
                    LetGo(cut, length);
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }),
        ("a trace cut at or past its header's end opens cut short with nothing and its file is written over and deleted at once", () =>
        {
            string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;
            try
            {
                string cut = Path.Combine(folder, "cut.nettrace");
                foreach (int length in new[] { HeaderLength - 1, HeaderLength + 4, Written.Length })
                {
                    File.WriteAllBytes(cut, Written[..length]);
                    Snapshot opened = Snapshot.Read(cut);
                    Assert.True(opened is { CutShort: not null, Samples: 0, Threads.Count: 0 },
                        $"The trace cut at {length} bytes opened as {opened.Samples} samples on {opened.Threads.Count} threads, cut short: {opened.CutShort ?? "no"}");
                    LetGo(cut, length);
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }));

    /// <summary>Writes over the trace just read and deletes it, as the person may at once: neither is refused while a read
    /// still holds it.</summary>
    private static void LetGo(string cut, int length)
    {
        try
        {
            File.WriteAllBytes(cut, Written);
            File.Delete(cut);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            Assert.Fail($"The trace cut at {length} bytes was still held once read: {held.Message}");
        }
        Assert.False(File.Exists(cut), $"The trace cut at {length} bytes was still there once deleted");
    }
}
