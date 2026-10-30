using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A heap dump, written by Passenger itself where it can rather than by the runtime through its diagnostic port:
/// the runtime starts its createdump as its own child, whose progress lines go into the program's output, which a program
/// speaking a protocol over it (a language or MCP server) would take for its own. On macOS and Linux Passenger runs the
/// program's runtime's createdump; on Windows, where createdump.exe dumps only its parent, it writes the dump as
/// createdump.exe does (<see cref="DotnetProcesses.WriteHeapDump"/>). Either reads the program as a debugger does, so where
/// it cannot (Linux's ptrace kept to a program's ancestors, a runtime with no createdump beside it) only the runtime can
/// write the dump. A dump, as large as the heap, lives in the temporary folder until its snapshot is done with it, and one
/// a Passenger left there is swept.</summary>
internal static class DumpTool
{
    private const string Prefix = "passenger-heap-";

    /// <summary>The dumps this process has under way, which a sweep leaves.</summary>
    private static readonly ConcurrentDictionary<string, bool> UnderWay = new(StringComparer.Ordinal);

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

    /// <summary>A new dump's path in the temporary folder, under way until deleted (<see cref="DeleteAfterAsync"/>): named
    /// for this process and the program, so a sweep tells a dump of a Passenger still running from one left.</summary>
    public static string Named(int processId) => Named(Path.GetTempPath(), processId);

    internal static string Named(string folder, int processId)
    {
        string dump = Path.Combine(folder, $"{Prefix}{Environment.ProcessId}-{processId}-{Guid.NewGuid().ToString("N")[..8]}.dmp");
        UnderWay[dump] = true;
        return dump;
    }

    /// <summary>Writes a dump of the program's heap to <paramref name="dump"/>: with createdump where given, by the program's
    /// runtime through its diagnostic port where <paramref name="runtime"/> is, else as Windows' createdump.exe does. Nothing
    /// ends it early: it holds the program while it writes, and one ended midway would leave it held, so a snapshot given
    /// up leaves it to end by itself, and its dump is deleted then (<see cref="DeleteAfterAsync"/>).</summary>
    public static async Task WriteAsync(string? tool, DiagnosticsClient? runtime, int processId, string dump)
    {
        if (runtime is not null)
        {
            await runtime.WriteDumpAsync(DumpType.WithHeap, dump, logDumpGeneration: false, CancellationToken.None).ConfigureAwait(false);
            return;
        }
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
            result = await running.ConfigureAwait(false);
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

    /// <summary>Deletes a dump once what writes and reads it have ended, however they ended, its snapshot having said
    /// whatever failed: a writer given up writes on, and Windows deletes no file open. One the system will not delete is
    /// left to the next sweep.</summary>
    public static async Task DeleteAfterAsync(string dump, params Task?[] users)
    {
        foreach (Task? user in users)
        {
            if (user is not null)
            {
                await user.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                // Seen here, so its fault is not raised again as unobserved.
                _ = user.Exception;
            }
        }
        try
        {
            File.Delete(dump);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
        }
        UnderWay.TryRemove(dump, out _);
    }

    /// <summary>Deletes the dumps in the temporary folder that no snapshot has under way: those of a Passenger that ended
    /// before deleting its own, or whose delete the system refused. A Passenger's still running stays, and one whose name
    /// says not whose, once an hour old: a snapshot reads its dump within minutes.</summary>
    public static void Sweep() => Sweep(Path.GetTempPath(), Environment.ProcessId, DotnetProcesses.Runs, DateTime.UtcNow);

    internal static void Sweep(string folder, int self, Func<int, bool> running, DateTime now)
    {
        List<string> dumps;
        try
        {
            dumps = [.. Directory.EnumerateFiles(folder, $"{Prefix}*.dmp")];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (string dump in dumps)
        {
            string[] parts = Path.GetFileNameWithoutExtension(dump)[Prefix.Length..].Split('-');
            bool left = parts.Length == 3 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int maker)
                ? maker == self ? !UnderWay.ContainsKey(dump) : !running(maker)
                : now - File.GetLastWriteTimeUtc(dump) > TimeSpan.FromHours(1);
            if (!left)
            {
                continue;
            }
            try
            {
                File.Delete(dump);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
            }
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
