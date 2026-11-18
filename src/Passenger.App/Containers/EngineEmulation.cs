using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>How a container engine runs an image built for x86-64 alone (amd64), as an image of SQL Server's is.</summary>
public enum Amd64Runs
{
    /// <summary>Not known: the engine was not asked, or does not say.</summary>
    Unknown,

    /// <summary>As built, the engine being x86-64.</summary>
    Natively,

    /// <summary>Translated by Apple's Rosetta in the engine's virtual machine, the engine being Arm.</summary>
    Rosetta,

    /// <summary>By an emulator other than Rosetta (QEMU), the engine being Arm.</summary>
    Emulated,

    /// <summary>Not at all: an Arm engine with no emulator for x86-64.</summary>
    Never,
}

/// <summary>Reads how an engine runs x86-64 images from what it and its maker keep: its processor (<c>docker version</c>),
/// Podman's machine (<c>podman machine inspect</c>'s Rosetta) and the emulators it registered (<c>podman info</c>'s
/// emulatedArchitectures), and Docker Desktop's settings on a Mac, the one place it keeps whether it uses Rosetta: its VM
/// manager, of which the Apple Virtualization framework's alone takes Rosetta, and the Use Rosetta setting.</summary>
public static class EngineEmulation
{
    /// <summary>Docker Desktop's settings files on a Mac, the newer first.</summary>
    public static IReadOnlyList<string> DockerDesktopSettings(string home) =>
    [
        Path.Combine(home, "Library", "Group Containers", "group.com.docker", "settings-store.json"),
        Path.Combine(home, "Library", "Group Containers", "group.com.docker", "settings.json"),
    ];

    /// <summary>The Apple Virtualization framework, as Docker Desktop's settings name its VM manager.</summary>
    private const string AppleVirtualization = "apple-virtualization-framework";

    /// <summary>How an engine of this processor (docker's name, amd64 or arm64) runs x86-64 images, given whether it uses
    /// Rosetta and whether it has an emulator for x86-64 at all, each null where not known. An Arm engine without Rosetta
    /// emulates x86-64 unless it is known to have no emulator for it.</summary>
    public static Amd64Runs Of(string? processor, bool? rosetta, bool? emulates) => processor switch
    {
        "amd64" or "x86_64" => Amd64Runs.Natively,
        "arm64" or "aarch64" => (rosetta, emulates) switch
        {
            (true, _) => Amd64Runs.Rosetta,
            (_, false) => Amd64Runs.Never,
            (false, _) => Amd64Runs.Emulated,
            _ => Amd64Runs.Unknown,
        },
        _ => Amd64Runs.Unknown,
    };

    /// <summary>Whether Docker Desktop's settings have it run x86-64 through Rosetta: on the Apple Virtualization framework
    /// with Use Rosetta on (an older file names the framework by UseVirtualizationFramework alone); null where they don't say.</summary>
    public static bool? DockerDesktopRosetta(string settings)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(settings);
            JsonElement root = read.RootElement;
            if (root.ValueKind != JsonValueKind.Object || JsonMembers.Bool(root, "UseVirtualizationFrameworkRosetta") is not { } rosetta)
            {
                return null;
            }
            bool? apple = JsonMembers.Scalar(root, "EngineTypeMac") is { } engine ? engine == AppleVirtualization : JsonMembers.Bool(root, "UseVirtualizationFramework");
            return apple is { } framework ? framework && rosetta : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether Podman's machine uses Rosetta, from <c>podman machine inspect</c>'s list (the default machine's,
    /// the first); null where it names none.</summary>
    public static bool? MachineRosetta(string inspected)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(inspected);
            return read.RootElement.ValueKind == JsonValueKind.Array && read.RootElement.GetArrayLength() > 0
                ? JsonMembers.Bool(read.RootElement[0], "Rosetta")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether Podman's engine has an emulator for x86-64, by the platforms <c>podman info</c>'s host lists as
    /// emulated; null where it lists none, a Podman that does not say.</summary>
    public static bool? Emulates(JsonElement info) =>
        JsonMembers.Value(JsonMembers.Object(info, "host"), "emulatedArchitectures") is { ValueKind: JsonValueKind.Array } listed
            ? listed.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "linux/amd64")
            : null;
}
