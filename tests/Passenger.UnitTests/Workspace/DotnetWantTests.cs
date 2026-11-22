using System.Buffers.Binary;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What .NET the banner says the machine or the folder lacks, worst first, and the SDK Install .NET SDK installs
/// for it: no dotnet, a runtime alone, for a translated Passenger an SDK of its processor, global.json's SDK, an SDK older
/// than a project's framework; and, lacking nothing, an SDK found where no PATH names it.</summary>
public class DotnetWantTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\dotnet" : "/usr/local/share/dotnet";

    private static DotnetMachine Machine(DotnetPlace place, params string[] sdks) =>
        new(new DotnetFound(Path.Combine(Root, "dotnet"), place), new DotnetInstallation(Root, sdks, ["10.0.14"]));

    private static readonly (string, IReadOnlyList<string>)[] TenOnly = [("App", ["net10.0"])];

    [Fact]
    public void A_folder_wants_the_SDK_its_projects_need_and_lacks_nothing_it_has() => Cases.All(
        ("no dotnet wants the newest SDK and refuses what needs one", No_dotnet_wants_the_newest_SDK_and_refuses_what_needs_one),
        ("nothing read yet lacks nothing", Nothing_read_yet_lacks_nothing),
        ("a runtime alone wants the newest SDK and names its folder", A_runtime_alone_wants_the_newest_SDK_and_names_its_folder),
        ("a project targeting a newer dotnet than the folders SDK wants that channel without refusing its build", A_project_targeting_a_newer_dotnet_than_the_folders_SDK_wants_that_channel_without_refusing_its_build),
        ("frameworks the SDK builds and those not of dotnet lack nothing", Frameworks_the_SDK_builds_and_those_not_of_dotnet_lack_nothing));

    private void No_dotnet_wants_the_newest_SDK_and_refuses_what_needs_one()
    {
        DotnetWant want = DotnetWant.Of(new DotnetMachine(null, null), null, null, TenOnly);
        Assert.Equal((DotnetLack.NoDotnet, SdkWanted.Newest, true), (want.Lack, want.Install, want.NoSdkAtAll));
    }

    private void Nothing_read_yet_lacks_nothing()
    {
        Assert.Equal(DotnetLack.None, DotnetWant.Of(null, null, null, TenOnly).Lack);
    }

    private void A_runtime_alone_wants_the_newest_SDK_and_names_its_folder()
    {
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path), null, null, TenOnly);
        Assert.Equal((DotnetLack.RuntimeOnly, SdkWanted.Newest, Root), (want.Lack, want.Install, want.Root));
    }

    [Fact]
    public void A_global_json_decides_the_SDK_a_folder_wants() => Cases.All(
        ("the SDK global json asks for is the one wanted", The_SDK_global_json_asks_for_is_the_one_wanted),
        ("an SDK global json chose that does not build a project names it and the installed SDKs that do and wants none installed", An_SDK_global_json_chose_that_does_not_build_a_project_names_it_and_the_installed_SDKs_that_do_and_wants_none_installed),
        ("a global json says whether it allows prereleases and the SDK wanted rolls as it lets it", A_global_json_says_whether_it_allows_prereleases_and_the_SDK_wanted_rolls_as_it_lets_it),
        ("a folder with no SDK to ask wants the version its global json asks for as it lets it go never the newest", A_folder_with_no_SDK_to_ask_wants_the_version_its_global_json_asks_for_as_it_lets_it_go_never_the_newest));

    private void The_SDK_global_json_asks_for_is_the_one_wanted()
    {
        MissingSdk missing = MissingSdk.From("A compatible .NET SDK was not found.\n\nRequested SDK version: 9.0.100\nglobal.json file: /work/global.json\n");
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "10.0.403"), missing, null, TenOnly);
        Assert.Equal((DotnetLack.GlobalJsonSdk, new SdkWanted("9.0.100"), true), (want.Lack, want.Install, want.NoSdkAtAll));
    }

    private void A_project_targeting_a_newer_dotnet_than_the_folders_SDK_wants_that_channel_without_refusing_its_build()
    {
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "10.0.403"), null, "10.0.403", [("Lib", ["net8.0"]), ("App", ["net10.0", "net11.0-windows"])]);
        Assert.Equal((DotnetLack.OlderThanProject, new SdkWanted(Channel: "11.0"), ("App", "11.0"), false), (want.Lack, want.Install, want.Newer, want.NoSdkAtAll));
    }

    private void An_SDK_global_json_chose_that_does_not_build_a_project_names_it_and_the_installed_SDKs_that_do_and_wants_none_installed()
    {
        GlobalJsonSdk asked = new("/work/global.json", "8.0.100", "major", 2);
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "11.0.100-rc.3.26522.148", "11.0.100-rc.2.26504.105", "10.0.403", "10.0.113", "9.0.319"), null, "9.0.319",
            TenOnly, asked);
        Assert.Equal((DotnetLack.OlderThanProject, (SdkWanted?)null, asked, (string?)"/work/global.json"), (want.Lack, want.Install, want.ChosenBy, want.GlobalJson));
        Assert.Equal(["11.0.100-rc.3.26522.148", "10.0.403"], want.Builders);
    }

    private void A_global_json_says_whether_it_allows_prereleases_and_the_SDK_wanted_rolls_as_it_lets_it()
    {
        GlobalJsonSdk? read = GlobalJsonSdk.Of("/work/global.json", """{ "sdk": { "version": "10.0.500", "rollForward": "latestMajor", "allowPrerelease": true } }""");
        Assert.True(read?.AllowPrerelease, "global.json's allowPrerelease was not read");
        Assert.Equal(new SdkWanted("10.0.500", "latestMajor", AllowPrerelease: true), read!.Rolling(new SdkWanted("10.0.500")));
        Assert.False(GlobalJsonSdk.Of("/work/global.json", """{ "sdk": { "allowPrerelease": false } }""")?.AllowPrerelease);
        Assert.Null(GlobalJsonSdk.Of("/work/global.json", """{ "sdk": { "version": "10.0.500" } }""")?.AllowPrerelease);
        Assert.Null(GlobalJsonSdk.Of("/work/global.json", """{ "sdk": { "allowPrerelease": "yes" } }""")?.AllowPrerelease);
    }

    /// <summary>Asked before any dotnet could read the folder's global.json, the SDK wanted is the one it would take, so one
    /// install gives the folder an SDK it uses.</summary>
    private void A_folder_with_no_SDK_to_ask_wants_the_version_its_global_json_asks_for_as_it_lets_it_go_never_the_newest()
    {
        GlobalJsonSdk pinned = new("/work/global.json", "8.0.100", "latestFeature", 2, AllowPrerelease: false);
        Assert.Equal(new SdkWanted("8.0.100", "latestFeature", AllowPrerelease: false), pinned.Rolling(SdkWanted.Newest));
        Assert.Equal(SdkWanted.Newest, (pinned with { Version = null }).Rolling(SdkWanted.Newest));
    }

    private void Frameworks_the_SDK_builds_and_those_not_of_dotnet_lack_nothing()
    {
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "11.0.100-rc.3.26522.148", "10.0.403"), null, "11.0.100-rc.3.26522.148",
            [("Old", ["net48", "netstandard2.0"]), ("App", ["net11.0", "net10.0-android"])]);
        Assert.Equal((DotnetLack.None, (string?)"11.0.100-rc.3.26522.148"), (want.Lack, want.Sdk));
    }

    [Fact]
    public void A_translated_Passenger_wants_an_SDK_of_its_own_processor() => Cases.All(
        ("a translated Passenger whose x64 installation holds a runtime alone beside the systems own SDK lacks an SDK of its processor", A_translated_Passenger_whose_x64_installation_holds_a_runtime_alone_beside_the_systems_own_SDK_lacks_an_SDK_of_its_processor),
        ("a translated Passenger with no SDK at all lacks an SDK of its processor and installs none", A_translated_Passenger_with_no_SDK_at_all_lacks_an_SDK_of_its_processor_and_installs_none));

    /// <summary>An x64 Passenger the system translates (Windows' emulation on an Arm64 PC, Rosetta on a Mac) finds the x64
    /// installation beside the system's own first; holding a runtime alone, where the system's own holds an SDK, what lacks
    /// is an SDK of this Passenger's processor, never the SDK the system's runtime-only installation would want installed.</summary>
    private void A_translated_Passenger_whose_x64_installation_holds_a_runtime_alone_beside_the_systems_own_SDK_lacks_an_SDK_of_its_processor()
    {
        string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            DotnetFound x64 = Installation(Path.Join(root, "x64"), 0x01000007);
            DotnetFound systems = Installation(Path.Join(root, "arm64"), 0x0100000C, "11.0.100");
            DotnetMachine Translated(bool translated) => DotnetMachine.Of(x64, translated, () => [x64, systems], DotnetInstallation.Read);

            DotnetWant want = DotnetWant.Of(Translated(true), null, null, TenOnly);
            Assert.Equal((DotnetLack.OtherProcessor, (SdkWanted?)null, true, (string?)systems.Root, (string?)"11.0.100"),
                (want.Lack, want.Install, want.NoSdkAtAll, want.SystemsOwn, want.Sdk));
            DotnetWant untranslated = DotnetWant.Of(Translated(false), null, null, TenOnly);
            Assert.Equal((DotnetLack.RuntimeOnly, (string?)x64.Root), (untranslated.Lack, untranslated.Root));

            // The x64 SDK installed beside it is what a translated Passenger loads: nothing lacks.
            Sdk(x64.Root, "11.0.100");
            Assert.Equal(DotnetLack.None, DotnetWant.Of(Translated(true), null, null, TenOnly).Lack);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An x64 Passenger the system translates with no .NET SDK at all, no .NET or a runtime alone, lacks an SDK of
    /// its processor as it does beside the system's own SDK, Install .NET SDK installing none: the one it would install is
    /// the system's own. Not translated, the same machine wants the newest SDK installed.</summary>
    private void A_translated_Passenger_with_no_SDK_at_all_lacks_an_SDK_of_its_processor_and_installs_none()
    {
        DotnetWant none = DotnetWant.Of(new DotnetMachine(null, null) { Translated = true }, null, null, TenOnly);
        Assert.Equal((DotnetLack.OtherProcessor, (SdkWanted?)null, (SdkWanted?)null, true, (string?)null, (string?)null),
            (none.Lack, none.Install, none.Installs, none.NoSdkAtAll, none.Root, none.SystemsOwn));
        DotnetWant runtime = DotnetWant.Of(Machine(DotnetPlace.Path) with { Translated = true }, null, null, TenOnly);
        Assert.Equal((DotnetLack.OtherProcessor, (SdkWanted?)null, true, (string?)Root, (string?)null),
            (runtime.Lack, runtime.Installs, runtime.NoSdkAtAll, runtime.Root, runtime.SystemsOwn));

        DotnetWant untranslated = DotnetWant.Of(new DotnetMachine(null, null), null, null, TenOnly);
        Assert.Equal((DotnetLack.NoDotnet, SdkWanted.Newest), (untranslated.Lack, untranslated.Installs));
        DotnetWant alone = DotnetWant.Of(Machine(DotnetPlace.Path), null, null, TenOnly);
        Assert.Equal((DotnetLack.RuntimeOnly, SdkWanted.Newest), (alone.Lack, alone.Installs));
    }

    /// <summary>An installation in <paramref name="folder"/> whose dotnet is a Mach-O program of <paramref name="cpu"/>,
    /// holding the runtime 11.0.0 and these SDKs.</summary>
    private static DotnetFound Installation(string folder, int cpu, params string[] sdks)
    {
        byte[] program = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(program, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(program.AsSpan(4), cpu);
        Directory.CreateDirectory(Path.Join(folder, "shared", "Microsoft.NETCore.App", "11.0.0"));
        File.WriteAllBytes(Path.Join(folder, Dotnet.HostName), program);
        foreach (string sdk in sdks)
        {
            Sdk(folder, sdk);
        }
        return new DotnetFound(Path.Join(folder, Dotnet.HostName), DotnetPlace.Path);
    }

    private static void Sdk(string folder, string version)
    {
        string sdk = Directory.CreateDirectory(Path.Join(folder, "sdk", version)).FullName;
        File.WriteAllText(Path.Join(sdk, "dotnet.dll"), "cli\n");
        File.WriteAllText(Path.Join(sdk, "dotnet.runtimeconfig.json"), """{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0" } } }""");
    }
}
