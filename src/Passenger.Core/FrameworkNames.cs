namespace Passenger.Core;

/// <summary>The names of .NET's frameworks Passenger reads in a project's restore, its evaluation and a package's
/// metadata, each named once: the shared frameworks a project references and a target framework's short name.</summary>
[IdentifierHome]
public static class FrameworkNames
{
    /// <summary>The Windows desktop shared framework whole, as a project using both WPF and Windows Forms references it.</summary>
    public const string WindowsDesktop = "Microsoft.WindowsDesktop.App";

    /// <summary>The Windows desktop shared framework's WPF profile (<c>UseWPF</c>).</summary>
    public const string Wpf = "Microsoft.WindowsDesktop.App.WPF";

    /// <summary>The Windows desktop shared framework's Windows Forms profile (<c>UseWindowsForms</c>).</summary>
    public const string WindowsForms = "Microsoft.WindowsDesktop.App.WindowsForms";

    /// <summary>.NET's identifier in a target framework's long name (<c>.NETCoreApp,Version=v10.0</c>).</summary>
    private const string NetCoreApp = ".NETCoreApp";

    /// <summary>Whether a shared framework a project references is the Windows desktop's, whole or a profile of it.</summary>
    public static bool IsWindowsDesktop(string framework) =>
        Is(framework, WindowsDesktop) || framework.StartsWith(WindowsDesktop + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a framework's shared framework references take in WPF: its profile, or the Windows desktop whole.</summary>
    public static bool UsesWpf(IEnumerable<string> references) => references.Any(r => Is(r, Wpf) || Is(r, WindowsDesktop));

    /// <summary>Whether a framework's shared framework references take in Windows Forms: its profile, or the Windows desktop
    /// whole.</summary>
    public static bool UsesWindowsForms(IEnumerable<string> references) => references.Any(r => Is(r, WindowsForms) || Is(r, WindowsDesktop));

    /// <summary>A target framework as a project names it: net10.0 for an assets file's <c>.NETCoreApp,Version=v10.0</c> or a
    /// package's <c>.NETCoreApp10.0</c>; any other as it is.</summary>
    public static string Short(string framework)
    {
        if (!framework.StartsWith(NetCoreApp, StringComparison.OrdinalIgnoreCase))
        {
            return framework;
        }
        string version = framework[NetCoreApp.Length..].Replace(",Version=v", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return $"net{version}";
    }

    /// <summary>A framework's name as MSBuild compares it, in any letter case.</summary>
    private static bool Is(string framework, string name) => framework.Equals(name, StringComparison.OrdinalIgnoreCase);
}
