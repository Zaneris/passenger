using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Passenger.Profiler;

/// <summary>A .NET program's runtime read from a dump or a running process through the DAC in the program's own runtime
/// folder, never one downloaded.</summary>
public static class ProgramRuntimes
{
    /// <summary>Each DAC read through, by its path, loaded once more so it is never unloaded (<see cref="Open"/>).</summary>
    private static readonly ConcurrentDictionary<string, IntPtr> KeptDacs = new(StringComparer.Ordinal);

    /// <summary>The runtime through the DAC beside it, which on macOS and Linux stays loaded for the IDE's life: the DAC
    /// leaves a thread-local destructor behind, which would run from unmapped memory as a thread that used it ends once it
    /// was unloaded.</summary>
    public static ClrRuntime Open(ClrInfo clr)
    {
        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        string? folder = Path.GetDirectoryName(clr.ModuleInfo.FileName);
        string? dac = string.IsNullOrEmpty(folder) ? null : clr.DebuggingLibraries
            .Where(l => l.Kind == DebugLibraryKind.Dac && l.Platform == platform && l.TargetArchitecture == RuntimeInformation.ProcessArchitecture)
            .Select(l => Path.Combine(folder, Path.GetFileName(l.FileName)))
            .FirstOrDefault(File.Exists);
        if (dac is null)
        {
            throw new InvalidOperationException($"the program's .NET {clr.Version.Major}.{clr.Version.Minor} has no DAC beside it to read its heap with");
        }
        if (!OperatingSystem.IsWindows())
        {
            KeptDacs.GetOrAdd(dac, NativeLibrary.Load);
        }
        return clr.CreateRuntime(dac, ignoreMismatch: true);
    }
}
