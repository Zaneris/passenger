using System.Runtime.InteropServices;
using Passenger.App.Endpoints;

namespace Passenger.Tests;

/// <summary>A request's body named as a file (<c>&lt; ./body.json</c>) that is a FIFO is refused at once, each time it is
/// sent, saying so: nothing is sent, and no read is left waiting for a writer that may never come.</summary>
[Collection(Beside.Name)]
public sealed partial class EndpointBodyFileTests
{
    /// <summary>The error open gives a writer that will not wait while no reader has the FIFO open or waits to.</summary>
    private const int NoReader = 6;

    [Fact]
    public async Task A_body_file_that_is_a_FIFO_is_refused_at_once_each_time_leaving_no_read_waiting_on_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A FIFO is a file of macOS and Linux");
        CancellationToken test = TestContext.Current.CancellationToken;
        string fifo = Path.Combine(TestFolders.Create("endpoint-fifo-"), "body.json");
        Assert.True(MakeFifo(fifo, Convert.ToUInt32("600", 8)) == 0, $"No FIFO could be made at {fifo}: error {Marshal.GetLastPInvokeError()}");
        // Port 9 (discard) of this machine, which nothing here sends to: the body is read before any connection.
        EndpointCall call = new("POST", "http://127.0.0.1:9/items", [], string.Empty) { BodyFile = fifo };
        int writer = -1;
        try
        {
            for (int send = 1; send <= 2; send++)
            {
                // An hour's timeout, so a request waiting to open the FIFO is still under way when the wait gives up.
                Task<EndpointResponse> sent = EndpointClient.SendAsync(call, new SendOptions { Timeout = TimeSpan.FromHours(1) }, test);
                Assert.True(await Waiting.Within(sent, TimeSpan.FromSeconds(20), test),
                    $"Send {send} of a request whose body's file is a FIFO nothing writes was still under way after 20 s: it waits to open the FIFO, where it is refused at once");
                Assert.Equal($"The body's file {fifo} could not be read: it is a FIFO, not a regular file", (await sent).Error);
            }
            writer = Open(fifo, OperatingSystem.IsMacOS() ? 0x1 | 0x4 : 0x1 | 0x800);
            int error = Marshal.GetLastPInvokeError();
            Assert.True(writer < 0 && error == NoReader,
                $"A writer that will not wait opened the FIFO ({writer}, error {error}) after two refused sends: a read was left waiting to open it, a thread blocked for good each send");
        }
        finally
        {
            // The reads left waiting to open it end once a writer has opened and closed it.
            if (writer < 0)
            {
                writer = Open(fifo, OperatingSystem.IsMacOS() ? 0x1 | 0x4 : 0x1 | 0x800);
            }
            if (writer >= 0)
            {
                _ = Close(writer);
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeFifo(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}
