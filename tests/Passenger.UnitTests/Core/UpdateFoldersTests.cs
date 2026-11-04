using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What a Linux update reads and keeps: the SHA-256 a release's SHA256SUMS gives its archive, the version an
/// unpacked app's deps.json gives Passenger, and which of the folders an update keeps beside the install a copy removes.</summary>
public class UpdateFoldersTests
{
    /// <summary>The folder an install for one person is in, written as this system writes a path.</summary>
    private static readonly string Opt = OperatingSystem.IsWindows() ? @"C:\Users\ada\opt" : "/home/ada/.local/opt";

    private const string Sum = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    [Theory]
    [InlineData($"{Sum}  Passenger-linux-x64.tar.gz\n", "Passenger-linux-x64.tar.gz")]
    [InlineData($"{Sum} *Passenger-linux-x64.tar.gz\r\n", "Passenger-linux-x64.tar.gz")]
    [InlineData($"0000000000000000000000000000000000000000000000000000000000000000  Passenger-linux-arm64.tar.gz\n{Sum}  Passenger-linux-x64.tar.gz", "Passenger-linux-x64.tar.gz")]
    public void The_sums_give_a_files_SHA_256_in_lowercase_whichever_mode_sha256sum_wrote_it_in(string sums, string file)
    {
        string? read = ReleaseSums.Of(sums, file);
        Assert.True(read == Sum.ToLowerInvariant(), $"SHA256SUMS reading '{sums.Trim()}' gave {file} {read ?? "no SHA-256"}, so its download would be refused");
    }

    [Theory]
    [InlineData("Passenger-linux-x64.tar.gz.sig")]
    [InlineData("passenger-linux-x64.tar.gz")]
    [InlineData("x64.tar.gz")]
    public void The_sums_give_no_SHA_256_to_a_file_they_do_not_name_exactly(string file)
    {
        string? read = ReleaseSums.Of($"{Sum}  Passenger-linux-x64.tar.gz\nnot a line\n", file);
        Assert.True(read is null, $"SHA256SUMS naming Passenger-linux-x64.tar.gz alone gave {file} the SHA-256 {read}, so another file would be taken for it");
    }

    [Theory]
    [InlineData("""{"libraries":{"Avalonia/12.0.0":{"type":"package"},"Passenger/0.3.0":{"type":"project","serviceable":false}}}""", "0.3.0")]
    [InlineData("""{"libraries":{"Passenger/0.3.0":{"type":"package"}}}""", null)]
    [InlineData("""{"targets":{}}""", null)]
    [InlineData("not json", null)]
    public void The_apps_version_is_the_one_its_deps_json_gives_its_own_project(string deps, string? version)
    {
        string? read = UpdateFolders.AppVersion(deps);
        Assert.True(read == version, $"The deps.json '{deps}' was read as Passenger {read ?? "of no version"}, not {version ?? "of no version"}, "
            + "so an archive of another release would be taken for this one, or this one refused");
    }

    [Fact]
    public void A_folder_is_beside_the_install_named_for_the_release_unpacked_into_it()
    {
        string beside = UpdateFolders.Beside(Path.Combine(Opt, "passenger") + Path.DirectorySeparatorChar, "1.2.0");
        Assert.True(beside == Path.Combine(Opt, ".passenger.update-1.2.0"), $"The update's folder is {beside}, not beside the install, where the two can trade places");
        Assert.True(UpdateFolders.VersionOf(beside) == "1.2.0", $"The update's folder {beside} was read as {UpdateFolders.VersionOf(beside)}'s");
        Assert.True(UpdateFolders.VersionOf(Path.Combine(Opt, "passenger")) is null, "The install itself was read as an update's folder, which a copy removes");
        Assert.True(UpdateFolders.VersionOf(Path.Combine(Opt, ".passenger.update-")) is null, "A folder named for no version was read as an update's");
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", null, true)]
    [InlineData("1.1.0", "1.2.0", null, true)]
    [InlineData("1.10.0", "1.9.0", null, false)]
    [InlineData("1.3.0", "1.2.0", "1.3.0", false)]
    [InlineData("1.3.0", "1.2.0", "1.4.0", true)]
    [InlineData("1.3.0", "1.2.0", "", true)]
    public void A_copy_removes_what_an_update_left_but_a_release_not_yet_in_place(string folder, string running, string? keep, bool removed)
    {
        bool stale = UpdateFolders.Stale(folder, running, keep);
        Assert.True(stale == removed, $"Passenger {running}{(keep is null ? string.Empty : $" making {keep} ready")} {(stale ? "removes" : "keeps")} the update's folder of {folder}, "
            + (removed ? "which is the release it replaced or one no longer wanted, left on the disk" : "which holds a release still to install"));
    }
}
