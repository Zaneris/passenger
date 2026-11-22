using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Where a .NET keeps an SDK's workloads, as the SDK finds them: in the installation alone, unless it carries the
/// userlocal marker of the SDK's feature band (whatever its pre-release), when the user's own .dotnet under
/// DOTNET_CLI_HOME, else the home folder, comes first and takes the installs; never the installation twice, where that
/// folder is the installation itself.</summary>
public sealed class WorkloadFoldersTests : IDisposable
{
    private readonly string place = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(place, recursive: true);

    [Fact]
    public void An_SDKs_workloads_are_installed_where_dotnet_reads_them() => Cases.All(
        ("a dotnet installs an SDKs workloads for each user by the marker of its feature band whatever its pre release", () => Apart(test => test.A_dotnet_installs_an_SDKs_workloads_for_each_user_by_the_marker_of_its_feature_band_whatever_its_pre_release())),
        ("the users own dotnet comes first and takes the installs only where the SDKs band carries the marker", () => Apart(test => test.The_users_own_dotnet_comes_first_and_takes_the_installs_only_where_the_SDKs_band_carries_the_marker())),
        ("a dotnet installed in the users own dotnet folder is its one folder however its home is reached", () => Apart(test => test.A_dotnet_installed_in_the_users_own_dotnet_folder_is_its_one_folder_however_its_home_is_reached())),
        ("the SDKs home is DOTNET CLI HOME else the systems home folder", () => Apart(test => test.The_SDKs_home_is_DOTNET_CLI_HOME_else_the_systems_home_folder())));

    /// <summary>Runs a check on a test of its own, as each ran before, its state its own.</summary>
    private static void Apart(Action<WorkloadFoldersTests> check)
    {
        using WorkloadFoldersTests test = new();
        check(test);
    }

    private void A_dotnet_installs_an_SDKs_workloads_for_each_user_by_the_marker_of_its_feature_band_whatever_its_pre_release()
    {
        string root = Path.Combine("/usr", "lib64", "dotnet");
        string Band(string band) => Path.Combine(root, "metadata", "workloads", band, "userlocal");

        Assert.Equal(Band("11.0.100"), WorkloadFolders.UserLocalMarker(root, "11.0.100-rc.3.26522.148"));
        Assert.Equal(Band("10.0.100"), WorkloadFolders.UserLocalMarker(root, "10.0.113"));
        Assert.Equal(Band("9.0.300"), WorkloadFolders.UserLocalMarker(root, "9.0.305+abc"));
        Assert.Null(WorkloadFolders.UserLocalMarker(root, "11.0"));
        Assert.Null(WorkloadFolders.UserLocalMarker(root, "eleven"));
    }

    private void The_users_own_dotnet_comes_first_and_takes_the_installs_only_where_the_SDKs_band_carries_the_marker()
    {
        string root = Directory.CreateDirectory(Path.Combine(place, "usr", "lib64", "dotnet")).FullName;
        string home = Directory.CreateDirectory(Path.Combine(place, "home", "ann")).FullName;
        string own = Path.Combine(home, ".dotnet");

        Assert.False(WorkloadFolders.MayInstallForEachUser(root), "A .NET with no marker of any band was said to install workloads for each user");
        Assert.Equal(new WorkloadFolders(root, null), WorkloadFolders.Of(root, "10.0.113", home));

        Marked(root, "10.0.100");
        Assert.True(WorkloadFolders.MayInstallForEachUser(root), "A .NET carrying 10.0.100's marker was said to install every SDK's workloads into itself");
        WorkloadFolders tenth = WorkloadFolders.Of(root, "10.0.113", home);
        Assert.Equal(new WorkloadFolders(root, own), tenth);
        Assert.Equal([own, root], tenth.All);
        Assert.Equal(own, tenth.Installs);
        Assert.Equal([Path.Combine(own, "packs"), Path.Combine(root, "packs")], tenth.Under(WorkloadFolders.Packs));
        Assert.Equal([Path.Combine(own, "template-packs"), Path.Combine(root, "template-packs")], tenth.Under(WorkloadFolders.TemplatePacks));

        // Another band's SDK, an SDK unknown and no home keep to the installation.
        Assert.Equal(root, WorkloadFolders.Of(root, "11.0.100-rc.3.26522.148", home).Installs);
        Assert.Null(WorkloadFolders.Of(root, null, home).UserLocal);
        Assert.Null(WorkloadFolders.Of(root, "10.0.113", null).UserLocal);
        Assert.Equal([root], WorkloadFolders.Of(root, "10.0.113", "").All);
    }

    private void A_dotnet_installed_in_the_users_own_dotnet_folder_is_its_one_folder_however_its_home_is_reached()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A link to a folder needs Developer Mode on Windows");
        string home = Directory.CreateDirectory(Path.Combine(place, "var", "home", "ann")).FullName;
        string root = Directory.CreateDirectory(Path.Combine(home, ".dotnet")).FullName;
        Marked(root, "10.0.100");
        string linked = Path.Combine(place, "home");
        Directory.CreateSymbolicLink(linked, Path.Combine(place, "var", "home"));

        Assert.Equal([root], WorkloadFolders.Of(root, "10.0.102", home).All);
        Assert.Equal([root], WorkloadFolders.Of(root, "10.0.102", Path.Combine(linked, "ann")).All);
    }

    private void The_SDKs_home_is_DOTNET_CLI_HOME_else_the_systems_home_folder()
    {
        string homeVariable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        Dictionary<string, string> both = new() { ["DOTNET_CLI_HOME"] = "/srv/cli", [homeVariable] = "/home/ann" };
        Dictionary<string, string> home = new() { ["DOTNET_CLI_HOME"] = "", [homeVariable] = "/home/ann" };

        Assert.Equal("/srv/cli", WorkloadFolders.CliHome(name => both.GetValueOrDefault(name)));
        Assert.Equal("/home/ann", WorkloadFolders.CliHome(name => home.GetValueOrDefault(name)));
        Assert.Null(WorkloadFolders.CliHome(_ => null));
    }

    private static void Marked(string root, string band)
    {
        string marker = Path.Combine(root, "metadata", "workloads", band, "userlocal");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);
    }
}
