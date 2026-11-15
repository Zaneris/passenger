using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A rename of a name Passenger read in files itself rather than one a language server worked out (a Godot node's or
/// name's, a compose file's): each file's edits in its text as it was read, made as one command of global undo only where the
/// file is still that text, and what it came to with the lines its edits start on in each.</summary>
internal sealed class PlacedRename(string old, string name)
{
    private readonly Dictionary<string, (string Read, List<TextEdit> Edits)> files = new(PathNames.Comparer);

    /// <summary>Edits of a file in its text as it was read, which the file's other edits were found in too; a file given none
    /// is not changed.</summary>
    public void Add(string file, string read, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }
        List<TextEdit> placed = files.TryGetValue(file, out (string Read, List<TextEdit> Edits) had) ? had.Edits : [];
        placed.AddRange(edits);
        files[file] = (read, placed);
    }

    /// <summary>How many files it changes.</summary>
    public int Count => files.Count;

    /// <summary>Each file's text as it was read, by its path.</summary>
    public IReadOnlyDictionary<string, string> Read => files.ToDictionary(f => f.Key, f => f.Value.Read, PathNames.Comparer);

    /// <summary>Each file's change: its edits, once each, where it is the text they were found in, and none where it is not,
    /// which refuses the command.</summary>
    private IReadOnlyList<XamlFileChange> Changes => [.. files.Select(f =>
    {
        string was = f.Value.Read;
        IReadOnlyList<TextEdit> these = [.. f.Value.Edits.Distinct().OrderBy(t => t.Start)];
        return new XamlFileChange(f.Key, now => now == was ? these : []);
    })];

    /// <summary>The rename made as one command of global undo: refused, saying <paramref name="none"/>, where no file takes it,
    /// or as <paramref name="behind"/> says of a file no longer as it was read; null once made, else why not.</summary>
    public async Task<string?> LandAsync(CodeActions actions, string none, Func<IReadOnlyDictionary<string, string>, Task<string?>>? behind = null)
    {
        if (files.Count == 0)
        {
            return Refusals.NotRenamed(old, none);
        }
        if (behind is not null && await behind(Read) is { } stale)
        {
            return stale;
        }
        return await actions.ApplyFileChangesAsync($"Rename {old} to {name}", Changes);
    }

    /// <summary>What the rename came to, said as <paramref name="said"/>: each file changed, with the lines (from one) its edits
    /// start on in its text as it was read.</summary>
    public RefactorOutcome Outcome(string said) => new(said, null, [.. files.Select(f =>
    {
        TextSnapshot read = TextSnapshot.From(f.Value.Read);
        return new RefactoredFile(f.Key, RefactoredChange.Changed, [.. f.Value.Edits.Select(e => read.GetLineFromOffset(e.Start) + 1).Distinct()]);
    })], []);
}
