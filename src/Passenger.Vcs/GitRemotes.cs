namespace Passenger.Vcs;

/// <summary>A remote: its name and the URL git fetches it from, with the one it pushes to where git's config gives that
/// apart (remote.NAME.pushurl).</summary>
public sealed record RemoteInfo(string Name, string Url, string? PushUrl = null);

/// <summary>A remote to add (<paramref name="Was"/> null) or to change: the name it had, the name and the URL it has from
/// then on, and for a new one whether its branches are fetched at once.</summary>
public sealed record RemotePlan(string? Was, string Name, string Url, bool Fetch = false);

/// <summary>A local branch to push: the remote and the branch there it goes to, and whether the branch tracks that one
/// from then on.</summary>
public sealed record PushPlan(string Branch, string Remote, string RemoteBranch, bool Track);

/// <summary>What a push would send, as this repository last fetched the remote: the commits the remote's branch lacks
/// (those no branch of the remote has where it has none of the name yet), whether it has one of the name, and how many
/// commits it has that the branch lacks, which git refuses to push over.</summary>
public sealed record PushPreview(IReadOnlyList<CommitInfo> Sends, bool RemoteHasBranch, int RemoteAhead);

public sealed partial class Git
{
    /// <summary>The remotes in git's order, each with its URLs as git's config writes them (never rewritten by an
    /// insteadOf, as <c>git remote -v</c> shows them); one whose config names no URL has an empty one.</summary>
    public async Task<IReadOnlyList<RemoteInfo>> RemoteUrlsAsync()
    {
        IReadOnlyList<string> names = await RemotesAsync();
        GitResult urls = await RunAsync(Root, ["config", "-z", "--get-regexp", @"^remote\..*\.(url|pushurl)$"], answers: [1]);
        return Remotes.Read(names, urls.Succeeded ? urls.Output : string.Empty);
    }

    /// <summary>A remote added under a name, fetched from <paramref name="url"/>; nothing is fetched yet.</summary>
    public Task<GitResult> AddRemoteAsync(string name, string url) => RunAsync(Root, ["remote", "add", "--end-of-options", name, url]);

    /// <summary>A remote renamed, its remote-tracking branches and the branches tracking them following it.</summary>
    public Task<GitResult> RenameRemoteAsync(string from, string to) => RunAsync(Root, ["remote", "rename", "--end-of-options", from, to]);

    /// <summary>The URL a remote is fetched from (and pushed to, where it has no push URL of its own) set.</summary>
    public Task<GitResult> SetRemoteUrlAsync(string name, string url) => RunAsync(Root, ["remote", "set-url", "--end-of-options", name, url]);

    /// <summary>A remote removed, with its remote-tracking branches and its branches' upstreams on it.</summary>
    public Task<GitResult> RemoveRemoteAsync(string name) => RunAsync(Root, ["remote", "remove", "--end-of-options", name]);

    /// <summary>One remote's branches fetched, those it deleted let go of here. Canceled, as <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> FetchRemoteAsync(string remote, CancellationToken cancel = default, GitAsker? asker = null) =>
        RunAsync(Root, ["fetch", "--prune", "--end-of-options", remote], cancel: cancel, asker: asker);

    /// <summary>A local branch pushed as planned, whatever it tracks: to the remote's branch named, tracked from then on
    /// only where the plan says. Canceled, as <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> PushToAsync(PushPlan plan, CancellationToken cancel = default, GitAsker? asker = null) =>
        RunAsync(Root, ["push", .. plan.Track ? new[] { "-u" } : [], "--end-of-options", plan.Remote, $"refs/heads/{plan.Branch}:refs/heads/{plan.RemoteBranch}"], cancel: cancel, asker: asker);

    /// <summary>What pushing a local branch to a remote's branch would send, as the remote was last fetched.</summary>
    public async Task<PushPreview> PushPreviewAsync(string branch, string remote, string remoteBranch, int most = 50)
    {
        string local = $"refs/heads/{branch}";
        string there = $"refs/remotes/{remote}/{remoteBranch}";
        if (await CommitOfAsync(there) is null)
        {
            // Nothing of the name there yet: what no branch of the remote has goes.
            using GitOutput fresh = await OutputOfAsync(Root, ["log", "--topo-order", $"--max-count={most}", CommitFormat, local, "--not", $"--remotes={remote}", "--"]);
            return new PushPreview(fresh.Succeeded ? Commits(fresh.Bytes) : [], false, 0);
        }
        IReadOnlyList<CommitInfo> sends = await LogAsync(most, [$"{there}..{local}"]);
        GitResult ahead = await RunAsync(Root, ["rev-list", "--count", "--end-of-options", $"{local}..{there}", "--"], answers: [NotThere]);
        return new PushPreview(sends, true, ahead.Succeeded && int.TryParse(ahead.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture, out int count) ? count : 0);
    }
}

/// <summary>What git answers of remotes, read apart from any git.</summary>
public static class Remotes
{
    /// <summary>The remotes of <paramref name="names"/> (git remote's lines, in its order) with their URLs from what
    /// <c>git config -z --get-regexp</c> wrote of remote.*.url and remote.*.pushurl, the first of each kept as git
    /// fetches and pushes by it. A remote's name may hold dots, so its key is "remote." and the name, then the key's last
    /// part.</summary>
    public static IReadOnlyList<RemoteInfo> Read(IReadOnlyList<string> names, string config)
    {
        Dictionary<string, string> fetch = new(StringComparer.Ordinal);
        Dictionary<string, string> push = new(StringComparer.Ordinal);
        foreach (string entry in config.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = entry.IndexOf('\n', StringComparison.Ordinal);
            string key = split < 0 ? entry : entry[..split];
            string value = split < 0 ? string.Empty : entry[(split + 1)..];
            if (key.EndsWith(".pushurl", StringComparison.Ordinal) && key.Length > "remote..pushurl".Length)
            {
                push.TryAdd(key["remote.".Length..^".pushurl".Length], value);
            }
            else if (key.EndsWith(".url", StringComparison.Ordinal) && key.Length > "remote..url".Length)
            {
                fetch.TryAdd(key["remote.".Length..^".url".Length], value);
            }
        }
        return [.. names.Select(name => new RemoteInfo(name, fetch.GetValueOrDefault(name, string.Empty), push.GetValueOrDefault(name)))];
    }

    /// <summary>Why git takes no remote of this name (git's own rule: refs/remotes/NAME/x a ref it takes, a leading dash
    /// refused as an option would be), or where another remote has it already; null where it does.</summary>
    public static string? WhyNot(string name, IReadOnlyList<string> others) =>
        RefNames.WhyNot(name, "remote") ?? (others.Contains(name, StringComparer.Ordinal) ? $"There is a remote named {name} already" : null);

    /// <summary>Why a URL is none git can fetch from: empty, or holding white space at its ends or a control character.</summary>
    public static string? WhyNotUrl(string url) =>
        url.Length == 0 ? "Give the URL of the repository: an https:// or ssh:// address, git@host:path, or a folder"
        : url.Trim().Length != url.Length ? "A URL cannot begin or end with a space"
        : url.Any(char.IsControl) ? "A URL cannot hold a control character"
        : null;
}
