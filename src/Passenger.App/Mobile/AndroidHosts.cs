using System.Runtime.InteropServices;
using Passenger.Core;

namespace Passenger.App.Mobile;

/// <summary>Whether this machine builds Android apps. .NET's Android workload carries its build tools for Linux (aapt2,
/// the native libraries its tasks load) as x64 programs alone, though its manifests name every Linux processor and it
/// installs on each, so Linux on any other processor builds none: a rule, as nothing the workload or the SDK answers says
/// so. Where the workload's Linux pack is installed, its aapt2 answers for itself, by the processor its ELF header names.</summary>
public static class AndroidHosts
{
    /// <summary>The pack holding the Android workload's build tools on Linux, in .NET's packs folder.</summary>
    private const string LinuxPack = "Microsoft.Android.Sdk.Linux";

    /// <summary>Where Android apps are built, as the words for where they are not say it.</summary>
    public const string Builders = "Windows, a Mac or x64 Linux";

    /// <summary>Why Android apps are built only on <see cref="Builders"/>, <paramref name="host"/> being this machine.</summary>
    public static string Why(string host) => $".NET's Android build tools are x64 programs, which {host} does not run";

    /// <summary>This machine as a person names it ("Linux on Arm64") where it builds no Android app, asked of the packs of
    /// the .NET <paramref name="folder"/>'s projects take; null where it builds them.</summary>
    public static string? Unbuilt(string? folder) =>
        OperatingSystem.IsLinux() ? Unbuilt(RuntimeInformation.OSArchitecture, ToolsMachine(folder)) : null;

    /// <summary>Linux on <paramref name="processor"/> as a person names it where it builds no Android app, given the
    /// machine the installed pack's aapt2 is for (its ELF e_machine; null with none installed); null where it builds them.</summary>
    public static string? Unbuilt(Architecture processor, ushort? toolsMachine) =>
        (toolsMachine is { } machine ? machine != MachineOf(processor) : processor is not Architecture.X64) ? $"Linux on {Named(processor)}" : null;

    /// <summary>The processor an ELF file is for (its e_machine, in the byte order its header names), from its first bytes;
    /// null where they are no ELF header.</summary>
    public static ushort? Machine(ReadOnlySpan<byte> header) =>
        header.Length >= 20 && header[..4].SequenceEqual("\u007fELF"u8) && header[5] is 1 or 2
            ? header[5] == 1 ? (ushort)(header[18] | header[19] << 8) : (ushort)(header[18] << 8 | header[19])
            : null;

    /// <summary>The machine the newest installed Linux pack's aapt2 is for; null where there is no .NET, none is installed or
    /// it can't be read.</summary>
    private static ushort? ToolsMachine(string? folder)
    {
        try
        {
            string packs = Path.Combine(Dotnet.Root(folder), "packs", LinuxPack);
            if (!Directory.Exists(packs)
                || Directory.EnumerateDirectories(packs).MaxBy(v => Version.TryParse(Path.GetFileName(v).Split('-')[0], out Version? version) ? version : null) is not { } newest)
            {
                return null;
            }
            using FileStream aapt2 = File.OpenRead(Path.Combine(newest, "tools", "Linux", "aapt2"));
            Span<byte> header = stackalloc byte[20];
            return aapt2.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length ? Machine(header) : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The ELF e_machine of a processor's programs; null for one no pack is built for.</summary>
    private static ushort? MachineOf(Architecture processor) => processor switch
    {
        Architecture.X64 => 62,
        Architecture.Arm64 => 183,
        Architecture.Arm => 40,
        Architecture.X86 => 3,
        Architecture.RiscV64 => 243,
        Architecture.LoongArch64 => 258,
        _ => null,
    };

    private static string Named(Architecture processor) => processor switch
    {
        Architecture.Arm64 => "Arm64",
        Architecture.Arm => "32-bit Arm",
        Architecture.X86 => "32-bit x86",
        Architecture.RiscV64 => "RISC-V",
        _ => processor.ToString(),
    };
}
