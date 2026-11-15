namespace Passenger.Core;

/// <summary>The names of .NET's frameworks Passenger reads in a project's restore, its evaluation and a package's
/// metadata, each named once: the shared frameworks a project references, a target framework's short name and the
/// platform it names.</summary>
[IdentifierHome]
public static class FrameworkNames
{
    /// <summary>The Windows desktop shared framework whole, as a project using both WPF and Windows Forms references it.</summary>
    public const string WindowsDesktop = "Microsoft.WindowsDesktop.App";

    /// <summary>The Windows desktop shared framework's WPF profile (<c>UseWPF</c>).</summary>
    public const string Wpf = "Microsoft.WindowsDesktop.App.WPF";

    /// <summary>The Windows desktop shared framework's Windows Forms profile (<c>UseWindowsForms</c>).</summary>
    public const string WindowsForms = "Microsoft.WindowsDesktop.App.WindowsForms";

    // The platforms a target framework names after its dash (net11.0-android), as PlatformOf reads them.
    public const string Android = "android";
    public const string Ios = "ios";
    public const string MacCatalyst = "maccatalyst";
    public const string Tvos = "tvos";
    public const string Macos = "macos";
    public const string Windows = "windows";
    public const string Tizen = "tizen";

    /// <summary>.NET's identifier in a target framework's long name (<c>.NETCoreApp,Version=v10.0</c>).</summary>
    private const string NetCoreApp = ".NETCoreApp";

    /// <summary>The platform a target framework names, in lower case as MSBuild compares it in any: android of
    /// net11.0-android36.0, windows of net11.0-windows10.0.19041.0 and of net11.0-windows; null where it names none.</summary>
    public static string? PlatformOf(string? framework)
    {
        int dash = framework?.IndexOf('-') ?? -1;
        string platform = dash < 0 ? string.Empty : new([.. framework![(dash + 1)..].TakeWhile(char.IsLetter)]);
        return platform.Length > 0 ? platform.ToLowerInvariant() : null;
    }

    /// <summary>Whether a target framework names its platform's version after the platform (36.0 of net11.0-android36.0),
    /// as a desktop Windows app's net11.0-windows does not.</summary>
    public static bool NamesPlatformVersion(string framework) =>
        PlatformOf(framework) is { } platform && framework.Length > framework.IndexOf('-') + 1 + platform.Length;

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

    /// <summary>The .NET a target framework is for, its platform left off, in any letter case as MSBuild takes it: 8.0 of
    /// net8.0, NET8.0-windows, netcoreapp8.0 and <c>.NETCoreApp,Version=v8.0</c>, 3.1 of netcoreapp3.1; null for any other
    /// framework (net48, netstandard2.0) or none.</summary>
    public static Version? VersionOf(string? framework)
    {
        string name = (framework ?? string.Empty).Trim().Split('-')[0];
        (string? number, bool coreApp) = name.StartsWith(NetCoreApp, StringComparison.OrdinalIgnoreCase)
            ? (name[NetCoreApp.Length..].Replace(",Version=v", string.Empty, StringComparison.OrdinalIgnoreCase), true)
            : name.StartsWith(CoreAppShort, StringComparison.OrdinalIgnoreCase) ? (name[CoreAppShort.Length..], true)
            : name.StartsWith(Net, StringComparison.OrdinalIgnoreCase) ? (name[Net.Length..], false)
            : (null, false);
        // net4.8 is .NET Framework's; from net5.0 the short name is .NET's own.
        return number is { Length: > 0 } && char.IsAsciiDigit(number[0]) && number.Contains('.', StringComparison.Ordinal)
            && Version.TryParse(number, out Version? version) && (coreApp || version.Major >= 5) ? version : null;
    }

    /// <summary>The major version of the .NET a target framework is for (10 of net10.0 and NET10.0-windows); null for any
    /// other framework or none.</summary>
    public static int? Major(string? framework) => VersionOf(framework)?.Major;

    /// <summary>Whether a target framework is .NET Framework's (net48, net472, <c>.NETFramework,Version=v4.8</c>), which
    /// Passenger never runs: it runs CoreCLR's alone.</summary>
    public static bool IsNetFramework(string framework) =>
        framework.StartsWith(NetFramework, StringComparison.OrdinalIgnoreCase)
        || (framework.Length > Net.Length && framework.StartsWith(Net, StringComparison.OrdinalIgnoreCase) && framework[Net.Length..].All(char.IsAsciiDigit));

    /// <summary>.NET Framework's identifier in a target framework's long name (<c>.NETFramework,Version=v4.8</c>).</summary>
    private const string NetFramework = ".NETFramework";

    /// <summary>A short name's start: .NET's from net5.0, .NET Framework's below.</summary>
    private const string Net = "net";

    /// <summary>A short name's start for .NET before 5 (netcoreapp3.1), which later versions may also be written with.</summary>
    private const string CoreAppShort = "netcoreapp";

    /// <summary>A framework's name as MSBuild compares it, in any letter case.</summary>
    private static bool Is(string framework, string name) => framework.Equals(name, StringComparison.OrdinalIgnoreCase);
}
