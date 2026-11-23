using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What an update reads and keeps: the SHA-256 a release's SHA256SUMS gives its archive, the version an unpacked
/// app's deps.json gives Passenger, which of the folders an update keeps beside the install a copy removes, and what a
/// copy's place says of who updates it: a portable data folder, a package manager's file or WinGet's folder.</summary>
public sealed class UpdateFoldersTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>The folder an install for one person is in, written as this system writes a path.</summary>
    private static readonly string Opt = OperatingSystem.IsWindows() ? @"C:\Users\ada\opt" : "/home/ada/.local/opt";

    private const string Sum = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    [Fact]
    public void An_update_lands_only_as_its_sums_name_it_and_a_copy_keeps_a_release_not_yet_in_place() => Cases.All(
        ("the sums give a files SHA 256 in lowercase whichever mode sha256sum wrote it in", () => Cases.Each<(string, string)>(
        [
            ($"{Sum}  Passenger-linux-x64.tar.gz\n", "Passenger-linux-x64.tar.gz"),
            ($"{Sum} *Passenger-linux-x64.tar.gz\r\n", "Passenger-linux-x64.tar.gz"),
            ($"0000000000000000000000000000000000000000000000000000000000000000  Passenger-linux-arm64.tar.gz\n{Sum}  Passenger-linux-x64.tar.gz", "Passenger-linux-x64.tar.gz"),
        ], row => The_sums_give_a_files_SHA_256_in_lowercase_whichever_mode_sha256sum_wrote_it_in(row.Item1, row.Item2))),
        ("the sums give no SHA 256 to a file they do not name exactly", () => Cases.Each<string>(
        [
            "Passenger-linux-x64.tar.gz.sig",
            "passenger-linux-x64.tar.gz",
        ], The_sums_give_no_SHA_256_to_a_file_they_do_not_name_exactly)),
        ("the apps version is the one its deps json gives its own project", () => Cases.Each<(string, string?)>(
        [
            ("""{"libraries":{"Avalonia/12.0.0":{"type":"package"},"Passenger/0.3.0":{"type":"project","serviceable":false}}}""", "0.3.0"),
            ("""{"libraries":{"Passenger/0.3.0":{"type":"package"}}}""", null),
            ("not json", null),
        ], row => The_apps_version_is_the_one_its_deps_json_gives_its_own_project(row.Item1, row.Item2))),
        ("a folder is beside the install named for the release unpacked into it", A_folder_is_beside_the_install_named_for_the_release_unpacked_into_it),
        ("a copy removes what an update left but a release not yet in place", () => Cases.Each<(string, string, string?, bool)>(
        [
            ("1.2.0", "1.2.0", null, true),
            ("1.10.0", "1.9.0", null, false),
            ("1.3.0", "1.2.0", "1.3.0", false),
            ("1.3.0", "1.2.0", "1.4.0", true),
        ], row => A_copy_removes_what_an_update_left_but_a_release_not_yet_in_place(row.Item1, row.Item2, row.Item3, row.Item4))),
        ("a package managers file names the manager and the command that updates the copy and one that is no such file is refused", () => Cases.Each<(string, string?, string?, bool)>(
        [
            ("pacman\n", "pacman", null, false),
            ("\uFEFFAUR\r\nyay -Syu passenger-ide-bin\r\n\r\n", "AUR", "yay -Syu passenger-ide-bin", false),
            ("", null, null, false),
            ("\nbrew upgrade passenger\n", null, null, true),
            ("pacman\nyay -Syu\nanother line\n", null, null, true),
            ("pac\u0007man\n", null, null, true),
            (new string('x', 4097), null, null, true),
        ], row => A_package_managers_file_names_the_manager_and_the_command_that_updates_the_copy_and_one_that_is_no_such_file_is_refused(row.Item1, row.Item2, row.Item3, row.Item4))),
        ("a copys folder is where a person keeps it and its data folder makes it portable", A_copys_folder_is_where_a_person_keeps_it_and_its_data_folder_makes_it_portable),
        ("a data folder that is no folder and a package managers file that is none are refused naming them", A_data_folder_that_is_no_folder_and_a_package_managers_file_that_is_none_are_refused_naming_them));

    private void The_sums_give_a_files_SHA_256_in_lowercase_whichever_mode_sha256sum_wrote_it_in(string sums, string file)
    {
        string? read = ReleaseSums.Of(sums, file);
        Assert.True(read == Sum.ToLowerInvariant(), $"SHA256SUMS reading '{sums.Trim()}' gave {file} {read ?? "no SHA-256"}, so its download would be refused");
    }

    private void The_sums_give_no_SHA_256_to_a_file_they_do_not_name_exactly(string file)
    {
        string? read = ReleaseSums.Of($"{Sum}  Passenger-linux-x64.tar.gz\nnot a line\n", file);
        Assert.True(read is null, $"SHA256SUMS naming Passenger-linux-x64.tar.gz alone gave {file} the SHA-256 {read}, so another file would be taken for it");
    }

    private void The_apps_version_is_the_one_its_deps_json_gives_its_own_project(string deps, string? version)
    {
        string? read = UpdateFolders.AppVersion(deps);
        Assert.True(read == version, $"The deps.json '{deps}' was read as Passenger {read ?? "of no version"}, not {version ?? "of no version"}, "
            + "so an archive of another release would be taken for this one, or this one refused");
    }

    private void A_folder_is_beside_the_install_named_for_the_release_unpacked_into_it()
    {
        string beside = UpdateFolders.Beside(Path.Combine(Opt, "passenger") + Path.DirectorySeparatorChar, "1.2.0");
        Assert.True(beside == Path.Combine(Opt, ".passenger.update-1.2.0"), $"The update's folder is {beside}, not beside the install, where the two can trade places");
        Assert.True(UpdateFolders.VersionOf(beside) == "1.2.0", $"The update's folder {beside} was read as {UpdateFolders.VersionOf(beside)}'s");
        Assert.True(UpdateFolders.VersionOf(Path.Combine(Opt, "passenger")) is null, "The install itself was read as an update's folder, which a copy removes");
        Assert.True(UpdateFolders.VersionOf(Path.Combine(Opt, ".passenger.update-")) is null, "A folder named for no version was read as an update's");
    }

    private void A_copy_removes_what_an_update_left_but_a_release_not_yet_in_place(string folder, string running, string? keep, bool removed)
    {
        bool stale = UpdateFolders.Stale(folder, running, keep);
        Assert.True(stale == removed, $"Passenger {running}{(keep is null ? string.Empty : $" making {keep} ready")} {(stale ? "removes" : "keeps")} the update's folder of {folder}, "
            + (removed ? "which is the release it replaced or one no longer wanted, left on the disk" : "which holds a release still to install"));
    }

    private void A_package_managers_file_names_the_manager_and_the_command_that_updates_the_copy_and_one_that_is_no_such_file_is_refused(string text, string? manager,
        string? command, bool refused)
    {
        PackageManagerMark? read = ThisCopy.MarkOf(System.Text.Encoding.UTF8.GetBytes(text));
        Assert.True(refused ? read is null : read == new PackageManagerMark(manager, command),
            $"A package manager's file reading '{text[..Math.Min(text.Length, 60)]}' was read as {read?.ToString() ?? "none"}, "
            + (refused ? "where Passenger is to refuse it as it starts" : $"not {manager ?? "an unnamed manager"} with {command ?? "no command"}, so the person is told the wrong way to update"));
    }

    private void A_copys_folder_is_where_a_person_keeps_it_and_its_data_folder_makes_it_portable()
    {
        string windows = Directory.CreateDirectory(Path.Combine(root, "stick", "Passenger")).FullName;
        Assert.True(ThisCopy.Of(windows, []) is { Data: null, PackageManager: null } plain && plain.Folder == windows,
            $"A copy with no data folder beside it was read as {ThisCopy.Of(windows, [])}, not one keeping its state where an installed copy does");
        string data = Directory.CreateDirectory(Path.Combine(windows, ThisCopy.DataName)).FullName;
        Assert.True(ThisCopy.Of(windows + Path.DirectorySeparatorChar, []).Data == PathNames.Canonical(data),
            $"A copy with {data} beside its executable keeps its state in {ThisCopy.Of(windows, []).Data ?? "the person's own folders"}");

        string linux = Directory.CreateDirectory(Path.Combine(root, "unpacked", "passenger")).FullName;
        string app = Directory.CreateDirectory(Path.Combine(linux, "app")).FullName;
        Assert.True(ThisCopy.FolderOf(app) == app, "An app folder with no launcher above it was taken for the Linux archive's, its folder above it the copy's");
        File.WriteAllText(Path.Combine(linux, "passenger"), "#!/bin/sh\n");
        Assert.True(ThisCopy.FolderOf(app) == linux, $"The Linux archive's copy is kept in {ThisCopy.FolderOf(app)}, not its passenger folder {linux}, where its data folder goes");

        string bundle = Path.Combine(root, "Apps", "Passenger.app");
        Assert.True(ThisCopy.FolderOf(Path.Combine(bundle, "Contents", "MacOS")) == Path.Combine(root, "Apps"),
            $"A Mac app's copy is kept in {ThisCopy.FolderOf(Path.Combine(bundle, "Contents", "MacOS"))}, inside its bundle, whose seal a data folder would break");

        string packages = Path.Combine(root, "WinGet", "Packages");
        string installed = Directory.CreateDirectory(Path.Combine(packages, "PassengerIDE.Passenger_Microsoft.Winget.Source_8wekyb3d8bbwe", "Passenger")).FullName;
        Assert.True(ThisCopy.Of(installed, [packages]).PackageManager == new PackageManagerMark("WinGet", "winget upgrade --id PassengerIDE.Passenger"),
            $"A copy WinGet unpacked was read as {ThisCopy.Of(installed, [packages]).PackageManager?.ToString() ?? "no package manager's"}, so it would not name winget upgrade");
        File.WriteAllText(Path.Combine(installed, ThisCopy.PackageManagerName), "Scoop\nscoop update passenger\n");
        Assert.True(ThisCopy.Of(installed, [packages]).PackageManager == new PackageManagerMark("Scoop", "scoop update passenger"),
            "A package manager's own file beside the executable did not win over the folder it lies in");
    }

    private void A_data_folder_that_is_no_folder_and_a_package_managers_file_that_is_none_are_refused_naming_them()
    {
        string copy = Directory.CreateDirectory(Path.Combine(root, "refused")).FullName;
        string data = Path.Combine(copy, ThisCopy.DataName);
        File.WriteAllText(data, "not a folder\n");
        FormatException file = Assert.Throws<FormatException>(() => ThisCopy.Of(copy, []));
        Assert.True(file.Message.StartsWith($"{data} is no folder", StringComparison.Ordinal), $"A data file beside Passenger was refused saying: {file.Message}");
        File.Delete(data);

        string marker = Path.Combine(copy, ThisCopy.PackageManagerName);
        File.WriteAllText(marker, "one\ntwo\nthree\n");
        FormatException mark = Assert.Throws<FormatException>(() => ThisCopy.Of(copy, []));
        Assert.True(mark.Message.StartsWith($"{marker} is not a package manager's file", StringComparison.Ordinal), $"A package manager's file of three lines was refused saying: {mark.Message}");
        Assert.True(ThisCopy.MarkOf([0x70, 0xC3, 0x28, 0x0A]) is null, "A package manager's file that is not UTF-8 was read as one");
    }
}
