using System.Collections.Concurrent;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>The check before a launch's build: a project whose build would read only files stamped as they were before its
/// last build here, the same command line under the same SDK, and whose outputs are as that build left them (its own and
/// its references' assemblies), launches with no build. Anything the project host's evaluations cannot account for builds
/// (<see cref="BuildInputs.Unknown"/>), as does a file changed, come or gone, and outputs another build rewrote. What a
/// build left is kept for the process only, so an IDE started again builds first.</summary>
public static class UpToDate
{
    /// <summary>What a build read before it began and what it left once it succeeded, by its command line and SDK.</summary>
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<Stamp> Reads, IReadOnlyList<Stamp> Made)> Builds = new(StringComparer.Ordinal);

    /// <summary>The longest the check waits for the host to say what a build reads, past which the build runs.</summary>
    private static readonly TimeSpan Answering = TimeSpan.FromSeconds(10);

    /// <summary>A build about to run: its key, what it reads now, whether its last build here still stands, and how the host
    /// was asked (its folder, the project, the build's global properties).</summary>
    public sealed record Check(string Key, BuildInputs Inputs, bool Holds, string Folder, string Project, IReadOnlyDictionary<string, string> Properties);

    /// <summary>What a build of a project (a .csproj, built where its own build puts it) would read now, asked of the host
    /// for the folder the build runs in, with whether its last build here stands; null for one that cannot be checked, or a
    /// host that could not say. On any thread.</summary>
    public static async Task<Check?> CheckAsync(BuildStep step, IReadOnlyList<string> arguments, bool windowsTargeting, string folder, CancellationToken cancel)
    {
        if (step.Output is not null || !step.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        if (step.Framework is not null)
        {
            properties["TargetFramework"] = step.Framework;
        }
        if (windowsTargeting)
        {
            properties["EnableWindowsTargeting"] = "true";
        }
        BuildInputs inputs;
        string sdk;
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bounded.CancelAfter(Waits.Of(Answering));
        try
        {
            sdk = await Dotnet.SdkAsync(folder, bounded.Token);
            ProjectHost host = await ProjectHosts.ForAsync(folder, bounded.Token);
            inputs = await host.InputsAsync(step.Project, properties, bounded.Token);
        }
        catch (Exception unknown) when (unknown is ProjectHostFailed or InvalidOperationException or IOException or TimeoutException
            || (unknown is OperationCanceledException && bounded.IsCancellationRequested))
        {
            return null;
        }
        string key = $"{sdk}\n{folder}\n{string.Join('\n', arguments)}";
        bool holds = inputs.Unknown is null && Builds.TryGetValue(key, out (IReadOnlyList<Stamp> Reads, IReadOnlyList<Stamp> Made) last)
            && last.Reads.SequenceEqual(inputs.Reads) && last.Made.Count > 0 && last.Made.All(Stamps.Holds);
        return new Check(key, inputs, holds, folder, step.Project, properties);
    }

    /// <summary>Keeps what a build that succeeded read before it began, but the restore's outputs its own restore wrote (read
    /// again where nothing else it reads changed meanwhile), and what it left: the files it listed as written into its
    /// output folder and its references' assemblies. Nothing is kept when it listed none. On any thread.</summary>
    public static async Task BuiltAsync(Check check, CancellationToken cancel)
    {
        Builds.TryRemove(check.Key, out _);
        if (check.Inputs is not { Unknown: null, Output: { } output, Written: { } written } || !File.Exists(written))
        {
            return;
        }
        IReadOnlyList<Stamp> reads = check.Inputs.Reads;
        HashSet<string> restored = new(check.Inputs.Restored, PathNames.Comparer);
        if (reads.Any(read => restored.Contains(read.Path) && !Stamps.Holds(read)))
        {
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            bounded.CancelAfter(Waits.Of(Answering));
            try
            {
                ProjectHost host = await ProjectHosts.ForAsync(check.Folder, bounded.Token);
                BuildInputs after = await host.InputsAsync(check.Project, check.Properties, bounded.Token);
                if (after.Unknown is null && after.Reads.Count == reads.Count
                    && after.Reads.Zip(reads).All(pair => PathNames.Same(pair.First.Path, pair.Second.Path) && (pair.First == pair.Second || restored.Contains(pair.First.Path))))
                {
                    reads = after.Reads;
                }
            }
            catch (Exception unknown) when (unknown is ProjectHostFailed or InvalidOperationException or IOException or OperationCanceledException)
            {
                // What it read before it began is kept, which its next start finds changed and builds again.
            }
        }
        string folder = PathNames.Canonical(output);
        List<string> made;
        try
        {
            made = [.. File.ReadAllLines(written).Select(line => line.Trim()).Where(line => line.Length > 0 && PathNames.IsUnder(PathNames.Canonical(line), folder))];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (made.Count > 0)
        {
            Builds[check.Key] = (reads, [.. made.Concat(check.Inputs.Referenced).Distinct(PathNames.Comparer).Select(Stamps.File)]);
        }
    }

    /// <summary>Lets go of a build's last state, once a build of it fails or is stopped.</summary>
    public static void Forget(Check check) => Builds.TryRemove(check.Key, out _);
}
