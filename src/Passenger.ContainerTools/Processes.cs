using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Passenger.Core;

namespace Passenger.ContainerTools;

/// <summary>A .NET program of the container: what Attach to Process shows of it, whether it was started held for Hot
/// Reload (DOTNET_MODIFIABLE_ASSEMBLIES=debug), whether its code is a Release build's, and why the debugger cannot
/// attach to it, when it cannot.</summary>
public sealed record ContainerProcess(int Id, string Name, string CommandLine, string User, bool HotReload, bool Optimized, string? WhyNot);

/// <summary>The <c>processes</c> mode: the container's .NET programs, those <see cref="DotnetProcesses"/> finds and
/// those whose runtime opened no pipe for a debugger, found by the runtime's library in their memory, each saying
/// why.</summary>
public static class Processes
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(TextWriter output) => output.WriteLine(JsonSerializer.Serialize(List(), Json));

    public static IReadOnlyList<ContainerProcess> List()
    {
        List<ContainerProcess> found = [];
        HashSet<int> listed = [];
        foreach (DotnetProcess process in DotnetProcesses.List())
        {
            listed.Add(process.Id);
            found.Add(new ContainerProcess(process.Id, process.Name, process.CommandLine, process.User,
                Variable(process.Id, RuntimeVariables.ModifiableAssemblies) is { } modifiable && modifiable.Equals("debug", StringComparison.OrdinalIgnoreCase),
                Optimized(process), null));
        }
        foreach (int id in Running())
        {
            if (id == Environment.ProcessId || listed.Contains(id) || DotnetProcesses.RuntimeFolder(id) is null || DotnetProcesses.Describe(id) is not { } process)
            {
                continue;
            }
            // Held at its start by its diagnostic port, so no debugger's pipe yet: attachable, unless its debugger is off.
            bool held = HeldStart.Holds(name => Variable(id, name));
            string? debuggerOff = HeldStart.DebuggerOff(name => Variable(id, name));
            found.Add(new ContainerProcess(process.Id, process.Name, process.CommandLine, process.User,
                held && debuggerOff is null && Variable(id, RuntimeVariables.ModifiableAssemblies) is { } modifiable
                    && modifiable.Equals("debug", StringComparison.OrdinalIgnoreCase),
                Optimized(process), debuggerOff ?? (held ? null : WhyUnreachable(id))));
        }
        return found;
    }

    /// <summary>Why a .NET program with its debugger on opened no pipe for one: its temporary folder elsewhere, or unknown.</summary>
    private static string WhyUnreachable(int id) =>
        Variable(id, "TMPDIR") is { Length: > 0 } temporary && !SameFolder(temporary, Path.GetTempPath())
            ? $"Its runtime keeps the debugger's pipes in TMPDIR {temporary}, which the container's other programs do not share"
            : "Its runtime opened no pipe for a debugger";

    private static bool SameFolder(string one, string other) => string.Equals(one.TrimEnd('/'), other.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Every process of the container by its id.</summary>
    private static IEnumerable<int> Running()
    {
        string[] folders;
        try
        {
            folders = Directory.GetDirectories("/proc");
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string folder in folders)
        {
            if (int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                yield return id;
            }
        }
    }

    /// <summary>A variable of a process's environment as it started, or null.</summary>
    private static string? Variable(int id, string name)
    {
        try
        {
            string prefix = name + "=";
            return File.ReadAllText($"/proc/{id}/environ").Split('\0').FirstOrDefault(v => v.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the program's own assembly (<see cref="DotnetProcess.Assembly"/>) was built optimized (a Release
    /// build): its DebuggableAttribute leaves the JIT's optimizations on. False when unreadable.</summary>
    private static bool Optimized(DotnetProcess process)
    {
        try
        {
            if (process.Assembly is not { } assembly)
            {
                return false;
            }
            string path = Path.IsPathRooted(assembly) ? assembly : Path.Combine(new DirectoryInfo($"/proc/{process.Id}/cwd").ResolveLinkTarget(true)?.FullName ?? "/", assembly);
            return AssemblyFacts.Read(path) is { Optimized: true };
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
