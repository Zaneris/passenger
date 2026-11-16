using System.Diagnostics;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>How git runs in a folder the person has not trusted (<see cref="WorkspaceTrust"/>): no program the repository's
/// own configuration names, so no fsmonitor, filter driver, textconv, external diff or merge driver, credential helper,
/// ssh command, pager or editor of its, and no hook. Each such key the repository's configuration (or a file it includes)
/// sets is given empty on git's command line, which leaves a filter undone and fails a read that needs the others; the
/// person's own configuration stands, but its hooks.</summary>
internal static partial class UntrustedGit
{
    /// <summary>The configuration every git in an untrusted folder starts with, before the repository's keys: no fsmonitor,
    /// hooks looked for where there are none, and no ext:: transport.</summary>
    private static readonly string[] Always =
    [
        "-c", $"{GitConfigKeys.CoreFsmonitor}=false",
        "-c", $"{GitConfigKeys.CoreHooksPath}={(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")}",
        "-c", "protocol.ext.allow=never",
    ];

    /// <summary>The arguments that come before a git command's own in an untrusted folder: <see cref="Always"/>, and each key
    /// of the repository's whose value git would run, given empty (a filter not required). The repository's configuration
    /// is read by a git that runs nothing of it; one that cannot be read leaves <see cref="Always"/> alone.</summary>
    public static async Task<IReadOnlyList<string>> ArgumentsAsync(string git, string folder)
    {
        List<string> arguments = [.. Always];
        ProcessStartInfo start = ChildProcesses.Command(git, [.. Always, "config", "--list", "--show-scope", "--name-only", "-z"], folder);
        start.Environment[GitVariables.TerminalPrompt] = "0";
        ChildResult listed;
        try
        {
            listed = await ChildProcesses.RunAsync(start, limit: TimeSpan.FromSeconds(30));
        }
        catch (Exception unread) when (ChildFailures.Of(unread) || unread is TimeoutException)
        {
            return arguments;
        }
        string[] entries = listed.Output.Split('\0');
        for (int at = 0; at + 1 < entries.Length; at += 2)
        {
            if (entries[at] is "local" or "worktree" && Runs().IsMatch(entries[at + 1]))
            {
                string key = entries[at + 1];
                arguments.AddRange(["-c", $"{key}="]);
                if (Filter().Match(key) is { Success: true } filter)
                {
                    arguments.AddRange(["-c", $"filter.{filter.Groups[1].Value}.required=false"]);
                }
            }
        }
        return arguments;
    }

    /// <summary>A key, as `git config --name-only` writes it, whose value git runs as a program.</summary>
    [GeneratedRegex(@"^(?:filter\..+\.(?:clean|smudge|process)|diff\..+\.(?:textconv|command)|merge\..+\.driver|diff\.external|core\.(?:sshcommand|pager|editor|askpass|gitproxy|alternaterefscommand)|credential\.(?:.+\.)?helper|gpg\.(?:.+\.)?program|sequence\.editor|uploadpack\.packobjectshook)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Runs();

    [GeneratedRegex(@"^filter\.(.+)\.(?:clean|smudge|process)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Filter();
}
