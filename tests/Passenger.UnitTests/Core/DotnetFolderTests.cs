using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Where a dotnet run for a folder works: an SDK's command for a folder not trusted (every folder, in this process)
/// works outside it, in the person's home, so the global.json over the folder never names the SDK that runs; a program's
/// own run (by its path, or after exec) works in the folder, which no global.json chooses anything for.</summary>
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
}
