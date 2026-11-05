using Passenger.App.Godot;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Xvfb's start given up as it waits for the server to say its display, against a stand-in on the PATH that never
/// does: no server is left running. On the PATH of the process, so never beside what runs programs of its own.</summary>
public sealed class VirtualDisplaysTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("xvfb-"));

    public void Dispose() => TestFolders.Delete(folder);

    [Fact]
    public async Task Xvfb_given_up_as_it_starts_leaves_no_server_running()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Xvfb is Linux's, and the stand-in for it is a shell script");
            return;
        }
        string program = Path.Combine(folder, GodotExecutables.VirtualDisplay);
        File.WriteAllText(program, "#!/bin/sh\nexec sleep 600\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        int server = 0;
        void Heard(object? sender, ChildStart child)
        {
            if (child.Start?.FileName == program)
            {
                server = child.ProcessId;
            }
        }
        string? path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + path);
        ChildProcesses.Started += Heard;
        using CancellationTokenSource giving = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            Task<string?> opening = VirtualDisplays.OpenAsync(giving.Token);
            Assert.True(await Waiting.UntilAsync(() => server != 0, TimeSpan.FromSeconds(30), giving.Token), "The stand-in Xvfb never started");
            await giving.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            Assert.True(await Waiting.UntilAsync(() => !DotnetProcesses.Runs(server), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "An Xvfb whose start was given up runs on, held by no game and past any idle wait");
        }
        finally
        {
            ChildProcesses.Started -= Heard;
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }
}
