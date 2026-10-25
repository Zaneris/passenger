using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.History;

/// <summary>A row of a Local History: a change set that touched what the history is of (a file or a folder), titled by
/// its name or by what it did, the newest first. A file's row carries what the file held before the set, which its diff
/// shows and Revert puts back.</summary>
public sealed record HistoryRow(long Set, DateTimeOffset Time, string Title, string? Name, bool IsLabel, bool System)
{
    /// <summary>A file's content before the set, by hash: null when the file did not exist then.</summary>
    public string? Version { get; init; }

    /// <summary>Where the file was then, before a rename since.</summary>
    public string? PathThen { get; init; }

    /// <summary>The files the set touched there.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>A file of a folder that is not now what it was before a row's change set: its content then and now by hash,
/// null where it did not exist.</summary>
public sealed record HistoryDifference(string Path, string? Then, string? Now)
{
    /// <summary>What happened to it since: made, deleted, or changed.</summary>
    public ChangeKind Kind => Then is null ? ChangeKind.Added : Now is null ? ChangeKind.Deleted : ChangeKind.Modified;

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>What a history shows, worked out from what Local History kept: the rows of a file (followed back through its
/// renames) or of a folder, and what a folder's files were before a row. Each walks back from what is known now, undoing
/// one change at a time. Pure, and run off the UI thread.</summary>
public static class HistoryRows
{
    /// <summary>A file's rows: the sets that changed, made, deleted or moved it, under each name it had, and the labels
    /// put while its content was known, each with what the file held before it.</summary>
    public static IReadOnlyList<HistoryRow> OfFile(IReadOnlyList<HistoryEvent> events, IReadOnlyDictionary<string, string?> known, string path)
    {
        string name = path;
        string? state = known.GetValueOrDefault(path);
        bool isKnown = known.ContainsKey(path);
        List<HistoryRow> rows = [];
        List<bool> knownThen = [];
        for (int i = events.Count - 1; i >= 0; i--)
        {
            HistoryEvent kept = events[i];
            string then = name;
            switch (kept.Kind)
            {
                case HistoryKind.Label:
                    break;
                case HistoryKind.Moved when PathNames.Within(name, kept.To!):
                    name = PathNames.Moved(name, kept.To!, kept.Path!);
                    then = name;
                    break;
                case HistoryKind.Seen when PathNames.Same(kept.Path!, name):
                    // Before it was first seen nothing is known of it.
                    isKnown = false;
                    continue;
                case HistoryKind.Changed or HistoryKind.Deleted when PathNames.Same(kept.Path!, name):
                    state = kept.Before;
                    isKnown = true;
                    break;
                case HistoryKind.Created when PathNames.Same(kept.Path!, name):
                    state = null;
                    isKnown = true;
                    break;
                default:
                    continue;
            }
            HistoryRow row = new(kept.Set, kept.Time, Title(kept, [kept], single: true), kept.Name, kept.Kind == HistoryKind.Label, kept.System)
            {
                Version = state,
                PathThen = then,
                Files = kept.Path is { } file ? [file] : [],
            };
            if (rows.Count > 0 && rows[^1].Set == kept.Set)
            {
                rows[^1] = rows[^1] with { Version = state, PathThen = then, Time = kept.Time };
                knownThen[^1] = isKnown;
            }
            else
            {
                rows.Add(row);
                knownThen.Add(isKnown);
            }
        }
        return [.. rows.Where((_, i) => knownThen[i])];
    }

    /// <summary>A folder's rows: the sets that touched a file in it, and every label.</summary>
    public static IReadOnlyList<HistoryRow> OfFolder(IReadOnlyList<HistoryEvent> events, string folder)
    {
        List<(long Set, List<HistoryEvent> Events)> sets = [];
        for (int i = events.Count - 1; i >= 0; i--)
        {
            HistoryEvent kept = events[i];
            bool touches = kept.Kind == HistoryKind.Label
                || kept.Kind != HistoryKind.Seen && (PathNames.Within(kept.Path!, folder) || kept.To is { } to && PathNames.Within(to, folder));
            if (!touches)
            {
                continue;
            }
            if (sets.Count > 0 && sets[^1].Set == kept.Set)
            {
                sets[^1].Events.Add(kept);
            }
            else
            {
                sets.Add((kept.Set, [kept]));
            }
        }
        return [.. sets.Select(s =>
        {
            HistoryEvent first = s.Events[^1];
            return new HistoryRow(s.Set, first.Time, Title(first, s.Events, single: false), first.Name, first.Kind == HistoryKind.Label, first.System)
            {
                Files = [.. s.Events.Select(e => e.To ?? e.Path).OfType<string>().Distinct(PathNames.Comparer)],
            };
        })];
    }

    /// <summary>The files of a folder that are not now what they were before a set: each with its content then and now.</summary>
    public static IReadOnlyList<HistoryDifference> Since(IReadOnlyList<HistoryEvent> events, IReadOnlyDictionary<string, string?> known, long set, string folder)
    {
        int first = -1;
        for (int i = 0; i < events.Count && first < 0; i++)
        {
            first = events[i].Set == set ? i : -1;
        }
        if (first < 0)
        {
            return [];
        }
        StringComparer comparer = PathNames.Comparer;
        Dictionary<string, string?> then = new(known, comparer);
        for (int i = events.Count - 1; i >= first; i--)
        {
            HistoryEvent kept = events[i];
            switch (kept.Kind)
            {
                case HistoryKind.Changed or HistoryKind.Deleted:
                    then[kept.Path!] = kept.Before;
                    break;
                case HistoryKind.Created:
                    then[kept.Path!] = null;
                    break;
                case HistoryKind.Moved:
                    foreach (KeyValuePair<string, string?> moved in then.Where(k => PathNames.Within(k.Key, kept.To!)).ToList())
                    {
                        then.Remove(moved.Key);
                        then[PathNames.Moved(moved.Key, kept.To!, kept.Path!)] = moved.Value;
                    }
                    break;
            }
        }
        return [.. then.Keys.Union(known.Keys, comparer)
            .Where(p => PathNames.IsUnder(p, folder))
            .Select(p => new HistoryDifference(p, then.GetValueOrDefault(p), known.GetValueOrDefault(p)))
            .Where(d => d.Then != d.Now)
            .OrderBy(d => d.Path, comparer)];
    }

    /// <summary>A set's title: its own name (a label, External change, a revert, a Git checkout), else what it did, in a
    /// file's history briefly ("Change") and in a folder's by file ("Modify Shop.cs", "Change Shop.cs + 2 more").</summary>
    private static string Title(HistoryEvent first, IReadOnlyList<HistoryEvent> all, bool single)
    {
        if (first.Name is { } name)
        {
            return name;
        }
        List<HistoryEvent> changes = [.. all.Where(e => e.Kind != HistoryKind.Seen)];
        if (changes.Count > 1)
        {
            return $"Change {FileName(changes[^1])} + {changes.Count - 1} more";
        }
        return first.Kind switch
        {
            HistoryKind.Created => $"Create {FileName(first)}",
            HistoryKind.Deleted => $"Delete {FileName(first)}",
            HistoryKind.Moved when Path.GetDirectoryName(first.Path) == Path.GetDirectoryName(first.To) => $"Rename {FileName(first)} to {Path.GetFileName(first.To)}",
            HistoryKind.Moved => $"Move {FileName(first)} to {Path.GetFileName(Path.GetDirectoryName(first.To))}",
            _ when single => "Change",
            _ => $"Modify {FileName(first)}",
        };
    }

    private static string FileName(HistoryEvent kept) => Path.GetFileName(kept.Path ?? string.Empty);
}
