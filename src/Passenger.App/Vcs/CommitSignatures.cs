using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>The signatures of the commits a window shows (the Log's rows, a history's, vcs_log's), each read once and kept
/// by hash for the working tree it was read in: a row asks as it is shown and those asked for together are one read, so a
/// history of thousands of signed commits runs a verifier only for those seen. Forgotten when the person refreshes the Log
/// or the signing set-up changes what git trusts. A UI-thread object.</summary>
public sealed class CommitSignatures
{
    private readonly Dictionary<string, CommitSignature?> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> wanted = new(StringComparer.Ordinal);
    private readonly HashSet<string> reading = new(StringComparer.Ordinal);
    private Git? of;
    private bool posted;
    private int forgotten;

    /// <summary>Raised when signatures asked for as rows were shown have been read.</summary>
    public event EventHandler? Landed;

    /// <summary>A commit's signature where it has been read (null for one unsigned); false where it has not.</summary>
    public bool TryGet(Git repository, string hash, out CommitSignature? signature)
    {
        signature = null;
        return repository == of && known.TryGetValue(hash, out signature);
    }

    /// <summary>A shown commit's signature, read soon with the others shown meanwhile, then <see cref="Landed"/> raised.</summary>
    public void Want(Git repository, string hash)
    {
        UiThread.Check();
        Follow(repository);
        if (known.ContainsKey(hash) || reading.Contains(hash) || !wanted.Add(hash) || posted)
        {
            return;
        }
        posted = true;
        UiThread.Post(() => IdeErrors.Observe(ReadWantedAsync(repository)));
    }

    /// <summary>These commits' signatures (null for one unsigned), those not read before read by one git and kept.</summary>
    public async Task<IReadOnlyDictionary<string, CommitSignature?>> ReadAsync(Git repository, IReadOnlyCollection<string> hashes)
    {
        UiThread.Check();
        Follow(repository);
        int since = forgotten;
        string[] unread = [.. hashes.Distinct(StringComparer.Ordinal).Where(h => !known.ContainsKey(h))];
        if (unread.Length > 0)
        {
            string noSigners = await BackgroundWork.Run(NoSigners);
            IReadOnlyDictionary<string, CommitSignature> read = await repository.SignaturesAsync(unread, noSigners);
            if (repository == of && since == forgotten)
            {
                foreach (string hash in unread)
                {
                    known[hash] = read.GetValueOrDefault(hash);
                }
            }
        }
        Dictionary<string, CommitSignature?> answer = new(StringComparer.Ordinal);
        foreach (string hash in hashes)
        {
            answer[hash] = known.GetValueOrDefault(hash);
        }
        return answer;
    }

    /// <summary>Every signature read is read again when next shown: what git trusts may have changed.</summary>
    public void Forget()
    {
        UiThread.Check();
        known.Clear();
        forgotten++;
        Landed?.Invoke(this, EventArgs.Empty);
    }

    private void Follow(Git repository)
    {
        if (repository != of)
        {
            of = repository;
            known.Clear();
            wanted.Clear();
            reading.Clear();
            forgotten++;
        }
    }

    private async Task ReadWantedAsync(Git repository)
    {
        posted = false;
        if (repository != of || wanted.Count == 0)
        {
            return;
        }
        string[] asked = [.. wanted];
        wanted.Clear();
        reading.UnionWith(asked);
        try
        {
            await ReadAsync(repository, asked);
        }
        finally
        {
            reading.ExceptWith(asked);
        }
        Landed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An allowed signers file that lists no one, in the state's cache, made once: what an SSH signature is checked
    /// against where the person's git config names none.</summary>
    private static string NoSigners()
    {
        string path = Path.Combine(State.StateStore.DefaultRoot, "cache", "no_allowed_signers");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, []);
        }
        return path;
    }
}
