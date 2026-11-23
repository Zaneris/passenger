namespace Passenger.Vcs;

/// <summary>A tag as it stands: its name, the commit it names, and for an annotated tag its message (its signature apart),
/// who tagged it and whether it is signed; a lightweight tag is a name for its commit alone.</summary>
public sealed record TagInfo(string Name, string Commit, string? Message = null, string? Tagger = null, bool Signed = false)
{
    public bool IsAnnotated => Message is not null;
}

/// <summary>A tag to make at <paramref name="Commit"/>, or to move there from where the tag of its name stands
/// (<paramref name="Moving"/>): lightweight (no <paramref name="Message"/>) or annotated with one, signed or not, and
/// pushed to a remote (<paramref name="PushTo"/>) or kept in this repository alone.</summary>
public sealed record TagPlan(string Name, CommitInfo Commit, string? Message, bool Sign, string? PushTo, TagInfo? Moving)
{
    public bool IsAnnotated => Message is not null;
}

public sealed partial class Git
{
    /// <summary>The tag of this name as it stands, or null where there is none.</summary>
    public async Task<TagInfo?> TagOfAsync(string name)
    {
        if (TagNames.WhyNot(name) is not null)
        {
            return null;
        }
        string full = $"refs/tags/{name}";
        GitResult read = await RunAsync(Root, ["for-each-ref", "--format=%(refname)%1f%(objecttype)%1f%(objectname)%1f%(*objectname)%1f%(taggername)%1f%(contents:signature)%1f%(contents)%1e", full]);
        return read.Succeeded ? TagNames.Read(read.Output, full) : null;
    }

    /// <summary>The commit a revision names (a branch, a tag, a hash, HEAD~2) as the log lists it, or null where it names
    /// none.</summary>
    public async Task<CommitInfo?> CommitAtAsync(string revision)
    {
        if (await CommitOfAsync(revision.Trim()) is not { } hash)
        {
            return null;
        }
        using GitOutput log = await OutputOfAsync(Root, ["log", "-1", CommitFormat, "--end-of-options", hash, "--"]);
        return log.Succeeded ? Commits(log.Bytes).FirstOrDefault() : null;
    }

    /// <summary>A tag made as planned, or moved there from where it stood (-f): a lightweight one never signed, whatever
    /// tag.gpgsign says (which would make it annotated and ask for a message); an annotated one with its message as
    /// written (comment lines kept), signed through <paramref name="signer"/> where planned.</summary>
    public async Task<GitResult> MakeTagAsync(TagPlan plan, Signer? signer = null)
    {
        string[] force = plan.Moving is null ? [] : ["-f"];
        if (plan.Message is not { } message)
        {
            return await RunAsync(Root, ["tag", "--no-sign", .. force, "--end-of-options", plan.Name, plan.Commit.Hash]);
        }
        using CommitFile file = CommitFile.Message(message);
        return await RunSignedAsync(["tag", "-a", plan.Sign ? "-s" : "--no-sign", "--cleanup=whitespace", "-F", file.Path, .. force, "--end-of-options", plan.Name, plan.Commit.Hash],
            signer, NoEditor);
    }

    /// <summary>A tag pushed to a remote, replacing the remote's own of that name where told; git's answer as --porcelain
    /// writes it, which <see cref="TagNames.RemoteHasAnother"/> reads. Canceled, as <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> PushTagAsync(string remote, string name, bool replace, CancellationToken cancel = default, GitAsker? asker = null) =>
        RunAsync(Root, ["push", "--porcelain", "--end-of-options", remote, $"{(replace ? "+" : string.Empty)}refs/tags/{name}:refs/tags/{name}"], cancel: cancel, asker: asker);

    /// <summary>A tag deleted on a remote. Canceled, as <see cref="FetchUpstreamAsync"/>.</summary>
    public Task<GitResult> DeleteRemoteTagAsync(string remote, string name, CancellationToken cancel = default, GitAsker? asker = null) =>
        RunAsync(Root, ["push", "--end-of-options", remote, $":refs/tags/{name}"], cancel: cancel, asker: asker);
}

/// <summary>A tag's name as git takes one, and what git answers of tags, read apart from any git.</summary>
public static class TagNames
{
    /// <summary>Why git takes no tag of this name (git check-ref-format's rules for refs/tags/name, and git tag's own
    /// refusal of a leading dash), in words a person reads; null where it takes it.</summary>
    public static string? WhyNot(string name)
    {
        if (name.Length == 0)
        {
            return "Name the tag";
        }
        if (name[0] == '-')
        {
            return "A tag's name cannot begin with a dash";
        }
        foreach (char c in name)
        {
            if (c < ' ' || c == '\x7f')
            {
                return "A tag's name cannot hold a control character";
            }
            if (c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return c == ' ' ? "A tag's name cannot hold a space" : $"A tag's name cannot hold {c}";
            }
        }
        if (name.Contains("..", StringComparison.Ordinal))
        {
            return "A tag's name cannot hold two dots together";
        }
        if (name.Contains("@{", StringComparison.Ordinal))
        {
            return "A tag's name cannot hold @{";
        }
        if (name[0] == '/' || name[^1] == '/' || name.Contains("//", StringComparison.Ordinal))
        {
            return "A tag's name cannot begin or end with a slash, nor hold two together";
        }
        if (name[^1] == '.')
        {
            return "A tag's name cannot end with a dot";
        }
        foreach (string part in name.Split('/'))
        {
            if (part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal))
            {
                return part.StartsWith('.') ? "No part of a tag's name between slashes can begin with a dot" : "No part of a tag's name between slashes can end with .lock";
            }
        }
        return null;
    }

    /// <summary>The tag <paramref name="full"/> of what for-each-ref wrote (its name, its object's type and name, the
    /// commit an annotated tag names, the tagger, the signature and the message, which ends with the signature, each record
    /// ended by 0x1e); null where it is not there.</summary>
    public static TagInfo? Read(string output, string full)
    {
        foreach (string record in output.Split('\x1e'))
        {
            string[] field = record.TrimStart('\n').Split('\x1f', 7);
            if (field.Length < 7 || field[0] != full)
            {
                continue;
            }
            string name = full["refs/tags/".Length..];
            string signature = field[5];
            string message = signature.Length > 0 && field[6].EndsWith(signature, StringComparison.Ordinal) ? field[6][..^signature.Length] : field[6];
            return field[1] == "tag"
                ? new TagInfo(name, field[3].Length > 0 ? field[3] : field[2], message.TrimEnd(), field[4].Length > 0 ? field[4] : null, signature.Length > 0)
                : new TagInfo(name, field[2]);
        }
        return null;
    }

    /// <summary>Whether a push of tags (git push --porcelain) was refused because the remote has its own tag of the name
    /// that names another commit: a refused line ('!') git says "already exists" of, which --porcelain never
    /// translates.</summary>
    public static bool RemoteHasAnother(GitResult pushed) =>
        pushed.Output.Split('\n').Any(line => line.StartsWith("!\t", StringComparison.Ordinal) && line.Contains("refs/tags/", StringComparison.Ordinal)
            && line.TrimEnd('\r').EndsWith("(already exists)", StringComparison.Ordinal));
}
