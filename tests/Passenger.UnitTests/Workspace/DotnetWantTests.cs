using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What .NET the banner says the machine or the folder lacks, worst first, and the SDK Install .NET SDK installs
/// for it: no dotnet, a runtime alone, global.json's SDK, an SDK older than a project's framework; and, lacking nothing, an
/// SDK found where no PATH names it.</summary>
public class DotnetWantTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\dotnet" : "/usr/local/share/dotnet";

    private static DotnetMachine Machine(DotnetPlace place, params string[] sdks) =>
        new(new DotnetFound(Path.Combine(Root, "dotnet"), place), new DotnetInstallation(Root, sdks, ["10.0.14"]));

    private static readonly (string, IReadOnlyList<string>)[] TenOnly = [("App", ["net10.0"])];

    [Fact]
    public void No_dotnet_wants_the_newest_SDK_and_refuses_what_needs_one()
    {
        DotnetWant want = DotnetWant.Of(new DotnetMachine(null, null), null, null, TenOnly);
        Assert.Equal((DotnetLack.NoDotnet, SdkWanted.Newest, true), (want.Lack, want.Install, want.NoSdkAtAll));
    }

    [Fact]
    public void Nothing_read_yet_lacks_nothing()
    {
        Assert.Equal(DotnetLack.None, DotnetWant.Of(null, null, null, TenOnly).Lack);
    }

    [Fact]
    public void A_runtime_alone_wants_the_newest_SDK_and_names_its_folder()
    {
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path), null, null, TenOnly);
        Assert.Equal((DotnetLack.RuntimeOnly, SdkWanted.Newest, Root), (want.Lack, want.Install, want.Root));
    }

    [Fact]
    public void The_SDK_global_json_asks_for_is_the_one_wanted()
    {
        MissingSdk missing = MissingSdk.From("A compatible .NET SDK was not found.\n\nRequested SDK version: 9.0.100\nglobal.json file: /work/global.json\n");
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "10.0.403"), missing, null, TenOnly);
        Assert.Equal((DotnetLack.GlobalJsonSdk, new SdkWanted("9.0.100"), true), (want.Lack, want.Install, want.NoSdkAtAll));
    }

    [Fact]
    public void A_project_targeting_a_newer_dotnet_than_the_folders_SDK_wants_that_channel_without_refusing_its_build()
    {
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "10.0.403"), null, "10.0.403", [("Lib", ["net8.0"]), ("App", ["net10.0", "net11.0-windows"])]);
        Assert.Equal((DotnetLack.OlderThanProject, new SdkWanted(Channel: "11.0"), ("App", "11.0"), false), (want.Lack, want.Install, want.Newer, want.NoSdkAtAll));
    }

    [Fact]
    public void Frameworks_the_SDK_builds_and_those_not_of_dotnet_lack_nothing()
    {
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "11.0.100-rc.3.26522.148", "10.0.403"), null, "11.0.100-rc.3.26522.148",
            [("Old", ["net48", "netstandard2.0"]), ("App", ["net11.0", "net10.0-android"])]);
        Assert.Equal((DotnetLack.None, (string?)"11.0.100-rc.3.26522.148"), (want.Lack, want.Sdk));
    }

    [Fact]
    public void An_SDK_found_where_no_PATH_names_it_lacks_nothing_but_says_so()
    {
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Installed, "10.0.403"), null, null, TenOnly);
        Assert.Equal((DotnetLack.OffPath, null, "10.0.403", false), (want.Lack, want.Install, want.Sdk, want.NoSdkAtAll));
    }

    [Fact]
    public void A_framework_names_the_dotnet_it_targets()
    {
        Assert.Equal(new Version(11, 0), DotnetWant.Targeted("net11.0-windows10.0.19041.0"));
        Assert.Equal(new Version(5, 0), DotnetWant.Targeted("NET5.0"));
        Assert.Null(DotnetWant.Targeted("net472"));
        Assert.Null(DotnetWant.Targeted("netcoreapp3.1"));
    }
}
