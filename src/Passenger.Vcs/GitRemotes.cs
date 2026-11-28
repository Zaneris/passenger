namespace Passenger.Vcs;

/// <summary>A remote: its name and the URL git fetches it from, with the one it pushes to where git's config gives that
/// apart (remote.NAME.pushurl).</summary>
public sealed record RemoteInfo(string Name, string Url, string? PushUrl = null);

/// <summary>A remote to add (<paramref name="Was"/> null) or to change: the name it had, the name and the URL it has from
/// then on, and for a new one whether its branches are fetched at once.</summary>
public sealed record RemotePlan(string? Was, string Name, string Url, bool Fetch = false);

/// <summary>A local branch to push: the remote and the branch there it goes to, whether the branch tracks that one from
/// then on, and whether it replaces what the remote's branch has that the branch lacks (Force Push).</summary>
public sealed record PushPlan(string Branch, string Remote, string RemoteBranch, bool Track, bool Force = false);

/// <summary>What a push would send, as this repository last fetched the remote: the commits the remote's branch lacks
/// (those no branch of the remote has where it has none of the name yet), newest first and at most
/// <see cref="MostListed"/> of them, how many in all, whether it has one of the name, and how many commits it has that
/// the branch lacks, which git refuses to push over but for a force push.</summary>
public sealed record PushPreview(IReadOnlyList<CommitInfo> Sends, int SendsCount, bool RemoteHasBranch, int RemoteAhead)
{
    /// <summary>How many of the commits it sends are listed at most, however many it sends.</summary>
    public const int MostListed = 1000;
}

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
    /// only where the plan says, and forced only where it says (<see cref="Forcing"/>). Canceled, as
    /// <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> PushToAsync(PushPlan plan, CancellationToken cancel = default, GitAsker? asker = null) =>
        RunAsync(Root, ["push", .. plan.Track ? new[] { "-u" } : [], .. Forcing(plan.Force), "--end-of-options", plan.Remote, $"refs/heads/{plan.Branch}:refs/heads/{plan.RemoteBranch}"],
            cancel: cancel, asker: asker);

    /// <summary>What a force push adds to git's push: the remote's branch replaced only where it still stands where this
    /// repository last fetched it (a lease on its remote-tracking branch, or on there being none), and only where the branch
    /// has once held what that was (git 2.30), so a fetch nobody took in never lends the lease to commits nobody saw.</summary>
    private static IReadOnlyList<string> Forcing(bool force) => force ? ["--force-with-lease", "--force-if-includes"] : [];

    /// <summary>What pushing a local branch to a remote's branch would send, as the remote was last fetched, the commits
    /// listed at most <paramref name="most"/>.</summary>
    public async Task<PushPreview> PushPreviewAsync(string branch, string remote, string remoteBranch, int most = PushPreview.MostListed)
    {
        string local = $"refs/heads/{branch}";
        string there = $"refs/remotes/{remote}/{remoteBranch}";
        if (await CommitOfAsync(there) is null)
        {
            // Nothing of the name there yet: what no branch of the remote has goes.
            string[] fresh = [local, "--not", $"--remotes={remote}", "--"];
            using GitOutput listed = await OutputOfAsync(Root, ["log", "--topo-order", $"--max-count={most}", CommitFormat, .. fresh]);
            GitResult counted = await RunAsync(Root, ["rev-list", "--count", .. fresh]);
            IReadOnlyList<CommitInfo> news = listed.Succeeded ? Commits(listed.Bytes) : [];
            return new PushPreview(news, Count(counted.Output) ?? news.Count, false, 0);
        }
        IReadOnlyList<CommitInfo> sends = await LogAsync(most, [$"{there}..{local}"]);
        // The commits only the branch has, then those only the remote's branch has.
        GitResult apart = await RunAsync(Root, ["rev-list", "--left-right", "--count", "--end-of-options", $"{local}...{there}", "--"], answers: [NotThere]);
        string[] counts = apart.Succeeded ? apart.Output.Split('\t', StringSplitOptions.TrimEntries) : [];
        return new PushPreview(sends, counts.Length == 2 && Count(counts[0]) is { } ahead ? ahead : sends.Count, true, counts.Length == 2 ? Count(counts[1]) ?? 0 : 0);
    }

    private static int? Count(string said) => int.TryParse(said.Trim(), System.Globalization.CultureInfo.InvariantCulture, out int count) ? count : null;
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
