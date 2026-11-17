using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Passenger.Core;

/// <summary>A walk up from a folder, as Passenger looks for a project's files over it (a global.json, a node_modules, an
/// .editorconfig, a Directory.Build.props): the folder and each above it, nearest first, while each is the person's,
/// ending before the first another user owns or others may write (as /tmp), and what each holds taken only where it is the
/// person's too (<see cref="Found"/>), so nothing another user put there is read. On macOS and Linux a folder or file is
/// the person's where they own it, whatever its mode, or root does and neither every user nor a group but their own may
/// write in it; on Windows, where they, the system, the Administrators or a Windows service own it, or its volume keeps no
/// owners (FAT, exFAT). What a walk passed over, and why, is said in the log once. Any thread.</summary>
public static class FoldersUp
{
    /// <summary>LocalSystem's and the Administrators group's SIDs, and the start of every Windows service's (TrustedInstaller's).</summary>
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string Services = "S-1-5-80-";

    /// <summary>The owner a test's stand-in gives a path (<see cref="HeldByAnother"/>), on macOS and Linux and on Windows.</summary>
    private const uint AnotherUser = uint.MaxValue - 1;
    private const string AnotherAccount = "S-1-5-21-0-0-0-1001";

    /// <summary>The folders and files a walk was said to pass over.</summary>
    private static readonly ConcurrentDictionary<string, byte> Told = new(PathNames.Comparer);

    /// <summary>Whether each group, by its id, is the person's own, asked once.</summary>
    private static readonly ConcurrentDictionary<uint, bool> OwnGroups = new();

    /// <summary>Whether each Windows volume, by its root, keeps owners, asked once.</summary>
    private static readonly ConcurrentDictionary<string, bool> Secured = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths a test reads as another user's.</summary>
    private static readonly ConcurrentDictionary<string, byte> Others = new(PathNames.Comparer);

    /// <summary>The person's own SID on Windows, read once.</summary>
    private static string? personsSid;

    /// <summary>The folder and each above it that is there, nearest first, as <see cref="Path.GetDirectoryName(string)"/>
    /// names them, up to the first that is not the person's; none for no folder (a name with none before it).</summary>
    public static IEnumerable<string> From(string folder)
    {
        for (string? at = folder; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            (bool there, string? why) = WhyNotPersons(at);
            if (why is not null)
            {
                Tell(at, $"Walks up for a project's files (global.json, node_modules, .editorconfig) stop below {at}: {why}");
                yield break;
            }
            if (there)
            {
                yield return at;
            }
        }
    }

    /// <summary>What a folder of the walk holds at a path below it (<c>global.json</c>, <c>node_modules/eslint/package.json</c>),
    /// where it is there and it and each folder down to it is the person's; null where it is not there, or another user
    /// may have put it there, which the log says once.</summary>
    public static string? Found(string folder, string relative)
    {
        string top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string path = Path.GetFullPath(Path.Join(top, relative));
        if (!PathNames.IsUnder(path, top) || !Path.Exists(path))
        {
            return null;
        }
        for (string? at = path; at is not null && at.Length > top.Length; at = Path.GetDirectoryName(at))
        {
            if (WhyNotPersons(at) is (_, { } why))
            {
                Tell(at, $"A walk up for a project's files passes over {path}: {(at == path ? string.Empty : $"{at}: ")}{why}");
                return null;
            }
        }
        return path;
    }

    /// <summary>A test's stand-in: a folder or file read as another user's until disposed, whoever owns it.</summary>
    public static IDisposable HeldByAnother(string path)
    {
        Others[path] = 0;
        return new Standing(path);
    }

    /// <summary>Why a macOS or Linux folder or file is not the person's, by its owner, group and permissions; null where it
    /// is. <paramref name="ownGroup"/> answers whether a group is the person's own, <paramref name="named"/> names a user and
    /// <paramref name="groupNamed"/> a group in the words said.</summary>
    internal static string? WhyNot(uint owner, uint group, UnixFileMode mode, uint person, Func<uint, bool> ownGroup, Func<uint, string> named,
        Func<uint, string> groupNamed)
    {
        if (owner == person)
        {
            return null;
        }
        if (owner != 0)
        {
            return $"another user, {named(owner)}, owns it";
        }
        if (mode.HasFlag(UnixFileMode.OtherWrite))
        {
            return "every user may write in it";
        }
        if (mode.HasFlag(UnixFileMode.GroupWrite) && !ownGroup(group))
        {
            return $"the group {groupNamed(group)} may write in it";
        }
        return null;
    }

    /// <summary>Why a Windows folder or file is not the person's, by its owner's SID (null where it could not be read), the
    /// person's, and whether its volume keeps owners at all; null where it is.</summary>
    internal static string? WhyNot(string? owner, string? person, bool secured)
    {
        if (!secured)
        {
            return null;
        }
        if (owner is null)
        {
            return "its owner could not be read";
        }
        return string.Equals(owner, person, StringComparison.OrdinalIgnoreCase) || owner.Equals(LocalSystem, StringComparison.OrdinalIgnoreCase)
            || owner.Equals(Administrators, StringComparison.OrdinalIgnoreCase) || owner.StartsWith(Services, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"another account, {owner}, owns it";
    }

    /// <summary>Whether a group is the person's own: named as they are, and listing no other member.</summary>
    internal static bool IsOwnGroup(string? me, (string? Name, IReadOnlyList<string> Members)? group) =>
        me is not null && group is { Name: { } name } entry && name == me && entry.Members.All(m => m == me);

    private static (bool There, string? Why) WhyNotPersons(string path) => OperatingSystem.IsWindows() ? OnWindows(path) : OnUnix(path);

    /// <summary>Says once in the log why a walk passed over a folder or file.</summary>
    private static void Tell(string at, string line)
    {
        if (Told.TryAdd(at, 0))
        {
            PassengerLog.Write(line);
        }
    }

    private static (bool There, string? Why) OnUnix(string path)
    {
        uint me = Libc.EffectiveUserId();
        (uint Owner, uint Group, UnixFileMode Mode)? held = Others.ContainsKey(path) ? (AnotherUser, 0, UnixFileMode.UserRead | UnixFileMode.UserWrite)
            : UnixFileMetadata.HolderOf(path);
        if (held is not { } read)
        {
            return Path.Exists(path) ? (true, "its owner could not be read") : (false, null);
        }
        return (true, WhyNot(read.Owner, read.Group, read.Mode, me,
            group => OwnGroups.GetOrAdd(group, id => IsOwnGroup(Libc.Account(me).Name, Libc.Group(id))),
            user => user == AnotherUser ? "a test's other user" : Libc.Account(user).Name ?? $"uid {user}",
            group => Libc.Group(group)?.Name ?? $"gid {group}"));
    }

    [SupportedOSPlatform("windows")]
    private static (bool There, string? Why) OnWindows(string path)
    {
        personsSid ??= WindowsIdentity.GetCurrent().User?.Value;
        if (Others.ContainsKey(path))
        {
            return (true, WhyNot(AnotherAccount, personsSid, secured: true));
        }
        if (Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root && !Secured.GetOrAdd(root, r => Kernel32.KeepsSecurity(r) ?? true))
        {
            return (Path.Exists(path), WhyNot(null, personsSid, secured: false));
        }
        string? owner;
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        }
        catch (Exception gone) when (gone is DirectoryNotFoundException or FileNotFoundException)
        {
            return (false, null);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return (true, $"its owner could not be read ({unread.Message})");
        }
        return (true, WhyNot(owner, personsSid, secured: true));
    }

    /// <summary>A path read as another user's while it stands.</summary>
    private sealed class Standing(string path) : IDisposable
    {
        public void Dispose() => Others.TryRemove(path, out _);
    }
}
