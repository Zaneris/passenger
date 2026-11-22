using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>How git runs in a folder the person has not trusted (<see cref="WorkspaceTrust"/>): on the folder's files alone,
/// and running no program the repository names. Its working tree is the folder its .git is in, whatever core.worktree
/// says. Each key whose value git runs that its configuration (or a file it includes) sets, or a submodule's checked out
/// in it, is given empty through git's environment, which keeps a key whole where -c splits it at its first '='. No
/// fsmonitor, hook or maintenance runs, no submodule is checked out, and no remote is reached, so nothing a remote's
/// settings name runs (git takes the first upload-pack given, the repository's). The person's own configuration stands,
/// but its hooks and filters: a filter of theirs (git-lfs's) would run on the repository's files with its settings. So
/// a file git stores through a filter is committed, stashed or written out by git only once the folder is trusted
/// (<see cref="FilteredAsync"/>).</summary>
internal static partial class UntrustedGit
{
    /// <summary>The configuration every git in an untrusted folder runs with: no fsmonitor; hooks looked for where there are
    /// none; no submodule checked out, which would take a configuration from .git/modules that no listing read; and no
    /// maintenance after a commit or a merge, whose gc runs each recentObjectsHook, a list no value given empty clears.</summary>
    private static readonly (string Key, string Value)[] Always =
    [
        (GitConfigKeys.CoreFsmonitor, "false"),
        (GitConfigKeys.CoreHooksPath, OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
        (GitConfigKeys.SubmoduleRecurse, "false"),
        (GitConfigKeys.MaintenanceAuto, "false"),
        (GitConfigKeys.GcAuto, "0"),
    ];

    /// <summary>The most submodules checked out in an untrusted folder whose configuration is read before each git there, as
    /// the folder's is: two gits more for every one.</summary>
    private const int MostSubmodules = 64;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Fits a git to run in an untrusted folder: its working tree the folder its .git is in, the settings it is
    /// given through its environment, after any the environment gives already, and no protocol to reach a remote by. Null
    /// once fitted, else why git is not run: a configuration could not be read, a submodule's working tree is named
    /// elsewhere (where no git it starts can be told otherwise), or this git takes no settings from its environment (one
    /// before 2.31).</summary>
    public static async Task<string?> FitAsync(ProcessStartInfo start, string git, string folder)
    {
        List<(string Key, string Value)> given = [.. Always];
        HashSet<string> read = new(PathNames.Comparer);
        Queue<string> repositories = new([folder]);
        while (repositories.TryDequeue(out string? repository))
        {
            if (!read.Add(PathNames.Canonical(repository)))
            {
                continue;
            }
            if (read.Count > MostSubmodules + 1)
            {
                return $"git was not run: the folder is not trusted, and it has more than {Numbers.Said(MostSubmodules)} submodules checked out, whose configurations are each read first";
            }
            ChildResult listed;
            ChildResult staged;
            string? named = null;
            try
            {
                listed = await ReadAsync(git, repository, OwnTop(repository), ["config", "--list", "--show-scope", "--name-only", "-z"]);
                // A git that cannot read the index (or finds no repository) compares no submodule with it, so enters none.
                staged = listed.Succeeded ? await ReadAsync(git, repository, OwnTop(repository), ["ls-files", "--stage", "-z", ":/"]) : listed;
                named = read.Count > 1 ? await WorkTreeNamedAsync(git, repository) : null;
            }
            catch (Exception unread) when (ChildFailures.Of(unread))
            {
                return Unread(repository, unread.Message);
            }
            if (named is not null)
            {
                return $"git was not run: the folder is not trusted, and its submodule {repository} names its working tree at {named}";
            }
            if (!listed.Succeeded)
            {
                return Unread(repository, ChildResult.FirstLineOf(listed.Error));
            }
            if (Overrides(listed.Output) is not { } own)
            {
                return "git was not run: the folder is not trusted, and this git (one before 2.31) takes no settings from its environment, which keep the programs the repository names from running";
            }
            given.AddRange(own);
            if (staged.Succeeded)
            {
                foreach (string linked in Gitlinks(staged.Output))
                {
                    string at = Path.GetFullPath(linked, repository);
                    if (Path.Exists(Path.Combine(at, ".git")))
                    {
                        repositories.Enqueue(at);
                    }
                }
            }
        }
        Give(start.Environment, given);
        start.Environment[GitVariables.AllowProtocol] = string.Empty;
        if (OwnTop(folder) is { } top)
        {
            start.Environment[GitVariables.WorkTree] = top;
        }
        return null;

        static string Unread(string repository, string why) =>
            $"git was not run: the folder is not trusted, and the configuration of {repository} could not be read: {why}";
    }

    /// <summary>The working tree a repository's own settings name (core.worktree) where it is not the folder its .git is
    /// in, as git names it; null where they name none, or that folder, or git cannot say.</summary>
    public static async Task<string?> WorkTreeNamedAsync(string git, string folder)
    {
        if (OwnTop(folder) is not { } own)
        {
            return null;
        }
        ChildResult top = await ReadAsync(git, folder, null, ["rev-parse", "--show-toplevel"]);
        string named = top.Output.TrimEnd('\n', '\r');
        return top.Succeeded && named.Length > 0 && !PathNames.Same(PathNames.Canonical(named), PathNames.Canonical(own)) ? named : null;
    }

    /// <summary>The folder a repository's .git (its folder, or a file naming one) is in, looked for from
    /// <paramref name="folder"/> up as git looks: the working tree git has of its own; null where there is none, or a bare
    /// repository is found first, which has none.</summary>
    internal static string? OwnTop(string folder)
    {
        for (string? at = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); at is not null; at = Path.GetDirectoryName(at))
        {
            if (Path.Exists(Path.Combine(at, ".git")))
            {
                return at;
            }
            if (File.Exists(Path.Combine(at, "HEAD")) && Directory.Exists(Path.Combine(at, "objects")) && Directory.Exists(Path.Combine(at, "refs")))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The drivers of the filters a configuration gives a program (clean, smudge or process), none of which runs
    /// in a folder not trusted: a file one of them stores goes in, and is written out, as git keeps it, never as its
    /// filter would (git-lfs's pointer in place of the file). Null where git could not say.</summary>
    public static async Task<IReadOnlySet<string>?> DriversAsync(string git, string repository)
    {
        try
        {
            ChildResult listed = await ReadAsync(git, repository, OwnTop(repository), ["config", "--list", "--name-only", "-z"]);
            if (!listed.Succeeded)
            {
                return null;
            }
            HashSet<string> drivers = new(StringComparer.Ordinal);
            foreach (string key in listed.Output.Split('\0'))
            {
                if (Filter().Match(key) is { Success: true } filter)
                {
                    drivers.Add(filter.Groups[1].Value);
                }
            }
            return drivers;
        }
        catch (Exception unread) when (ChildFailures.Of(unread))
        {
            return null;
        }
    }

    /// <summary>Which of <paramref name="names"/> (from the working tree's top, as git takes them on its input) git stores
    /// through a filter of <paramref name="drivers"/> (<see cref="DriversAsync"/>), by their place in the list, each with
    /// its filter's name, as git reads the attributes in the working tree, or, given <paramref name="source"/>, in that
    /// revision's tree (a check-attr from git 2.40). Null where git could not say.</summary>
    public static async Task<IReadOnlyDictionary<int, string>?> FilteredAsync(string git, string repository, IReadOnlyList<string> names, IReadOnlySet<string> drivers,
        string? source = null)
    {
        Dictionary<int, string> filtered = [];
        if (names.Count == 0 || drivers.Count == 0)
        {
            return filtered;
        }
        StringBuilder input = new();
        foreach (string name in names)
        {
            input.Append(name).Append('\0');
        }
        try
        {
            ChildResult attributes = await ReadAsync(git, repository, OwnTop(repository),
                ["check-attr", .. source is null ? Array.Empty<string>() : [$"--source={source}"], "-z", "--stdin", "filter"], input.ToString());
            // Each name's path, attribute and value, in the order given.
            string[] fields = attributes.Output.Split('\0');
            if (!attributes.Succeeded || fields.Length < (names.Count * 3) + 1)
            {
                return null;
            }
            for (int at = 0; at < names.Count; at++)
            {
                // Git's words for no filter set, -filter, and filter alone, which names no driver.
                if (fields[(at * 3) + 2] is not ("unspecified" or "unset" or "set") and { } driver && drivers.Contains(driver))
                {
                    filtered[at] = driver;
                }
            }
            return filtered;
        }
        catch (Exception unread) when (ChildFailures.Of(unread))
        {
            return null;
        }
    }

    /// <summary>Why a file is not stored in a folder not trusted: git would store it through its filter, which runs only
    /// once the folder is trusted.</summary>
    public static string Unfiltered(string name, string filter) =>
        $"The folder is not trusted: {name} is stored through Git's {filter} filter, which runs only once the folder is trusted";

    /// <summary>What a git that runs nothing of the repository's writes in it: given <see cref="Always"/>, its working tree
    /// <paramref name="workTree"/> where one is given, reaching no remote, every pathspec read as git reads one unless told
    /// otherwise (:/, the tree's top), <paramref name="input"/> written to it.</summary>
    private static async Task<ChildResult> ReadAsync(string git, string repository, string? workTree, IReadOnlyList<string> arguments, string? input = null)
    {
        ProcessStartInfo start = ChildProcesses.Command(git, arguments, repository);
        Give(start.Environment, Always);
        if (workTree is not null)
        {
            start.Environment[GitVariables.WorkTree] = workTree;
        }
        start.Environment[GitVariables.AllowProtocol] = string.Empty;
        start.Environment[GitVariables.TerminalPrompt] = "0";
        start.Environment.Remove(GitVariables.LiteralPathspecs);
        // 128: no repository is there, or its index cannot be read.
        return await ChildProcesses.RunAsync(start, input, limit: Limit, answers: [128]);
    }

    /// <summary>The settings a configuration's listing (`git config --list --show-scope --name-only -z`, given
    /// <see cref="Always"/> through the environment) has given: each key of the repository's own (local, worktree) whose
    /// value git runs given empty; and wherever set, each filter's programs given empty and the filter made not required,
    /// and each hook its configuration declares turned off. Null where it lists <see cref="Always"/> nowhere as given on
    /// the command: a git that takes no setting from its environment.</summary>
    internal static IReadOnlyList<(string Key, string Value)>? Overrides(string listed)
    {
        string[] entries = listed.Split('\0');
        HashSet<string> commanded = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> given = new(StringComparer.Ordinal);
        List<(string Key, string Value)> overrides = [];
        for (int at = 0; at + 1 < entries.Length; at += 2)
        {
            string key = entries[at + 1];
            if (entries[at] is "command")
            {
                commanded.Add(key);
            }
            if (Filter().Match(key) is { Success: true } filter)
            {
                Set(key, string.Empty);
                Set($"filter.{filter.Groups[1].Value}.required", "false");
            }
            else if (Hook().Match(key) is { Success: true } hook)
            {
                // Turned off by its name as listed, which git tells apart by case, whichever of its keys is set.
                Set($"{hook.Groups[1].Value}.enabled", "false");
            }
            else if (entries[at] is "local" or "worktree" && Runs().IsMatch(key))
            {
                Set(key, string.Empty);
            }
        }
        return Always.All(setting => commanded.Contains(setting.Key)) ? overrides : null;

        void Set(string key, string value)
        {
            if (given.Add(key))
            {
                overrides.Add((key, value));
            }
        }
    }

    /// <summary>The paths of the submodules an index holds, as `git ls-files --stage -z` writes its entries (mode, object,
    /// stage, a tab and the path): those of mode 160000, each once.</summary>
    internal static IReadOnlyList<string> Gitlinks(string staged)
    {
        List<string> paths = [];
        foreach (string entry in staged.Split('\0'))
        {
            int tab = entry.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && entry.StartsWith("160000 ", StringComparison.Ordinal) && entry[(tab + 1)..] is { Length: > 0 } path && !paths.Contains(path))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary>Settings given to git through its environment after those it gives already, which these outrank where both
    /// name a key; a count git could not read is replaced, as git would refuse it. Taken away: an inherited `git -c`'s
    /// settings, which git reads after these, and the file `git config` would read in place of every scope's, where a
    /// listing would miss these and the repository's own, which every other command reads.</summary>
    internal static void Give(IDictionary<string, string?> environment, IReadOnlyList<(string Key, string Value)> settings)
    {
        environment.Remove(GitVariables.ConfigParameters);
        environment.Remove(GitVariables.Config);
        int first = environment.TryGetValue(GitVariables.ConfigCount, out string? count) && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int given)
            ? given
            : 0;
        for (int at = 0; at < settings.Count; at++)
        {
            environment[GitVariables.ConfigKey(first + at)] = settings[at].Key;
            environment[GitVariables.ConfigValue(first + at)] = settings[at].Value;
        }
        environment[GitVariables.ConfigCount] = (first + settings.Count).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A key, as `git config --name-only` writes it, whose value git runs as a program (git-config's own list, and
    /// those of git-archive, git-send-email and git-svn), or git-lfs does (its extensions and custom transfers, whose
    /// arguments a shell reads), but those of a remote and its transport, which no git here reaches.</summary>
    [GeneratedRegex(@"^(?:diff\..+\.(?:textconv|command)|diff\.external|merge\..+\.driver|(?:diff|merge)tool\..+\.(?:cmd|path)"
        + @"|core\.(?:sshcommand|pager|editor|askpass|gitproxy|alternaterefscommand)|pager\..+|sequence\.editor|interactive\.difffilter|credential\.(?:.+\.)?helper"
        + @"|gpg\.(?:.+\.)?program|gpg\.ssh\.defaultkeycommand|trailer\..+\.(?:cmd|command)|alias\..+|uploadpack\.packobjectshook|submodule\..+\.update"
        + @"|(?:browser|man)\..+\.(?:cmd|path)|guitool\..+\.cmd|(?:web|help|instaweb)\.browser|instaweb\.httpd|imap\.tunnel|tar\..+\.command|svn\.authorsprog"
        + @"|sendemail\.(?:.+\.)?(?:smtpserver|sendmailcmd|tocmd|cccmd|headercmd)"
        + @"|lfs\.extension\..+\.(?:clean|smudge)|lfs\.customtransfer\..+\.(?:path|args)|lfs\.(?:.+\.)?standalonetransferagent)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Runs();

    /// <summary>A key of a filter's that names a program, its driver's name the first group.</summary>
    [GeneratedRegex(@"^filter\.(.+)\.(?:clean|smudge|process)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Filter();

    /// <summary>A key of a hook's, the section and the hook's name before its last part the first group: hook.NAME.command,
    /// or hook.command for the hook of no name.</summary>
    [GeneratedRegex(@"^(hook(?:\..*)?)\.[^.]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Hook();
}
