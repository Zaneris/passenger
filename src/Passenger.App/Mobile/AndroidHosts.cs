using System.Runtime.InteropServices;
using Passenger.Core;

namespace Passenger.App.Mobile;

/// <summary>Whether this machine builds Android apps. .NET's Android workload carries its build tools for Linux (aapt2,
/// the native libraries its tasks load) as x64 programs alone, though its manifests name every Linux processor and it
/// installs on each, so Linux on any other processor builds none: a rule, as nothing the workload or the SDK answers says
/// so. Where the workload's Linux pack is installed, its aapt2 answers for itself, by the processor its header names.</summary>
public static class AndroidHosts
{
    /// <summary>The pack holding the Android workload's build tools on Linux, in .NET's packs folder.</summary>
    private const string LinuxPack = "Microsoft.Android.Sdk.Linux";

    /// <summary>Where Android apps are built, as the words for where they are not say it.</summary>
    public const string Builders = "Windows, a Mac or x64 Linux";

    /// <summary>Why Android apps are built only on <see cref="Builders"/>, <paramref name="host"/> being this machine.</summary>
    public static string Why(string host) => $".NET's Android build tools are x64 programs, which {host} does not run";

    /// <summary>Where and why what builds for Android builds, said after it ("AndroidApp builds …"), <paramref name="host"/>
    /// being this machine.</summary>
    public static string Elsewhere(string host) => $"for Android only on {Builders}: {Why(host)}";

    /// <summary>This machine as a person names it ("Linux on Arm64") where it builds no Android app, asked of the packs of
    /// the .NET <paramref name="folder"/>'s projects take; null where it builds them.</summary>
    public static string? Unbuilt(string? folder) =>
        OperatingSystem.IsLinux() ? Unbuilt(RuntimeInformation.OSArchitecture, ToolsProcessors(folder)) : null;

    /// <summary>Linux on <paramref name="processor"/> as a person names it where it builds no Android app, given the
    /// processors the installed pack's aapt2 is built for (<see cref="ProgramProcessors"/>; none with none installed or
    /// read); null where it builds them.</summary>
    public static string? Unbuilt(Architecture processor, IReadOnlyList<Architecture> tools) =>
        (tools.Count > 0 ? !tools.Contains(processor) : processor is not Architecture.X64) ? $"Linux on {Named(processor)}" : null;

    /// <summary>The processors the newest installed Linux pack's aapt2 is built for; none where there is no .NET, none is
    /// installed or it can't be read.</summary>
    private static IReadOnlyList<Architecture> ToolsProcessors(string? folder)
    {
        try
        {
            string packs = Path.Combine(Dotnet.Root(folder), "packs", LinuxPack);
            return Directory.Exists(packs) && VersionOrder.Newest(Directory.EnumerateDirectories(packs)) is { } newest
                ? ProgramProcessors.Of(Path.Combine(newest, "tools", "Linux", "aapt2"))
                : [];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Named(Architecture processor) => processor switch
    {
        Architecture.Arm64 => "Arm64",
        Architecture.Arm => "32-bit Arm",
        Architecture.X86 => "32-bit x86",
        Architecture.RiscV64 => "RISC-V",
        _ => processor.ToString(),
    };
}
