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
/// version, since the default context would refuse one older than the 12.x the hook was compiled against, and Avalonia's
/// runtime XAML compiler from beside the hook where the program carries none. Nothing of it touches Avalonia, so a hook
/// asks it before Avalonia is loaded.</summary>
internal sealed class AppAvaloniaContext(string what, string hook) : AssemblyLoadContext($"{what} ({Path.GetFileName(hook)})")
{
    /// <summary>Avalonia's runtime XAML compiler (XAML Hot Reload's), which a program seldom carries.</summary>
    private const string XamlLoader = "Avalonia.Markup.Xaml.Loader";

    /// <summary>The Avalonia the program will load, from the assemblies the runtime was given for it (null when it has
    /// none), and whether the hooks serve it: its major version theirs.</summary>
    public static (Version? Version, bool Served) ProgramAvalonia()
    {
        Version? version = StartupHooks.TrustedAssembly("Avalonia.Base.dll") is { } path ? AssemblyName.GetAssemblyName(path).Version : null;
        return (version, version?.Major == StartupHooks.AvaloniaMajor);
    }

    protected override Assembly? Load(AssemblyName name) =>
        name.Name is XamlLoader && StartupHooks.TrustedAssembly(XamlLoader + ".dll") is null
            && Path.Combine(Path.GetDirectoryName(hook)!, XamlLoader + ".dll") is { } carried && File.Exists(carried)
            ? LoadFromAssemblyPath(carried)
            : name.Name is { } simple && (simple.StartsWith("Avalonia", StringComparison.Ordinal) || simple.StartsWith("MicroCom", StringComparison.Ordinal))
            ? Default.LoadFromAssemblyName(new AssemblyName(simple))
            : null;
}
