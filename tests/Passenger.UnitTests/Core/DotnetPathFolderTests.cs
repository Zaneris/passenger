using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The folder a shell finds an installation's dotnet in: the installation's own where it holds .NET's layout alone,
/// as its installers and the SDK lay it down; else one of the user's own, in their temporary folder, holding that dotnet
/// alone (a link to it, or on Windows a dotnet.cmd running it), never the installation's folder with a program of another
/// name beside its dotnet; one for each installation, kept as it is once right and put right where it is not.</summary>
public sealed class DotnetPathFolderTests : IDisposable
{
    private readonly string installation = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    private readonly string state = Path.Combine(Path.DirectorySeparatorChar + "no-such-folder", $"Passenger-{Guid.NewGuid():N}");

    private readonly List<string> made = [];

    public DotnetPathFolderTests()
    {
        // .NET's own layout, as its installers, dotnet-install and the SDK at its home lay it down.
        foreach (string file in new[] { Dotnet.HostName, OperatingSystem.IsWindows() ? "dnx.cmd" : "dnx", "LICENSE.txt", "ThirdPartyNotices.txt",
            "10.0.100.dotnetFirstUseSentinel", "10.0.100.toolpath.sentinel", "10.0.100_MachineId.dotnetUserLevelCache", ".workloadAdvertisingManifestSentinel10.0.100" })
        {
            File.WriteAllText(Path.Combine(installation, file), "#!/bin/sh\n");
        }
        foreach (string folder in new[] { "host", "sdk", "shared", "packs", "templates", "sdk-manifests", "metadata", "library-packs", "tools" })
        {
            Directory.CreateDirectory(Path.Combine(installation, folder));
        }
    }

    public void Dispose()
    {
        Directory.Delete(installation, recursive: true);
        foreach (string folder in made.Where(Directory.Exists))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private string Lent(string root)
    {
        string folder = Dotnet.HostAlone(root, state);
        made.Add(folder);
        return folder;
    }

    [Fact]
    public void A_shells_dotnet_comes_first_on_the_path_from_a_folder_holding_it_alone() => Cases.All(
        ("an installation holding dotnets layout alone is its own folder on the path", () => Apart(test => test.An_installation_holding_dotnets_layout_alone_is_its_own_folder_on_the_path())),
        ("an installation holding anything else lends its dotnet through a folder holding it alone", () => Cases.Each<(string, bool)>(
        [
            ("git", false),
            ("bin", true),
        ], row => Apart(test => test.An_installation_holding_anything_else_lends_its_dotnet_through_a_folder_holding_it_alone(row.Item1, row.Item2)))),
        ("the folder lending an installations dotnet holds that dotnet alone and is the users own", () => Apart(test => test.The_folder_lending_an_installations_dotnet_holds_that_dotnet_alone_and_is_the_users_own())),
        ("a dotnet put wrong in the folder is put right and another installation has a folder of its own", () => Apart(test => test.A_dotnet_put_wrong_in_the_folder_is_put_right_and_another_installation_has_a_folder_of_its_own())));

    /// <summary>Runs a check on a test of its own, as each ran before, its state its own.</summary>
    private static void Apart(Action<DotnetPathFolderTests> check)
    {
        using DotnetPathFolderTests test = new();
        check(test);
    }

    private void An_installation_holding_dotnets_layout_alone_is_its_own_folder_on_the_path()
    {
        Assert.True(Dotnet.PathFolder(installation, state) == installation,
            $"An installation holding .NET's layout alone, {string.Join(", ", Directory.EnumerateFileSystemEntries(installation).Select(Path.GetFileName))}, was not put on the PATH itself");
        Assert.False(Directory.EnumerateDirectories(UserTemporary.Folder(), Path.GetFileName(UserTemporary.Of(state)) + "-dotnet-*").Any(),
            "A folder lending dotnet was made for an installation that is put on the PATH itself");
    }

    private void An_installation_holding_anything_else_lends_its_dotnet_through_a_folder_holding_it_alone(string name, bool folder)
    {
        string other = Path.Combine(installation, name);
        if (folder)
        {
            Directory.CreateDirectory(other);
        }
        else
        {
            File.WriteAllText(other, "#!/bin/sh\n");
        }
        string lent = Dotnet.PathFolder(installation, state);
        made.Add(lent);
        Assert.True(lent != installation && lent == Dotnet.HostAlone(installation, state),
            $"An installation holding {name} beside .NET's layout was put on the PATH as {lent}, so a shell could run {name} in place of the system's");
    }

    private void The_folder_lending_an_installations_dotnet_holds_that_dotnet_alone_and_is_the_users_own()
    {
        File.WriteAllText(Path.Combine(installation, "git"), "#!/bin/sh\n");
        string folder = Lent(installation);
        Assert.True(Path.GetDirectoryName(folder) == Path.TrimEndingDirectorySeparator(UserTemporary.Folder()),
            $"The folder lending dotnet, {folder}, is not in the user's own temporary folder, {UserTemporary.Folder()}");
        string[] held = [.. Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>()];
        string host = Path.Combine(installation, Dotnet.HostName);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(held.SequenceEqual([Dotnet.HostCommand]), $"The folder first on a shell's PATH holds {string.Join(", ", held)}, where it lends dotnet alone");
            string command = File.ReadAllText(Path.Combine(folder, Dotnet.HostCommand));
            Assert.True(command == $"@\"{host}\" %*\r\n", $"dotnet.cmd runs {command}, not the installation's dotnet with the arguments given");
            return;
        }
        Assert.True(held.SequenceEqual([Dotnet.HostName]), $"The folder first on a shell's PATH holds {string.Join(", ", held)}, so the shell would run them in place of the system's");
        Assert.True(new FileInfo(Path.Combine(folder, Dotnet.HostName)).LinkTarget == host,
            $"The folder's dotnet is no link to {host}, so the shell would not run the installation's dotnet");
        UnixFileMode mode = File.GetUnixFileMode(folder);
        Assert.True(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            $"The folder lending dotnet has mode {mode}, so another user could put a program in it that the shell would run");
    }

    private void A_dotnet_put_wrong_in_the_folder_is_put_right_and_another_installation_has_a_folder_of_its_own()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows lets a user make no link without Developer Mode");
        string folder = Lent(installation);
        string link = Path.Combine(folder, Dotnet.HostName);
        File.Delete(link);
        File.CreateSymbolicLink(link, Path.Combine(installation, "git"));
        Assert.Equal(folder, Lent(installation));
        Assert.True(new FileInfo(link).LinkTarget == Path.Combine(installation, Dotnet.HostName),
            $"The folder's dotnet still names {new FileInfo(link).LinkTarget}, not the installation's dotnet");

        string other = Path.Combine(installation, "other");
        Directory.CreateDirectory(other);
        Assert.NotEqual(folder, Lent(other));
    }
}
