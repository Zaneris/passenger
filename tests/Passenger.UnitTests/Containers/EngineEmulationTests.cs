using System.Text.Json;
using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>How an engine runs an image built for x86-64 alone is read from what it and its maker say: an x86-64 engine as
/// built; an Arm one through Rosetta where Docker Desktop's settings or Podman's machine say it uses it, else through
/// another emulator, or not at all where Podman lists no emulator for x86-64; unknown where nothing says.</summary>
public sealed class EngineEmulationTests
{
    [Fact]
    public void How_an_engine_runs_an_x86_64_image_is_read_from_what_it_and_its_maker_say() => Cases.All(
        ("an x86 64 engine runs an x86 64 image as built whatever else is said", An_x86_64_engine_runs_an_x86_64_image_as_built_whatever_else_is_said),
        ("an Arm engine runs it through Rosetta where said else emulated or not at all and unknown where nothing says", An_Arm_engine_runs_it_through_Rosetta_where_said_else_emulated_or_not_at_all_and_unknown_where_nothing_says),
        ("docker Desktop uses Rosetta only on the Apple Virtualization framework with Use Rosetta on", Docker_Desktop_uses_Rosetta_only_on_the_Apple_Virtualization_framework_with_Use_Rosetta_on),
        ("docker Desktops settings that do not say or cannot be read say nothing of Rosetta", Docker_Desktops_settings_that_do_not_say_or_cannot_be_read_say_nothing_of_Rosetta),
        ("podmans machine says whether it uses Rosetta in its first inspected machine", Podmans_machine_says_whether_it_uses_Rosetta_in_its_first_inspected_machine),
        ("podmans info says whether it emulates x86 64 by the platforms its host lists as emulated", Podmans_info_says_whether_it_emulates_x86_64_by_the_platforms_its_host_lists_as_emulated));

    private void An_x86_64_engine_runs_an_x86_64_image_as_built_whatever_else_is_said()
    {
        Assert.Equal(Amd64Runs.Natively, EngineEmulation.Of("amd64", false, false));
        Assert.Equal(Amd64Runs.Natively, EngineEmulation.Of("x86_64", null, null));
    }

    private void An_Arm_engine_runs_it_through_Rosetta_where_said_else_emulated_or_not_at_all_and_unknown_where_nothing_says()
    {
        Assert.Equal(Amd64Runs.Rosetta, EngineEmulation.Of("arm64", true, true));
        Assert.Equal(Amd64Runs.Emulated, EngineEmulation.Of("arm64", false, null));
        Assert.Equal(Amd64Runs.Emulated, EngineEmulation.Of("aarch64", false, true));
        Assert.Equal(Amd64Runs.Never, EngineEmulation.Of("arm64", false, false));
        Assert.Equal(Amd64Runs.Never, EngineEmulation.Of("arm64", null, false));
        Assert.Equal(Amd64Runs.Unknown, EngineEmulation.Of("arm64", null, true));
        Assert.Equal(Amd64Runs.Unknown, EngineEmulation.Of(null, true, true));
    }

    private void Docker_Desktop_uses_Rosetta_only_on_the_Apple_Virtualization_framework_with_Use_Rosetta_on()
    {
        Assert.True(EngineEmulation.DockerDesktopRosetta("""{"EngineTypeMac":"apple-virtualization-framework","UseVirtualizationFramework":true,"UseVirtualizationFrameworkRosetta":true}"""));
        Assert.False(EngineEmulation.DockerDesktopRosetta("""{"EngineTypeMac":"apple-virtualization-framework","UseVirtualizationFrameworkRosetta":false}"""));
        // Docker VMM takes no Rosetta, whatever the setting kept from the framework says.
        Assert.False(EngineEmulation.DockerDesktopRosetta("""{"EngineTypeMac":"docker-vmm","UseVirtualizationFramework":true,"UseVirtualizationFrameworkRosetta":true}"""));
        // An older file names the framework by UseVirtualizationFramework alone.
        Assert.True(EngineEmulation.DockerDesktopRosetta("""{"UseVirtualizationFramework":true,"UseVirtualizationFrameworkRosetta":true}"""));
        Assert.False(EngineEmulation.DockerDesktopRosetta("""{"UseVirtualizationFramework":false,"UseVirtualizationFrameworkRosetta":true}"""));
    }

    private void Docker_Desktops_settings_that_do_not_say_or_cannot_be_read_say_nothing_of_Rosetta()
    {
        Assert.Null(EngineEmulation.DockerDesktopRosetta("""{"EngineTypeMac":"apple-virtualization-framework"}"""));
        Assert.Null(EngineEmulation.DockerDesktopRosetta("""{"UseVirtualizationFrameworkRosetta":true}"""));
        Assert.Null(EngineEmulation.DockerDesktopRosetta("""{"UseVirtualizationFrameworkRosetta":"yes","EngineTypeMac":"apple-virtualization-framework"}"""));
        Assert.Null(EngineEmulation.DockerDesktopRosetta("[]"));
        Assert.Null(EngineEmulation.DockerDesktopRosetta("{\"UseVirtualizationFrameworkRosetta\": tr"));
    }

    private void Podmans_machine_says_whether_it_uses_Rosetta_in_its_first_inspected_machine()
    {
        Assert.False(EngineEmulation.MachineRosetta("""[{"Name":"podman-machine-default","Rosetta":false,"State":"running"}]"""));
        Assert.True(EngineEmulation.MachineRosetta("""[{"Name":"podman-machine-default","Rosetta":true}]"""));
        Assert.Null(EngineEmulation.MachineRosetta("""[{"Name":"podman-machine-default"}]"""));
        Assert.Null(EngineEmulation.MachineRosetta("[]"));
        Assert.Null(EngineEmulation.MachineRosetta("Error: no machine"));
    }

    private void Podmans_info_says_whether_it_emulates_x86_64_by_the_platforms_its_host_lists_as_emulated()
    {
        Assert.Equal(true, Emulates("""{"host":{"arch":"arm64","emulatedArchitectures":["linux/386","linux/amd64","linux/arm64be"]}}"""));
        Assert.Equal(false, Emulates("""{"host":{"arch":"arm64","emulatedArchitectures":["linux/riscv64"]}}"""));
        Assert.Null(Emulates("""{"host":{"arch":"arm64"}}"""));
        Assert.Null(Emulates("""{"ID":"docker's own info"}"""));

        static bool? Emulates(string info)
        {
            using JsonDocument read = JsonDocument.Parse(info);
            return EngineEmulation.Emulates(read.RootElement);
        }
    }
}
