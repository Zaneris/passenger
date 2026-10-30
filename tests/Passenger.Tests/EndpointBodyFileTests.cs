using System.Runtime.InteropServices;
using Passenger.App.Endpoints;

namespace Passenger.Tests;

/// <summary>A request's body named as a file (<c>&lt; ./body.json</c>) is read within the request's timeout and its stop:
/// a FIFO that nothing writes holds the request no longer than either, and nothing is sent.</summary>
[Collection(Beside.Name)]
public sealed partial class EndpointBodyFileTests
{
    [Fact]
    public async Task A_body_file_that_nothing_writes_ends_its_request_at_the_timeout_and_at_a_stop()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A FIFO is a file of macOS and Linux");
        CancellationToken test = TestContext.Current.CancellationToken;
        string fifo = Path.Combine(TestFolders.Create("endpoint-fifo-"), "body.json");
        Assert.True(MakeFifo(fifo, Convert.ToUInt32("600", 8)) == 0, $"No FIFO could be made at {fifo}: error {Marshal.GetLastPInvokeError()}");
        // Port 9 (discard) of this machine, which nothing here sends to: the body is read before any connection.
        EndpointCall call = new("POST", "http://127.0.0.1:9/items", [], string.Empty) { BodyFile = fifo };
        try
        {
            Task<EndpointResponse> timed = EndpointClient.SendAsync(call, new SendOptions { Timeout = TimeSpan.FromSeconds(1) }, test);
            Assert.True(await Waiting.Within(timed, TimeSpan.FromSeconds(20), test),
                "A request whose body's file is a FIFO nothing writes was still under way long after its 1 s timeout: Test would wait for good");
            Assert.Equal($"The body's file {fifo} was not read within 1 s", (await timed).Error);

            using CancellationTokenSource stop = new();
            Task<EndpointResponse> stopped = EndpointClient.SendAsync(call, new SendOptions { Timeout = TimeSpan.FromHours(1) }, stop.Token);
            await Waiting.ForAsync(TimeSpan.FromMilliseconds(200), "the body's file being opened before the stop", test);
            Assert.False(stopped.IsCompleted, $"A request whose body's file is a FIFO nothing writes ended ({stopped.Status}) before its stop, which this test then cannot show");
            stop.Cancel();
            Assert.True(await Waiting.Within(stopped, TimeSpan.FromSeconds(20), test),
                "A request whose body's file is a FIFO nothing writes went on after its stop: Stop would do nothing");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);
        }
        finally
        {
            // The reads left waiting to open it end once a writer has opened and closed it.
            int writer = Open(fifo, OperatingSystem.IsMacOS() ? 0x1 | 0x4 : 0x1 | 0x800);
            if (writer >= 0)
            {
                _ = Close(writer);
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeFifo(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}
