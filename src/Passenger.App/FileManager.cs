using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The person's file manager (the Finder, Explorer, a Linux desktop's), where the IDE shows a file chosen in its
/// folder or a folder's contents. The one way the IDE opens it, as <see cref="MainWindow.Browse"/> is the one way it opens
/// the browser: a folder through the system's launcher; a file chosen in it through the only commands that choose one
/// (macOS's <c>open -R</c>, Explorer's <c>/select</c>), run as every child is (<see cref="ChildProcesses"/>), their output
/// the IDE's to read, never an agent's protocol. The file manager they reach is the person's, never ended with the IDE.
/// A platform with no launcher (the headless one tests, screenshots and an agent's windowless Passenger run on) opens
/// nothing, as it opens no browser.</summary>
public static class FileManager
{
    /// <summary>What the IDE calls it: the Finder, Explorer, or on Linux whichever file manager the desktop
    /// has.</summary>
    public static string Name { get; } = OperatingSystem.IsMacOS() ? "Finder" : OperatingSystem.IsWindows() ? "Explorer" : "File Manager";

    /// <summary>The action's words for showing a file there: Reveal in Finder on macOS, Show in Explorer
    /// elsewhere.</summary>
    public static string Showing { get; } = OperatingSystem.IsMacOS() ? $"Reveal in {Name}" : $"Show in {Name}";

    /// <summary>Shows a file chosen in its folder, or a folder's contents, in the window's file manager.</summary>
    public static void Show(TopLevel window, string path)
    {
        // Avalonia's stand-in for a platform with no launcher: the headless one.
        if (window.Launcher.GetType().Name == "NoopLauncher")
        {
            return;
        }
        if (Directory.Exists(path) || !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            // Linux's file managers have no one way to choose a file: its folder is shown.
            string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
            IdeErrors.Observe(window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)));
            return;
        }
        System.Diagnostics.ProcessStartInfo start = OperatingSystem.IsMacOS()
            ? ChildProcesses.Command("open", ["-R", path])
            : ChildProcesses.Command("explorer.exe", []);
        if (OperatingSystem.IsWindows())
        {
            // Explorer reads its command line its own way: the comma and the quotes are its, not an argument's quoting.
            start.Arguments = $"/select,\"{path}\"";
        }
        // Explorer answers 1 when it has handed the request to the running shell.
        _ = ChildProcesses.RunAsync(start, limit: TimeSpan.FromSeconds(30), answers: OperatingSystem.IsWindows() ? [1] : null)
            .ContinueWith(ran => ran.Exception, TaskScheduler.Default);
    }
}
