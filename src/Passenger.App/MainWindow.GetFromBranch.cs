using Passenger.App.Editors;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>Get from Branch: files take a branch's, tag's or revision's version whole, as one command of global Undo,
/// which puts the person's own back.</summary>
public partial class MainWindow
{
    /// <summary>Each file takes <paramref name="revision"/>'s text of it (at <c>Then</c>, else at <c>Origin</c>, where the last
    /// commit has a file renamed since), written in that version's encoding and line breaks, the open ones showing it; a file
    /// the revision has none of goes to the Trash. One command Undo takes back whole; refused, nothing written, where a file is
    /// binary in the revision, a symbolic link in it or here (whose text is a link's target, never a file's) or, in a folder
    /// not trusted, stored through a filter. What came of it, said in the status bar.</summary>
    public async Task<VcsOutcome> GetFromAsync(string revision, string version, IReadOnlyList<(string Path, string Then, string? Origin)> files)
    {
        VcsController vcs = session.Vcs;
        if (vcs.Repository is null)
        {
            return Told(false, vcs.NoRepository);
        }
        if (await vcs.WhyNotWrittenOutAsync([.. files.Select(f => f.Path)]) is { } filtered)
        {
            return Told(false, filtered);
        }
        List<(string Path, FileText Text)> writes = [];
        List<string> deletes = [];
        foreach ((string path, string then, string? origin) in files)
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                return Told(false, $"{Path.GetFileName(path)} is a symbolic link here: Get from Branch takes a text file's version alone");
            }
            string at = then;
            FileVersion? was = await vcs.VersionAsync(revision, then, path);
            if (was is null && origin is not null && !PathNames.Same(origin, then))
            {
                at = origin;
                was = await vcs.VersionAsync(revision, origin, path);
            }
            if (was is not null && await vcs.IsLinkAtAsync(revision, at))
            {
                return Told(false, $"{Path.GetFileName(path)} is a symbolic link in {version}: Get from Branch takes a text file's version alone");
            }
            if (was is { Stored: null })
            {
                return Told(false, $"{Path.GetFileName(path)} is binary in {version}: Get from Branch takes a text file's version alone");
            }
            if (was?.Stored is { } stored)
            {
                writes.Add((path, stored));
            }
            else if (File.Exists(path))
            {
                deletes.Add(path);
            }
        }
        string[] names = [.. files.Select(f => Path.GetFileName(f.Path))];
        string what = Plural.Listed(names);
        if (vcs != session.Vcs)
        {
            return new VcsOutcome(false, "The window left the folder");
        }
        using CommandHistory.Recording made = Editors.Commands.Record($"Get {what} from {version}");
        if (writes.Count > 0 && await ChangeFileAsync([.. writes.Select(w => w.Path)], () =>
        {
            foreach ((string path, FileText text) in writes)
            {
                FileText.Save(path, text.Text, text.Encoding, text.HasBom, text.CrBreaks);
            }
            return null;
        }, made) is { } problem)
        {
            return Told(false, $"{what} did not take {version}'s version: {await made.TakenBackAsync(problem)}");
        }
        if (deletes.Count > 0 && await TrashAsync(deletes, made) is not [])
        {
            return Told(false, $"{what} did not take {version}'s version: {await made.TakenBackAsync(status.Text ?? "a file could not be moved to the Trash")}");
        }
        string[] taken = [.. writes.Select(w => Path.GetFileName(w.Path))];
        string[] trashed = [.. deletes.Select(Path.GetFileName).OfType<string>()];
        string[] said =
        [
            .. taken.Length == 0 ? [] : new[] { $"{Plural.Listed(taken)} now {Plural.Agreeing(taken.Length, "has", "have")} {version}'s version" },
            .. trashed.Length == 0 ? [] : new[] { $"{Plural.Listed(trashed)}, which {version} has not, went to the Trash" },
        ];
        return Told(true, $"{string.Join("; ", said)}: Undo puts yours back");

        VcsOutcome Told(bool succeeded, string message)
        {
            status.Text = message;
            ActionWork.Tell(succeeded, message);
            return new VcsOutcome(succeeded, message);
        }
    }
}
