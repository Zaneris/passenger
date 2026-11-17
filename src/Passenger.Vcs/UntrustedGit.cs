using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>How git runs in a folder the person has not trusted (<see cref="WorkspaceTrust"/>): no program the repository
/// names. Each key whose value git runs that its configuration (or a file it includes) sets, or a submodule's checked out
/// in it, is given empty through git's environment, which keeps a key whole where -c splits it at its first '=': a filter
/// is left undone, and a read that needs one of the others fails. No fsmonitor, hook or maintenance runs, no submodule is
/// checked out, and no remote is reached, so nothing a remote's settings name runs (git takes the first upload-pack given,
/// the repository's). The person's own configuration stands, but its hooks.</summary>
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

    /// <summary>Fits a git to run in an untrusted folder: the settings it is given through its environment, after any the
    /// environment gives already, and no protocol to reach a remote by. Null once fitted, else why git is not run: a
    /// configuration could not be read, or this git takes none from its environment (one before 2.31).</summary>
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
                return $"git was not run: the folder is not trusted, and it has more than {MostSubmodules} submodules checked out, whose configurations are each read first";
            }
            ChildResult listed;
            ChildResult staged;
            try
            {
                listed = await ReadAsync(git, repository, ["config", "--list", "--show-scope", "--name-only", "-z"]);
                // A git that cannot read the index (or finds no repository) compares no submodule with it, so enters none.
                staged = listed.Succeeded ? await ReadAsync(git, repository, ["ls-files", "--stage", "-z", ":/"]) : listed;
            }
            catch (Exception unread) when (ChildFailures.Of(unread))
            {
                return Unread(repository, unread.Message);
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
        return null;

        static string Unread(string repository, string why) =>
            $"git was not run: the folder is not trusted, and the configuration of {repository} could not be read: {why}";
    }

    /// <summary>What a git that runs nothing of the repository's writes in it: given <see cref="Always"/> and reaching no
    /// remote, every pathspec read as git reads one unless told otherwise (:/, the tree's top).</summary>
    private static async Task<ChildResult> ReadAsync(string git, string repository, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = ChildProcesses.Command(git, arguments, repository);
        Give(start.Environment, Always);
        start.Environment[GitVariables.AllowProtocol] = string.Empty;
        start.Environment[GitVariables.TerminalPrompt] = "0";
        start.Environment.Remove(GitVariables.LiteralPathspecs);
        // 128: no repository is there, or its index cannot be read.
        return await ChildProcesses.RunAsync(start, limit: Limit, answers: [128]);
    }

    /// <summary>The settings a configuration's listing (`git config --list --show-scope --name-only -z`, given
    /// <see cref="Always"/> through the environment) has given empty: each key of the repository's own (local, worktree)
    /// whose value git runs, a filter's made not required. Null where it lists <see cref="Always"/> nowhere as given on the
    /// command: a git that takes no setting from its environment.</summary>
    internal static IReadOnlyList<(string Key, string Value)>? Overrides(string listed)
    {
        string[] entries = listed.Split('\0');
        HashSet<string> commanded = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> emptied = new(StringComparer.Ordinal);
        List<(string Key, string Value)> overrides = [];
        for (int at = 0; at + 1 < entries.Length; at += 2)
        {
            string key = entries[at + 1];
            if (entries[at] is "command")
            {
                commanded.Add(key);
            }
            else if (entries[at] is "local" or "worktree" && Runs().IsMatch(key) && emptied.Add(key))
            {
                overrides.Add((key, string.Empty));
                if (Filter().Match(key) is { Success: true } filter && emptied.Add($"filter.{filter.Groups[1].Value}.required"))
                {
                    overrides.Add(($"filter.{filter.Groups[1].Value}.required", "false"));
                }
            }
        }
        return Always.All(setting => commanded.Contains(setting.Key)) ? overrides : null;
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
    /// name a key; a count git could not read is replaced, as git would refuse it.</summary>
    internal static void Give(IDictionary<string, string?> environment, IReadOnlyList<(string Key, string Value)> settings)
    {
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

    /// <summary>A key, as `git config --name-only` writes it, whose value git runs as a program (git-config's own list), but
    /// those of a remote and its transport, which no git here reaches.</summary>
    [GeneratedRegex(@"^(?:filter\..+\.(?:clean|smudge|process)|diff\..+\.(?:textconv|command)|diff\.external|merge\..+\.driver|(?:diff|merge)tool\..+\.(?:cmd|path)"
        + @"|core\.(?:sshcommand|pager|editor|askpass|gitproxy|alternaterefscommand)|pager\..+|sequence\.editor|interactive\.difffilter|credential\.(?:.+\.)?helper"
        + @"|gpg\.(?:.+\.)?program|gpg\.ssh\.defaultkeycommand|trailer\..+\.(?:cmd|command)|alias\..+|uploadpack\.packobjectshook|submodule\..+\.update"
        + @"|(?:browser|man)\..+\.(?:cmd|path)|guitool\..+\.cmd|(?:web|help|instaweb)\.browser|instaweb\.httpd|imap\.tunnel"
        + @"|sendemail\.(?:.+\.)?(?:smtpserver|tocmd|cccmd|headercmd))$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Runs();

    [GeneratedRegex(@"^filter\.(.+)\.(?:clean|smudge|process)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Filter();
}
