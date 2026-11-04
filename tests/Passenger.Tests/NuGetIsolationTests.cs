using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.Tests;

/// <summary>No test's restore reaches nuget.org or the person's feeds: in a test's folder with no nuget.config of its own,
/// the .NET command line's NuGet lists no package source, and the NuGet window finds no feed, audit source or package
/// source mapping in effect there, the run's own nuget.config above the folder clearing those of the user's and the
/// machine's files (on Windows the user's is the person's own); on macOS and Linux that user file is the run's.</summary>
public sealed class NuGetIsolationTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("nuget-isolation-"));

    public void Dispose() => TestFolders.Delete(folder);

    [Fact]
    public async Task A_test_folder_with_no_nuget_config_of_its_own_restores_from_no_package_source()
    {
        ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["nuget", "list", "source", "--format", "short"], folder),
            limit: Restore.Limit.Scaled(), cancel: TestContext.Current.CancellationToken);
        Assert.True(listed.Succeeded, $"dotnet nuget list source did not answer in a test's folder (exit code {listed.ExitCode}):\n{listed.Output}{listed.Error}");
        string[] sources = [.. listed.Output.Split('\n', StringSplitOptions.TrimEntries).Where(line => line.StartsWith("E ", StringComparison.Ordinal) || line.StartsWith("D ", StringComparison.Ordinal))];
        Assert.True(sources.Length == 0,
            $"A restore in a test's folder would take the package sources [{string.Join(", ", sources)}], so a package the cache lacks would come from there rather than fail the test");
    }

    [AvaloniaFact]
    public void The_nuget_window_on_a_test_folder_finds_no_feed_audit_source_or_mapping_in_effect_but_the_runs_clearing_file_above_it()
    {
        TestFolders.Write(folder, "Lib.csproj", TestProjects.Library());
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.Perform(AppActions.ShowNuGet);
            Ui.Landed(() => window.NuGet.PackageSources.Files.Count > 0, "the folder's NuGet settings read");
            PackageSourceSet settings = window.NuGet.PackageSources;
            Assert.True(settings.Problem is null, $"NuGet's settings for a test's folder do not read: {settings.Problem}");
            Assert.True(settings.Feeds.Count == 0 && settings.AuditSources.Count == 0 && !settings.Mapped,
                $"A test's folder has the feeds [{string.Join(", ", settings.Feeds.Select(f => $"{f.Name} {f.Source} from {f.DefinedIn}"))}], the audit sources "
                + $"[{string.Join(", ", settings.AuditSources.Select(f => f.Source))}] and a mapping {settings.Mapped}, which a restore there would ask beside the package cache");
            string runs = Path.Combine(PathNames.Canonical(TestRun.Root), "nuget.config");
            Assert.True(settings.Files.Any(f => PathNames.Same(f.Path, runs) && f.Clears),
                $"NuGet reads no nuget.config of the run's above a test's folder that clears the feeds of the user's and the machine's files: [{string.Join(", ", settings.Files.Select(f => $"{f.Path} ({f.Place})"))}]");
            if (!OperatingSystem.IsWindows())
            {
                ConfigFile user = Assert.Single(settings.Files, f => f.Place == ConfigPlace.User);
                Assert.True(PathNames.IsUnder(user.Path, PathNames.Canonical(TestRun.Root)) && user.Feeds.Count == 0,
                    $"The user's nuget.config NuGet reads is {user.Path}, naming [{string.Join(", ", user.Feeds.Select(f => f.Value))}], not the run's own naming no feed");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
