using Passenger.Core;

namespace Passenger.App;

/// <summary><c>passenger-ide</c>, Passenger's command in a terminal on every system, which runs Passenger with a folder, a
/// solution or a file and its flags, a Passenger running already taking it. Linux's install.sh links it (tools/linux).
/// Windows' installer puts the folder of the program this build carries (bin\passenger-ide.exe, src/Passenger.Launcher)
/// on the person's own Path as it installs and updates, and takes it off as it uninstalls (<see cref="Installation"/>).
/// On macOS, whose disk image installs nothing, the person adds it from the Tools menu and removes it there: a link in
/// ~/.local/bin, where Linux's is too and no administrator is asked, to the script the app's bundle carries, the folder
/// put on their shell's PATH where it is not.</summary>
public static class PassengerCommand
{
    /// <summary>The command's name, never <c>passenger</c>: Phusion Passenger's is.</summary>
    public const string Name = "passenger-ide";

    /// <summary>A test's app bundle in place of the one this Passenger runs from.</summary>
    public static string? BundleStandIn { get; set; }

    /// <summary>The folder the command is carried in: bin beside the app, or its bundle's Resources/bin.</summary>
    public static string Carried => CarriedFiles.Folder("bin");

    /// <summary>Puts the folder of the command carried at the end of the person's own Path on Windows: false where it was
    /// there.</summary>
    public static bool PutOnPath() => UserEnvironment.Put(Carried, first: false);

    /// <summary>Takes the folder of the command carried off the person's own Path on Windows: false where it was not there.</summary>
    public static bool TakeOffPath() => UserEnvironment.Take(Carried);

    /// <summary>The folder the command is linked in on macOS: ~/.local/bin, of a test's home in a test.</summary>
    public static string Folder => Path.Combine(ShellPath.Home, ".local", "bin");

    /// <summary>The command's link on macOS.</summary>
    public static string Link => Path.Combine(Folder, Name);

    /// <summary>The command on macOS as it stands now, off the window's thread: what the link leads to, and what puts its
    /// folder on the shell's PATH where nothing does, as the PATH stands, or, <paramref name="shellRead"/>, once the login
    /// shell's environment is read.</summary>
    public static MacCommand Read(bool shellRead = false)
    {
        if (shellRead)
        {
            ShellEnvironment.Ready();
        }
        string? bundle = BundleStandIn ?? MacBundles.Of(Environment.ProcessPath);
        string? launcher = bundle is null ? null : PathNames.Canonical(Path.Combine(bundle, "Contents", "Resources", "bin", Name));
        string? unlinkable = (bundle is null ? Refusals.CommandOfNoBundle
            : bundle.Contains("/AppTranslocation/", StringComparison.Ordinal) ? Refusals.CommandOfTranslocated(bundle)
            : bundle.StartsWith("/Volumes/", StringComparison.Ordinal) && Libc.Access(Path.GetDirectoryName(bundle)!, Libc.MayWrite) != 0 ? Refusals.CommandOfDiskImage(bundle)
            : !File.Exists(launcher) ? Refusals.CommandNotCarried(launcher!)
            : null)?.Shown;
        return new MacCommand(Link, launcher, unlinkable, Held(launcher), ShellPath.ForCommands(Folder, Name));
    }

    /// <summary>What is at the command's place: nothing, a link to this Passenger's launcher or another Passenger's (an app
    /// moved, or another copy), or anything else, which is the person's own.</summary>
    private static CommandHeld Held(string? launcher)
    {
        FileInfo link = new(Link);
        if (link.LinkTarget is { } target)
        {
            string led = Path.GetFullPath(target, Folder);
            return launcher is not null && PathNames.Same(PathNames.Canonical(led), launcher) ? CommandHeld.This
                : IsLauncher(led) ? CommandHeld.Passengers
                : CommandHeld.Others;
        }
        return link.Exists || Directory.Exists(Link) ? CommandHeld.Others : CommandHeld.None;
    }

    /// <summary>Whether a path is where an app's bundle carries Passenger's command, whether it is there now or not.</summary>
    private static bool IsLauncher(string path) =>
        Path.GetFileName(path) == Name && Path.GetDirectoryName(path) is { } bin && Path.GetFileName(bin) == "bin"
        && Path.GetDirectoryName(bin) is { } resources && Path.GetFileName(resources) == "Resources"
        && Path.GetDirectoryName(resources) is { } contents && Path.GetFileName(contents) == "Contents"
        && Path.GetDirectoryName(contents) is { } bundle && MacBundles.Named(bundle);

    /// <summary>Links the command to this Passenger's launcher, in place of another Passenger's link, then puts its folder on
    /// the shell's PATH where nothing does; read again first, so what came at its place since is left as it is. The command
    /// as it stands after, or an <see cref="IOException"/> saying why nothing was linked.</summary>
    public static MacCommand Add()
    {
        MacCommand now = Read(shellRead: true);
        if (now.Unlinkable is { } why)
        {
            throw new IOException(why);
        }
        if (now.Held == CommandHeld.Others)
        {
            throw new IOException(Refusals.CommandOthers(now.Link).English);
        }
        if (now.Held != CommandHeld.This)
        {
            Directory.CreateDirectory(Folder);
            string made = $"{Link}.passenger-{Environment.ProcessId}";
            File.Delete(made);
            File.CreateSymbolicLink(made, now.Launcher!);
            if (now.Held == CommandHeld.Passengers)
            {
                AtomicFile.Move(made, Link);
            }
            else if (!AtomicFile.MoveNew(made, Link))
            {
                throw new IOException(Refusals.CommandOthers(now.Link).English);
            }
        }
        now.OnPath?.Make();
        return Read();
    }

    /// <summary>Removes the command's link where it leads to a Passenger, leaving its folder on the shell's PATH, where other
    /// programs' commands may be.</summary>
    public static MacCommand Remove()
    {
        MacCommand now = Read();
        if (now.Held is CommandHeld.This or CommandHeld.Passengers)
        {
            File.Delete(now.Link);
        }
        return Read();
    }
}

/// <summary>What is at the command's place on macOS.</summary>
public enum CommandHeld
{
    None,
    This,
    Passengers,
    Others,
}

/// <summary>The command on macOS as read: its link, this Passenger's launcher (null for one in no bundle) and why none is
/// linked to it, what is at the link's place, and the change putting its folder on the shell's PATH (null where it is).</summary>
public sealed record MacCommand(string Link, string? Launcher, string? Unlinkable, CommandHeld Held, ShellPath? OnPath);
