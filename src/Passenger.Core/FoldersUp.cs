using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Passenger.Core;

/// <summary>A walk up from a folder, as Passenger looks for a project's files over it (a global.json, a node_modules, an
/// .editorconfig, a Directory.Build.props): the folder and each above it, nearest first, while each is the person's,
/// ending before the first another user owns or others may write (as /tmp), where anyone may have put such a file. A
/// folder not there holds none and is passed. On macOS and Linux a folder is the person's where they or root own it and
/// neither every user nor a group but their own may write in it; on Windows, where they, the system, the Administrators
/// or a Windows service own it. Where a walk ended, and why, is said in the log once. Any thread.</summary>
public static class FoldersUp
{
    /// <summary>LocalSystem's and the Administrators group's SIDs, and the start of every Windows service's (TrustedInstaller's).</summary>
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string Services = "S-1-5-80-";

    /// <summary>The folders a walk was said to end at.</summary>
    private static readonly ConcurrentDictionary<string, byte> Told = new(PathNames.Comparer);

    /// <summary>Whether each group, by its id, is the person's own, asked once.</summary>
    private static readonly ConcurrentDictionary<uint, bool> OwnGroups = new();

    /// <summary>The person's own SID on Windows, read once.</summary>
    private static string? personsSid;

    /// <summary>The folder and each above it that is there, nearest first, as <see cref="Path.GetDirectoryName(string)"/>
    /// names them, up to the first that is not the person's.</summary>
    public static IEnumerable<string> From(string folder)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            (bool there, string? why) = OperatingSystem.IsWindows() ? OnWindows(at) : OnUnix(at);
            if (why is not null)
            {
                if (Told.TryAdd(at, 0))
                {
                    PassengerLog.Write($"Walks up for a project's files (global.json, node_modules, .editorconfig) stop below {at}: {why}");
                }
                yield break;
            }
            if (there)
            {
                yield return at;
            }
        }
    }

    /// <summary>Why a macOS or Linux folder is not the person's, by its owner, group and permissions; null where it is.
    /// <paramref name="ownGroup"/> answers whether a group is the person's own, <paramref name="named"/> names a user and
    /// <paramref name="groupNamed"/> a group in the words said.</summary>
    internal static string? WhyNot(uint owner, uint group, UnixFileMode mode, uint person, Func<uint, bool> ownGroup, Func<uint, string> named,
        Func<uint, string> groupNamed)
    {
        if (owner != person && owner != 0)
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

    /// <summary>Why a Windows folder is not the person's, by its owner's SID (null where it could not be read) and the
    /// person's; null where it is.</summary>
    internal static string? WhyNot(string? owner, string? person)
    {
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

    private static (bool There, string? Why) OnUnix(string folder)
    {
        if (UnixFileMetadata.HolderOf(folder) is not { } held)
        {
            return Directory.Exists(folder) ? (true, "its owner could not be read") : (false, null);
        }
        uint me = Libc.EffectiveUserId();
        return (true, WhyNot(held.Owner, held.Group, held.Mode, me,
            group => OwnGroups.GetOrAdd(group, id => IsOwnGroup(Libc.Account(me).Name, Libc.Group(id))),
            user => Libc.Account(user).Name ?? $"uid {user}",
            group => Libc.Group(group)?.Name ?? $"gid {group}"));
    }

    [SupportedOSPlatform("windows")]
    private static (bool There, string? Why) OnWindows(string folder)
    {
        string? owner;
        try
        {
            owner = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value;
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
        personsSid ??= WindowsIdentity.GetCurrent().User?.Value;
        return (true, WhyNot(owner, personsSid));
    }
}
