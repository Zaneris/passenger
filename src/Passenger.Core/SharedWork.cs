using System.Runtime.CompilerServices;

namespace Passenger.Core;

/// <summary>Work asked for by key from any thread (a download), begun once however many ask at once: an ask takes the key's
/// task under way, or one that ended and still stands, and only where there is neither is the work begun anew, on the
/// thread pool through <see cref="BackgroundWork"/>. An ask that stops waiting (its own <c>WaitAsync</c>) never ends the
/// task or lets go of it. Without <c>stands</c> a task is let go once it ended, holding no answer after.</summary>
public sealed class SharedWork<TKey, T>(Func<Task<T>, bool>? stands = null, IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private readonly Dictionary<TKey, Task<T>> tasks = new(comparer);

    /// <summary>The key's task under way, else the one that ended and stands, else <paramref name="work"/> begun now.</summary>
    public Task<T> Take(TKey key, Func<Task<T>> work, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        lock (tasks)
        {
            if (tasks.TryGetValue(key, out Task<T>? held) && (!held.IsCompleted || stands?.Invoke(held) == true))
            {
                return held;
            }
            Task<T>? begun = null;
            begun = tasks[key] = BackgroundWork.Run(stands is null ? () => LetGoAsync(key, work, () => begun) : work, file: file, line: line);
            return begun;
        }
    }

    /// <summary>The work, its task let go once it ended: its end takes the lock only after <see cref="Take"/> stored it.</summary>
    private async Task<T> LetGoAsync(TKey key, Func<Task<T>> work, Func<Task<T>?> begun)
    {
        try
        {
            return await work();
        }
        finally
        {
            lock (tasks)
            {
                if (tasks.TryGetValue(key, out Task<T>? held) && held == begun())
                {
                    tasks.Remove(key);
                }
            }
        }
    }
}
