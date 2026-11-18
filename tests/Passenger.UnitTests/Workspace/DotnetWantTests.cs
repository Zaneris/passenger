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
    public void An_SDK_global_json_chose_that_does_not_build_a_project_names_it_and_the_installed_SDKs_that_do_and_wants_none_installed()
    {
        GlobalJsonSdk asked = new("/work/global.json", "8.0.100", "major", 2);
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "11.0.100-rc.3.26522.148", "11.0.100-rc.2.26504.105", "10.0.403", "10.0.113", "9.0.319"), null, "9.0.319",
            TenOnly, asked);
        Assert.Equal((DotnetLack.OlderThanProject, (SdkWanted?)null, asked, (string?)"/work/global.json"), (want.Lack, want.Install, want.ChosenBy, want.GlobalJson));
        Assert.Equal(["11.0.100-rc.3.26522.148", "10.0.403"], want.Builders);
    }

    [Fact]
    public void An_SDK_global_json_chose_where_none_installed_builds_the_project_wants_the_projects_channel()
    {
        GlobalJsonSdk asked = new("/work/global.json", "8.0.100", "major", 2);
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "9.0.319", "8.0.420"), null, "9.0.319", TenOnly, asked);
        Assert.Equal((DotnetLack.OlderThanProject, new SdkWanted(Channel: "10.0"), asked), (want.Lack, want.Install, want.ChosenBy));
        Assert.Empty(want.Builders);
        Assert.Null(DotnetWant.Of(Machine(DotnetPlace.Path, "10.0.403", "9.0.319"), null, "9.0.319", TenOnly, asked with { Version = null }).ChosenBy);
    }

    [Fact]
    public void A_global_json_asks_its_version_and_roll_forward_on_the_line_its_version_is_on()
    {
        GlobalJsonSdk? read = GlobalJsonSdk.Of("/work/global.json", "{\n  // pinned\n  \"msbuild-sdks\": { \"version\": \"x\" },\n  \"sdk\": {\n    \"rollForward\": \"major\",\n    \"version\": \"8.0.100\",\n  },\n}\n");
        Assert.Equal(new GlobalJsonSdk("/work/global.json", "8.0.100", "major", 5), read);
        Assert.Equal(new GlobalJsonSdk("/work/global.json", null, null, 1), GlobalJsonSdk.Of("/work/global.json", "{\n  \"sdk\": {}\n}"));
        Assert.Equal(new GlobalJsonSdk("/work/global.json", null, null, 0), GlobalJsonSdk.Of("/work/global.json", "{}"));
        Assert.Null(GlobalJsonSdk.Of("/work/global.json", "[1]"));
        Assert.Null(GlobalJsonSdk.Of("/work/global.json", "{ \"sdk\": "));
        Assert.Null(GlobalJsonSdk.Read(null));
    }

    [Fact]
    public void A_global_json_says_whether_it_allows_prereleases_and_the_SDK_wanted_rolls_as_it_lets_it()
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
    [Fact]
    public void A_folder_with_no_SDK_to_ask_wants_the_version_its_global_json_asks_for_as_it_lets_it_go_never_the_newest()
    {
        GlobalJsonSdk pinned = new("/work/global.json", "8.0.100", "latestFeature", 2, AllowPrerelease: false);
        Assert.Equal(new SdkWanted("8.0.100", "latestFeature", AllowPrerelease: false), pinned.Rolling(SdkWanted.Newest));
        Assert.Equal(SdkWanted.Newest, (pinned with { Version = null }).Rolling(SdkWanted.Newest));
    }

    /// <summary>A project's channel says nothing of prereleases: global.json does, for whatever it chooses, an RC it would then
    /// refuse being no SDK the folder can use.</summary>
    [Fact]
    public void A_projects_channel_keeps_to_what_global_json_says_of_prereleases_and_to_nothing_else_it_asks()
    {
        GlobalJsonSdk asked = new("/work/global.json", "10.0.100", "latestPatch", 2, AllowPrerelease: false);
        Assert.Equal(new SdkWanted(Channel: "11.0", AllowPrerelease: false), asked.Rolling(new SdkWanted(Channel: "11.0")));
        Assert.Equal(new SdkWanted(Channel: "11.0", AllowPrerelease: true), (asked with { AllowPrerelease = true }).Rolling(new SdkWanted(Channel: "11.0")));
        Assert.Equal(new SdkWanted(Channel: "11.0"), (asked with { AllowPrerelease = null }).Rolling(new SdkWanted(Channel: "11.0")));
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

    /// <summary>An x64 Passenger the system translates (Windows' emulation on an Arm64 PC, Rosetta on a Mac) finds the x64
    /// installation beside the system's own first; holding a runtime alone, where the system's own holds an SDK, what lacks
    /// is an SDK of this Passenger's processor, never the SDK the system's runtime-only installation would want installed.</summary>
    [Fact]
    public void A_translated_Passenger_whose_x64_installation_holds_a_runtime_alone_beside_the_systems_own_SDK_lacks_an_SDK_of_its_processor()
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

    /// <summary>The system's own dotnet found first, holding an SDK the project host refused to load into this translated
    /// Passenger, lacks the same as an x64 one beside it with a runtime alone.</summary>
    [Fact]
    public void A_translated_Passengers_project_host_refusing_the_systems_own_SDK_lacks_an_SDK_of_its_processor()
    {
        MissingSdk refused = MissingSdk.OtherProcessor("/usr/local/share/dotnet/dotnet is no x64 program");
        DotnetWant want = DotnetWant.Of(Machine(DotnetPlace.Path, "11.0.100"), refused, null, TenOnly);
        Assert.Equal((DotnetLack.OtherProcessor, (SdkWanted?)null, true, (string?)Root), (want.Lack, want.Install, want.NoSdkAtAll, want.SystemsOwn));

        DotnetWant beside = DotnetWant.Of(Machine(DotnetPlace.Path) with { SystemsOwn = new DotnetInstallation("/arm64", ["11.0.100"], ["11.0.0"]) }, null, null, TenOnly);
        Assert.Equal((DotnetLack.OtherProcessor, (string?)"/arm64", (string?)"11.0.100"), (beside.Lack, beside.SystemsOwn, beside.Sdk));
    }

    /// <summary>An x64 Passenger the system translates with no .NET SDK at all, no .NET or a runtime alone, lacks an SDK of
    /// its processor as it does beside the system's own SDK, Install .NET SDK installing none: the one it would install is
    /// the system's own. Not translated, the same machine wants the newest SDK installed.</summary>
    [Fact]
    public void A_translated_Passenger_with_no_SDK_at_all_lacks_an_SDK_of_its_processor_and_installs_none()
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

    /// <summary>An x64 Passenger the system translates whose x64 SDK is not the one global.json asks for, or builds no project's
    /// .NET, wants that SDK as any Passenger does, but Install .NET SDK installs none: it would install the system's own
    /// processor's, which this Passenger cannot load.</summary>
    [Fact]
    public void A_translated_Passengers_want_of_an_SDK_beside_its_x64_one_is_none_Install_dotnet_SDK_installs()
    {
        MissingSdk missing = MissingSdk.From("A compatible .NET SDK was not found.\n\nRequested SDK version: 9.0.100\nglobal.json file: /work/global.json\n");
        DotnetMachine translated = Machine(DotnetPlace.Path, "10.0.403") with { Translated = true };
        DotnetWant asked = DotnetWant.Of(translated, missing, null, TenOnly);
        Assert.Equal((DotnetLack.GlobalJsonSdk, new SdkWanted("9.0.100"), (SdkWanted?)null, true), (asked.Lack, asked.Install, asked.Installs, asked.Translated));
        DotnetWant newer = DotnetWant.Of(translated, null, "10.0.403", [("App", ["net11.0"])]);
        Assert.Equal((DotnetLack.OlderThanProject, new SdkWanted(Channel: "11.0"), (SdkWanted?)null), (newer.Lack, newer.Install, newer.Installs));

        Assert.Equal(new SdkWanted("9.0.100"), DotnetWant.Of(Machine(DotnetPlace.Path, "10.0.403"), missing, null, TenOnly).Installs);
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

    [Fact]
    public void A_framework_names_the_dotnet_it_targets()
    {
        Assert.Equal(new Version(11, 0), DotnetWant.Targeted("net11.0-windows10.0.19041.0"));
        Assert.Equal(new Version(5, 0), DotnetWant.Targeted("NET5.0"));
        Assert.Null(DotnetWant.Targeted("net472"));
        Assert.Null(DotnetWant.Targeted("netcoreapp3.1"));
    }
}
