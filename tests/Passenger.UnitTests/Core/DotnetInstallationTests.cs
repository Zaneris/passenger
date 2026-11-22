using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What an installation's folder is read as holding: an SDK only with the runtime it runs on, as the host would
/// find one; and the installation chosen found before the dotnet running this program.</summary>
public sealed class DotnetInstallationTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void An_SDK_is_taken_only_where_the_runtime_it_runs_on_is_there() => Cases.AllApart<DotnetInstallationTests>(
        ("an SDK short of the runtime it runs on is no SDK", test => test.An_SDK_short_of_the_runtime_it_runs_on_is_no_SDK()),
        ("an SDK takes its runtime or a later one of its major never an earlier or the next major", test => test.An_SDK_takes_its_runtime_or_a_later_one_of_its_major_never_an_earlier_or_the_next_major()),
        ("an SDK that says nothing of its runtime is taken at its folders word", _ => Cases.EachApart<DotnetInstallationTests, string?>(
        [
            null,
            "not json",
        ], (test, row) => test.An_SDK_that_says_nothing_of_its_runtime_is_taken_at_its_folders_word(row))));

    private void An_SDK_short_of_the_runtime_it_runs_on_is_no_SDK()
    {
        Sdk("10.0.403", Runs("10.0.14"));
        Runtime("9.0.1");

        DotnetInstallation read = DotnetInstallation.Read(root);

        Assert.Empty(read.Sdks);
        Assert.Null(read.NewestSdk);
        Assert.Equal(["9.0.1"], read.Runtimes);
    }

    private void An_SDK_takes_its_runtime_or_a_later_one_of_its_major_never_an_earlier_or_the_next_major()
    {
        Sdk("10.0.403", Runs("10.0.14"));
        Runtime("10.0.15");
        Assert.Equal(["10.0.403"], DotnetInstallation.Read(root).Sdks);

        Sdk("11.0.100", Runs("11.0.2"));
        Runtime("11.0.1");
        Sdk("9.0.319", Runs("9.0.21"));
        Assert.Equal(["10.0.403"], DotnetInstallation.Read(root).Sdks);
    }

    private void An_SDK_that_says_nothing_of_its_runtime_is_taken_at_its_folders_word(string? config)
    {
        Sdk("10.0.403", config);
        Assert.Equal(["10.0.403"], DotnetInstallation.Read(root).Sdks);
    }

    [Fact]
    public void A_folders_dotnet_is_the_first_installation_holding_the_SDK_its_trusted_global_json_takes() => Cases.AllApart<DotnetInstallationTests>(
        ("the installation chosen comes before the dotnet running this program while its dotnet is there", test => test.The_installation_chosen_comes_before_the_dotnet_running_this_program_while_its_dotnet_is_there()),
        ("a folder whose global json asks an SDK the first dotnet lacks takes the next that holds it the homes own too", test => test.A_folder_whose_global_json_asks_an_SDK_the_first_dotnet_lacks_takes_the_next_that_holds_it_the_homes_own_too()),
        ("a global jsons sdk paths are searched in their order a relative one from its folder host being each dotnet found", test => test.A_global_jsons_sdk_paths_are_searched_in_their_order_a_relative_one_from_its_folder_host_being_each_dotnet_found()),
        ("a global json in a folder not trusted names no dotnet of its own the dotnets found searched alone", test => test.A_global_json_in_a_folder_not_trusted_names_no_dotnet_of_its_own_the_dotnets_found_searched_alone()),
        ("the homes own dotnet is looked for after DOTNET ROOT the PATH and the installers places", test => test.The_homes_own_dotnet_is_looked_for_after_DOTNET_ROOT_the_PATH_and_the_installers_places()));

    private void The_installation_chosen_comes_before_the_dotnet_running_this_program_while_its_dotnet_is_there()
    {
        string host = Path.Join(root, Dotnet.HostName);
        File.WriteAllText(host, "the installed host\n");
        Dotnet.Chosen = root;
        try
        {
            Assert.Equal(new DotnetFound(host, DotnetPlace.Installed), Dotnet.Find());
            Assert.Equal(host, Dotnet.Host());

            File.Delete(host);
            Assert.NotEqual(host, Dotnet.Find()?.Host);
        }
        finally
        {
            Dotnet.Chosen = null;
        }
    }

    private void A_folder_whose_global_json_asks_an_SDK_the_first_dotnet_lacks_takes_the_next_that_holds_it_the_homes_own_too()
    {
        DotnetFound system = Installation("system", "10.0.403", "11.0.100-rc.3.26522.148");
        DotnetFound path = Installation("path", "9.0.319");
        DotnetFound home = Installation(Path.Join("home", ".dotnet"), "11.0.100", "9.0.303");
        string folder = Directory.CreateDirectory(Path.Join(root, "work", "src")).FullName;
        File.WriteAllText(Path.Join(root, "work", "global.json"), """{ "sdk": { "version": "11.0.100", "rollForward": "latestFeature", "allowPrerelease": false } }""");
        List<string> read = [];
        DotnetInstallation Read(string installation)
        {
            read.Add(Path.GetFileName(installation));
            return DotnetInstallation.Read(installation);
        }

        Assert.Equal((home, true), Dotnet.Choose([system, path, path, home], folder, Read));
        Assert.True(read.SequenceEqual(["system", "path", ".dotnet"]), $"The installations were read as {string.Join(", ", read)}, not each once in order up to the one taken");

        File.WriteAllText(Path.Join(root, "work", "global.json"), """{ "sdk": { "version": "12.0.100" } }""");
        Assert.Equal((system, false), Dotnet.Choose([system, path, home], folder, DotnetInstallation.Read));

        File.Delete(Path.Join(root, "work", "global.json"));
        Assert.Equal((system, true), Dotnet.Choose([system, path, home], folder, DotnetInstallation.Read));
        Assert.Equal((path, true), Dotnet.Choose([Installation("runtime"), path, home], folder, DotnetInstallation.Read));
    }

    private void A_global_jsons_sdk_paths_are_searched_in_their_order_a_relative_one_from_its_folder_host_being_each_dotnet_found()
    {
        DotnetFound system = Installation("system", "10.0.403");
        DotnetFound home = Installation(Path.Join("home", ".dotnet"), "11.0.100");
        DotnetFound own = Installation(Path.Join("work", ".dotnet"), "11.0.100") with { Place = DotnetPlace.GlobalJson };
        DotnetFound elsewhere = Installation("elsewhere", "11.0.100") with { Place = DotnetPlace.GlobalJson };
        string folder = Directory.CreateDirectory(Path.Join(root, "work", "src")).FullName;
        (DotnetFound?, bool) Asking(string paths)
        {
            File.WriteAllText(Path.Join(root, "work", "global.json"), $$"""{ "sdk": { "version": "11.0.100", "rollForward": "disable", "paths": {{paths}} } }""");
            return Dotnet.Choose([system, home], folder, DotnetInstallation.Read, trusts: _ => true);
        }

        Assert.Equal((own, true), Asking("""[ ".dotnet", "$host$" ]"""));
        Assert.Equal((home, true), Asking("""[ "$host$", ".dotnet" ]"""));
        Assert.Equal((own, true), Asking("""[ 7, "missing", ".dotnet" ]"""));
        Assert.Equal((elsewhere, true), Asking($"[ \"{Path.Join(root, "elsewhere").Replace("\\", "\\\\", StringComparison.Ordinal)}\" ]"));
        Assert.Equal((home, true), Asking("null"));

        // Without $host$ the installations found are never looked in, the one holding the SDK neither: dotnet finds none.
        Assert.Equal((system, false), Asking("""[ "missing" ]"""));
        Assert.Equal((system, false), Asking("[]"));
    }

    private void A_global_json_in_a_folder_not_trusted_names_no_dotnet_of_its_own_the_dotnets_found_searched_alone()
    {
        DotnetFound system = Installation("system", "10.0.403");
        DotnetFound home = Installation(Path.Join("home", ".dotnet"), "11.0.100");
        Installation(Path.Join("work", ".dotnet"), "11.0.100");
        string folder = Directory.CreateDirectory(Path.Join(root, "work", "src")).FullName;
        File.WriteAllText(Path.Join(root, "work", "global.json"), """{ "sdk": { "version": "11.0.100", "rollForward": "disable", "paths": [ ".dotnet" ] } }""");
        List<string> asked = [];

        (DotnetFound?, bool) chosen = Dotnet.Choose([system, home], folder, DotnetInstallation.Read, trusts: place =>
        {
            asked.Add(place);
            return false;
        });

        Assert.Equal((home, true), chosen);
        Assert.Equal([Path.Join(root, "work")], asked);
    }

    private void The_homes_own_dotnet_is_looked_for_after_DOTNET_ROOT_the_PATH_and_the_installers_places()
    {
        DotnetFound home = Installation(Path.Join("home", ".dotnet"), "11.0.100");
        DotnetFound last = Dotnet.Candidates(Path.Join(root, "home")).Last();
        Assert.Equal((PathNames.Canonical(home.Host), DotnetPlace.Installed), (PathNames.Canonical(last.Host), last.Place));
    }

    /// <summary>An installation under the test's folder holding these SDKs, each with its runtime, as one dotnet finds.</summary>
    private DotnetFound Installation(string name, params string[] sdks)
    {
        string installation = Directory.CreateDirectory(Path.Join(root, name)).FullName;
        string host = Path.Join(installation, Dotnet.HostName);
        File.WriteAllText(host, "the installed host\n");
        foreach (string sdk in sdks)
        {
            string runtime = $"{sdk.Split('.')[0]}.0.0{(sdk.Contains('-', StringComparison.Ordinal) ? sdk[sdk.IndexOf('-', StringComparison.Ordinal)..] : string.Empty)}";
            string folder = Directory.CreateDirectory(Path.Join(installation, "sdk", sdk)).FullName;
            File.WriteAllText(Path.Join(folder, "dotnet.dll"), "cli\n");
            File.WriteAllText(Path.Join(folder, "dotnet.runtimeconfig.json"), Runs(runtime));
            Directory.CreateDirectory(Path.Join(installation, "shared", "Microsoft.NETCore.App", runtime));
        }
        return new DotnetFound(host, DotnetPlace.Installed);
    }

    private static string Runs(string runtime) => $$"""{ "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "{{runtime}}" } } }""";

    private void Sdk(string version, string? config)
    {
        string folder = Directory.CreateDirectory(Path.Join(root, "sdk", version)).FullName;
        File.WriteAllText(Path.Join(folder, "dotnet.dll"), "cli\n");
        if (config is not null)
        {
            File.WriteAllText(Path.Join(folder, "dotnet.runtimeconfig.json"), config);
        }
    }

    private void Runtime(string version) => Directory.CreateDirectory(Path.Join(root, "shared", "Microsoft.NETCore.App", version));
}
