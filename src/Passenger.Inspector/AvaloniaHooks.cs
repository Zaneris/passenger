using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>What Passenger's hooks into an Avalonia program share (the inspector, the test frames' hook, and the tests'
/// own rig) of Avalonia's insides: the top levels its media context draws, which is no public list, read by Avalonia's
/// own names, so a rename fails here, saying what is gone.</summary>
internal static class AvaloniaHooks
{
    private static PropertyInfo? instance;

    /// <summary>The top levels Avalonia's media context draws: every window and popup shown.</summary>
    public static List<TopLevel> Drawn() =>
        MediaContextField("_topLevels").GetValue(MediaContext()) is IDictionary drawn
            ? [.. drawn.Keys.OfType<TopLevel>()]
            : throw new MissingFieldException("Avalonia's MediaContext no longer keeps its top levels as a dictionary in _topLevels");

    /// <summary>The window a popup is over, through the popups it is over.</summary>
    public static TopLevel Owner(PopupRoot popup)
    {
        TopLevel owner = popup.ParentTopLevel;
        while (owner is PopupRoot { ParentTopLevel: { } parent })
        {
            owner = parent;
        }
        return owner;
    }

    /// <summary>Avalonia's media context, which draws every top level, on the UI thread that made it.</summary>
    public static object MediaContext() =>
        (instance ??= MediaContextType().GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMemberException("Avalonia's MediaContext no longer has its Instance")).GetValue(null)
        ?? throw new InvalidOperationException("Avalonia has no media context");

    /// <summary>One of the media context's own fields, by its name.</summary>
    public static FieldInfo MediaContextField(string name) =>
        MediaContextType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException($"Avalonia's MediaContext no longer has {name}");

    private static Type MediaContextType() =>
        typeof(Visual).Assembly.GetType("Avalonia.Media.MediaContext") ?? throw new MissingMemberException("Avalonia no longer has its MediaContext");
}

/// <summary>Where a hook runs: its own context, which takes Avalonia's assemblies from the program whatever their 12.x
/// version, since the default context would refuse one older than the 12.x the hook was compiled against, and those of
/// Avalonia's the hook carries, the build fitting the program's Avalonia, where the program has none of its own
/// (<see cref="Carried"/>). Nothing of it touches Avalonia, so a hook asks it before Avalonia is loaded.</summary>
internal sealed class AppAvaloniaContext(string what, string hook) : AssemblyLoadContext($"{what} ({Path.GetFileName(hook)})")
{
    /// <summary>What a program seldom carries of Avalonia's and the inspector brings: the runtime XAML compiler (XAML Hot
    /// Reload's) and the headless platform (an app nobody is to see), each built against an Avalonia 12, Passenger's beside
    /// the hook and older ones' in a folder each under <see cref="OlderFolder"/>.</summary>
    private static readonly string[] Carried = ["Avalonia.Markup.Xaml.Loader", "Avalonia.Headless"];

    /// <summary>The folder beside the hook holding what it carries built against older Avalonias than Passenger's.</summary>
    private const string OlderFolder = "avalonia";

    /// <summary>Avalonia's platforms that draw where people see them, by their assemblies.</summary>
    public static readonly string[] ScreenPlatforms = ["Avalonia.Native", "Avalonia.X11", "Avalonia.Win32", "Avalonia.LinuxFramebuffer"];

    /// <summary>Whether the program's own code uses Avalonia's headless platform, so its Main may draw there itself. A build
    /// that only carries the platform, for its tests or through a package, leaves no reference in the program.</summary>
    public static bool ProgramUsesHeadless() => Assembly.GetEntryAssembly()?.GetReferencedAssemblies().Any(a => a.Name == "Avalonia.Headless") == true;

    /// <summary>The Avalonia the program will load (<see cref="Taken"/>), null when it has none; and why the hooks cannot serve
    /// it, null where they can: no Avalonia, another major version than theirs, or no Avalonia.Controls, as a program taking
    /// Avalonia.Base alone (a library's tool) has, which makes no windows and whose Application the hooks' threads cannot
    /// read.</summary>
    public static (Version? Version, string? WhyNot) ProgramAvalonia()
    {
        Version? version = Taken("Avalonia.Base");
        string? whyNot = version is null ? "The app does not use Avalonia"
            : version.Major != StartupHooks.AvaloniaMajor ? $"The app runs Avalonia {version.ToString(3)}; the Live Visual Tree needs Avalonia {StartupHooks.AvaloniaMajor}"
            : Taken("Avalonia.Controls") is null ? "The app uses Avalonia without its controls (no Avalonia.Controls among its assemblies), so it has no windows to inspect"
            : null;
        return (version, whyNot);
    }

    /// <summary>Whether the program carries one of Avalonia's platforms that draw where people see them, among the assemblies
    /// the runtime was given for it: without one, its Avalonia draws on no screen.</summary>
    public static bool CarriesScreenPlatform() => ScreenPlatforms.Any(platform => StartupHooks.TrustedAssembly(platform + ".dll") is not null);

    /// <summary>An assembly's version as the program will load it, from the assemblies the runtime was given for it, else (an
    /// app on a device, whose host finds its assemblies itself) as the runtime finds it by name; null when it has none.</summary>
    private static Version? Taken(string name) =>
        StartupHooks.TrustedAssembly(name + ".dll") is { } path ? AssemblyName.GetAssemblyName(path).Version : Found(name);

    /// <summary>An assembly's version as the default context finds it by name, loaded where the program loads it, none of
    /// its code run; null where it finds none.</summary>
    private static Version? Found(string name)
    {
        try
        {
            return Default.LoadFromAssemblyName(new AssemblyName(name)).GetName().Version;
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Of the builds of an assembly a hook carries, the one the program takes: the newest built against an Avalonia no
    /// newer than the program's, as each implements Avalonia's platform interfaces as its own version declares them, else the
    /// oldest (an app on a device is given Passenger's alone), which may still fit it; null where none is carried.</summary>
    private static string? CarriedFor(string hook, string name)
    {
        string beside = Path.GetDirectoryName(hook)!;
        string older = Path.Combine(beside, OlderFolder);
        IEnumerable<string> folders = Directory.Exists(older) ? Directory.GetDirectories(older).Prepend(beside) : [beside];
        Version? avalonia = Taken("Avalonia.Base");
        // Avalonia's packages ship at one version, so a carried assembly's own is the Avalonia it was built against.
        (string Path, Version Version)[] builds = [.. folders.Select(folder => Path.Combine(folder, name + ".dll")).Where(File.Exists)
            .Select(path => (Path: path, Version: AssemblyName.GetAssemblyName(path).Version ?? new Version())).OrderBy(build => build.Version)];
        return builds.LastOrDefault(build => avalonia is null || build.Version <= avalonia).Path ?? builds.FirstOrDefault().Path;
    }

    protected override Assembly? Load(AssemblyName name) =>
        name.Name is { } own && Carried.Contains(own) && StartupHooks.TrustedAssembly(own + ".dll") is null
            && CarriedFor(hook, own) is { } carried
            ? LoadFromAssemblyPath(carried)
            : name.Name is { } simple && (simple.StartsWith("Avalonia", StringComparison.Ordinal) || simple.StartsWith("MicroCom", StringComparison.Ordinal))
            ? Default.LoadFromAssemblyName(new AssemblyName(simple))
            : null;
}
