using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The window's bookmarks, the open folder's (its session's <see cref="ProjectSession.Bookmarks"/>): Toggle
/// Bookmark on the caret's line, Next and Previous across files, the Bookmarks window, and every open file's ribbons.</summary>
public partial class MainWindow
{
    private ToolWindows.BookmarksView? bookmarksView;

    /// <summary>The open folder's bookmarks.</summary>
    public BookmarkStore Bookmarks => session.Bookmarks;

    private void AddBookmarks()
    {
        // Built as it is first shown, over the session's bookmarks then.
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Bookmarks, ToolWindowTitles.Bookmarks, IconKeys.Bookmark, () =>
        {
            bookmarksView = new(session.Bookmarks, (path, line) =>
                Editors.Find(path) is { HoldsText: true } open && line < open.Text.Snapshot.LineCount ? open.Text.Snapshot.GetLineText(line) : string.Empty);
            bookmarksView.GoToRequested += (_, bookmark) => NavigateTo(bookmark.Path, new TextPosition(bookmark.Line, 0));
            return bookmarksView;
        }, Docking.ToolAnchor.LeftBottom)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowBookmarks,
            Menus = [new MenuPlace("Navigate/Bookmarks", 0, 3)],
        });
        AddBookmarkActions();
        Editors.Opened += (_, document) =>
        {
            document.ShowBookmarks(session.Bookmarks.LinesIn(document.Path), session.Bookmarks.MnemonicsIn(document.Path));
            document.Text.Changed += (_, _) => session.Bookmarks.Follow(document.Path);
        };
    }

    /// <summary>A session's bookmarks in the Bookmarks window and the open files' ribbons, while it is the window's.</summary>
    private void BindBookmarks(ProjectSession next)
    {
        bookmarksView?.Bind(next.Bookmarks);
        next.Bookmarks.Changed += (_, path) =>
        {
            if (session != next)
            {
                return;
            }
            foreach (OpenDocument document in Editors.Documents.Where(d => d.Path == path))
            {
                document.ShowBookmarks(next.Bookmarks.LinesIn(path), next.Bookmarks.MnemonicsIn(path));
            }
        };
        foreach (OpenDocument document in Editors.Documents)
        {
            document.ShowBookmarks(next.Bookmarks.LinesIn(document.Path), next.Bookmarks.MnemonicsIn(document.Path));
        }
    }

    /// <summary>Toggle Bookmark on the caret's line, Next and Previous Bookmark from it through every file, and the numbered
    /// bookmarks' Toggle Bookmark and Go to Bookmark 0 to 9.</summary>
    private void AddBookmarkActions()
    {
        actions!.Add(new IdeAction(AppActions.ToggleBookmark, ActionTitles.ToggleBookmark, ActionGroups.Bookmarks, context => session.Bookmarks.Toggle(context.File!.Path, context.Caret.Line))
        {
            WhyNot = Refusals.NeedsLines,
            Menus = [new MenuPlace("Navigate/Bookmarks", 0, 0)],
        });
        Step(AppActions.NextBookmark, ActionTitles.NextBookmark, 1);
        Step(AppActions.PreviousBookmark, ActionTitles.PreviousBookmark, -1);
        for (int n = 0; n < AppActions.NumberedBookmarks.Count; n++)
        {
            char digit = (char)('0' + n);
            actions.Add(new IdeAction(AppActions.NumberedBookmarks[n].Toggle, ActionTitles.ToggleNumberedBookmark(digit.ToString()), ActionGroups.Bookmarks,
                context => session.Bookmarks.ToggleNumbered(context.File!.Path, context.Caret.Line, digit))
            {
                WhyNot = Refusals.NeedsLines,
            });
            actions.Add(new IdeAction(AppActions.NumberedBookmarks[n].GoTo, ActionTitles.GoToNumberedBookmark(digit.ToString()), ActionGroups.Bookmarks, _ =>
            {
                Bookmark numbered = session.Bookmarks.Numbered(digit)!;
                NavigateTo(numbered.Path, new TextPosition(numbered.Line, 0));
            })
            {
                WhyNot = _ => session.Bookmarks.Numbered(digit) is null ? $"No bookmark {digit}" : null,
            });
        }

        void Step(string id, Words title, int step) => actions.Add(new IdeAction(id, title, ActionGroups.Bookmarks, context =>
        {
            Bookmark next = session.Bookmarks.Next(context.File?.Path, context.File is null ? -1 : context.Caret.Line, step)!;
            NavigateTo(next.Path, new TextPosition(next.Line, 0));
        })
        {
            WhyNot = _ => session.Bookmarks.All.Count == 0 ? "No bookmarks" : null,
            Menus = [new MenuPlace("Navigate/Bookmarks", 0, step > 0 ? 1 : 2)],
        });
    }
}
