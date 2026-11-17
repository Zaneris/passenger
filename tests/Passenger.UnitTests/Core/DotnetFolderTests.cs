using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Where a dotnet run for a folder works: an SDK's command for a folder not trusted (every folder, in this process),
/// or one under a global.json in a folder not trusted, works outside it, in the person's home, so that global.json never
/// names the SDK that runs; a program's own run (by its path, or after exec) works in the folder, which no global.json
/// chooses anything for.</summary>
public sealed class DotnetFolderTests
{
    private static readonly string Host = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "dotnet-folder-tests", "dotnet"));

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string Repository = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "dotnet-folder-tests", "repo"));

    [Fact]
    public void An_sdks_command_for_a_folder_not_trusted_works_in_the_persons_home()
    {
        foreach (string[] arguments in (string[][])[["--version"], ["new", "list"], ["msbuild", "App.csproj"], ["workload", "install", "android"], []])
        {
            Assert.True(ChildProcesses.DotnetCommand(Host, arguments, Repository).WorkingDirectory == Home,
                $"dotnet {string.Join(' ', arguments)} for a folder not trusted works in {ChildProcesses.DotnetCommand(Host, arguments, Repository).WorkingDirectory}, where its global.json names the SDK");
        }
        Assert.Equal(Home, ChildProcesses.DotnetQuestion(Host, ["--version"], Repository).WorkingDirectory);
    }

    [Fact]
    public void A_programs_own_run_works_in_its_folder_and_an_sdks_command_for_a_folder_holding_home_on_the_disks_root()
    {
        Assert.Equal(Repository, ChildProcesses.DotnetCommand(Host, ["exec", "--runtimeconfig", "App.runtimeconfig.json", "App.dll"], Repository).WorkingDirectory);
        Assert.Equal(Repository, ChildProcesses.DotnetCommand(Host, [Path.Combine(Repository, "Tool.dll"), "-Plugin"], Repository).WorkingDirectory);
        string above = Path.GetDirectoryName(Home)!;
        Assert.Equal(Path.GetPathRoot(above), ChildProcesses.DotnetCommand(Host, ["--version"], above).WorkingDirectory);
        Assert.Equal(string.Empty, ChildProcesses.DotnetCommand(Host, ["--version"], null).WorkingDirectory);
    }

    /// <summary>A trusted folder takes what the host reads there only where the nearest global.json over it, whose sdk.paths
    /// name the SDK that runs, is in a folder trusted too: one in a folder above it not trusted sends its dotnet elsewhere,
    /// one in the folder itself does not.</summary>
    [Fact]
    public void A_trusted_folder_takes_only_a_global_json_in_a_folder_trusted_too()
    {
        string repository = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            string app = Directory.CreateDirectory(Path.Join(repository, "src", "App")).FullName;
            bool Trusts(string folder) => PathNames.Within(folder, app);
            Assert.True(Dotnet.TakesOnlyTrusted(app, Trusts), "A trusted folder with no global.json over it was found to take what a folder not trusted chose");
            Assert.False(Dotnet.TakesOnlyTrusted(repository, Trusts), "A folder not trusted was found to take only what trusted folders chose");

            File.WriteAllText(Path.Join(repository, "global.json"), """{ "sdk": { "paths": [ ".sdk" ] } }""");
            Assert.False(Dotnet.TakesOnlyTrusted(app, Trusts), "A trusted folder takes the SDK a global.json in the folder above it, not trusted, names");
            Assert.True(Dotnet.TakesOnlyTrusted(app, folder => PathNames.Within(folder, repository)), "A folder whose global.json's folder is trusted was found not to take it");

            File.WriteAllText(Path.Join(app, "global.json"), """{ "sdk": { "rollForward": "latestMajor" } }""");
            Assert.True(Dotnet.TakesOnlyTrusted(app, Trusts), "A trusted folder holding its own global.json was found to take the one above it");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }
}
