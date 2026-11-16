using Passenger.Core;

namespace Passenger.App.Editors;

/// <summary>What Undo or Redo of a command of several files asks before it puts them as they were: the files whose text it
/// changes, each it renames or moves back, those it brings back and those it deletes, a copy it made said as one, every
/// one by its name as it stands now ("Undo Rename Widget to Gadget? It changes Uses.cs and Gadget.cs, and renames
/// Gadget.cs back to Widget.cs.").</summary>
public static class UndoQuestion
{
    /// <summary>The question before a command named as Undo names it: the files whose text it <paramref name="changed"/>,
    /// where each stands now; the files and folders it <paramref name="moves"/> from where each stands now to where it goes;
    /// those it <paramref name="brought"/> back and <paramref name="deleted"/>; the <paramref name="copies"/> it made, which
    /// Undo deletes and Redo brings back; and the <paramref name="parts"/> it changes besides, by name. Redo's takes nothing
    /// back.</summary>
    public static string Of(string command, bool redo, IReadOnlyList<string> changed, IReadOnlyList<(string Now, string Then)> moves,
        IReadOnlyList<string> brought, IReadOnlyList<string> deleted, IReadOnlyList<string> parts, IReadOnlyList<string>? copies = null)
    {
        string back = redo ? string.Empty : " back";
        copies ??= [];
        string[] edited = [.. changed.Where(c => !brought.Concat(deleted).Concat(copies).Contains(c, PathNames.Comparer)).Select(Name).Distinct(), .. parts];
        List<string> said = [];
        if (edited.Length > 0)
        {
            said.Add($"changes {Plural.Listed(edited)}");
        }
        foreach ((string now, string then) in moves)
        {
            said.Add(PathNames.Same(Path.GetDirectoryName(now) ?? now, Path.GetDirectoryName(then) ?? then)
                ? $"renames {Name(now)}{back} to {Name(then)}"
                : $"moves {Name(now)}{back} to {Name(Path.GetDirectoryName(then) ?? then)}/{Name(then)}");
        }
        // Told from a file of its name that it replaced.
        List<string> copied = [.. copies.Select(Name).Distinct()];
        string[] theCopies = copied.Count == 0 ? [] : [$"{Plural.Noun(copied.Count, "the copy", "the copies")} {Plural.Listed(copied)}"];
        string[] comes = [.. brought.Select(Name).Distinct(), .. redo ? theCopies : []];
        string[] goes = [.. deleted.Select(Name).Distinct(), .. redo ? [] : theCopies];
        if (comes.Length > 0)
        {
            said.Add($"brings back {Plural.Listed(comes)}");
        }
        if (goes.Length > 0)
        {
            said.Add($"deletes {Plural.Listed(goes)}");
        }
        string verb = redo ? "Redo" : "Undo";
        return said.Count switch
        {
            0 => $"{verb} {command}?",
            1 => $"{verb} {command}? It {said[0]}.",
            _ => $"{verb} {command}? It {string.Join(", ", said[..^1])}, and {said[^1]}.",
        };
    }

    /// <summary>A path's last name, a folder's too.</summary>
    private static string Name(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
}
