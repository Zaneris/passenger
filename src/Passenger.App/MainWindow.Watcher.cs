using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Changes made outside the IDE, seen at once: the Explorer, Go to File, Git and the open files follow what
/// another program does in the folder, a project or solution changed on disk is read again, and an open file with
/// unsaved edits that changed on disk asks which to keep (File Cache Conflict).</summary>
public partial class MainWindow
{
    /// <summary>How long an open file found gone has to come back before it is taken for deleted: Git's checkout and
    /// rollback, and editors and formatters that save so, delete a file and write it anew, and the changes seen may be
    /// handed over between the two.</summary>
    private static readonly TimeSpan ComeBack = TimeSpan.FromMilliseconds(500);

    private readonly HashSet<OpenDocument> conflicted = [];
    private readonly HashSet<OpenDocument> gone = [];

    /// <summary>A save that found the file changed on disk asks which version to keep, as a change seen does.</summary>
    private void AddConflicts() => Editors.SaveConflicted += (_, document) =>
    {
        if (conflicted.Add(document))
        {
            IdeErrors.Observe(ResolveConflictAsync(document));
        }
    };

    /// <summary>How the folders opened from now on are watched: the system's way, unless another is chosen.</summary>
    public FolderWatching Watching { get; set; } = FolderWatching.Platform;

    /// <summary>The open folder's watcher.</summary>
    public FolderWatcher? FolderWatcher => session.Watcher;

    private void OnFolderChanged(FolderChanges changes)
    {
        if (session.Folder is not { } folder)
        {
            return;
        }
        session.LocalHistory?.Noticed(changes.Paths);
        if (changes.Projects || changes.Lost)
        {
            // Lost changes may have been a project's or the solution's: read again, which shows the whole Explorer anew.
            ReloadSolution();
        }
        else if (changes.Structure)
        {
            Explorer.RefreshFolders(changes.Paths.Select(Path.GetDirectoryName).OfType<string>());
            // A file come or gone may be one a project's wildcards take in: its folder's stamp says which.
            session.Evaluate();
        }
        else if (changes.Restored.Count > 0)
        {
            // A restore writes the props and targets a project imports beside its assets.
            session.Evaluate();
        }
        // A C# file written may have gained or lost the AppHost SDK's line; one come or gone is the index's walk's to find.
        session.AspireFiles.Changed(changes.Paths, every: changes.Lost);
        if (changes.Structure)
        {
            session.Index.Build(folder);
        }
        // An .editorconfig written, made or deleted: each open file follows what the ones over it say now.
        if (changes.Paths.Any(p => Path.GetFileName(p) == EditorConfig.FileName))
        {
            foreach (OpenDocument document in Editors.Documents)
            {
                document.ReadConventions();
            }
        }
        // A settings file, a launch profile or a compose file written: the reads of keys nothing gave are looked at again.
        if (changes.Lost || changes.Paths.Any(p => ConfigurationFiles.IsSettings(p) || Path.GetFileName(p).Equals(Run.LaunchProfile.SettingsFile, StringComparison.OrdinalIgnoreCase)
            || Web.ComposeDeclarations.IsCompose(p)))
        {
            session.CSharp?.ConfigurationChanged();
        }
        // A .resx or XLIFF file written: the reads of localized keys are looked at again.
        if (changes.Lost || changes.Paths.Any(Passenger.Xaml.Localization.ResourceFiles.Holds))
        {
            session.CSharp?.LocalizationChanged();
            session.Xaml.LocalizationChanged();
        }
        PackageSourcesChanged(changes);
        session.Tests.Finder.FolderChanged(changes);
        session.Xaml.FilesChanged(changes.Paths.Concat(changes.Restored));
        session.MSBuild.FilesChanged(changes.Paths, changes.Structure);
        session.Web?.FilesChanged(changes.Paths);
        EndpointFilesChanged(changes);
        FollowOpenFiles(changes.Paths, every: changes.Lost);
        if (changes.Paths.Count > 0 || changes.Git)
        {
            IdeErrors.Observe(session.Vcs.RefreshAsync());
        }
    }

    /// <summary>The open files a change names, or names a folder over (a folder moved, renamed or trashed is seen as that
    /// folder alone): each still there takes the disk's text; one gone follows its folder where a folder just come holds
    /// it as it was (a move, whose halves may be seen apart), else is awaited as a file deleted. With <paramref name="every"/>
    /// (changes lost) every open file is looked at so.</summary>
    private void FollowOpenFiles(IReadOnlySet<string> paths, bool every)
    {
        List<string>? come = null;
        foreach (OpenDocument document in Editors.Documents.ToList())
        {
            // A folder still there is named too as a file in it changes (Windows'), which leaves its other files as they are.
            bool named = every || paths.Contains(document.Path) || paths.Any(p => PathNames.IsUnder(document.Path, p) && !Directory.Exists(p));
            if (File.Exists(document.Path))
            {
                if (named)
                {
                    FollowDisk(document);
                }
                continue;
            }
            come ??= [.. paths.Where(Directory.Exists)];
            if (!FollowMove(document, come) && named)
            {
                AwaitReturn(document);
            }
        }
    }

    /// <summary>Whether an open file gone with a folder followed it to a folder just come, with the other open files in it:
    /// the highest folder over it that is gone is the one moved, and the new one holds the file as last loaded or saved.</summary>
    private bool FollowMove(OpenDocument document, IReadOnlyList<string> come)
    {
        string? moved = null;
        for (string? folder = Path.GetDirectoryName(document.Path); folder is not null && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            moved = folder;
        }
        if (moved is null || come.FirstOrDefault(to => !PathNames.IsUnder(moved, to) && document.IsSavedAt(PathNames.Moved(document.Path, moved, to))) is not { } place)
        {
            return false;
        }
        Editors.FollowMove(moved, place);
        return true;
    }

    /// <summary>Files a program the IDE ran has just written (dotnet add package's project file): those open take the
    /// disk's text now, as the folder watcher would have them a moment later, so what ran it answers with them changed.</summary>
    public void FollowWritten(IEnumerable<string> paths)
    {
        HashSet<string> written = new(paths.Select(PathNames.Canonical), StringComparer.Ordinal);
        foreach (OpenDocument document in Editors.Documents.Where(d => written.Contains(d.Path) && File.Exists(d.Path)).ToList())
        {
            FollowDisk(document);
        }
    }

    /// <summary>Files the IDE itself is about to write on disk (a project's references or items, the solution's entries):
    /// those open with unsaved edits are saved first, so what is written holds the edits and no File Cache Conflict asks
    /// afterwards which of the two to keep, losing the other. Null once saved, else why not, and nothing is to be written.</summary>
    private string? SaveBeforeWriting(IEnumerable<string> paths)
    {
        HashSet<string> writing = new(paths.Select(PathNames.Canonical), PathNames.Comparer);
        foreach (OpenDocument document in Editors.Documents.Where(d => d.IsModified && writing.Contains(d.Path)).ToList())
        {
            if (!Editors.Save(document))
            {
                return $"{document.Name} could not be saved first";
            }
        }
        return null;
    }

    /// <summary>An open file changed on disk: its text is taken, or with unsaved edits the person is asked which to keep.</summary>
    private void FollowDisk(OpenDocument document)
    {
        if (document.IsWorkbook)
        {
            // Its tab holds no text to take: its table reads the file again.
            Tables.TableFile.For(document)?.Follow();
            return;
        }
        if (document.Asset is not null)
        {
            // Nor does a preview's: its picture or face is decoded again off the UI thread, the one shown kept until then.
            document.Reload();
            return;
        }
        if (!document.ChangedOnDisk())
        {
            return;
        }
        if (!document.IsModified)
        {
            document.Reload();
        }
        else if (conflicted.Add(document))
        {
            IdeErrors.Observe(ResolveConflictAsync(document));
        }
    }

    /// <summary>An open file found gone: back within a moment, it takes the new text; still gone, it was deleted
    /// elsewhere and its tab goes, unless it holds edits that would go with it.</summary>
    private void AwaitReturn(OpenDocument document)
    {
        if (document.IsModified || !gone.Add(document))
        {
            return;
        }
        // Held while it waits, so a screenshot or a test finds the tab as the wait leaves it.
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Delay(ComeBack).ContinueWith(_ =>
        {
            UiThread.Post(() =>
            {
                gone.Remove(document);
                if (!Editors.Documents.Contains(document))
                {
                    return;
                }
                if (File.Exists(document.Path))
                {
                    FollowDisk(document);
                }
                else if (!document.IsModified)
                {
                    Editors.CloseDeleted(document);
                }
            });
            held.Dispose();
        }, TaskScheduler.Default));
    }

    /// <summary>File Cache Conflict: the disk's version, or the edits made here, written over the disk's. Until it is
    /// answered the file is never saved, so the answer is the person's.</summary>
    private async Task ResolveConflictAsync(OpenDocument document)
    {
        try
        {
            bool fromDisk = await AskConflict(document);
            if (fromDisk)
            {
                document.Reload(overUnsaved: true);
            }
            else
            {
                Editors.SaveOver(document);
            }
        }
        finally
        {
            conflicted.Remove(document);
        }
    }

    /// <summary>The question itself: true for Load File System Changes.</summary>
    public Func<OpenDocument, Task<bool>> AskConflict
    {
        get => askConflict ?? (document => new ConfirmDialog("File Cache Conflict",
            $"Changes have been made to {document.Name} in memory and on disk. Load the version on disk, losing the changes made here, or keep the changes made here, writing them over it?",
            "Load File System Changes", "Keep Memory Changes").ShowDialog<bool>(this));
        set => askConflict = value;
    }

    private Func<OpenDocument, Task<bool>>? askConflict;
}
