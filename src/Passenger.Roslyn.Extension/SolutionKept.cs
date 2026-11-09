using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Passenger.Roslyn.Extension;

/// <summary>What the extension's readers keep by a solution's documents or projects, kept apart for each workspace the
/// server has (the host's, Miscellaneous Files', any other), and let go of where that workspace's solution no longer has
/// the key: a file deleted or renamed, a project unloaded or loaded again under a new id. A scan of one workspace never
/// touches another's, and a workspace gone takes its own with it. Any thread.</summary>
internal sealed class SolutionKept<TKey, T>(Func<Solution, TKey, bool> has) where TKey : notnull
{
    private readonly ConditionalWeakTable<Microsoft.CodeAnalysis.Workspace, ConcurrentDictionary<TKey, T>> byWorkspace = [];

    /// <summary>What is kept for the solution's workspace.</summary>
    public ConcurrentDictionary<TKey, T> Of(Solution solution) => byWorkspace.GetValue(solution.Workspace, _ => new());

    /// <summary>Lets go of what is kept for the solution's workspace under keys the solution no longer has.</summary>
    public void LetGoOfGone(Solution solution)
    {
        ConcurrentDictionary<TKey, T> kept = Of(solution);
        foreach (KeyValuePair<TKey, T> each in kept)
        {
            if (!has(solution, each.Key))
            {
                kept.TryRemove(each);
            }
        }
    }
}

/// <summary>The caches of <see cref="SolutionKept{TKey, T}"/> by a solution's documents and by its projects.</summary>
internal static class SolutionKept
{
    public static SolutionKept<DocumentId, T> ByDocument<T>() => new((solution, id) => solution.ContainsDocument(id));

    public static SolutionKept<ProjectId, T> ByProject<T>() => new((solution, id) => solution.ContainsProject(id));
}
