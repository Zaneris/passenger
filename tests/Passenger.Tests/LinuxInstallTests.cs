using System.Diagnostics;
using System.Runtime.Versioning;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Linux archive's install.sh run as a person runs it, for one person, over a home of the test's own and an
/// unpacked folder standing in for the archive's (tools/linux's own files, a script for the app): what it finds where it
/// installs is either Passenger's to replace or left as it was. Each run is a shell of its own over the test's folders, so
/// the class runs beside the window tests.</summary>
[Collection(Beside.Name)]
public sealed class LinuxInstallTests
{
    /// <summary>Why the tests skip on Windows.</summary>
    private const string NoShell = "install.sh is a POSIX shell script for Linux, which Windows has no shell to run";

    /// <summary>The name of the desktop entry and the files beside it.</summary>
    private const string App = "io.github.zaneris.passenger";

    private const UnixFileMode Runnable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>The command's name on the PATH.</summary>
    private const string Command = "passenger-ide";

    /// <summary>Another program of the command's name in the bin folder, a link to one, or a folder, is left as it was,
    /// and the install stops naming it before it has installed anything.</summary>
    [Theory]
    [InlineData("program")]
    [InlineData("link")]
    [InlineData("folder")]
    public async Task An_install_leaves_another_programs_command_of_its_name_and_refuses_naming_it(string what)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoShell);
            return;
        }
        string root = PathNames.Canonical(TestFolders.Create("install-"));
        string home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        string command = Path.Combine(Directory.CreateDirectory(Path.Combine(home, ".local", "bin")).FullName, Command);
        switch (what)
        {
            case "program":
                Program(command);
                break;
            case "link":
                File.CreateSymbolicLink(command, Program(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "gems", "bin")).FullName, Command)));
                break;
            default:
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(command).FullName, "notes.txt"), "kept");
                break;
        }
        string before = Held(command);

        ChildResult install = await Install(Unpacked(Path.Combine(root, "download"), "1"), home);

        Assert.True(Held(command) == before,
            $"An install over {what} at ~/.local/bin/{Command} made it {Held(command)} where it was {before}: what the person had there is gone");
        Assert.False(install.Succeeded, $"An install over {what} at ~/.local/bin/{Command} went ahead without its command: {install.Output}");
        Assert.True(install.Error.Contains(command, StringComparison.Ordinal),
            $"An install refused over {what} at ~/.local/bin/{Command} did not say where: {install.Said}");
        Assert.False(Directory.Exists(Path.Combine(home, ".local", "opt", "passenger")),
            $"An install refused over {what} at ~/.local/bin/{Command} installed the app all the same, with no command to start it");
    }

    /// <summary>The command's former name, <c>passenger</c>: an earlier install's link of it, leading to the install, moves
    /// to <c>passenger-ide</c> as the install is updated, and an uninstall takes either away; Phusion Passenger's own
    /// <c>passenger</c>, a program or a link to one, is left as it was, the install going ahead beside it.</summary>
    [Theory]
    [InlineData("ours")]
    [InlineData("program")]
    [InlineData("link")]
    public async Task An_install_moves_its_former_passenger_link_to_the_new_name_and_leaves_phusions_passenger_as_it_was(string what)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoShell);
            return;
        }
        string root = PathNames.Canonical(TestFolders.Create("install-"));
        string home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        string bin = Directory.CreateDirectory(Path.Combine(home, ".local", "bin")).FullName;
        string former = Path.Combine(bin, "passenger");
        string command = Path.Combine(bin, Command);
        string installed = Path.Combine(home, ".local", "opt", "passenger");
        ChildResult install = await Install(Unpacked(Path.Combine(root, "first"), "1"), home);
        Assert.True(install.Succeeded, $"The first release's install failed: {install.Said}");
        switch (what)
        {
            case "ours":
                File.CreateSymbolicLink(former, Path.Combine(installed, "passenger"));
                break;
            case "program":
                Program(former);
                break;
            default:
                File.CreateSymbolicLink(former, Program(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "gems", "bin")).FullName, "passenger")));
                break;
        }
        string before = Held(former);

        install = await Install(Unpacked(Path.Combine(root, "second"), "2"), home);

        Assert.True(install.Succeeded, $"An update with {what} at ~/.local/bin/passenger refused: {install.Said}");
        Assert.True(new FileInfo(command).LinkTarget == Path.Combine(installed, "passenger"),
            $"An update with {what} at ~/.local/bin/passenger left ~/.local/bin/{Command} {Held(command)}, not leading to the install");
        if (what == "ours")
        {
            Assert.True(!File.Exists(former) && new FileInfo(former).LinkTarget is null,
                $"An update left the earlier install's ~/.local/bin/passenger {Held(former)} beside ~/.local/bin/{Command}");
            File.CreateSymbolicLink(former, Path.Combine(installed, "passenger"));
            install = await Install(Path.Combine(installed, "install.sh"), home, uninstall: true);
            Assert.True(install.Succeeded, $"The uninstall refused: {install.Said}");
            Assert.True(new FileInfo(former).LinkTarget is null && new FileInfo(command).LinkTarget is null,
                $"The uninstall left ~/.local/bin/passenger {Held(former)} and ~/.local/bin/{Command} {Held(command)}");
        }
        else
        {
            Assert.True(Held(former) == before,
                $"An update made Phusion Passenger's {what} at ~/.local/bin/passenger {Held(former)} where it was {before}: the person's program is gone");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static string Program(string path)
    {
        File.WriteAllText(path, "#!/bin/sh\necho 'Phusion Passenger(R) 6.0.27'\n");
        File.SetUnixFileMode(path, Runnable);
        return path;
    }

    /// <summary>A link at the command's path leading to another Passenger (an unpacked folder linked by hand), or to this
    /// install's launcher whose folder was deleted, is Passenger's to replace: the install goes ahead and links its own.</summary>
    [Fact]
    public async Task An_install_takes_over_a_link_that_leads_to_a_passenger()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoShell);
            return;
        }
        string root = PathNames.Canonical(TestFolders.Create("install-"));
        string home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        string command = Path.Combine(Directory.CreateDirectory(Path.Combine(home, ".local", "bin")).FullName, Command);
        string installed = Path.Combine(home, ".local", "opt", "passenger");
        string earlier = Unpacked(Path.Combine(root, "earlier"), "1");
        File.CreateSymbolicLink(command, Path.Combine(Path.GetDirectoryName(earlier)!, "passenger"));

        ChildResult install = await Install(Unpacked(Path.Combine(root, "download"), "2"), home);

        Assert.True(install.Succeeded, $"An install over a link to another unpacked Passenger refused: {install.Said}");
        Assert.True(new FileInfo(command).LinkTarget == Path.Combine(installed, "passenger"),
            $"An install over a link to another unpacked Passenger left the command {Held(command)}, not leading to the install");

        Directory.Delete(installed, recursive: true);
        install = await Install(Path.Combine(root, "download", "passenger", "install.sh"), home);

        Assert.True(install.Succeeded, $"An install whose command's link leads to its own deleted folder refused: {install.Said}");
        Assert.True(File.Exists(Path.Combine(installed, "app", "Passenger")), "An install over a link to its own deleted folder did not put the app back");
    }

    /// <summary>An update whose move of the new release into place fails, once the installed one is set aside, leaves the
    /// installed release where it was, every file as it was and nothing beside it; the same update run again then lands.
    /// The failure is a <c>mv</c> on the PATH refusing the new folder: a parent made read-only would refuse the first move
    /// as well, before anything was set aside.</summary>
    [Fact]
    public async Task An_update_whose_last_move_fails_leaves_the_installed_passenger_as_it_was()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoShell);
            return;
        }
        string root = PathNames.Canonical(TestFolders.Create("install-"));
        string home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        string opt = Path.Combine(home, ".local", "opt");
        string installed = Path.Combine(opt, "passenger");
        ChildResult install = await Install(Unpacked(Path.Combine(root, "first"), "1"), home);
        Assert.True(install.Succeeded, $"The first release's install failed: {install.Said}");
        string before = Held(installed);
        string programs = Directory.CreateDirectory(Path.Combine(root, "programs")).FullName;
        File.WriteAllText(Path.Combine(programs, "mv"),
            "#!/bin/sh\ncase $1 in\n    *.new-*) echo \"mv: cannot move '$1' to '$2': Permission denied\" >&2; exit 1 ;;\nesac\nexec /bin/mv \"$@\"\n");
        File.SetUnixFileMode(Path.Combine(programs, "mv"), Runnable);
        string update = Unpacked(Path.Combine(root, "second"), "2");

        install = await Install(update, home, programs);

        Assert.True(Held(installed) == before,
            $"An update whose last move failed left ~/.local/opt/passenger {Held(installed)} where it was {before}: the person's Passenger is gone");
        Assert.False(install.Succeeded, $"An update whose last move failed said it was installed: {install.Output}");
        Assert.True(Directory.EnumerateFileSystemEntries(opt).Select(Path.GetFileName).SequenceEqual(["passenger"]),
            $"An update whose last move failed left {string.Join(", ", Directory.EnumerateFileSystemEntries(opt).Select(Path.GetFileName))} in ~/.local/opt");

        install = await Install(update, home);

        Assert.True(install.Succeeded, $"The update run again after its last move failed refused: {install.Said}");
        Assert.True(File.ReadAllText(Path.Combine(installed, "app", "release.txt")) == "2",
            $"The update run again after its last move failed left ~/.local/opt/passenger {Held(installed)}, not the second release");
        Assert.True(Directory.EnumerateFileSystemEntries(opt).Select(Path.GetFileName).SequenceEqual(["passenger"]),
            $"The update left {string.Join(", ", Directory.EnumerateFileSystemEntries(opt).Select(Path.GetFileName))} in ~/.local/opt");
    }

    /// <summary>The installed install.sh given another unpacked release with <c>--from</c> installs that one in its place,
    /// and run again with nothing given refreshes the menu entry where it is, leaving the install as it is.</summary>
    [Fact]
    public async Task An_installed_install_sh_installs_the_release_from_names_and_run_again_refreshes_the_menu_entry()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoShell);
            return;
        }
        string root = PathNames.Canonical(TestFolders.Create("install-"));
        string home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        string installed = Path.Combine(home, ".local", "opt", "passenger");
        string entry = Path.Combine(home, ".local", "share", "applications", App + ".desktop");
        ChildResult install = await Install(Unpacked(Path.Combine(root, "first"), "1"), home);
        Assert.True(install.Succeeded, $"The first release's install failed: {install.Said}");
        string second = Path.GetDirectoryName(Unpacked(Path.Combine(root, "second"), "2"))!;

        install = await Install(Path.Combine(installed, "install.sh"), home, from: second);

        Assert.True(install.Succeeded, $"The installed install.sh given --from {second} refused: {install.Said}");
        Assert.True(File.ReadAllText(Path.Combine(installed, "app", "release.txt")) == "2",
            $"The installed install.sh given --from the second release left ~/.local/opt/passenger {Held(installed)}");
        Assert.True(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(installed)!).Select(Path.GetFileName).SequenceEqual(["passenger"]),
            $"The install --from left {string.Join(", ", Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(installed)!).Select(Path.GetFileName))} in ~/.local/opt");

        File.Delete(entry);
        string before = Held(installed);
        install = await Install(Path.Combine(installed, "install.sh"), home);

        Assert.True(install.Succeeded, $"The installed install.sh run again refused: {install.Said}");
        Assert.True(File.ReadAllText(entry).Contains($"Exec=\"{installed}/passenger\" %f", StringComparison.Ordinal),
            $"The installed install.sh run again did not make the menu entry naming the install's launcher: {Held(entry)}");
        Assert.True(Held(installed) == before, $"The installed install.sh run again changed the install, to {Held(installed)} from {before}");
    }

    /// <summary>Runs an unpacked folder's install.sh for one person, <paramref name="home"/> theirs, with a folder of
    /// programs before the PATH's own where one is given, installing another unpacked folder where one is, or removing
    /// the install.</summary>
    private static async Task<ChildResult> Install(string script, string home, string? programs = null, string? from = null, bool uninstall = false)
    {
        string[] arguments = uninstall ? [script, "--uninstall"] : from is null ? [script] : [script, "--from", from];
        ProcessStartInfo start = ChildProcesses.Command("/bin/sh", arguments, home);
        start.Environment["HOME"] = home;
        start.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
        start.Environment.Remove("SUDO_USER");
        if (programs is not null)
        {
            start.Environment["PATH"] = programs + Path.PathSeparator + start.Environment["PATH"];
        }
        return await ChildProcesses.RunAsync(start, limit: TimeSpan.FromMinutes(1).Scaled(), cancel: TestContext.Current.CancellationToken);
    }

    /// <summary>A passenger/ folder in <paramref name="folder"/> as the archive unpacks, its app a script that names
    /// <paramref name="release"/>, as does app/release.txt beside it; its install.sh's path.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string Unpacked(string folder, string release)
    {
        string linux = Path.Combine(XamlTests.Repository, "tools", "linux");
        string passenger = Directory.CreateDirectory(Path.Combine(folder, "passenger")).FullName;
        string app = Directory.CreateDirectory(Path.Combine(passenger, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "Passenger"), $"#!/bin/sh\necho 'Passenger {release}'\n");
        File.SetUnixFileMode(Path.Combine(app, "Passenger"), Runnable);
        File.WriteAllText(Path.Combine(app, "release.txt"), release);
        foreach (string script in (string[])["install.sh", "passenger"])
        {
            File.Copy(Path.Combine(linux, script), Path.Combine(passenger, script));
            File.SetUnixFileMode(Path.Combine(passenger, script), Runnable);
        }
        File.Copy(Path.Combine(linux, App + ".desktop"), Path.Combine(passenger, App + ".desktop"));
        File.Copy(Path.Combine(linux, App + ".xml"), Path.Combine(passenger, App + ".xml"));
        File.WriteAllText(Path.Combine(passenger, App + ".png"), "png");
        File.WriteAllText(Path.Combine(passenger, App + ".svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        return Path.Combine(passenger, "install.sh");
    }

    /// <summary>What a path holds, said whole: a link's target, a file's mode and bytes (a short one's text, a longer one's
    /// hash), a folder's entries each so.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string Held(string path)
    {
        FileInfo file = new(path);
        if (file.LinkTarget is { } target)
        {
            return $"a link to {target}";
        }
        if (Directory.Exists(path))
        {
            return "a folder of " + string.Join(", ", Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)
                .Select(entry => $"{Path.GetFileName(entry)} ({Held(entry)})"));
        }
        if (!file.Exists)
        {
            return "nothing";
        }
        byte[] bytes = File.ReadAllBytes(path);
        return $"a file of mode {File.GetUnixFileMode(path)} " + (bytes.Length <= 100
            ? $"reading \"{System.Text.Encoding.UTF8.GetString(bytes)}\""
            : $"of {bytes.Length} bytes hashing to {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]}");
    }
}
