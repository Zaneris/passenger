using Passenger.App.Debugging;

namespace Passenger.App.Run.Devices;

/// <summary>What a start of an app that runs as a process of this Mac (an iOS simulator's, a Mac Catalyst app) carries in for
/// it: Passenger's files (the relay, the inspector, the update handlers' hook) copied into a folder the app reads, whatever
/// macOS keeps it from reading elsewhere, and the variables its start gives it, naming them there.</summary>
internal static class CarriedIn
{
    /// <summary>The folder made where it is not, each folder made for it the user's alone, as macOS makes a container's own
    /// (a start may come before the app's first, and so before macOS has made its container); why not, or null.</summary>
    public static string? Make(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            return "only a Mac runs an app whose files are carried in";
        }
        try
        {
            // A mode given makes only the last folder so: those above it, made first, are made one by one.
            Stack<string> missing = [];
            for (string? at = Path.TrimEndingDirectorySeparator(folder); at is not null && !Directory.Exists(at); at = Path.GetDirectoryName(at))
            {
                missing.Push(at);
            }
            foreach (string made in missing)
            {
                Directory.CreateDirectory(made, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return null;
        }
        catch (Exception unmade) when (unmade is IOException or UnauthorizedAccessException)
        {
            return unmade.Message;
        }
    }

    /// <summary>The files copied into <paramref name="folder"/> by their names, the folder made where it is not; why not, or null.</summary>
    public static string? Copy(string folder, IReadOnlyList<string> files)
    {
        if (Make(folder) is { } unmade)
        {
            return unmade;
        }
        try
        {
            foreach (string file in files)
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
            }
            return null;
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            return unwritten.Message;
        }
    }

    /// <summary>The variables a start gives the app over the plan's <paramref name="environment"/>: its token, by which its
    /// <paramref name="process"/> is found, the inspector's (its hook loaded from <paramref name="folder"/>, first among the
    /// startup hooks), the relay's and the update handlers' hook's (carried too, first again), each where the start has one.</summary>
    public static Dictionary<string, string> Variables(string folder, AppProcess process, IReadOnlyDictionary<string, string> environment, DeviceInspector? inspector,
        RelaySession? relay, UpdateHandlerHook? handlers)
    {
        Dictionary<string, string> given = new(environment) { [AppProcess.Variable] = process.Token };
        if (inspector is not null)
        {
            Over(given, inspector.Variables(folder, [.. given]));
        }
        Over(given, relay?.Given ?? new Dictionary<string, string>());
        if (handlers is not null)
        {
            Over(given, handlers.Over(given, Path.Combine(folder, Path.GetFileName(UpdateHandlerHook.HookPath))));
        }
        return given;

        static void Over(Dictionary<string, string> under, IEnumerable<KeyValuePair<string, string>> over)
        {
            foreach ((string name, string value) in over.ToList())
            {
                under[name] = value;
            }
        }
    }

    /// <summary>The folder and all in it taken out where it is, what could not be said in the log. Never throws.</summary>
    public static void TakeOut(string folder, string app)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception left) when (left is IOException or UnauthorizedAccessException)
        {
            IdeLog.Write(IdeLog.Ide, $"What a start carried into {app} was not all taken out ({folder}): {left.Message}");
        }
    }
}
