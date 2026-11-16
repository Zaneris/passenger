using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

// A Mac Catalyst app's run here: its plan made from its build, and its process found and ended with the run.
public static partial class MacCatalystApps
{
    /// <summary>A built plan's app read from its build, the plan then opening it (<see cref="Opening"/>, through
    /// <see cref="Shell"/>); a plan of no Mac Catalyst app as it is; why not, in words for the person.</summary>
    public static async Task<(LaunchPlan? Plan, string? Problem)> ReadyAsync(LaunchPlan plan, IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
    {
        if (plan.MacCatalyst is not { App: null } launch || plan.Project is not { } project)
        {
            return (plan, null);
        }
        MSBuildAnswer said = await MSBuildQuestions.AskAsync(project, launch.Framework, globals, new MSBuildQuestion([MSBuildNames.ComputeRunArguments, MSBuildNames.ReadAppManifest])
        {
            Properties = [MSBuildNames.AppBundleDir, MSBuildNames.NativeExecutableName, MSBuildNames.BundleIdentifier, MSBuildNames.RuntimeIdentifier, MSBuildNames.LinkMode],
        }, cancel);
        string name = Path.GetFileNameWithoutExtension(project);
        if (!said.Succeeded || said.Property(MSBuildNames.AppBundleDir) is not { Length: > 0 } bundle
            || said.Property(MSBuildNames.NativeExecutableName) is not { Length: > 0 } program)
        {
            return (null, $"MSBuild did not say where {name}'s Mac Catalyst app is: {said.Said.FirstLine}");
        }
        string folder = PathNames.Canonical(Path.Combine(Path.GetDirectoryName(project)!, bundle));
        string package = said.Property(MSBuildNames.BundleIdentifier) ?? string.Empty;
        MacCatalystApp app = new(folder, Path.Combine(MacBundles.Programs(folder), program))
        {
            Package = package.Length > 0 ? package : name,
            RuntimeIdentifier = said.Property(MSBuildNames.RuntimeIdentifier) ?? string.Empty,
            Whole = said.Property(MSBuildNames.LinkMode) is not { Length: > 0 } link || link.Equals("None", StringComparison.OrdinalIgnoreCase),
        };
        if (!File.Exists(app.Executable))
        {
            return (null, $"Could not run {name}: its Mac Catalyst app {Path.GetFileName(folder)} has not been built");
        }
        bool sandboxed = await SandboxedAsync(folder, cancel);
        if (sandboxed && package.Length == 0)
        {
            return (null, $"MSBuild did not say {name}'s bundle identifier, which names the container its sandbox keeps it in: {said.Said.FirstLine}");
        }
        string into;
        string pipes;
        try
        {
            int start = Interlocked.Increment(ref carried);
            string own = SocketFolders.Own(State.StateStore.DefaultRoot);
            into = sandboxed ? Path.Combine(MacCatalystApp.ContainerTemporary(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), package),
                    MacCatalystApp.CarriedName(Environment.ProcessId, start))
                : Path.Combine(own, $"{Environment.ProcessId}-{start}.catalyst");
            pipes = Path.Combine(own, MacCatalystApp.PipesName(Environment.ProcessId, start));
        }
        catch (Exception unowned) when (unowned is IOException or UnauthorizedAccessException)
        {
            return (null, $"Could not run {name}: Passenger has no folder of its own to carry its inspector into: {unowned.Message}");
        }
        app = app with { Carried = into, Pipes = pipes };
        IReadOnlyList<string> opening = Opening(app, plan.Unseen, [.. plan.OwnArguments]);
        return (plan with
        {
            Notes = app.Whole ? plan.Notes : [.. plan.Notes, MacCatalystApp.Trimmed(name)],
            Program = Shell,
            Arguments = opening,
            Leading = opening.Count - plan.OwnArguments.Count(),
            MacCatalyst = launch with { App = app },
        }, null);
    }

    /// <summary>The folders this process has carried into for apps, which number each one's name apart.</summary>
    private static int carried;

    /// <summary>Whether the app's signature gives it the App Sandbox, as codesign reads it from the bundle: a sandboxed app runs
    /// in its container, the only place it reads Passenger's files and listens on sockets.</summary>
    private static async Task<bool> SandboxedAsync(string bundle, CancellationToken cancel)
    {
        ChildResult read = await ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/codesign", ["-d", "--entitlements", "-", "--xml", bundle]),
            limit: TimeSpan.FromSeconds(30), cancel: cancel);
        return read.Succeeded && MacCatalystApp.Sandboxed(read.Output);
    }

    /// <summary>The app's process this start opened (<see cref="AppProcess"/>) adopted once it runs, so it ends with
    /// Passenger as its children do, and ended with its run: once its opener has <paramref name="ended"/> (the app ended, or
    /// Stop ended the opener), whatever of it is left goes. Never throws.</summary>
    public static async Task EndWithAsync(MacCatalystApp app, Task ended)
    {
        if (await app.Process.FindAsync(program => PathNames.Same(program, app.Executable), ended) is null)
        {
            return;
        }
        app.Process.Adopt(app.Executable);
        await ended.Ended();
        app.Process.End();
    }
}
