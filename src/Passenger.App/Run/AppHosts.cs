using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>A project's apphost as a plan starts it: the file, beside the assembly it runs, and the variable naming the .NET
/// installation it takes its runtime from (<see cref="RuntimeVariables.RootFor"/>): the SDK's, or with <paramref name="X86"/>
/// the x86 one (<see cref="X86Programs"/>).</summary>
public sealed record AppHost(string Program, string RootVariable, bool X86 = false);

/// <summary>Whether a plan starts a project through the apphost the SDK made for it, as dotnet run does: the program keeps
/// its own name, icon and, on Windows, its manifest, which <c>dotnet</c> running its assembly loses. An apphost built for
/// the SDK's own runtime identifier starts so, on the installation the SDK is part of, and a win-x86 one under a 64-bit
/// Windows SDK on the x86 installation, as Windows runs it, a 64-bit .NET loading no x86 assembly. One built for another
/// system or processor (an x64 build on an Arm Mac, a Linux one on Windows) would take another installation's runtime or
/// none, and the debugger could not follow it, so its assembly runs under <c>dotnet</c>.</summary>
public static class AppHosts
{
    /// <summary>The 32-bit x86 processor's name, as PlatformTarget and a runtime identifier write it.</summary>
    public const string X86 = "x86";

    /// <summary>The properties of a project's evaluation <see cref="Of"/> and <see cref="X86Programs.Builds"/> read.</summary>
    public static readonly string[] Properties =
    [
        MSBuildNames.RunCommand, MSBuildNames.RuntimeIdentifier, MSBuildNames.DefaultAppHostRuntimeIdentifier, MSBuildNames.NETCoreSdkRuntimeIdentifier,
        MSBuildNames.NETCoreSdkPortableRuntimeIdentifier, MSBuildNames.TargetFrameworkVersion, MSBuildNames.PlatformTarget,
    ];

    /// <summary>Whether a RunCommand is the apphost the SDK makes beside the assembly <paramref name="target"/>: the
    /// assembly's name, with .exe for Windows, in its folder.</summary>
    public static bool Is(string command, string target)
    {
        if (Path.GetDirectoryName(command) is not { Length: > 0 } folder || !PathNames.Same(PathNames.Canonical(folder), Path.GetDirectoryName(target)!))
        {
            return false;
        }
        StringComparer names = PathNames.NamesIn(folder);
        string name = Path.GetFileName(command);
        string assembly = Path.GetFileNameWithoutExtension(target);
        return names.Equals(name, assembly) || names.Equals(name, assembly + ".exe");
    }

    /// <summary>The apphost that starts the assembly <paramref name="target"/> by its project's evaluation
    /// (<paramref name="properties"/>, <see cref="Properties"/> among them); null where dotnet run starts no apphost of it,
    /// or where the apphost is built for a runtime identifier the SDK's is not, with why in that case.</summary>
    public static (AppHost? Started, string? Why) Of(IReadOnlyDictionary<string, string> properties, string target)
    {
        string? command = Value(MSBuildNames.RunCommand);
        string? runtime = Value(MSBuildNames.RuntimeIdentifier) ?? Value(MSBuildNames.DefaultAppHostRuntimeIdentifier);
        if (command is null || !Is(command, target) || runtime is null || Value(MSBuildNames.NETCoreSdkRuntimeIdentifier) is not { } sdk)
        {
            return (null, null);
        }
        bool x86 = OnWindows(runtime) && ProcessorOf(runtime).Equals(X86, StringComparison.OrdinalIgnoreCase)
            && OnWindows(sdk) && !ProcessorOf(sdk).Equals(X86, StringComparison.OrdinalIgnoreCase);
        if (!x86 && !string.Equals(runtime, sdk, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(runtime, Value(MSBuildNames.NETCoreSdkPortableRuntimeIdentifier), StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"its apphost is built for {runtime}, and the .NET it builds with is {sdk}'s");
        }
        string program = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileName(command));
        return (new AppHost(program, RootVariable(runtime, Value(MSBuildNames.TargetFrameworkVersion)), x86), null);

        string? Value(string name) => properties.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>The variable naming the .NET installation an apphost for <paramref name="runtime"/> reads first, as dotnet run
    /// sets it: its processor's from .NET 6 on (<paramref name="frameworkVersion"/>, "v10.0"), else the one of every
    /// processor, which a 32-bit apphost on 64-bit Windows reads as <see cref="RuntimeVariables.RootX86"/>.</summary>
    public static string RootVariable(string runtime, string? frameworkVersion)
    {
        string processor = ProcessorOf(runtime);
        if (frameworkVersion is { Length: > 1 } written && Version.TryParse(written.TrimStart('v', 'V'), out Version? version) && version.Major >= 6)
        {
            return RuntimeVariables.RootFor(processor);
        }
        return OnWindows(runtime) && processor.Equals(X86, StringComparison.OrdinalIgnoreCase) ? RuntimeVariables.RootX86 : RuntimeVariables.Root;
    }

    /// <summary>A runtime identifier's processor, after its last dash ("x86" of win-x86).</summary>
    private static string ProcessorOf(string runtime) => runtime[(runtime.LastIndexOf('-') + 1)..];

    /// <summary>Whether a runtime identifier is Windows' (win-x86, win10-x64).</summary>
    private static bool OnWindows(string runtime) => runtime.StartsWith("win", StringComparison.OrdinalIgnoreCase);
}
