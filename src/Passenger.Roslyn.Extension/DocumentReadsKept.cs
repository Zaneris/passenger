using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Passenger.Roslyn.Extension;

/// <summary>What a reader found in each document of a project, kept with what it was read from: the document's text and the
/// parse options its syntax was read with, and, where the reader bound its code, the declarations of its project and the
/// projects it references (their dependent semantic version, which an edit inside a body never moves). So an edit reads again
/// only its own document, and an edit of declarations only it and the documents whose code was bound.</summary>
internal sealed class DocumentReadsKept<T>
{
    private readonly SolutionKept<DocumentId, Entry> kept = SolutionKept.ByDocument<Entry>();

    private sealed record Entry(VersionStamp Text, ParseOptions? Parsing, VersionStamp? Declarations, T Found);

    /// <summary>What a reader found in a document, and whether it bound the document's code to find it: one whose syntax
    /// alone said it holds nothing is never read again for what is declared beside it.</summary>
    public readonly record struct Read(T Found, bool Bound);

    /// <summary>What each of the documents of <paramref name="project"/> holds, in their order: as kept, or read again by
    /// <paramref name="read"/> where what it was read from has changed, those documents side by side, as binding each is
    /// its own work.</summary>
    public async Task<IReadOnlyList<T>> OfAsync(Project project, IEnumerable<Document> documents, Func<Document, CancellationToken, Task<Read>> read,
        CancellationToken cancel)
    {
        kept.LetGoOfGone(project.Solution);
        ConcurrentDictionary<DocumentId, Entry> entries = kept.Of(project.Solution);
        VersionStamp declarations = await project.GetDependentSemanticVersionAsync(cancel).ConfigureAwait(false);
        Document[] all = [.. documents];
        T[] found = new T[all.Length];
        List<(int Index, VersionStamp Text)> stale = [];
        for (int i = 0; i < all.Length; i++)
        {
            VersionStamp text = await all[i].GetTextVersionAsync(cancel).ConfigureAwait(false);
            if (entries.TryGetValue(all[i].Id, out Entry? entry) && entry.Text == text && ReferenceEquals(entry.Parsing, project.ParseOptions)
                && (entry.Declarations is not { } then || then == declarations))
            {
                found[i] = entry.Found;
            }
            else
            {
                stale.Add((i, text));
            }
        }
        await Parallel.ForEachAsync(stale, cancel, async (each, stop) =>
        {
            Read now = await read(all[each.Index], stop).ConfigureAwait(false);
            entries[all[each.Index].Id] = new Entry(each.Text, project.ParseOptions, now.Bound ? declarations : null, now.Found);
            found[each.Index] = now.Found;
        }).ConfigureAwait(false);
        return found;
    }
}
