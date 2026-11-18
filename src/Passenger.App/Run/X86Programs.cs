using System.Runtime.Versioning;
using Microsoft.Win32;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>A program built for 32-bit x86 (PlatformTarget x86, which a win-x86 runtime identifier implies), as Windows runs
/// it: by its apphost, or the x86 dotnet where it has none, on the x86 .NET installation, which Windows keeps apart from its
/// 64-bit one, found where an x86 apphost finds it. A 64-bit .NET cannot load its assembly, and .NET runs it only on Windows.</summary>
public static class X86Programs
{
    /// <summary>The shared framework every program runs on, and two whose installers are others: the desktop runtime's
    /// carries the first, ASP.NET Core's does not.</summary>
    private const string NetCore = "Microsoft.NETCore.App";
    private const string AspNetCore = "Microsoft.AspNetCore.App";

    /// <summary>The key, in the registry's 32-bit view, where .NET's x86 installer writes its InstallLocation.</summary>
    private const string RegisteredKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x86";

    /// <summary>Whether a project's evaluation builds a program that runs as a 32-bit x86 process: its PlatformTarget x86.</summary>
    public static bool Builds(IReadOnlyDictionary<string, string> properties) =>
        string.Equals(properties.GetValueOrDefault(MSBuildNames.PlatformTarget), AppHosts.X86, StringComparison.OrdinalIgnoreCase);

    /// <summary>The x86 .NET installation this machine's x86 apphost takes its runtime from, by a program's own variables
    /// (<paramref name="given"/>, its configuration's) over the IDE's (<see cref="Root(IReadOnlyDictionary{string,string},Func{string,string?},Func{string?},string)"/>).</summary>
    [SupportedOSPlatform("windows")]
    public static string Root(IReadOnlyDictionary<string, string> given) =>
        Root(given, Environment.GetEnvironmentVariable, Registered, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    /// <summary>The x86 .NET installation an x86 apphost takes its runtime from: the folder the variable it reads first
    /// names, DOTNET_ROOT_X86, then DOTNET_ROOT(x86), each the program's own (<paramref name="given"/>) over the IDE's
    /// (<paramref name="inherited"/>), then the program's own DOTNET_ROOT (the IDE's names its 64-bit .NET); else the one
    /// <paramref name="registered"/> for x86, else dotnet in <paramref name="programFilesX86"/>.</summary>
    public static string Root(IReadOnlyDictionary<string, string> given, Func<string, string?> inherited, Func<string?> registered, string programFilesX86)
    {
        foreach (string variable in (string[])[RuntimeVariables.RootFor(AppHosts.X86), RuntimeVariables.RootX86])
        {
            if ((Given(variable) ?? inherited(variable)) is { Length: > 0 } named)
            {
                return named;
            }
        }
        return Given(RuntimeVariables.Root) ?? (registered() is { Length: > 0 } location ? location : Path.Combine(programFilesX86, "dotnet"));

        string? Given(string variable) => given.FirstOrDefault(v => v.Key.Equals(variable, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value ? value : null;
    }

    /// <summary>The InstallLocation .NET's x86 installer registered, which an x86 apphost reads; null for none.</summary>
    [SupportedOSPlatform("windows")]
    private static string? Registered()
    {
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using RegistryKey? x86 = machine.OpenSubKey(RegisteredKey);
            return x86?.GetValue("InstallLocation") as string;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>What the x86 .NET in <paramref name="root"/> lacks to run the built <paramref name="assembly"/>: the shared
    /// frameworks its runtime configuration names that the installation has no version of to serve, each by the installer
    /// that brings it (<see cref="Installers"/>); none where it has them all, the build carries its runtime, or its
    /// configuration does not read. Its roll-forward is the program's own variable's (<paramref name="environment"/>), else
    /// the IDE's, else its configuration's.</summary>
    public static IReadOnlyList<(string Installer, string Version)> Lacking(string root, string assembly, IReadOnlyDictionary<string, string> environment)
    {
        if (RuntimeConfig.Read(assembly) is not { SelfContained: false } config)
        {
            return [];
        }
        string? rollForward = environment.FirstOrDefault(v => v.Key.Equals(RuntimeVariables.RollForward, StringComparison.OrdinalIgnoreCase)).Value
            ?? Environment.GetEnvironmentVariable(RuntimeVariables.RollForward) ?? config.RollForward;
        return Installers(Lacking(config, name => DotnetInstallation.Framework(root, name), rollForward));
    }

    /// <summary>The frameworks a runtime configuration names that no version <paramref name="installed"/> serves, as the
    /// host rolls forward by <paramref name="rollForward"/> (<see cref="DotnetInstallation.Serves"/>).</summary>
    public static IReadOnlyList<(string Name, string Version)> Lacking(RuntimeConfig config, Func<string, IReadOnlyList<string>> installed, string? rollForward)
    {
        bool anyMajor = rollForward is { } roll && (roll.Equals("Major", StringComparison.OrdinalIgnoreCase) || roll.Equals("LatestMajor", StringComparison.OrdinalIgnoreCase));
        return [.. config.Frameworks
            .Where(f => f.Version is { Length: > 0 })
            .Select(f => (f.Name, Version: f.Version!))
            .Where(f => !installed(f.Name).Any(version => DotnetInstallation.Serves(version, f.Version, anyMajor)))];
    }

    /// <summary>What a person installs for frameworks lacking, as .NET's downloads name each, with its major and minor
    /// version (".NET Desktop Runtime 8.0"): the desktop runtime alone where it lacks too, as it carries .NET's own.</summary>
    public static IReadOnlyList<(string Installer, string Version)> Installers(IReadOnlyList<(string Name, string Version)> lacking)
    {
        bool desktop = lacking.Any(f => FrameworkNames.IsWindowsDesktop(f.Name));
        return [.. lacking
            .Where(f => !(desktop && f.Name.Equals(NetCore, StringComparison.OrdinalIgnoreCase)))
            .Select(f => (Installer: f.Name switch
            {
                _ when FrameworkNames.IsWindowsDesktop(f.Name) => ".NET Desktop Runtime",
                _ when f.Name.Equals(AspNetCore, StringComparison.OrdinalIgnoreCase) => "ASP.NET Core Runtime",
                _ when f.Name.Equals(NetCore, StringComparison.OrdinalIgnoreCase) => ".NET Runtime",
                _ => f.Name,
            }, Version: string.Join('.', f.Version.Split('.', '-').Take(2))))];
    }
}
