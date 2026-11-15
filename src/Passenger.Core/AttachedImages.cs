using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;

namespace Passenger.Core;

/// <summary>macOS's disk images as hdiutil lists them attached, and one let go of by its disks. A volume unmounted while a
/// process holds its disk open leaves the disk attached, its mount point no handle on it any more: so an image is let go
/// of by each disk hdiutil lists of it, and counts as let go only once hdiutil lists it no more, what held a disk said
/// (lsof, as this user sees it) where it never does.</summary>
public static class AttachedImages
{
    /// <summary>One attachment of an image file: the file as hdiutil names it (as it was given, links and all), its whole
    /// disk (<c>/dev/disk5</c>), and where its volumes are mounted.</summary>
    public sealed record Attachment(string Image, string Disk, IReadOnlyList<string> Mounts);

    /// <summary>How letting an image go went: whether hdiutil lists it no more, the tries of detaching its disks that took (0
    /// where it was not attached), and what each try that failed said, with what held the disk then.</summary>
    public sealed record LetGo(bool Detached, int Tries, IReadOnlyList<string> Said);

    private static readonly TimeSpan ToolLimit = TimeSpan.FromMinutes(1);

    /// <summary>The attachments <c>hdiutil info -plist</c> lists; one whose disk is not yet made is passed over.</summary>
    public static IReadOnlyList<Attachment> Read(string plist)
    {
        using XmlReader reader = XmlReader.Create(new StringReader(plist), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        XElement? top = XDocument.Load(reader).Root?.Element("dict");
        List<Attachment> listed = [];
        foreach (XElement image in ValueOf(top, "images")?.Elements("dict") ?? [])
        {
            List<string> disks = [];
            List<string> mounts = [];
            foreach (XElement entity in ValueOf(image, "system-entities")?.Elements("dict") ?? [])
            {
                if (ValueOf(entity, "dev-entry")?.Value is { Length: > 0 } disk)
                {
                    disks.Add(disk);
                }
                if (ValueOf(entity, "mount-point")?.Value is { Length: > 0 } mount)
                {
                    mounts.Add(mount);
                }
            }
            if (ValueOf(image, "image-path")?.Value is { Length: > 0 } path && disks.Count > 0)
            {
                // The whole disk is the one its slices are named after: the shortest name.
                listed.Add(new Attachment(path, disks.OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal).First(), mounts));
            }
        }
        return listed;
    }

    /// <summary>The processes <c>lsof -F pcn</c> lists, each as "command (process N) on what it has open".</summary>
    public static IReadOnlyList<string> HoldersOf(string fields)
    {
        List<string> holders = [];
        string? process = null;
        string? command = null;
        List<string> names = [];
        void Done()
        {
            if (process is not null && names.Count > 0)
            {
                holders.Add($"{command ?? "a process"} (process {process}) on {string.Join(", ", names)}");
            }
            names.Clear();
            command = null;
        }
        foreach (string line in fields.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (line[0])
            {
                case 'p':
                    Done();
                    process = line[1..];
                    break;
                case 'c':
                    command = line[1..];
                    break;
                case 'n' when !names.Contains(line[1..]):
                    names.Add(line[1..]);
                    break;
            }
        }
        Done();
        return holders;
    }

    /// <summary>The attachments this Mac has, or null with why where hdiutil does not say.</summary>
    [SupportedOSPlatform("macos")]
    public static async Task<(IReadOnlyList<Attachment>? Listed, string Why)> ListAsync(CancellationToken cancel)
    {
        ChildResult info = await RunAsync(ChildProcesses.Command("/usr/bin/hdiutil", ["info", "-plist"]), cancel).ConfigureAwait(false);
        if (!info.Succeeded)
        {
            return (null, $"hdiutil info: {Words(info)}");
        }
        try
        {
            return (Read(info.Output), string.Empty);
        }
        catch (XmlException unread)
        {
            return (null, $"hdiutil info listed nothing readable: {unread.Message}");
        }
    }

    /// <summary>A disk's device nodes: the disk, its slices, and both raw.</summary>
    public static IReadOnlyList<string> NodesOf(string disk)
    {
        string name = Path.GetFileName(disk);
        return [.. Directory.EnumerateFileSystemEntries("/dev", $"*{name}*").Where(path => NodeOf(Path.GetFileName(path), name)).Order(StringComparer.Ordinal)];
    }

    /// <summary>What has these files or device nodes open, as this user sees it; empty where nothing does.</summary>
    [SupportedOSPlatform("macos")]
    public static async Task<IReadOnlyList<string>> HoldersAsync(IReadOnlyList<string> paths, CancellationToken cancel)
    {
        string[] there = [.. paths.Where(Path.Exists)];
        if (there.Length == 0)
        {
            return [];
        }
        // lsof exits 1 where any one is open in no process: the others are still listed.
        ChildResult open = await RunAsync(ChildProcesses.Command("/usr/sbin/lsof", ["-F", "pcn", .. there]), cancel, answers: [1]).ConfigureAwait(false);
        return HoldersOf(open.Output);
    }

    /// <summary>Holders as a sentence's end says them: "held by …", or that nothing this user runs has them open.</summary>
    public static string Held(IReadOnlyList<string> holders) =>
        holders.Count == 0 ? "nothing this user runs had it open" : $"held by {string.Join("; ", holders)}";

    /// <summary>Lets go of every attachment <paramref name="ofImage"/> takes, detaching each disk by
    /// <paramref name="detach"/> (given the disk and the try's number from 1), again after each of <paramref name="pauses"/>
    /// while hdiutil still lists one; <paramref name="trying"/> runs as each try begins, given those attachments.</summary>
    [SupportedOSPlatform("macos")]
    public static async Task<LetGo> DetachAsync(Func<Attachment, bool> ofImage, Func<string, int, ProcessStartInfo> detach, IReadOnlyList<TimeSpan> pauses,
        Func<IReadOnlyList<Attachment>, int, Task>? trying = null, CancellationToken cancel = default)
    {
        List<string> said = [];
        for (int attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                if (attempt - 1 > pauses.Count)
                {
                    return new LetGo(false, attempt - 1, said);
                }
                await Task.Delay(pauses[attempt - 2], cancel).ConfigureAwait(false);
            }
            (IReadOnlyList<Attachment>? listed, string why) = await ListAsync(cancel).ConfigureAwait(false);
            if (listed is null)
            {
                Say(said, why);
                return new LetGo(false, attempt - 1, said);
            }
            Attachment[] ours = [.. listed.Where(ofImage)];
            if (ours.Length == 0)
            {
                return new LetGo(true, attempt - 1, said);
            }
            if (trying is not null)
            {
                await trying(ours, attempt).ConfigureAwait(false);
            }
            foreach (string disk in ours.Select(a => a.Disk).Distinct(StringComparer.Ordinal))
            {
                ChildResult detached = await RunAsync(detach(disk, attempt), cancel).ConfigureAwait(false);
                if (!detached.Succeeded)
                {
                    Say(said, $"{Words(detached)}; {Held(await HoldersAsync(NodesOf(disk), cancel).ConfigureAwait(false))}");
                }
            }
        }
    }

    /// <summary>What a tool said, but hdiutil's warnings that a command is deprecated; its exit code where that is nothing.</summary>
    public static string Words(ChildResult result)
    {
        string[] lines = [.. $"{result.Error}\n{result.Output}".Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("hdiutil: WARNING", StringComparison.Ordinal))];
        return lines.Length == 0 ? ExitCodes.Said(result.ExitCode) : string.Join(' ', lines);
    }

    /// <summary>Whether a tool's words end saying the system refused it access to a file or folder, "Permission denied"
    /// or "Operation not permitted", as hdiutil's and diskutil's end.</summary>
    public static bool RefusesAccess(string said)
    {
        string words = said.TrimEnd().TrimEnd('.');
        return words.EndsWith("Permission denied", StringComparison.Ordinal) || words.EndsWith("Operation not permitted", StringComparison.Ordinal);
    }

    /// <summary>Whether a name in /dev is one of a disk's nodes: the disk, its slices (disk5s1), or either raw (rdisk5).</summary>
    internal static bool NodeOf(string entry, string disk)
    {
        string bare = entry.StartsWith('r') ? entry[1..] : entry;
        return bare == disk || (bare.StartsWith($"{disk}s", StringComparison.Ordinal) && bare.Length > disk.Length + 1
            && !bare.AsSpan(disk.Length).ContainsAnyExcept("s0123456789"));
    }

    private static void Say(List<string> said, string words)
    {
        if (!said.Contains(words, StringComparer.Ordinal))
        {
            said.Add(words);
        }
    }

    /// <summary>A tool run where one that could not start or finish is an exit code of -1 saying why.</summary>
    private static async Task<ChildResult> RunAsync(ProcessStartInfo start, CancellationToken cancel, IReadOnlyCollection<int>? answers = null)
    {
        try
        {
            return await ChildProcesses.RunAsync(start, limit: Waits.Of(ToolLimit), cancel: cancel, answers: answers).ConfigureAwait(false);
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            return new ChildResult(-1, string.Empty, error.Message);
        }
    }

    /// <summary>The element a plist dict gives a key: the one after the key's.</summary>
    private static XElement? ValueOf(XElement? dict, string key) =>
        dict?.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();
}
