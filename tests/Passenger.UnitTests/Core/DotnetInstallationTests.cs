using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What an installation's folder is read as holding: an SDK only with the runtime it runs on, as the host would
/// find one; and the installation chosen found before the dotnet running this program.</summary>
public sealed class DotnetInstallationTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void An_SDK_short_of_the_runtime_it_runs_on_is_no_SDK()
    {
        Sdk("10.0.403", Runs("10.0.14"));
        Runtime("9.0.1");

        DotnetInstallation read = DotnetInstallation.Read(root);

        Assert.Empty(read.Sdks);
        Assert.Null(read.NewestSdk);
        Assert.Equal(["9.0.1"], read.Runtimes);
    }

    [Fact]
    public void An_SDK_takes_its_runtime_or_a_later_one_of_its_major_never_an_earlier_or_the_next_major()
    {
        Sdk("10.0.403", Runs("10.0.14"));
        Runtime("10.0.15");
        Assert.Equal(["10.0.403"], DotnetInstallation.Read(root).Sdks);

        Sdk("11.0.100", Runs("11.0.2"));
        Runtime("11.0.1");
        Sdk("9.0.319", Runs("9.0.21"));
        Assert.Equal(["10.0.403"], DotnetInstallation.Read(root).Sdks);
    }

    [Fact]
    public void A_prerelease_SDK_takes_the_prerelease_it_names()
    {
        Sdk("11.0.100-rc.3.26522.148", Runs("11.0.0-rc.3.26522.148"));
        Assert.Empty(DotnetInstallation.Read(root).Sdks);

        Runtime("11.0.0-rc.3.26522.148");
        Assert.Equal(["11.0.100-rc.3.26522.148"], DotnetInstallation.Read(root).Sdks);
    }

    [Fact]
    public void An_SDK_naming_several_frameworks_needs_the_runtime_among_them()
    {
        Sdk("10.0.403", """{ "runtimeOptions": { "frameworks": [ { "name": "Microsoft.AspNetCore.App", "version": "10.0.14" }, { "name": "Microsoft.NETCore.App", "version": "10.0.14" } ] } }""");
        Assert.Empty(DotnetInstallation.Read(root).Sdks);

        Runtime("10.0.14");
        Assert.Equal(["10.0.403"], DotnetInstallation.Read(root).Sdks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{ "runtimeOptions": { } }""")]
    public void An_SDK_that_says_nothing_of_its_runtime_is_taken_at_its_folders_word(string? config)
    {
        Sdk("10.0.403", config);
        Assert.Equal(["10.0.403"], DotnetInstallation.Read(root).Sdks);
    }

    [Fact]
    public void The_installation_chosen_comes_before_the_dotnet_running_this_program_while_its_dotnet_is_there()
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
