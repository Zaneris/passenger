using System.Diagnostics;
using System.Globalization;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A heap dump Passenger writes itself, not the runtime through its diagnostic port: the runtime starts its
/// createdump as its own child, whose progress lines go into the program's output, which a program speaking a protocol
/// over it (a language or MCP server) would take for its own. On macOS and Linux Passenger runs the program's runtime's
/// createdump, what it says coming to Passenger; on Windows, where createdump.exe dumps only its parent, it writes the
/// dump as createdump.exe does (<see cref="DotnetProcesses.WriteHeapDump"/>). Either reads the program as a debugger
/// does, so where it cannot (Linux's ptrace kept to a program's ancestors, a runtime with no createdump beside it) only
/// the runtime can write the dump.</summary>
internal static class DumpTool
{
    /// <summary>Null when Passenger can write the program's dump, with the createdump it runs (none on Windows); else why not.</summary>
    public static (string? Tool, string? WhyNot) Find(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return (null, null);
        }
        if (OperatingSystem.IsLinux() && !CanTrace())
        {
            return (null, "this system lets a program be read only by those that started it (ptrace_scope)");
        }
        if (DotnetProcesses.RuntimeFolder(processId) is not { } folder)
        {
            return (null, "the program's runtime was not found among what it has loaded");
        }
        string tool = Path.Combine(folder, "createdump");
        return File.Exists(tool) ? (tool, null) : (null, $"its runtime has no createdump beside it, in {folder}");
    }

    /// <summary>Writes a dump of the program's heap to <paramref name="dump"/>, with createdump where given. Given up, it
    /// is left to end by itself, and its dump deleted then: it holds the program while it writes, and one ended midway
    /// would leave it held.</summary>
    public static async Task WriteAsync(string? tool, int processId, string dump, CancellationToken cancel)
    {
        Task<ChildResult> running;
        if (tool is null)
        {
            running = BackgroundWork.Run(() =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Only Windows' dumps are written without createdump");
                }
                DotnetProcesses.WriteHeapDump(processId, dump);
                return new ChildResult(0, string.Empty, string.Empty);
            }, CancellationToken.None);
        }
        else
        {
            running = ChildProcesses.RunAsync(ChildProcesses.Command(tool, ["--withheap", "--name", dump, processId.ToString(CultureInfo.InvariantCulture)]));
        }
        ChildResult result;
        try
        {
            result = await running.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            UnawaitedWork.Observe(DeleteAfterAsync(running, dump));
            throw;
        }
        catch (System.ComponentModel.Win32Exception refused)
        {
            throw new InvalidOperationException($"Windows wrote no dump of the program: {refused.Message}", refused);
        }
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"createdump wrote no dump of the program: {result.Said}");
        }
    }

    private static async Task DeleteAfterAsync(Task<ChildResult> running, string dump)
    {
        await running.ConfigureAwait(false);
        try
        {
            File.Delete(dump);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Whether this user's programs can be traced by a process that did not start them: Yama's ptrace_scope 0, or
    /// no Yama, or else CAP_SYS_PTRACE held (a container's root given it).</summary>
    private static bool CanTrace()
    {
        string scope;
        try
        {
            scope = File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        if (scope == "0")
        {
            return true;
        }
        const int SysPtrace = 19;
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("CapEff:", StringComparison.Ordinal))
            {
                return scope != "3" && ulong.TryParse(line.AsSpan("CapEff:".Length).Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong held)
                    && (held & (1UL << SysPtrace)) != 0;
            }
        }
        return false;
    }
}

/// <summary>Thrown before anything is written when only the program's runtime can write its heap's dump, which puts
/// createdump's progress lines into the program's own output: taken again with that allowed, the snapshot goes on.</summary>
public sealed class DumpIntoOutputException(string message) : InvalidOperationException(message);
