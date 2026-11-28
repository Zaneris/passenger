using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>A file or folder added to the repository's .gitignore from the Explorer or the file shown, as the Commit window
/// adds an unversioned one, and the ignore file's line deciding whether git ignores it, as git answers, gone to.</summary>
public partial class MainWindow
{
    private void AddGitIgnoreActions()
    {
        actions!.Add(new IdeAction(AppActions.AddToGitIgnore, ActionTitles.AddToGitIgnore, "Git", context => IdeErrors.Observe(IgnoreAsync(Target(context, fileOnly: false)!)))
        {
            WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? session.Vcs.NoRepository : WhyNoTarget(context, fileOnly: false))
                ?? (Unversioned(Target(context, fileOnly: false)!) ? null : Refusals.NotUnversioned),
            Menus = [new MenuPlace("Git", 2, 7)],
        });
        actions!.Add(new IdeAction(AppActions.GoToIgnoringLine, ActionTitles.GoToIgnoringLine, "Git", context => IdeErrors.Observe(GoToIgnoringLineAsync(Target(context, fileOnly: false)!)))
        {
            WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? session.Vcs.NoRepository : WhyNoTarget(context, fileOnly: false)),
            Menus = [new MenuPlace("Git", 2, 8)],
            Words = ["Why ignored", "check-ignore", ".gitignore"],
        });

        // Unversioned as the Commit window lists it: the path itself, or a file under the folder.
        bool Unversioned(string path) => session.Vcs.Changes.Any(c => c.Kind == ChangeKind.Unversioned && (PathNames.Same(c.Path, path) || PathNames.IsUnder(c.Path, path)));
    }

    /// <summary>Go to Ignoring Line: the ignore file's line git says decides a path opened at that line, and what it decides
    /// said: ignored, kept by a <c>!</c> line, matched though tracked, or matched by no line.</summary>
    public async Task GoToIgnoringLineAsync(string path)
    {
        if (session.Vcs.Repository is not { } git)
        {
            return;
        }
        string name = PathNames.Shown(path, Folder);
        using Working? work = actions!.Work.Begin(AppActions.GoToIgnoringLine, $"Asking git which line decides {name}…", stoppable: false);
        IgnoringLine? line = await git.IgnoringAsync(path);
        string said = line is null ? $"Git ignores nothing of {name}: no line of an ignore file matches it"
            : $"{name} {(line.Tracked ? "matches" : line.Ignored ? "is ignored by" : "is kept by")} line {line.Line} of {PathNames.Shown(line.File, Folder)} ({line.Pattern})"
                + (line.Tracked ? ", but git tracks it, so it is not ignored" : string.Empty);
        if (line is not null && File.Exists(line.File))
        {
            NavigateTo(line.File, new TextPosition(line.Line - 1, 0));
        }
        status.Text = said;
        work?.Tell(line is not null, said);
    }
}
