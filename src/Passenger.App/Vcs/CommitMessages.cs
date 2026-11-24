using Passenger.App.State;
using Passenger.Core;

namespace Passenger.App.Vcs;

/// <summary>The messages committed from the Commit window, kept for each repository the folder holds by its top folder,
/// newest first, each once and at most <see cref="CommitState.MostMessages"/>: what its message history offers to commit
/// again. Kept in the folder's state while the window keeps it, else for the window's life. A UI-thread object.</summary>
public sealed class CommitMessages
{
    private readonly Dictionary<string, List<string>> messages = new(PathNames.Comparer);
    private StateDocument<CommitState>? kept;

    /// <summary>The folder's documents from now on: what they kept taken first.</summary>
    public void Keep(StateDocument<CommitState> state)
    {
        UiThread.Check();
        kept = state;
        foreach ((string repository, List<string> committed) in state.Value.Messages)
        {
            messages[repository] = [.. committed];
        }
    }

    /// <summary>A repository's messages, by its top folder, newest first.</summary>
    public IReadOnlyList<string> Of(string repository) => messages.GetValueOrDefault(repository) ?? [];

    /// <summary>A message committed in a repository: put first, taken out from where it stood before, the oldest past the
    /// most let go. Blank lines and spaces at its end are not kept.</summary>
    public void Committed(string repository, string message)
    {
        UiThread.Check();
        string text = message.TrimEnd();
        if (text.Length is 0 or > CommitState.LongestMessage)
        {
            return;
        }
        if (!messages.TryGetValue(repository, out List<string>? list))
        {
            messages[repository] = list = [];
        }
        list.Remove(text);
        list.Insert(0, text);
        if (list.Count > CommitState.MostMessages)
        {
            list.RemoveRange(CommitState.MostMessages, list.Count - CommitState.MostMessages);
        }
        if (kept is not null)
        {
            CommitState now = kept.Value;
            kept.Set(now with { Messages = new Dictionary<string, List<string>>(now.Messages) { [repository] = [.. list] } });
        }
    }
}
