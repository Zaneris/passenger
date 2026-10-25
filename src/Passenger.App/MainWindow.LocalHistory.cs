using Passenger.App.History;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Local History: each version of the open folder's files saved, changed elsewhere ("External change"),
/// deleted or moved, with labels (Put Label's, a test run's, a Git operation's, a refactoring's), in its tool window.
/// File and the editor's menu have Local History's Show History (the file shown, or the Explorer's chosen row), Show
/// Project History, Recent Changes and Put Label; the Explorer has Show Local History. A version shows beside the file
/// now, an arrow putting one change back; Revert puts the whole version back as one undoable step.</summary>
public partial class MainWindow
{
    private const string LocalHistoryMenu = "File/Local History";

    private LocalHistoryView? localHistoryView;

    /// <summary>The refactoring last asked for, which names what it writes in Local History.</summary>
    private string? refactoring;

    /// <summary>How long and how much Local History keeps, and its clock: 5 days of activity unless given.</summary>
    public HistoryKeeping HistoryKeeping { get; init; } = new();

    /// <summary>The open folder's Local History, or null with none open.</summary>
    public LocalHistory? LocalHistory => session.LocalHistory;

    /// <summary>The Local History tool window, built as it is first shown or asked for.</summary>
    public LocalHistoryView LocalHistoryWindow => localHistoryView ?? (LocalHistoryView)Dock[ToolWindowIds.LocalHistory].Content;

    private void AddLocalHistory()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.LocalHistory, "Local History", IconKeys.History, () => localHistoryView = new LocalHistoryView(),
            Docking.ToolAnchor.BottomLeft)
        {
            Tools = () => localHistoryView?.Tabs,
        });
        // The menus' names, and Find Action's (Show Local History...) beside the Git window's Show History.
        actions!.Add(new IdeAction(AppActions.ShowLocalHistory, "Show Local History…", "Local History", context => ShowLocalHistory(Target(context, fileOnly: false)!))
        {
            TitleIn = _ => "Show History…",
            WhyNot = context => NeedsFolder(context) ?? WhyNoTarget(context, fileOnly: false),
            Menus = [new MenuPlace(LocalHistoryMenu, 0, 0)],
        });
        actions.Add(new IdeAction(AppActions.ShowProjectHistory, "Show Local History for Project…", "Local History", _ => ShowLocalHistory(Folder!))
        {
            TitleIn = _ => "Show Project History…",
            WhyNot = NeedsFolder,
            Menus = [new MenuPlace(LocalHistoryMenu, 1, 0)],
        });
        actions.Add(new IdeAction(AppActions.RecentChanges, "Recent Changes", "Local History", context => IdeErrors.Observe(ShowRecentChangesAsync()))
        {
            WhyNot = NeedsFolder,
            Menus = [new MenuPlace(LocalHistoryMenu, 1, 1)],
        });
        actions.Add(new IdeAction(AppActions.PutLabel, "Put Label…", "Local History", _ => ShowPutLabel())
        {
            WhyNot = NeedsFolder,
            Menus = [new MenuPlace(LocalHistoryMenu, 1, 2)],
        });
        Editors.Opened += (_, document) => LookAt(document);
        // A refactoring's activity; another action since (a quick fix) is not the refactoring.
        actions.Running += (_, action) => refactoring = action.Menus.Any(m => m.Path == "Refactor") ? action.Title.TrimEnd('.') : null;
        // What a refactoring writes to files not open is one change set under its name; a quick fix's is not named.
        Editors.Recording = () =>
        {
            string? name = refactoring;
            refactoring = null;
            return name is null ? null : session.LocalHistory?.During(name);
        };
    }

    /// <summary>A file the editor opened: its content is what the history knows of it from now.</summary>
    private void LookAt(OpenDocument document)
    {
        // A preview's or a workbook's bytes are no text the history keeps, and an image's run to 64 MB: never read for nothing.
        if (session.LocalHistory is not { } history || !document.HoldsText || !history.Keeps(document.Path))
        {
            return;
        }
        try
        {
            if (new FileInfo(document.Path).Length <= FileText.KeptLimit)
            {
                history.Looked(document.Path, FileText.Read(document.Path));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Gone or held as it opened: the history learns of it at its next save.
        }
    }

    /// <summary>Show History: a file's or folder's versions in a tab of the Local History window (the one open for it, if
    /// any); null, with why in the status bar, for one outside the open folder.</summary>
    public HistoryTab? ShowLocalHistory(string path)
    {
        if (session.LocalHistory is not { } history || Folder is not { } folder)
        {
            status.Text = Refusals.NoFolder;
            return null;
        }
        string full = PathNames.Canonical(path);
        if (!PathNames.Within(full, folder))
        {
            status.Text = $"{Path.GetFileName(full)} is not in the open folder";
            return null;
        }
        bool isFolder = Directory.Exists(full);
        ShowToolWindow(ToolWindowIds.LocalHistory);
        return LocalHistoryWindow.Show(full, Path.GetFileName(Path.TrimEndingDirectorySeparator(full)),
            () => new HistoryTab(history, new HistoryHost(Editors, RevertAsync, Diffs), full, isFolder));
    }

    /// <summary>Recent Changes: the folder's change sets, newest first; the one chosen opens in the project's
    /// history.</summary>
    public async Task<ChoicePopup?> ShowRecentChangesAsync()
    {
        if (session.LocalHistory is not { } history || Folder is not { } open)
        {
            status.Text = Refusals.NoFolder;
            return null;
        }
        IReadOnlyList<HistoryEvent> events = history.Snapshot().Events;
        IReadOnlyList<HistoryRow> rows = await BackgroundWork.Run(() => HistoryRows.OfFolder(events, open));
        ChoicePopup popup = new("Recent Changes", "Search by name",
            [.. rows.Select(r => new Choice(r.Title, ToolWindows.GitLogView.When(r.Time), r))], "There are no changes");
        popups.Show(popup, chosen =>
        {
            if (chosen?.Value is HistoryRow row)
            {
                ShowLocalHistory(open)?.SelectSet(row.Set);
            }
        });
        return popup;
    }

    /// <summary>Put Label: a name, put across the folder's history now.</summary>
    public NamePopup? ShowPutLabel()
    {
        if (session.LocalHistory is not { } history)
        {
            status.Text = Refusals.NoFolder;
            return null;
        }
        NamePopup popup = new("Put Label", string.Empty, name =>
        {
            string label = name.Trim();
            if (label.Length == 0)
            {
                return Task.FromResult<string?>("A label needs a name");
            }
            history.Label(label);
            status.Text = $"Label {label} created";
            return Task.FromResult<string?>(null);
        });
        popups.Show(popup, _ => { });
        return popup;
    }

    /// <summary>Revert Selected and Later Changes on a file or folder of the open folder, as its history's tab does with the
    /// row of a change set chosen: what it was before that set put back. Null once written, else why not.</summary>
    public async Task<string?> RevertLocalHistoryAsync(string path, long set)
    {
        if (session.LocalHistory is not { } history || Folder is not { } folder)
        {
            return Refusals.NoFolder;
        }
        string full = PathNames.Canonical(path);
        bool isFolder = Directory.Exists(full);
        (IReadOnlyList<HistoryEvent> events, IReadOnlyDictionary<string, string?> known) = history.Snapshot();
        IReadOnlyList<HistoryRow> rows = await BackgroundWork.Run(() => isFolder ? HistoryRows.OfFolder(events, full) : HistoryRows.OfFile(events, known, full));
        if (rows.FirstOrDefault(r => r.Set == set) is not { } row)
        {
            return $"No change set {set} is in the history of {PathNames.Shown(full, folder)}";
        }
        IReadOnlyList<(string Path, string? Version)> files = isFolder
            ? [.. (await BackgroundWork.Run(() => HistoryRows.Since(events, known, set, full))).Select(f => (f.Path, f.Then))]
            : [(full, row.Version)];
        return await RevertAsync(files, HistoryTab.RevertName(row));
    }

    /// <summary>Puts files back to versions, as one activity of the history: an open file's text and encoding as one
    /// undoable step over the lines that differ (<see cref="OpenDocument.Revert"/>); a closed one's bytes written as
    /// they were; one that did not exist then deleted (to the Trash, and kept in the history). Null once all are
    /// written, else why not.</summary>
    private async Task<string?> RevertAsync(IReadOnlyList<(string Path, string? Version)> files, string activity)
    {
        if (session.LocalHistory is not { } history)
        {
            return Refusals.NoFolder;
        }
        List<string> refused = [];
        using (history.During(activity))
        {
            foreach ((string path, string? version) in files)
            {
                if (version is null)
                {
                    if ((File.Exists(path) || Directory.Exists(path)) && !await DeleteAsync(path, asked: true))
                    {
                        refused.Add(Path.GetFileName(path));
                    }
                    continue;
                }
                byte[]? bytes = await BackgroundWork.Run(() => history.Content(version));
                if (bytes is null)
                {
                    refused.Add($"{Path.GetFileName(path)} (its content is no longer kept)");
                    continue;
                }
                OpenDocument? open = Editors.Find(path);
                try
                {
                    // A tab holding none of the file's text (a preview's, a workbook's, a binary file's) shows the bytes written.
                    if (open is not { HoldsText: true })
                    {
                        FileText.Restore(path, bytes);
                        if (open is not null)
                        {
                            FollowDisk(open);
                        }
                    }
                    else
                    {
                        open.Revert(bytes);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    refused.Add($"{Path.GetFileName(path)} ({error.Message})");
                    continue;
                }
                if (open is { IsModified: true } && !Editors.Save(open))
                {
                    refused.Add(open.Name);
                }
            }
        }
        FilesChanged();
        string? problem = refused.Count == 0 ? null : $"Cannot revert because {string.Join(", ", refused)} could not be written";
        status.Text = problem ?? activity;
        return problem;
    }
}
