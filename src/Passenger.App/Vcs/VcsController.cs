using Passenger.App.Containers;
using Passenger.App.Mcp;
using Passenger.App.Templates;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;
using Passenger.Workspace;

namespace Passenger.App.Vcs;

/// <summary>The open folder's Git working tree as the window shows it: the branch, the changed files, each open file's
/// change bars against the last commit, and committing, pushing, switching branches and rolling back, after which the
/// open files take what is on disk again. Its folder's session's: disposed with it, it lets go of the open files. A
/// UI-thread object.</summary>
public sealed class VcsController : IDisposable
{
    /// <summary>How long typing pauses before an annotated file is blamed again: a git each time, so longer.</summary>
    private static readonly TimeSpan BlameDelay = TimeSpan.FromMilliseconds(500);

    public const string Busy = "Git is still busy with the last change";


    /// <summary>What a commit's work is held under, which the Commit window's button starts and no action names.</summary>
    public const string Committing = "vcs.commit";

    /// <summary>The window's work under way, which a push, update, fetch or commit shows itself in from its first moment
    /// until it ends: null with no window to show it in.</summary>
    public ActionWork? Work { get; set; }

    private readonly Dictionary<OpenDocument, Tracked> documents = [];

    /// <summary>Files' bytes in the commit <see cref="textsOf"/> (null before the first commit), which a read sets once
    /// it has found HEAD since the folder opened (<see cref="textsRead"/>). They are kept until HEAD names another, so a
    /// refresh that finds it where it was reads none of them again.</summary>
    private readonly Dictionary<string, HeadBytes> heads = [];
    private string? textsOf;
    private bool textsRead;
    private readonly List<(string From, string To)> moves = [];

    /// <summary>Where each file the last status lists as renamed came from, by its path now: a rename git was told of
    /// wherever it was made (git mv in a terminal, a move here in an earlier session), which its bars, diff and rollback
    /// compare with.</summary>
    private Dictionary<string, string> renamed = new(PathNames.Comparer);

    private Git? git;
    private int opened;
    private TaskCompletionSource? opening;

    /// <summary>Reads go one at a time, so an older read never lands after a newer one (a save's status after the
    /// commit's); those asked for while one runs are one read when it ends, answered together, reading the history
    /// too when any of them asked for it.</summary>
    private bool reading;
    private TaskCompletionSource? asked;
    private bool historyAsked;

    /// <summary>The working tree the open folder is in, or null when it is in none.</summary>
    public Git? Repository => git;

    /// <summary>The signatures of the commits the window shows, read as they are shown.</summary>
    public CommitSignatures Signatures { get; } = new();

    /// <summary>Whether commits made here are signed off (--signoff), as the folder keeps it: the Commit window's Sign-off,
    /// which vcs_commit follows too.</summary>
    public bool SignOff
    {
        get => signOff;
        set
        {
            if (signOff != value)
            {
                signOff = value;
                KeptChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool signOff;

    /// <summary>Whether the Commit window lists its files under their folders, as the folder keeps it (Group by
    /// Directory).</summary>
    public bool GroupsByDirectory
    {
        get => groupsByDirectory;
        set
        {
            if (groupsByDirectory != value)
            {
                groupsByDirectory = value;
                KeptChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool groupsByDirectory;

    /// <summary>Raised when <see cref="SignOff"/> or <see cref="GroupsByDirectory"/> changes, for the folder to keep them.</summary>
    public event EventHandler? KeptChanged;

    /// <summary>Why git cannot read the folder's repository: it refused the one the folder is in (one another user owns,
    /// until safe.directory names it), in git's words, or it is not installed (<see cref="GitMissing"/>); null when it
    /// took one or found none. Each read looks again, so the window follows once git lets it in or is installed.</summary>
    public string? Refused { get; private set; }

    /// <summary>Whether git is not installed, so whether the folder is in a repository is not known.</summary>
    public bool GitMissing { get; private set; }

    /// <summary>What is said where Git is asked for and the window has none: why git cannot read one, where it cannot.</summary>
    public string NoRepository => Refused ?? Refusals.NoRepository;

    /// <summary>Why nothing goes to a remote or comes from one (Push, Fetch, Update Project, a branch's Update and Push…, a
    /// remote's branch deleted): the repository is in a folder not trusted, where git reaches no remote, as it could not
    /// be kept from running what the repository's settings for one name (<see cref="UntrustedGit"/>); null otherwise.</summary>
    public string? WhyNotRemote => git is { } repository && !WorkspaceTrust.Trusts(repository.Root) ? Refusals.Untrusted : null;

    /// <summary>Why Rollback cannot put <paramref name="changes"/> back: in a folder not trusted, git would write out one it
    /// stores through a filter, which runs only once the folder is trusted (<see cref="FileChange.Unfiltered"/>), as the last
    /// status read them; null where it can. A file added or unversioned is put back without git writing it.</summary>
    public string? WhyNotRollback(IReadOnlyList<FileChange> changes) =>
        changes.Where(c => c.Kind is not (ChangeKind.Unversioned or ChangeKind.Added)).Select(c => c.Unfiltered).FirstOrDefault(why => why is not null);

    /// <summary>Why the changes cannot be stashed: in a folder not trusted, git would store one it stores through a filter,
    /// and write it out again from the head, as the last status read them; null where they can be.</summary>
    public string? WhyNotStash => Changes.Select(c => c.Unfiltered).FirstOrDefault(why => why is not null);

    /// <summary>Why a branch cannot be checked out by its name (a local one, or a remote's one would be made from): in a
    /// folder not trusted, git would write out a file it stores through a filter; null where it can.</summary>
    public async Task<string?> WhyNotCheckoutAsync(string branch) => git is { } repository ? await repository.WhyNotCheckedOutAsync(branch) : null;

    /// <summary><see cref="WhyNotCheckoutAsync"/> of the branches panel's Checkout (<see cref="CheckoutRefAsync"/>).</summary>
    public async Task<string?> WhyNotCheckoutRefAsync(BranchInfo branch, IReadOnlyList<BranchInfo> all)
    {
        if (git is not { } repository)
        {
            return null;
        }
        if (branch.CheckedOutElsewhere is { } worktree)
        {
            return Refusals.InWorktree(branch.Name, worktree);
        }
        string? local = branch.Kind == RefKind.Local ? branch.Name
            : branch.Kind == RefKind.Remote && all.Any(b => b.Kind == RefKind.Local && b.Name == LocalNameOf(branch)) ? LocalNameOf(branch)
            : null;
        return local is not null ? await repository.WhyNotCheckedOutAsync(local) : await repository.WhyNotSwitchedAsync(branch.Ref.FullName);
    }

    /// <summary>Why a stash cannot be applied: in a folder not trusted, git would write out a file it stores through a
    /// filter; null where it can.</summary>
    public async Task<string?> WhyNotUnstashAsync(StashInfo stash) => git is { } repository ? await repository.WhyNotUnstashedAsync(stash) : null;

    /// <summary>The open folder, whose working tree each read looks for again while the window has none (git refused it,
    /// is not installed, or found none: one made since, by git init in a terminal or a clone, is taken), and a read after
    /// Git's own state moved looks for again where it has one (one removed, or another put in its place, is let go).</summary>
    private string? lookIn;

    /// <summary>Whether the next read looks for the working tree again though the window has one: Git's state moved.</summary>
    private bool lookAgain;

    /// <summary>Whether the open folder was trusted as its working tree was looked for: a read once that changed looks for
    /// it again, as git takes the tree a repository's settings name only in a folder trusted (<see cref="UntrustedGit"/>).</summary>
    private bool foundTrusted;

    /// <summary>The branch checked out, or null on a detached head or outside a working tree.</summary>
    public string? Branch { get; private set; }

    /// <summary>The upstream the branch tracks as git shortens it (origin/main), as the last status read it; null where it
    /// tracks none.</summary>
    public string? Upstream { get; private set; }

    /// <summary>How many commits the branch has that its upstream lacks, and the upstream has that it lacks, as the remote's
    /// branch was last fetched; null where it tracks none or its upstream is gone. Counted again only once the history
    /// moved (<see cref="DivergenceCounts"/>), never for a save.</summary>
    public (int Ahead, int Behind)? Divergence { get; private set; }

    /// <summary>How many times git has counted the commits between the branch and its upstream.</summary>
    public int DivergenceCounts { get; private set; }

    /// <summary>What the last count was of, and what it found.</summary>
    private ((Git Tree, string? Branch, string Upstream, UInt128? Refs) Of, (int Ahead, int Behind)? Found)? counted;

    /// <summary>The changed and new files, as the last status read them, but the unversioned files a test framework wrote
    /// as its own output (<see cref="IgnoredFolders.InToolOutput"/>).</summary>
    public IReadOnlyList<FileChange> Changes { get; private set; } = [];

    /// <summary>The files in conflict, as the last status read them (each also among <see cref="Changes"/>).</summary>
    public IReadOnlyList<GitConflict> Conflicts { get; private set; } = [];

    /// <summary>What the tree is in the middle of (a merge, a rebase), as the last status found it.</summary>
    public MergeState Merge { get; private set; } = MergeState.Idle;

    /// <summary>The conflicts' two sides in their words, for what the tree is in the middle of.</summary>
    public ConflictSides Sides => Merge.Sides(unstashed);

    /// <summary>The stash an unstash with conflicts applied, which names the sides until they are resolved.</summary>
    private string? unstashed;

    /// <summary>How many reads of the status have begun.</summary>
    private long readsBegun;

    /// <summary>The reads begun when the last unstash's git ended (all of them while it runs): only a read begun after
    /// it can find its conflicts resolved, as one begun before may have read the tree before git wrote them.</summary>
    private long readsBeforeUnstash;

    /// <summary>Raised when an operation (an update, an unstash, a cherry-pick, a rebase continued) stopped for
    /// conflicts.</summary>
    public event EventHandler? ConflictsFound;

    /// <summary>Raised when the branch or the changed files have been read again.</summary>
    public event EventHandler? Changed;

    /// <summary>How many times git has been asked for last-commit texts: once for all the open files together when HEAD
    /// moves, once for each file looked at since, and never by a refresh that finds HEAD where it was.</summary>
    public int HeadReads { get; private set; }

    /// <summary>How many times an open file's text has been compared with its last commit's: once after each pause in
    /// typing and each new last-commit text, never again for a popup, a rollback or a diff of the text its bars are of.</summary>
    public int Comparisons { get; private set; }

    /// <summary>The most memory one comparison has taken, in bytes allocated on its thread: a few times the file's text,
    /// however much of it differs.</summary>
    public long LargestComparison { get; private set; }

    /// <summary>Raised after the whole tree was read again, as after a commit, a checkout or an update, when the
    /// history may have moved too (a save alone changes only the files).</summary>
    public event EventHandler? Refreshed;

    /// <summary>The history moved (a commit, a checkout, a fetch, a reset), so the log is read again; raised after Refreshed
    /// when it did, never for a save.</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>The refs' hash and HEAD's commit as the history was last read.</summary>
    private (UInt128 Refs, string? Head)? knownHistory;

    /// <summary>Whether Git's own state moving is heard, by the folder watcher where Git's folder is the open folder's .git
    /// and by <see cref="gitWatchers"/> where it is elsewhere, so files changed alone leave the history unread.</summary>
    private bool historyHeard;

    /// <summary>Git's folders outside the open folder (a linked worktree's and its common folder, a submodule's, the
    /// repository's above a folder opened below its top), watched for Git's own state moving.</summary>
    private readonly List<GitWatcher> gitWatchers = [];

    /// <summary>The watchers of Git's folders outside the open folder.</summary>
    public IReadOnlyList<GitWatcher> GitWatchers => gitWatchers;

    /// <summary>Has each watcher of Git's folders outside the open folder look again, off the window's thread, for what the
    /// system dropped without a word (<see cref="WatchedTree.LookAgainAsync"/>), as the window comes in front: done once
    /// what they found is handed over.</summary>
    public Task WatchersLookAgainAsync()
    {
        UiThread.Check();
        return Task.WhenAll(gitWatchers.Select(watcher => watcher.LookAgainAsync()));
    }

    /// <summary>How Git's folders outside the open folder are watched: the session's way.</summary>
    public FolderWatching Watching { get; set; } = FolderWatching.Platform;

    /// <summary>What happened, for the status bar: what is under way (a push), and each operation's outcome in the words
    /// it returns them in (a commit made, a push refused and git's reason).</summary>
    public event EventHandler<string>? Message;

    /// <summary>A commit, an update, a rollback, a stash or a change of branch is running. Another is refused until it
    /// ends, since git's index lock would fail it halfway (a double-click on Commit).</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Raised when <see cref="IsBusy"/> changes, for the buttons that start one.</summary>
    public event EventHandler? BusyChanged;

    /// <summary>Done once nothing runs: what follows from an operation's end (the rebase offered to go on once the last
    /// conflict is resolved) waits for it, rather than being refused as busy.</summary>
    public Task Idle => IsBusy ? (idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;

    private TaskCompletionSource? idle;

    /// <summary>Saves every open file and waits for the saves still landing, run before anything git does to the files (a
    /// commit, a checkout, a stash, a rollback, an update): what is committed is what the editor shows, and nothing unsaved
    /// is left behind for the next save to write over what git put there. Null once all are on disk, else why not, and git
    /// is not run.</summary>
    public Func<Working?, CancellationToken, Task<string?>>? SaveFirst { get; set; }

    private Task<string?> SavedFirstAsync(Working? work = null) =>
        SaveFirst?.Invoke(work, work?.Stopping ?? CancellationToken.None) ?? Task.FromResult<string?>(null);

    /// <summary>Names what an operation does to the files, for Local History, by its activity's name (Git checkout,
    /// Rollback, Update from VCS): what it changes is kept under that name until the hold is disposed.</summary>
    public Func<string, IDisposable?>? Activity { get; set; }

    /// <summary>Has Local History read the files a Git operation is about to write (a checkout's, an update's, a stash's, a
    /// resolution's, a rollback's: <see cref="Git.Writing"/>) as they stand, so what it writes over each is kept as a change
    /// from that, under its activity's name.</summary>
    public Func<IReadOnlyList<string>, Task>? LookFirst { get; set; }

    private Task LookedFirstAsync(IReadOnlyList<string> paths) => LookFirst?.Invoke(paths) ?? Task.CompletedTask;

    /// <summary>Moves the files an operation takes away that git has nothing of (a rollback's unversioned files) to the
    /// Trash, as one command of Undo named as the operation, which brings them back: each that stayed, saying why. With
    /// none, they stay, said so.</summary>
    public Func<string, IReadOnlyList<string>, Task<IReadOnlyList<string>>>? MoveToTrash { get; set; }

    /// <summary>Done once the folder last opened is known to be in a working tree or not, so a question asked straight
    /// after a folder opens waits for that rather than hearing there is none.</summary>
    public Task Opened => opening?.Task ?? Task.CompletedTask;

    /// <summary>Finds the folder's working tree and reads it; a folder in none leaves the window without Git.</summary>
    public async Task OpenAsync(string folder)
    {
        UiThread.Check();
        int generation = ++opened;
        TaskCompletionSource lookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        opening = lookup;
        GitLookup found;
        try
        {
            bool trusted = WorkspaceTrust.Trusts(folder);
            found = await Git.OpenAsync(folder);
            if (generation != opened)
            {
                return;
            }
            foundTrusted = trusted;
            Take(folder, found);
        }
        finally
        {
            lookup.TrySetResult();
        }
        await RefreshAsync();
        if (generation == opened && Refused is { } refused)
        {
            Message?.Invoke(this, refused);
        }
        else if (generation == opened && found.WorkTreeNamed is { } named && git is { } repository)
        {
            Message?.Invoke(this, $"Git works on {repository.Root} alone while it is not trusted: the repository's settings name its working tree at {named}");
        }
    }

    /// <summary>What a lookup found becomes the window's working tree, read from the start.</summary>
    private void Take(string folder, GitLookup found)
    {
        git = found.Tree;
        if (git is not null)
        {
            git.Writing = LookedFirstAsync;
        }
        Refused = found.Refused;
        GitMissing = found.Missing;
        lookIn = folder;
        WatchGit(folder);
        historyHeard = git is not null;
        heads.Clear();
        textsOf = null;
        textsRead = false;
        moves.Clear();
        renamed = new(PathNames.Comparer);
    }

    /// <summary>Watches Git's folders the folder watcher does not: Git's folder and its common folder, wherever they are but
    /// the open folder's .git.</summary>
    private void WatchGit(string folder)
    {
        Unwatch();
        if (git is not { } repository)
        {
            return;
        }
        string own = Path.Join(folder, ".git");
        foreach (string state in new[] { repository.GitDir, repository.CommonDir }.Distinct(PathNames.Comparer).Where(d => !PathNames.Same(d, own)))
        {
            GitWatcher watcher = new(state, Watching);
            watcher.Moved += (_, _) =>
            {
                if (git == repository)
                {
                    IdeErrors.Observe(FolderChangedAsync(gitMoved: true));
                }
            };
            gitWatchers.Add(watcher);
        }
    }

    private void Unwatch()
    {
        foreach (GitWatcher watcher in gitWatchers)
        {
            watcher.Dispose();
        }
        gitWatchers.Clear();
    }

    /// <summary>The folder closed: the window is in no working tree, as with a folder outside one, until another opens;
    /// a lookup still under way for the folder is dropped.</summary>
    public Task CloseAsync()
    {
        UiThread.Check();
        opened++;
        Unwatch();
        git = null;
        Refused = null;
        GitMissing = false;
        lookIn = null;
        lookAgain = false;
        heads.Clear();
        textsOf = null;
        textsRead = false;
        moves.Clear();
        renamed = new(PathNames.Comparer);
        return RefreshAsync();
    }

    /// <summary>Its folder left, it follows the open files no more (their change bars and blame), and a lookup still under
    /// way is dropped; the views that showed it take the next folder's.</summary>
    public void Dispose()
    {
        UiThread.Check();
        opened++;
        Unwatch();
        foreach (OpenDocument document in documents.Keys.ToList())
        {
            Detach(document);
        }
    }

    /// <summary>Create Git Repository's title, its action's and the empty states' that offer it where the folder is in none.</summary>
    public const string CreateRepository = "Create Git Repository";

    /// <summary>Why Create Git Repository cannot run, or null where it can: no folder is open, the folder is in a working
    /// tree already, or git refuses the one it is in or is not installed.</summary>
    public string? WhyNotCreate => lookIn is null ? Refusals.NoFolder : git is not null ? Refusals.InRepository : Refused;

    /// <summary>Create Git Repository: the open folder made a working tree and read as it is when a folder opens, given
    /// first the .gitignore every repository Passenger makes is given where it has none (<see cref="RepositoryFiles.GitIgnoreAsync"/>),
    /// so build output is never offered for its first commit. A .gitignore that could not be written is said with the
    /// repository made; one written for a repository git then could not make is taken away again.</summary>
    public async Task<VcsOutcome> InitAsync()
    {
        UiThread.Check();
        if (WhyNotCreate is { } why)
        {
            return Report(false, why);
        }
        string folder = lookIn!;
        string ignore = Path.Join(folder, ".gitignore");
        bool ignoring = Path.Exists(ignore);
        if (!Begin(AppActions.CreateRepository, ignoring ? Creating : "Writing .gitignore…", out Working? work))
        {
            return Report(false, UnderWay(AppActions.CreateRepository));
        }
        using Working? held = work;
        using IDisposable busy = BackgroundWork.Scope();
        bool wrote = false;
        string? unwritten = null;
        if (!ignoring)
        {
            (string? text, string? unmade) = await RepositoryFiles.GitIgnoreAsync(folder);
            (IReadOnlyList<string> written, string? refused) = text is null ? ((IReadOnlyList<string>)[], unmade) : await RepositoryFiles.WriteAsync(folder, [(RepositoryFile.GitIgnore, text)]);
            wrote = written.Count > 0;
            unwritten = refused;
        }
        work?.Say(Creating);
        GitResult init = await Git.InitAsync(folder);
        if (!init.Succeeded)
        {
            if (wrote)
            {
                Unwrite(ignore);
            }
            return Report(false, $"No Git repository was created: {init.Message}");
        }
        await OpenAsync(folder);
        string created = $"Created a Git repository in {Path.GetFileName(folder)}";
        return git is null ? Report(false, NoRepository)
            : Report(true, unwritten is null ? created : $"{created}, with no .gitignore: {unwritten}");
    }

    /// <summary>Create Git Repository's stage as git makes the repository.</summary>
    private const string Creating = "Creating the Git repository…";

    /// <summary>Takes away the .gitignore Create Git Repository wrote for a repository git could not make.</summary>
    private static void Unwrite(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Git: the .gitignore written for a repository not made is kept: {kept.Message}");
        }
    }

    /// <summary>Reads the branch, the changed files and the history again, and when HEAD has moved every open file's
    /// last-commit text and change bars; done once a read begun after the call has landed.</summary>
    public Task RefreshAsync()
    {
        UiThread.Check();
        historyAsked = true;
        return ReadAsync();
    }

    /// <summary>Another program changed the folder's files, or Git's own state with <paramref name="gitMoved"/>: the status
    /// read again, and the history too where Git's state moved or the folder watcher cannot hear it move; where Git's state
    /// moved, the working tree is looked for again first, as a repository made, removed or replaced moves it.</summary>
    public Task FolderChangedAsync(bool gitMoved)
    {
        UiThread.Check();
        historyAsked |= gitMoved || !historyHeard;
        lookAgain |= gitMoved;
        return ReadAsync();
    }

    /// <summary>Asks for a read: the next one to start, which starts now unless one is running.</summary>
    private Task ReadAsync()
    {
        TaskCompletionSource next = asked ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!reading)
        {
            IdeErrors.Observe(ReadInTurnAsync());
        }
        return next.Task;
    }

    /// <summary>Runs the reads asked for, one after another, until none is.</summary>
    private async Task ReadInTurnAsync()
    {
        reading = true;
        try
        {
            while (asked is { } answered)
            {
                asked = null;
                bool history = historyAsked;
                bool again = lookAgain;
                historyAsked = false;
                lookAgain = false;
                try
                {
                    await ReadOnceAsync(history, again);
                    answered.SetResult();
                }
                catch (Exception failed)
                {
                    answered.SetException(failed);
                }
            }
        }
        finally
        {
            reading = false;
        }
    }

    /// <summary>One read: the status (the branch, HEAD and the files), with the refs when the history is wanted too. The
    /// working tree is looked for again first where the window has none, or <paramref name="again"/>, and after a status
    /// git could not read, unless it was just <paramref name="looked"/> for; another found is read whole.</summary>
    private async Task ReadOnceAsync(bool history, bool again, bool looked = false)
    {
        if (!looked && (git is null || again || (lookIn is { } folder && WorkspaceTrust.Trusts(folder) != foundTrusted)))
        {
            looked = true;
            history |= await LookAgainAsync();
        }
        if (git is not { } repository)
        {
            Branch = null;
            Upstream = null;
            Divergence = null;
            Changes = [];
            Conflicts = [];
            Merge = MergeState.Idle;
            unstashed = null;
            bool taken = !textsRead;
            if (taken)
            {
                ReadTexts(null, null);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            if (history)
            {
                Refreshed?.Invoke(this, EventArgs.Empty);
                // The log shows a folder just found in no working tree, as it shows a working tree's first read.
                if (knownHistory is not null || taken)
                {
                    knownHistory = null;
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            return;
        }
        GitStatus? status = null;
        MergeState? merge = null;
        UInt128? refs = null;
        (int Ahead, int Behind)? divergence = null;
        string? failure = null;
        long begun = 0;
        try
        {
            await indexed;
            begun = ++readsBegun;
            status = await repository.StatusAsync();
            merge = await repository.MergeStateAsync(status.Branch, status.Head);
            refs = history ? await repository.RefsAsync() : null;
            divergence = await DivergenceAsync(repository, status, refs ?? knownHistory?.Refs);
        }
        catch (InvalidOperationException failed)
        {
            failure = failed.Message;
        }
        if (readHeld is { } held)
        {
            readHeld = null;
            held.Answered.SetResult();
            await held.Until;
        }
        if (git != repository)
        {
            // Another folder was opened meanwhile: this is the last one's.
            return;
        }
        if (failure is not null && !looked && await LookAgainAsync())
        {
            // The working tree went, or another took its place, as it was read.
            await ReadOnceAsync(history: true, again: false, looked: true);
            return;
        }
        if (failure is not null)
        {
            Message?.Invoke(this, failure);
        }
        if (status is not null)
        {
            Branch = status.Branch;
            Upstream = status.Upstream;
            Divergence = divergence;
            Changes = status.Changes.Any(c => c.Kind is ChangeKind.Unversioned && IgnoredFolders.InToolOutput(c.Path))
                ? [.. status.Changes.Where(c => c.Kind is not ChangeKind.Unversioned || !IgnoredFolders.InToolOutput(c.Path))] : status.Changes;
            Conflicts = status.Conflicts;
            Merge = merge ?? MergeState.Idle;
            if (Conflicts.Count == 0 && begun > readsBeforeUnstash)
            {
                unstashed = null;
            }
            if (!textsRead || status.Head != textsOf)
            {
                renamed = Renames(status.Changes);
                ReadTexts(repository, status.Head);
            }
            else
            {
                TakeRenames(Renames(status.Changes));
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (!history)
        {
            return;
        }
        Refreshed?.Invoke(this, EventArgs.Empty);
        if (status is not null && refs is { } read && (read, status.Head) != knownHistory)
        {
            knownHistory = (read, status.Head);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The commits between the branch and its upstream: none to count where it tracks none or its upstream is gone,
    /// none apart where the status found the two at one commit, else as last counted while the branch, its upstream and the
    /// refs as the history last read them (<paramref name="refs"/>, which hold both commits) stay as they were, so a save or
    /// the window coming to the front counts none, and HEAD moving counts once its history is read.</summary>
    private async Task<(int Ahead, int Behind)?> DivergenceAsync(Git repository, GitStatus status, UInt128? refs)
    {
        if (status.Upstream is not { } upstream || status.Even is not { } even)
        {
            return null;
        }
        if (even)
        {
            return (0, 0);
        }
        (Git, string?, string, UInt128?) of = (repository, status.Branch, upstream, refs);
        if (counted is { } last && last.Of == of)
        {
            return last.Found;
        }
        DivergenceCounts++;
        (int Ahead, int Behind)? found = await repository.DivergenceAsync();
        counted = (of, found);
        return found;
    }

    /// <summary>What the next read of the status waits for once git has answered it (<see cref="HoldNextRead"/>).</summary>
    private (Task Until, TaskCompletionSource Answered)? readHeld;

    /// <summary>Holds the next read of the status, once git has answered it, from landing until <paramref name="until"/>
    /// ends, for a test of what the tree does meanwhile: done once git has answered.</summary>
    public Task HoldNextRead(Task until)
    {
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readHeld = (until, answered);
        return answered.Task;
    }

    /// <summary>Looks for the open folder's working tree again, taking what is found where it is not what the window holds (a
    /// repository made, removed or put in another's place by another program): whether it took another.</summary>
    private async Task<bool> LookAgainAsync()
    {
        if (lookIn is not { } folder)
        {
            return false;
        }
        int generation = opened;
        Git? held = git;
        bool trusted = WorkspaceTrust.Trusts(folder);
        GitLookup found = await Git.OpenAsync(folder);
        if (generation != opened || git != held)
        {
            return false;
        }
        foundTrusted = trusted;
        if (Holds(found))
        {
            return false;
        }
        Take(folder, found);
        return true;
    }

    /// <summary>Whether a lookup found what the window holds: the same working tree, or none for the same reason.</summary>
    private bool Holds(GitLookup found) => found.Tree is { } tree
        ? git is { } held && PathNames.Same(tree.Root, held.Root) && PathNames.Same(tree.GitDir, held.GitDir)
        : git is null && found.Refused == Refused && found.Missing == GitMissing;

    /// <summary>HEAD names another commit (or the tree is another): every open file's text in it, read by one git, and
    /// their change bars against it. A file open as its bytes (binary) has no bars, so its version is never read.</summary>
    private void ReadTexts(Git? repository, string? head)
    {
        textsRead = true;
        textsOf = head;
        heads.Clear();
        if (repository is not null && head is not null)
        {
            string[] paths = [.. documents.Keys.Where(d => d.HoldsText).Select(d => d.Path).Distinct().Where(repository.IsInside)];
            if (paths.Length > 0)
            {
                HeadReads++;
                Task<IReadOnlyDictionary<string, byte[]?>> read = repository.BytesAtAsync(head, [.. paths.SelectMany(p => new[] { p, Origin(p) })]);
                foreach (string path in paths)
                {
                    heads[path] = new HeadBytes(BytesOfAsync(read, path, Origin(path)));
                }
            }
        }
        foreach (OpenDocument document in documents.Keys)
        {
            IdeErrors.Observe(MarkAsync(document));
        }

        static async Task<StoredBytes?> BytesOfAsync(Task<IReadOnlyDictionary<string, byte[]?>> bytes, string path, string origin)
        {
            IReadOnlyDictionary<string, byte[]?> read = await bytes;
            return (read.GetValueOrDefault(path) ?? read.GetValueOrDefault(origin)) is { } stored ? StoredBytes.Of(stored) : null;
        }
    }

    /// <summary>Starts showing a file's change bars, worked out again as it is edited.</summary>
    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.ContainsKey(document))
        {
            return;
        }
        Tracked tracked = new(new Debounce(Editors.TypingPauses.ChangeBars, () => MarkAsync(document)), new Debounce(BlameDelay, () => BlameAsync(document)));
        tracked.OnChanged = (_, _) =>
        {
            // Outside a working tree a file has no bars to move; once one is found, every open file's are worked out.
            if (git is not null)
            {
                tracked.Marks.Restart();
            }
            if (tracked.Annotated)
            {
                tracked.Reblame.Restart();
            }
        };
        tracked.OnSaved = (_, _) => IdeErrors.Observe(ReadAsync());
        document.Text.Changed += tracked.OnChanged;
        document.Saved += tracked.OnSaved;
        documents[document] = tracked;
        IdeErrors.Observe(MarkAsync(document));
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out Tracked? tracked))
        {
            tracked.Marks.Stop();
            tracked.Reblame.Stop();
            // A blame still running lands nowhere.
            tracked.Blaming++;
            document.Text.Changed -= tracked.OnChanged;
            document.Saved -= tracked.OnSaved;
        }
    }

    /// <summary>The blame an annotated file shows, line by line, or null when it shows none.</summary>
    public IReadOnlyList<BlameLine>? BlameOf(OpenDocument document) => documents.GetValueOrDefault(document)?.Blame;

    /// <summary>Whether a file's annotations are open, its blame shown or on its way.</summary>
    public bool IsAnnotated(OpenDocument document) => documents.GetValueOrDefault(document)?.Annotated == true;

    /// <summary>Annotate with Git Blame: each line named in the gutter by the commit that last changed it, blamed again
    /// after each pause in typing until <see cref="CloseAnnotations"/>. False, with nothing shown, when Git does not
    /// track the file.</summary>
    public async Task<bool> AnnotateAsync(OpenDocument document)
    {
        UiThread.Check();
        if (!documents.TryGetValue(document, out Tracked? tracked))
        {
            return false;
        }
        tracked.Annotated = true;
        return await BlameAsync(document);
    }

    /// <summary>Close Annotations: the gutter's column goes, and a blame still running lands nowhere.</summary>
    public void CloseAnnotations(OpenDocument document)
    {
        UiThread.Check();
        if (documents.TryGetValue(document, out Tracked? tracked))
        {
            Unannotate(document, tracked);
        }
    }

    private static void Unannotate(OpenDocument document, Tracked tracked)
    {
        tracked.Annotated = false;
        tracked.Blame = null;
        tracked.Blaming++;
        tracked.Reblame.Stop();
        document.GutterAnnotations = [];
    }

    /// <summary>Blames the file's text as it is now and shows it, unless annotations closed, another blame began or the
    /// text changed meanwhile (the pause after that edit blames it again). False only when git said it does not track
    /// the file, which closes them.</summary>
    private async Task<bool> BlameAsync(OpenDocument document)
    {
        if (!documents.TryGetValue(document, out Tracked? tracked) || !tracked.Annotated)
        {
            return true;
        }
        int blaming = ++tracked.Blaming;
        TextSnapshot text = document.Text.Snapshot;
        IReadOnlyList<BlameLine>? lines = git is { } repository ? await repository.BlameAsync(document.Path, await StoredAsync(document, text)) : null;
        if (blaming != tracked.Blaming)
        {
            return true;
        }
        if (lines is null)
        {
            Unannotate(document, tracked);
            return false;
        }
        if (!ReferenceEquals(text, document.Text.Snapshot))
        {
            return true;
        }
        tracked.Blame = lines;
        document.GutterAnnotations = Labels(lines);
        return true;
    }

    /// <summary>A file's text as its save would write it (its encoding, byte order mark and CR breaks), off the window's
    /// thread.</summary>
    private static Task<byte[]> StoredAsync(OpenDocument document, TextSnapshot text)
    {
        System.Text.Encoding encoding = document.Encoding;
        bool bom = document.HasBom;
        bool cr = document.Separator == LineSeparator.CR;
        return BackgroundWork.Run(() => FileText.Encoded(text.ToString(), encoding, bom, cr));
    }

    /// <summary>Each commit's block named at its first line; a change not committed yet says so.</summary>
    private static List<string?> Labels(IReadOnlyList<BlameLine> lines)
    {
        List<string?> labels = [];
        for (int i = 0; i < lines.Count; i++)
        {
            BlameLine line = lines[i];
            bool first = i == 0 || lines[i - 1].Hash != line.Hash;
            string author = line.Author.Length > 14 ? line.Author[..14] : line.Author;
            labels.Add(!first ? null : line.IsCommitted ? $"{line.Time.ToLocalTime():yyyy-MM-dd} {author}" : "Not committed yet");
        }
        return labels;
    }

    /// <summary>Why a commit cannot be made of what is chosen (<paramref name="anyChosen"/>) with a message, in the order a
    /// commit refuses: files in conflict, a rebase stopped for them, nothing chosen, a chosen file no commit takes, the
    /// message empty; null when one can be made.</summary>
    public string? WhyNotCommit(bool anyChosen, string message, bool amend, IReadOnlyList<FileChange>? chosen = null) =>
        Conflicts.Count > 0 ? Refusals.CommitWithConflicts
        // Stopped for conflicts, a commit of its own would go beside the rebase's; a step that stops it (an edit) asks for one.
        : Merge.Operation is MergeOperation.Rebase && Merge.Stop is null ? $"{Merge.UnderWay}: Continue Rebase commits what is resolved and goes on, or Abort Rebase ends it"
        // A merge's commit finishes it even when it changed no file (every conflict resolved as HEAD had it).
        : !anyChosen && !amend && !Merge.CommitsWhole ? Refusals.NoFilesChosenToCommit
        : chosen?.Select(c => c.Uncommittable).FirstOrDefault(why => why is not null) is { } uncommittable ? uncommittable
        : message.Trim().Length == 0 ? Refusals.EmptyCommitMessage
        : null;

    /// <summary>Commits the chosen files (what git's index holds, <paramref name="staged"/>) with a message, signed as
    /// <paramref name="signing"/> says (as git's config says where it is null), then pushes when asked (as <see cref="PushAsync"/>
    /// asks): the commit's outcome with the commit made, and the push's. A commit not signed says why. Given
    /// <paramref name="movesFirst"/>, the moves of the files chosen whole are a commit of their own first, with that message
    /// (<see cref="Git.CommitMovesAsync"/>), unless amending or finishing a merge.</summary>
    public async Task<CommitResult> CommitAsync(IReadOnlyList<FileChange> chosen, string message, bool amend, bool push, bool ask = true, CommitSigning? signing = null,
        IReadOnlyDictionary<string, PartialCommit>? partial = null, string? movesFirst = null, bool staged = false)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return new CommitResult(Report(false, NoRepository), null, null);
        }
        if (((push ? WhyNotRemote : null) ?? WhyNotCommit(chosen.Count > 0, message, amend, chosen)) is { } refused)
        {
            return new CommitResult(Report(false, refused), null, null);
        }
        bool merging = Merge.CommitsWhole && !amend;
        if (!Begin(Committing, $"Committing {Plural.Of(chosen.Count, "file")}…", out Working? work))
        {
            return new CommitResult(Report(false, UnderWay(Committing)), null, null);
        }
        using Working? held = work;
        CommitResult result = await OneAtATimeAsync(async () =>
        {
            if (await SavedFirstAsync(work) is { } unsaved)
            {
                return new CommitResult(Report(false, unsaved), null, null);
            }
            string[] paths = [.. Paths(chosen)];
            string[] excluded = merging ? [.. Paths(Changes.Where(c => c.Kind is not ChangeKind.Unversioned && !chosen.Contains(c)))] : [];
            (Signer signer, SigningSetup setup) = await SignerAsync(repository, signing ?? CommitSigning.AsConfigured);
            CommitSigning given = signer.Signing;
            List<(string From, string To)> moved = movesFirst is null || amend || merging || staged ? [] : [.. Moves(chosen, partial)];
            CommitInfo? first = null;
            if (moved.Count > 0)
            {
                if (Failed(await repository.CommitMovesAsync(moved, movesFirst!, given, signer.Asker), given, setup) is { } unmoved)
                {
                    VcsOutcome refused = Report(false, unmoved);
                    await RefreshAsync();
                    return new CommitResult(refused, null, null);
                }
                first = await repository.HeadAsync();
                // The last commit has them where they went now, and their old names no more.
                paths = [.. paths.Except(moved.Select(m => m.From), PathNames.Comparer)];
            }
            GitResult commit = staged ? await repository.CommitIndexAsync(message, amend, given, signer.Asker)
                : await repository.CommitAsync(paths, message, amend, merging, excluded, given, signer.Asker, partial);
            if (Failed(commit, given, setup) is { } unmade)
            {
                VcsOutcome failed = Report(false, first is null ? unmade : $"The moves were committed as {first.Short}, but not the rest: {unmade}");
                await RefreshAsync();
                return new CommitResult(failed, null, null);
            }
            CommitInfo? made = await repository.HeadAsync();
            int count = chosen.Count;
            string committed = count switch
            {
                0 => "Committed",
                1 => "1 file committed",
                _ => $"{Numbers.Said(count)} files committed",
            };
            if (moved.Count > 0)
            {
                committed = $"{Plural.Of(moved.Count, "move")} committed, then {committed}";
            }
            VcsOutcome done = Report(true, $"{committed}: {ChildResult.FirstLineOf(message)}");
            await RefreshAsync();
            return new CommitResult(done, made, null) { Moves = first };
        }, refused => new CommitResult(refused, null, null));
        // Pushed once the commit has let go of the tree and ended its work, so a remote that does not answer holds neither.
        held?.Dispose();
        return push && result.Commit.Succeeded ? result with { Push = await PushAsync(ask) } : result;

        static IEnumerable<string> Paths(IEnumerable<FileChange> changes) => changes.SelectMany(c => c.From is { } from ? new[] { c.Path, from } : [c.Path]);
    }

    /// <summary>Why a commit failed in the person's words (its signing's, where it was to be signed); null once it was made.</summary>
    private static string? Failed(GitResult commit, CommitSigning given, SigningSetup setup)
    {
        commit = commit with { Output = ContainerSecrets.InSaid(commit.Output), Error = ContainerSecrets.InSaid(commit.Error) };
        if (commit.Succeeded)
        {
            return null;
        }
        bool signs = given.Sign ?? setup.Signs;
        return !signs ? commit.Message
            : commit.Unanswered is { } unanswered ? SigningFailure.Unsigned + unanswered
            : SigningFailure.Of(commit.Error, setup) ?? commit.Message;
    }

    /// <summary>The chosen files' moves, each where it was and where it is: those of renamed files chosen whole.</summary>
    private static IEnumerable<(string From, string To)> Moves(IEnumerable<FileChange> chosen, IReadOnlyDictionary<string, PartialCommit>? partial) =>
        chosen.Where(c => c is { Kind: ChangeKind.Renamed, From: not null } && partial?.ContainsKey(c.Path) != true).Select(c => (c.From!, c.Path));

    /// <summary>The chosen files moved whose content changed so far since that git, comparing the last commit with them as
    /// the disk has them, would find them no longer moved once committed (<see cref="Git.LostMovesAsync"/>): those a commit
    /// of the moves first keeps as renames. None where it takes none (an amend, a merge's commit).</summary>
    public async Task<IReadOnlyList<FileChange>> LostMovesAsync(IReadOnlyList<FileChange> chosen, IReadOnlyDictionary<string, PartialCommit>? partial = null)
    {
        UiThread.Check();
        List<(string From, string To)> moved = [.. Moves(chosen, partial)];
        if (git is not { } repository || moved.Count == 0 || Merge.CommitsWhole)
        {
            return [];
        }
        IReadOnlyList<(string From, string To)> lost = await repository.LostMovesAsync(moved);
        return [.. chosen.Where(c => lost.Any(l => l.To == c.Path && l.From == c.From))];
    }

    /// <summary>Push: the branch to the one it tracks, or where it tracks none, to the only remote or the one of several
    /// the person chooses (<see cref="ChooseRemote"/>, never asked of an agent: <paramref name="ask"/> false). Its work
    /// begins before anything is asked of git, so an agent's action hears where it went or why it did not.</summary>
    public async Task<VcsOutcome> PushAsync(bool ask = true)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (!Begin(AppActions.Push, $"Pushing {Branch}…", out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(AppActions.Push));
        }
        using Working? held = work;
        string? remote = null;
        if (!await repository.TracksAsync())
        {
            if (await repository.BranchAsync() is not { } branch)
            {
                return Report(false, "A detached head has no branch to push");
            }
            (remote, string? refused) = await RemoteForAsync(repository, branch, ask);
            if (refused is not null)
            {
                return Report(false, refused);
            }
        }
        if (await RemoteAsync(work, (stopping, asker) => repository.PushAsync(remote, stopping, asker)) is not { } pushed)
        {
            return Report(false, "Push stopped");
        }
        return Report(pushed.Succeeded, pushed.Succeeded ? $"Pushed {Branch}" : $"Push failed: {pushed.Message}");
    }

    /// <summary>Asks which of several remotes to take, given the question's words ("Push feature to") and the remotes; null
    /// once the person gives up. The window's, unset with none to ask in.</summary>
    public Func<string, IReadOnlyList<string>, Task<string?>>? ChooseRemote { get; set; }

    /// <summary>Where a branch that tracks no remote's branch is pushed: the only remote, or the one the person chooses of
    /// several; else why it is not pushed.</summary>
    private async Task<(string? Remote, string? Refused)> RemoteForAsync(Git repository, string branch, bool ask)
    {
        IReadOnlyList<string> remotes = await repository.RemotesAsync();
        if (remotes is [string only])
        {
            return (only, null);
        }
        if (remotes.Count == 0)
        {
            return (null, $"No remote to push {branch} to: the repository has none");
        }
        if (!ask || ChooseRemote is not { } choose)
        {
            return (null, $"{branch} tracks no remote's branch, and there are {Numbers.Said(remotes.Count)} remotes to push it to ({string.Join(", ", remotes)}): Push… in the window asks which");
        }
        return await choose($"Push {branch} to", remotes) is { } chosen ? (chosen, null) : (null, $"{branch} was not pushed: no remote was chosen");
    }

    /// <summary>Update Project: the upstream fetched, then its commits brought in as the person's git config says (merged
    /// where it says nothing), the uncommitted changes set aside and put back, then the open files and Git read again; one
    /// of Git's changes to the tree, refused while another runs and refusing others until it ends.</summary>
    public async Task<VcsOutcome> UpdateAsync()
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (Conflicts.Count > 0)
        {
            return Report(false, "Unmerged files detected. These conflicts must be resolved before update.");
        }
        if (!Begin(AppActions.UpdateProject, $"Updating {Branch}…", out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(AppActions.UpdateProject));
        }
        using Working? held = work;
        return await OneAtATimeAsync(async () =>
        {
            if (await repository.BranchAsync() is not { } branch)
            {
                return Report(false, "A detached head has no branch to update");
            }
            if (await repository.UpstreamAsync() is not { } upstream)
            {
                return Report(false, Refusals.Untracked(branch));
            }
            // Stop ends the fetch alone: the merge or rebase after it is the tree's own, never left halfway.
            if (await RemoteAsync(work, repository.FetchUpstreamAsync) is not { } fetched)
            {
                return Report(false, "Update stopped");
            }
            if (!fetched.Succeeded)
            {
                return Report(false, $"Update failed: {fetched.Message}");
            }
            if (await SavedFirstAsync(work) is { } unsaved)
            {
                return Report(false, unsaved);
            }
            UpdatePlan plan = await repository.UpdatePlanAsync(branch);
            (int ahead, int behind) = await repository.DivergenceAsync() ?? (0, 0);
            bool setAside = behind > 0 && await repository.HasTrackedChangesAsync();
            work?.Say(plan.Rebase && ahead > 0 ? $"Rebasing {branch} onto {upstream}…" : $"Merging {upstream} into {branch}…");
            // Named until Git is read again, so the folder watcher has heard of what it changed.
            using IDisposable? named = Activity?.Invoke("Update from VCS");
            (Signer signer, SigningSetup setup) = await SignerAsync(repository, CommitSigning.AsConfigured);
            GitResult integrated = Unsigned(await repository.IntegrateAsync(plan, signer), setup);
            ReloadOpenFiles();
            await RefreshAsync();
            if (Conflicted(Merge.Operation is MergeOperation.Rebase ? "Rebase stopped due to conflicts" : "Merged with conflicts") is { } stopped)
            {
                return stopped;
            }
            return Report(integrated.Succeeded, integrated.Succeeded ? Updated(branch, upstream, plan, ahead, behind, setAside) : $"Update failed: {integrated.Message}");
        }, refused => refused);
    }

    /// <summary>What an update did, in its words: the commits brought in, merged or rebased onto where the branch had its
    /// own too (merged, saying why, where git's config says neither), and the uncommitted changes set aside and put back.</summary>
    private static string Updated(string branch, string upstream, UpdatePlan plan, int ahead, int behind, bool setAside)
    {
        string commits = Plural.Of(behind, "commit");
        string how = behind == 0 ? $"already up to date with {upstream}"
            : ahead == 0 ? $"{commits} from {upstream}"
            : plan.Rebase ? $"rebased onto {commits} from {upstream}"
            : plan.Configured ? $"merged {commits} from {upstream}"
            : $"merged {commits} from {upstream}, as pull.rebase is not set";
        return $"Updated {branch}: {how}{(setAside ? "; local changes set aside and put back" : string.Empty)}";
    }

    /// <summary>After an operation that can stop for conflicts: when it did, what it came to, told to the status bar
    /// with the files, and the Conflicts dialog asked for; null when it left none.</summary>
    private VcsOutcome? Conflicted(string what)
    {
        if (Conflicts.Count == 0)
        {
            return null;
        }
        VcsOutcome stopped = Report(false, $"{what}: {string.Join(", ", Conflicts.Select(c => git?.Relative(c.Path) ?? c.Name))}");
        ConflictsFound?.Invoke(this, EventArgs.Empty);
        return stopped;
    }

    public async Task<VcsOutcome> FetchAsync()
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (!Begin(AppActions.Fetch, "Fetching…", out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(AppActions.Fetch));
        }
        using Working? held = work;
        GitResult? fetched = await RemoteAsync(work, repository.FetchAsync);
        await RefreshAsync();
        return fetched is null ? Report(false, "Fetch stopped")
            : Report(fetched.Succeeded, fetched.Succeeded ? "Fetched" : $"Fetch failed: {fetched.Message}");
    }

    /// <summary>One remote's branches fetched, from its menu in the branches panel.</summary>
    public async Task<VcsOutcome> FetchRemoteAsync(string remote)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (!Begin(AppActions.Fetch, $"Fetching {remote}…", out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(AppActions.Fetch));
        }
        using Working? held = work;
        GitResult? fetched = await RemoteAsync(work, (stopping, asker) => repository.FetchRemoteAsync(remote, stopping, asker));
        await RefreshAsync();
        return fetched is null ? Report(false, "Fetch stopped")
            : Report(fetched.Succeeded, fetched.Succeeded ? $"Fetched {remote}" : $"Fetch failed: {fetched.Message}");
    }

    /// <summary>Begins an operation's work in the window, saying its first stage: false when one of its kind is under way
    /// already. Only what goes to a remote is <paramref name="stoppable"/>: Stop ends git there, which leaves the tree
    /// as it was, where ending a change to the tree could leave it halfway.</summary>
    private bool Begin(string id, string stage, out Working? work, bool stoppable = false)
    {
        work = Work?.Begin(id, stage, stoppable);
        return Work is null || work is not null;
    }

    /// <summary>What git said of a remote, its words masked as every secret is (a password the person typed among them),
    /// or null once Stop ended it with all it started (a remote that stopped answering). What git asks on the way (a login,
    /// a key's passphrase) goes to the window, for the person or the agent's call this flow serves (<see cref="AskGit"/>).</summary>
    private async Task<GitResult?> RemoteAsync(Working? work, Func<CancellationToken, GitAsker?, Task<GitResult>> run)
    {
        CancellationToken stopping = work?.Stopping ?? CancellationToken.None;
        GitAsker? asker = Asker();
        try
        {
            GitResult said = await run(stopping, asker);
            return said with { Output = ContainerSecrets.InSaid(said.Output), Error = ContainerSecrets.InSaid(said.Error) };
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Asks the person what git asks while it reaches a remote (a login, a key's passphrase), given the agent's
    /// call the operation serves, if any: the window's, unset with none to ask in, when git asks nobody.</summary>
    public Func<GitQuestion, AgentCall?, CancellationToken, Task<GitAnswer>>? AskGit { get; set; }

    /// <summary>Who answers what an operation's git asks, through the window (<see cref="AskGit"/>), for the agent's call
    /// the operation serves, taken now in the call's own flow (git's questions come on the socket's); null with no window.</summary>
    private GitAsker? Asker()
    {
        if (AskGit is not { } ask)
        {
            return null;
        }
        AgentCall? call = AgentCall.Current;
        GitHold? held = changing;
        return AskerOf(async (question, cancel) =>
        {
            // The change's own hold let go too while the person answers, as its git's is.
            using IDisposable? away = held?.Apart();
            return await UiThread.InvokeAsync(() => ask(question, call, cancel));
        }) is { } asker ? asker with { Questions = questions } : null;
    }

    /// <summary>The gits this folder's operations run that may ask the person something, which end with it.</summary>
    private readonly GitQuestions questions = new();

    /// <summary>How long a git that may ask is let end on its own as the folder closes, within the window's end
    /// (<see cref="MainWindow.EndLimit"/>), before it is ended with all it started.</summary>
    public static readonly TimeSpan GitEndLimit = TimeSpan.FromSeconds(3);

    /// <summary>As the folder closes: every question its gits ask from now on answered as refused, and done once each git
    /// that may ask has ended on its own, taking away its lock (index.lock), or at <see cref="GitEndLimit"/> been ended; at
    /// once where none runs, so leaving the folder waits for nothing more.</summary>
    public Task EndGitAsync()
    {
        UiThread.Check();
        return questions.EndAsync(GitEndLimit);
    }

    /// <summary>The hold of the change to the tree under way (<see cref="OneAtATimeAsync"/>), let go while git asks the
    /// person something for it.</summary>
    private volatile GitHold? changing;

    /// <summary>How a change to the tree's commits are signed (the Commit window's as <paramref name="signing"/> says, any
    /// other's as git's config does): a key's passphrase asked in the window, a GPG key's through Passenger's own gpg; with
    /// the config read for what a failure says. Taken in the change's own flow, whose hold it lets go while it asks.</summary>
    private async Task<(Signer Signer, SigningSetup Setup)> SignerAsync(Git repository, CommitSigning signing)
    {
        SigningSetup setup = await repository.SigningAsync();
        GitAsker? asker = Asker();
        if (asker is not null && (signing.Sign ?? setup.Signs) && setup.Format is SigningFormat.OpenPgp && GpgPrograms.Gpg(setup.Program) is { } gpg)
        {
            signing = signing with { Config = [$"{GitConfigKeys.GpgProgram}={asker.Helper}"], Environment = new Dictionary<string, string> { [GpgLoopback.Variable] = gpg } };
        }
        else if (asker is not null && (signing.Sign ?? setup.Signs) && setup.Format is SigningFormat.X509 && GpgPrograms.Gpgsm(setup.X509Program) is { } gpgsm)
        {
            signing = signing with { Config = [$"{GitConfigKeys.GpgX509Program}={asker.Helper}"], Environment = new Dictionary<string, string> { [GpgLoopback.X509Variable] = gpgsm } };
        }
        return (new Signer(signing, asker), setup);
    }

    /// <summary>A change to the tree that may make commits (a merge, a cherry-pick, a revert, a rebase), run as
    /// <see cref="SwitchAsync"/> runs one, each commit signed as git's config has it, a key's passphrase asked in the window,
    /// and one not signed saying why and how to mend it.</summary>
    private Task<VcsOutcome> SignedSwitchAsync(Func<Git, Signer, Task<GitResult>> change, string done, string activity, string? conflicted = null) =>
        SwitchAsync(async repository =>
        {
            (Signer signer, SigningSetup setup) = await SignerAsync(repository, CommitSigning.AsConfigured);
            return Unsigned(await change(repository, signer), setup);
        }, done, activity, conflicted);

    /// <summary>Where a rebase stopped at a step of its list (an edit), in the words the Commit window says it in; null
    /// where none is under way, or it stopped for conflicts.</summary>
    private string? RebaseStopped() => Merge is { Operation: MergeOperation.Rebase, Stop: not null } ? Merge.UnderWay : null;

    /// <summary>A git that was to sign, its words masked as every secret is, and where it failed for its signing, what the
    /// person is told in place of git's words.</summary>
    private static GitResult Unsigned(GitResult said, SigningSetup setup)
    {
        said = said with { Output = ContainerSecrets.InSaid(said.Output), Error = ContainerSecrets.InSaid(said.Error) };
        if (said.Succeeded || !setup.Signs)
        {
            return said;
        }
        string? why = said.Unanswered is { } unanswered ? SigningFailure.Unsigned + unanswered : SigningFailure.Of(said.Error, setup);
        return why is null ? said : said with { Error = why, Unanswered = null };
    }

    /// <summary>How the tree's git config has a commit signed; none outside a working tree.</summary>
    public async Task<SigningSetup> SigningAsync()
    {
        UiThread.Check();
        return git is { } repository ? await repository.SigningAsync() : SigningSetup.None;
    }

    /// <summary>Raised when the signing set-up changed what git signs with, for the Commit window to say it again.</summary>
    public event EventHandler? SigningChanged;

    /// <summary>Commit Signing…'s choice set in git's config (<see cref="Git.SetSigningAsync"/>), an SSH key's public half
    /// read from its file (or taken from the key given whole) to list it as the person's; then the Commit window says it
    /// again and every signature is read again, as what git trusts may have changed.</summary>
    public async Task<VcsOutcome> SetUpSigningAsync(SigningChoice choice)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        string? publicKey = choice.Format is SigningFormat.Ssh && choice.Key is { } key ? await BackgroundWork.Run(() => PublicKeyOf(key, repository.Root)) : null;
        if (choice.Format is SigningFormat.Ssh && publicKey is null)
        {
            return Report(false, choice.Key is null ? "Signing with SSH needs a key: choose one, or type its file" : $"{choice.Key} is no SSH public key, nor beside one (.pub)");
        }
        GitResult set = await repository.SetSigningAsync(choice with { PublicKey = publicKey });
        Signatures.Forget();
        SigningChanged?.Invoke(this, EventArgs.Empty);
        string where = choice.Global ? "for all your repositories" : "for this repository";
        return Report(set.Succeeded, set.Succeeded
            ? $"Commits {where} are signed with {(await repository.SigningAsync()).KeyShown}{(choice.SignEvery ? string.Empty : " where Sign is checked")}"
            : $"Commit signing was not set up: {set.Message}");
    }

    /// <summary>An SSH key's public half: given whole (key::), its file's first line, or the .pub beside its private half.</summary>
    private static string? PublicKeyOf(string key, string root)
    {
        if (key.StartsWith("key::", StringComparison.Ordinal))
        {
            return key[5..];
        }
        if (key.StartsWith("ssh-", StringComparison.Ordinal))
        {
            return key;
        }
        string file = Path.GetFullPath(PathNames.Expanded(key) ?? key, root);
        foreach (string candidate in file.EndsWith(".pub", StringComparison.Ordinal) ? [file] : new[] { file + ".pub", file })
        {
            try
            {
                if (File.Exists(candidate) && File.ReadLines(candidate).FirstOrDefault()?.Trim() is { } line && line.Contains(' ', StringComparison.Ordinal)
                    && !line.Contains("PRIVATE KEY", StringComparison.Ordinal))
                {
                    return line;
                }
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Commit Signing: {candidate} could not be read: {unread.Message}");
            }
        }
        return null;
    }

    /// <summary>The allowed signers file Commit Signing… lists the person's key in where it sets signing up for all their
    /// repositories: git's own folder in the person's config folder (XDG_CONFIG_HOME, else ~/.config); null where the person
    /// has no home to find it in.</summary>
    public static string? GlobalAllowedSigners =>
        XdgFolders.Config(XdgFolders.Own) is { } config ? Path.Combine(config, "git", "allowed_signers") : null;

    /// <summary>Who answers git through <paramref name="ask"/>: this build's own executable, run by git as its helper, asking
    /// over a socket in the state's socket folder; null where that folder cannot be had, when git asks nobody.</summary>
    public static GitAsker? AskerOf(Func<GitQuestion, CancellationToken, Task<GitAnswer>> ask)
    {
        try
        {
            return new GitAsker(Product.Program, SocketFolders.Own(State.StateStore.DefaultRoot), ask);
        }
        catch (Exception unowned) when (unowned is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Git: asks nobody for a login, as the socket folder cannot be had: {unowned.Message}");
            return null;
        }
    }

    /// <summary>What an operation asked for again while it works says.</summary>
    private string UnderWay(string id) => Work?.Of(id) is { } under ? ActionWork.Busy(under) : "Already under way";

    /// <summary>An operation's outcome, told to the status bar in the words it is returned in.</summary>
    private VcsOutcome Report(bool succeeded, string message)
    {
        ActionWork.Tell(succeeded, message);
        Message?.Invoke(this, message);
        return new VcsOutcome(succeeded, message);
    }

    /// <summary>The last commit's message, which Amend starts from.</summary>
    public async Task<string?> LastMessageAsync()
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return null;
        }
        Task<string?> read = repository.LastMessageAsync();
        if (lastMessageHeld is { } held)
        {
            lastMessageHeld = null;
            await held;
        }
        return await read;
    }

    /// <summary>What the next read of the last commit's message waits for before it answers (<see cref="HoldNextLastMessage"/>).</summary>
    private Task? lastMessageHeld;

    /// <summary>Holds the next answer of <see cref="LastMessageAsync"/> until <paramref name="until"/> ends, for a test of
    /// what is typed meanwhile.</summary>
    public void HoldNextLastMessage(Task until) => lastMessageHeld = until;

    public Task<VcsOutcome> CheckoutAsync(string branch) =>
        SwitchAsync(repository => repository.CheckoutAsync(branch), $"Checked out {branch}", "Git checkout");

    /// <summary>The Git log's commit actions: each saves first, and the open files follow what it did.</summary>
    public Task<VcsOutcome> CheckoutRevisionAsync(string hash) =>
        SwitchAsync(repository => repository.CheckoutRevisionAsync(hash), $"Checked out {GitHashes.Short(hash)}", "Git checkout");

    public Task<VcsOutcome> NewBranchAtAsync(string name, string hash) =>
        SwitchAsync(repository => repository.NewBranchAtAsync(name, hash), $"Created and checked out {name} at {GitHashes.Short(hash)}", "Git checkout");

    public Task<VcsOutcome> CherryPickAsync(string hash) =>
        SignedSwitchAsync((repository, signer) => repository.CherryPickAsync(hash, signer), $"Cherry-picked {GitHashes.Short(hash)}", "Git cherry-pick", "Cherry-pick was performed with conflicts");

    public Task<VcsOutcome> RevertAsync(string hash) =>
        SignedSwitchAsync((repository, signer) => repository.RevertAsync(hash, signer), $"Reverted {GitHashes.Short(hash)}", "Git revert", "Revert was performed with conflicts");

    public Task<VcsOutcome> ResetAsync(string hash, ResetMode mode) =>
        SwitchAsync(repository => repository.ResetAsync(hash, mode), $"Reset {Branch ?? "HEAD"} to {GitHashes.Short(hash)} ({mode.ToString().ToLowerInvariant()})", "Git reset");

    /// <summary>The Git log's branches, as its branches panel lists them, with the remotes; none outside a working tree.</summary>
    public async Task<(IReadOnlyList<BranchInfo> Branches, IReadOnlyList<string> Remotes)> BranchTreeAsync() =>
        git is { } repository ? await repository.BranchTreeAsync() : ([], []);

    /// <summary>The local branches, the one committed to last first; none outside a working tree.</summary>
    public async Task<IReadOnlyList<BranchInfo>> LocalBranchesAsync() => git is { } repository ? await repository.LocalBranchesAsync() : [];

    /// <summary>Checkout from the branches panel: a local branch checked out; a remote's as a local branch of the same name
    /// tracking it, or that local branch where there is one already; a tag on its own (a detached head).</summary>
    public Task<VcsOutcome> CheckoutRefAsync(BranchInfo branch, IReadOnlyList<BranchInfo> all)
    {
        if (branch.Kind == RefKind.Local)
        {
            return CheckoutAsync(branch.Name);
        }
        if (branch.Kind == RefKind.Tag)
        {
            return SwitchAsync(repository => repository.CheckoutRevisionAsync(branch.Ref.FullName), $"Checked out {branch.Name}", "Git checkout");
        }
        string local = LocalNameOf(branch);
        return all.Any(b => b.Kind == RefKind.Local && b.Name == local)
            ? CheckoutAsync(local)
            : SwitchAsync(repository => repository.CheckoutTrackingAsync(branch.Name), $"Checked out new branch {local} from {branch.Name}", "Git checkout");
    }

    /// <summary>The local branch a remote's branch is checked out as: its name past the remote's.</summary>
    private static string LocalNameOf(BranchInfo branch) => branch.Name[(branch.Name.IndexOf('/', StringComparison.Ordinal) + 1)..];

    /// <summary>New Branch from: a branch made where another (or a tag) is, checked out.</summary>
    public Task<VcsOutcome> NewBranchFromAsync(string name, BranchInfo from) =>
        SwitchAsync(repository => repository.NewBranchAtAsync(name, from.Ref.FullName), $"Checked out new branch {name} from {from.Name}", "Git checkout");

    /// <summary>Merge into Current: another branch merged into the one checked out; one that stops for conflicts opens the
    /// Conflicts dialog. Named to git by git's own short name, which its message and conflict markers say ("Merge branch
    /// 'feature'"), never the full name's "refs/heads/feature".</summary>
    public Task<VcsOutcome> MergeAsync(BranchInfo branch)
    {
        string into = Branch ?? "HEAD";
        return SignedSwitchAsync((repository, signer) => repository.MergeAsync(branch.Ref.GitName ?? branch.Ref.FullName, signer), $"Merged {branch.Name} to {into}", "Git merge", $"{branch.Name} Merged with Conflicts");
    }

    /// <summary>Rebase Current onto Selected; one that stops for conflicts opens the Conflicts dialog.</summary>
    public Task<VcsOutcome> RebaseOntoAsync(BranchInfo onto)
    {
        if (Branch is not { } current)
        {
            return Task.FromResult(Report(false, "Rebase is not possible in the detached HEAD state"));
        }
        return SignedSwitchAsync((repository, signer) => repository.RebaseAsync(onto.Ref.FullName, signer: signer), $"Rebased {current} on {onto.Name}", "Git rebase", "Rebase stopped due to conflicts");
    }

    /// <summary>Checkout and Rebase onto Current: a local branch checked out and rebased onto the branch that was.</summary>
    public Task<VcsOutcome> CheckoutAndRebaseAsync(BranchInfo branch)
    {
        if (Branch is not { } current)
        {
            return Task.FromResult(Report(false, "Rebase is not possible in the detached HEAD state"));
        }
        return SignedSwitchAsync((repository, signer) => repository.RebaseAsync(current, branch.Name, signer), $"Checked out {branch.Name} and rebased it on {current}", "Git rebase", "Rebase stopped due to conflicts");
    }

    /// <summary>Why Interactively Rebase from Here cannot plan a rebase now: no working tree, or git in the middle of
    /// something; null where it can.</summary>
    public string? WhyNotInteractiveRebase => git is null ? NoRepository
        : Merge.UnderWay is { } underWay ? $"{underWay}: it is finished or aborted first"
        : IsBusy ? Busy
        : null;

    /// <summary>Interactively Rebase from Here's plan: <paramref name="from"/> and the commits after it up to HEAD, each
    /// picked, or why there is none.</summary>
    public async Task<(RebasePlan? Plan, string? WhyNot)> RebasePlanAsync(CommitInfo from)
    {
        UiThread.Check();
        if (WhyNotInteractiveRebase is { } refused)
        {
            return (null, refused);
        }
        return await git!.RebasePlanAsync(from.Hash);
    }

    /// <summary>An interactive rebase as planned, held as its action's work: one change in Local History, stopping where git
    /// does, at an edit (said as the Commit window says it) or for conflicts (the Conflicts dialog).</summary>
    public async Task<VcsOutcome> InteractiveRebaseAsync(RebasePlan plan)
    {
        if ((WhyNotInteractiveRebase ?? plan.WhyNot) is { } refused)
        {
            return Report(false, refused);
        }
        if (!Begin(AppActions.InteractiveRebase, "Rebasing…", out Working? work))
        {
            return Report(false, UnderWay(AppActions.InteractiveRebase));
        }
        using Working? held = work;
        string branch = Branch ?? "HEAD";
        int made = plan.Outcome().Count;
        return await SignedSwitchAsync((repository, signer) => repository.InteractiveRebaseAsync(plan, Product.Program, signer),
            $"Rebased {branch} as planned, making {Plural.Of(made, "commit")} of the {Numbers.Said(plan.Commits.Count)}", "Git rebase", "Rebase stopped due to conflicts");
    }

    /// <summary>Skip Commit, where a rebase stopped for its conflicts: that commit left out and the rest replayed, held as
    /// its action's work.</summary>
    public async Task<VcsOutcome> SkipRebaseAsync()
    {
        if (WhyNotSkip is { } refused)
        {
            return Report(false, refused);
        }
        if (!Begin(AppActions.SkipRebase, "Skipping the commit…", out Working? work))
        {
            return Report(false, UnderWay(AppActions.SkipRebase));
        }
        using Working? held = work;
        return await SignedSwitchAsync((repository, signer) => repository.SkipRebaseAsync(signer), "Rebase successful", "Git rebase", "Rebase stopped due to conflicts");
    }

    /// <summary>Why Skip Commit cannot leave a commit out now: no rebase under way, or one stopped at a step of its list,
    /// whose commit is made already; null where it stopped for conflicts.</summary>
    public string? WhyNotSkip => Merge.Operation is not MergeOperation.Rebase ? "No rebase in progress"
        : Merge.Stop is not null ? $"{Merge.UnderWay}, its commit made: Continue Rebase goes on"
        : null;

    /// <summary>Rename of a local branch.</summary>
    public Task<VcsOutcome> RenameBranchAsync(BranchInfo branch, string name) =>
        SwitchAsync(repository => repository.RenameBranchAsync(branch.Name, name), $"Branch {branch.Name} was renamed to {name}", "Git branch");

    /// <summary>Whether a local branch's commits are all in HEAD's history, so deleting it loses none.</summary>
    public async Task<bool> IsMergedAsync(BranchInfo branch) => git is { } repository && await repository.IsMergedAsync(branch.Ref.FullName);

    /// <summary>Delete: a local branch (one not merged only when <paramref name="force"/>), a remote's branch on the
    /// remote, or a tag.</summary>
    public Task<VcsOutcome> DeleteRefAsync(BranchInfo branch, bool force = false, IReadOnlyList<string>? remotes = null)
    {
        switch (branch.Kind)
        {
            case RefKind.Local:
                return SwitchAsync(repository => repository.DeleteBranchAsync(branch.Name, force), $"Deleted branch {branch.Name}", "Git branch");
            case RefKind.Tag:
                return SwitchAsync(repository => repository.DeleteTagAsync(branch.Name),
                    $"Deleted tag {branch.Name} here: a remote that has it keeps it until it is deleted there (Delete on Remote…)", "Git tag");
            default:
                string remote = (remotes ?? []).Where(r => branch.Name.StartsWith(r + "/", StringComparison.Ordinal)).MaxBy(r => r.Length)
                    ?? branch.Name[..Math.Max(0, branch.Name.IndexOf('/', StringComparison.Ordinal))];
                return RemoteSwitchAsync(AppActions.Push, $"Deleting {branch.Name}…", "Delete", (repository, stopping, asker) => repository.DeleteRemoteBranchAsync(remote, branch.Name[(remote.Length + 1)..], stopping, asker),
                    $"Deleted remote branch {branch.Name}", "Git push");
        }
    }

    /// <summary>The tag of this name as it stands, or null where there is none or no working tree.</summary>
    public async Task<TagInfo?> TagOfAsync(string name) => git is { } repository ? await repository.TagOfAsync(name) : null;

    /// <summary>The commit a revision names as the log lists it, or null where it names none or there is no working tree.</summary>
    public async Task<CommitInfo?> CommitAtAsync(string revision) => git is { } repository ? await repository.CommitAtAsync(revision) : null;

    /// <summary>The remotes' names; none outside a working tree.</summary>
    public async Task<IReadOnlyList<string>> RemotesAsync() => git is { } repository ? await repository.RemotesAsync() : [];

    /// <summary>New Tag…'s plan carried out, held as its action's work: the tag made, or moved from the commit it named, an
    /// annotated one signed as planned (a key's passphrase asked in the window, one not signed saying why and how to mend
    /// it), then pushed where planned, in place of the remote's own of the name where it moves one; told in words that say
    /// what it means for others.</summary>
    public async Task<VcsOutcome> MakeTagAsync(TagPlan plan)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (plan.PushTo is not null && WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        string making = plan.Moving is null ? $"Tagging {plan.Commit.Short} as {plan.Name}…" : $"Moving {plan.Name} to {plan.Commit.Short}…";
        if (!Begin(AppActions.NewTag, making, out Working? work, stoppable: plan.PushTo is not null))
        {
            return Report(false, UnderWay(AppActions.NewTag));
        }
        using Working? held = work;
        return await OneAtATimeAsync(async () =>
        {
            GitResult made;
            if (plan.Sign)
            {
                (Signer signer, SigningSetup setup) = await SignerAsync(repository, CommitSigning.AsConfigured with { Sign = true });
                made = Unsigned(await repository.MakeTagAsync(plan, signer), setup with { Signs = true });
            }
            else
            {
                made = await repository.MakeTagAsync(plan);
            }
            if (!made.Succeeded)
            {
                return Report(false, $"{plan.Name} was not {(plan.Moving is null ? "made" : "moved")}: {made.Message}");
            }
            string done = plan.Moving is null
                ? $"Tagged {plan.Commit.Short} as {plan.Name}{(plan.IsAnnotated ? plan.Sign ? ", annotated and signed" : ", annotated" : string.Empty)}"
                : $"Moved {plan.Name} from {GitHashes.Short(plan.Moving.Commit)} to {plan.Commit.Short}";
            if (plan.PushTo is not { } remote)
            {
                await RefreshAsync();
                return Report(true, plan.Moving is null ? $"{done}: it is in this repository alone until pushed" : $"{done} here: a remote that has it keeps it where it was until it is pushed");
            }
            work?.Say($"Pushing {plan.Name} to {remote}…");
            GitResult? pushed = await RemoteAsync(work, (stopping, asker) => repository.PushTagAsync(remote, plan.Name, replace: plan.Moving is not null, stopping, asker));
            await RefreshAsync();
            return pushed is null ? Report(false, $"{done} here, but its push to {remote} was stopped")
                : !pushed.Succeeded ? Report(false, $"{done} here, but it was not pushed to {remote}: {pushed.Message}")
                : Report(true, plan.Moving is null ? $"{done} and pushed it to {remote}, where whoever fetches gets it" : $"{done} here and on {remote}: whoever fetched it before keeps it where it was");
        }, refused => refused);
    }

    /// <summary>The remote a tag goes to or is deleted on: the only one, or the one of several the person chooses, asked
    /// as <paramref name="asking"/> ("Push tag v1.0 to"); null where there is none, said as every outcome is.</summary>
    public async Task<string?> TagRemoteAsync(string tag, string asking)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Refused(NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Refused(untrusted);
        }
        IReadOnlyList<string> remotes = await repository.RemotesAsync();
        return remotes switch
        {
            [string only] => only,
            [] => Refused($"The repository has no remote for tag {tag}"),
            _ when ChooseRemote is { } choose => await choose(asking, remotes) ?? Refused($"No remote was chosen for tag {tag}"),
            _ => Refused($"There are {Numbers.Said(remotes.Count)} remotes ({string.Join(", ", remotes)}) and none to ask which"),
        };

        string? Refused(string why)
        {
            Report(false, why);
            return null;
        }
    }

    /// <summary>A tag's Push…: the tag pushed to <paramref name="remote"/>, in place of the remote's own of the name where
    /// <paramref name="replace"/>; one the remote refused for having its own says so, which the window asks to replace.</summary>
    public async Task<TagPush> PushTagAsync(string tag, string remote, bool replace = false)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return new TagPush(Report(false, NoRepository), false);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return new TagPush(Report(false, untrusted), false);
        }
        if (!Begin(AppActions.Push, $"Pushing tag {tag} to {remote}…", out Working? work, stoppable: true))
        {
            return new TagPush(Report(false, UnderWay(AppActions.Push)), false);
        }
        using Working? held = work;
        GitResult? pushed = await RemoteAsync(work, (stopping, asker) => repository.PushTagAsync(remote, tag, replace, stopping, asker));
        await RefreshAsync();
        if (pushed is null)
        {
            return new TagPush(Report(false, "Push stopped"), false);
        }
        if (!pushed.Succeeded && TagNames.RemoteHasAnother(pushed))
        {
            return new TagPush(Report(false, $"{remote} has its own tag {tag}, naming another commit: pushing it in place of that one moves it there for whoever fetches"), true);
        }
        return new TagPush(pushed.Succeeded
            ? Report(true, replace ? $"Pushed tag {tag} to {remote} in place of its own: whoever fetched the old one keeps it until they fetch tags again with --force"
                : $"Pushed tag {tag} to {remote}, where whoever fetches gets it")
            : Report(false, $"Tag {tag} was not pushed to {remote}: {pushed.Message}"), false);
    }

    /// <summary>A tag's Delete on Remote…: the tag deleted on <paramref name="remote"/>, this repository's own kept.</summary>
    public async Task<VcsOutcome> DeleteRemoteTagAsync(string tag, string remote)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (!Begin(AppActions.Push, $"Deleting tag {tag} on {remote}…", out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(AppActions.Push));
        }
        using Working? held = work;
        GitResult? deleted = await RemoteAsync(work, (stopping, asker) => repository.DeleteRemoteTagAsync(remote, tag, stopping, asker));
        await RefreshAsync();
        return deleted is null ? Report(false, "Delete stopped")
            : deleted.Succeeded ? Report(true, $"Deleted tag {tag} on {remote}: a repository that fetched it keeps its own, this one's too, until it is deleted there")
            : Report(false, $"Tag {tag} was not deleted on {remote}: {deleted.Message}");
    }

    /// <summary>The repository's working trees, the main one first; none outside a working tree.</summary>
    public async Task<IReadOnlyList<WorktreeInfo>> WorktreesAsync() => git is { } repository ? await repository.WorktreesAsync() : [];

    /// <summary>Why New Worktree… cannot add one: no working tree, or a folder not trusted, where checking a branch out
    /// would run the repository's filters and hooks.</summary>
    public string? WhyNotNewWorktree => git is null ? NoRepository : WhyNotRemote;

    /// <summary>New Worktree…'s plan carried out, held as its action's work: a branch checked out in a folder of its own,
    /// made first where it is new; told in words that say what a worktree is.</summary>
    public async Task<VcsOutcome> AddWorktreeAsync(WorktreePlan plan)
    {
        UiThread.Check();
        if (WhyNotNewWorktree is { } refused)
        {
            return Report(false, refused);
        }
        if (!Begin(AppActions.NewWorktree, $"Checking {plan.Branch} out in {Path.GetFileName(plan.Folder)}…", out Working? work))
        {
            return Report(false, UnderWay(AppActions.NewWorktree));
        }
        using Working? held = work;
        GitResult added = await git!.AddWorktreeAsync(plan);
        await RefreshAsync();
        return added.Succeeded
            ? Report(true, $"Checked {plan.Branch} out in the worktree {PathNames.Shown(plan.Folder, null)}: its own files, sharing this repository's commits and branches")
            : Report(false, $"No worktree was made for {plan.Branch}: {added.Message}");
    }

    /// <summary>How many files a working tree has changed or new; null where git could not say (its folder gone).</summary>
    public async Task<int?> WorktreeChangesAsync(WorktreeInfo worktree) =>
        git is { } repository && worktree.Gone is null ? await repository.WorktreeChangesAsync(worktree.Folder) : null;

    /// <summary>A working tree removed: its folder moved to the Trash, its changes no commit holds with it (none where the
    /// folder is gone already), then forgotten by git; told where the folder went. Its branch and commits stay.</summary>
    public async Task<VcsOutcome> RemoveWorktreeAsync(WorktreeInfo worktree)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        Trashed? went = null;
        if (worktree.Gone is null)
        {
            try
            {
                went = Trash.Move(worktree.Folder);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                return Report(false, $"The worktree {worktree.Name} was not removed: its folder could not be moved to the Trash: {refused.Message}");
            }
        }
        GitResult forgot = await repository.ForgetWorktreeAsync(worktree.Folder);
        await RefreshAsync();
        string kept = worktree.BranchName is { } branch ? $"; {branch} and its commits stay" : string.Empty;
        string trashed = worktree.Gone is not null ? "Forgot the worktree" : went is null ? "Moved to the Trash the folder of the worktree"
            : $"Moved to the Trash, as {PathNames.Shown(went.At, null)}, the folder of the worktree";
        return forgot.Succeeded ? Report(true, $"{trashed} {worktree.Name}{kept}")
            : Report(false, $"{trashed} {worktree.Name}, but git did not forget it: {forgot.Message}");
    }

    /// <summary>Update of a local branch: the one checked out updated as Update Project does, another brought
    /// forward to the branch it tracks.</summary>
    public Task<VcsOutcome> UpdateBranchAsync(BranchInfo branch)
    {
        if (branch.IsCurrent)
        {
            return UpdateAsync();
        }
        if (branch.UpstreamRemote is not { } remote || branch.UpstreamBranch is not { } tracked)
        {
            return Task.FromResult(Report(false, Refusals.Untracked(branch.Name)));
        }
        return RemoteSwitchAsync(AppActions.UpdateProject, $"Updating {branch.Name}…", "Update", (repository, stopping, asker) => repository.FetchIntoAsync(remote, tracked, branch.Name, stopping, asker),
            $"Updated {branch.Name}", "Git update");
    }

    /// <summary>Push of a branch: pushed to the branch it tracks, or where it tracks none, to the only remote or the one
    /// of several the person chooses, then tracked.</summary>
    public async Task<VcsOutcome> PushBranchAsync(BranchInfo branch)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        string? remote = null;
        if (branch.UpstreamRemote is null || branch.UpstreamBranch is null)
        {
            (remote, string? refused) = await RemoteForAsync(repository, branch.Name, ask: true);
            if (refused is not null)
            {
                return Report(false, refused);
            }
        }
        if (!Begin(AppActions.Push, $"Pushing {branch.Name}…", out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(AppActions.Push));
        }
        using Working? held = work;
        GitResult? pushed = await RemoteAsync(work, (stopping, asker) => repository.PushBranchAsync(branch, remote, stopping, asker));
        await RefreshAsync();
        return pushed is null ? Report(false, "Push stopped")
            : Report(pushed.Succeeded, pushed.Succeeded ? $"Pushed {branch.Name}" : $"Push failed: {pushed.Message}");
    }

    /// <summary>The remotes with their URLs, in git's order; none outside a working tree.</summary>
    public async Task<IReadOnlyList<RemoteInfo>> RemoteUrlsAsync() => git is { } repository ? await repository.RemoteUrlsAsync() : [];

    /// <summary>A URL as Passenger shows it anywhere: a password or token it holds masked (<see cref="ContainerSecrets"/>).</summary>
    public static string UrlShown(string url) => ContainerSecrets.InSaid(url);

    /// <summary>Add Remote… and Edit Remote…'s plan carried out, held as Manage Remotes…'s work: a new remote added, then its
    /// branches fetched where planned; one there renamed (its branches here and those tracking them following) and given
    /// its new URL, each only where it changed; told in words that say what it means here.</summary>
    public async Task<VcsOutcome> SaveRemoteAsync(RemotePlan plan)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (plan.Fetch && WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (!Begin(AppActions.ManageRemotes, plan.Was is null ? $"Adding the remote {plan.Name}…" : $"Changing the remote {plan.Was}…", out Working? work, stoppable: plan.Fetch))
        {
            return Report(false, UnderWay(AppActions.ManageRemotes));
        }
        using Working? held = work;
        return await OneAtATimeAsync(async () =>
        {
            string url = UrlShown(plan.Url);
            if (plan.Was is null)
            {
                GitResult added = await repository.AddRemoteAsync(plan.Name, plan.Url);
                if (!added.Succeeded)
                {
                    await RefreshAsync();
                    return Report(false, $"The remote {plan.Name} was not added: {added.Message}");
                }
                string done = $"Added the remote {plan.Name}, at {url}";
                if (!plan.Fetch)
                {
                    await RefreshAsync();
                    return Report(true, $"{done}: Fetch brings its branches in");
                }
                work?.Say($"Fetching {plan.Name}…");
                GitResult? fetched = await RemoteAsync(work, (stopping, asker) => repository.FetchRemoteAsync(plan.Name, stopping, asker));
                await RefreshAsync();
                return fetched is null ? Report(false, $"{done}, but its fetch was stopped")
                    : fetched.Succeeded ? Report(true, $"{done} and fetched its branches")
                    : Report(false, $"{done}, but its branches were not fetched: {fetched.Message}");
            }
            List<string> said = [];
            string name = plan.Was;
            if (plan.Name != plan.Was)
            {
                GitResult renamed = await repository.RenameRemoteAsync(plan.Was, plan.Name);
                if (!renamed.Succeeded)
                {
                    await RefreshAsync();
                    return Report(false, $"The remote {plan.Was} was not renamed: {renamed.Message}");
                }
                name = plan.Name;
                said.Add($"Renamed the remote {plan.Was} to {plan.Name}, its branches here and those tracking them following");
            }
            if ((await repository.RemoteUrlsAsync()).FirstOrDefault(r => r.Name == name) is { } stands && stands.Url != plan.Url)
            {
                GitResult repointed = await repository.SetRemoteUrlAsync(name, plan.Url);
                await RefreshAsync();
                return repointed.Succeeded
                    ? Report(true, string.Join("; ", [.. said, $"{(said.Count == 0 ? $"The remote {name}" : "it")} fetches from and pushes to {url} now"]))
                    : Report(false, string.Join("; ", [.. said, $"{(said.Count == 0 ? $"The remote {name}" : "its")} URL was not changed: {repointed.Message}"]));
            }
            await RefreshAsync();
            return Report(true, said.Count > 0 ? said[0] : $"The remote {name} is as it was");
        }, refused => refused);
    }

    /// <summary>The local branches that track a remote's branch.</summary>
    public async Task<IReadOnlyList<string>> TrackingAsync(string remote) =>
        [.. (await LocalBranchesAsync()).Where(b => b.UpstreamRemote == remote).Select(b => b.Name)];

    /// <summary>Remove Remote…, once the person agreed: the remote forgotten with its branches here, the local branches that
    /// tracked it tracking nothing; the repository it named keeps everything.</summary>
    public async Task<VcsOutcome> RemoveRemoteAsync(string name)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (!Begin(AppActions.ManageRemotes, $"Removing the remote {name}…", out Working? work))
        {
            return Report(false, UnderWay(AppActions.ManageRemotes));
        }
        using Working? held = work;
        return await OneAtATimeAsync(async () =>
        {
            IReadOnlyList<string> tracking = await TrackingAsync(name);
            GitResult removed = await repository.RemoveRemoteAsync(name);
            await RefreshAsync();
            string untracked = tracking.Count == 0 ? string.Empty : $"; {Plural.Listed(tracking)} {(tracking.Count == 1 ? "tracks" : "track")} nothing now";
            return removed.Succeeded ? Report(true, $"Removed the remote {name} and its branches here{untracked}: the repository it named keeps everything")
                : Report(false, $"The remote {name} was not removed: {removed.Message}");
        }, refused => refused);
    }

    /// <summary>What pushing a local branch to a remote's branch would send, as the remote was last fetched; none outside a
    /// working tree.</summary>
    public async Task<PushPreview?> PushPreviewAsync(string branch, string remote, string remoteBranch) =>
        git is { } repository ? await repository.PushPreviewAsync(branch, remote, remoteBranch) : null;

    /// <summary>Push to…'s plan carried out, held as Push's work: the branch pushed to the remote's branch named, whatever
    /// it tracks, tracking that one from then on where planned.</summary>
    public async Task<VcsOutcome> PushToAsync(PushPlan plan)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (!Begin(AppActions.Push, $"Pushing {plan.Branch} to {plan.Remote}…", out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(AppActions.Push));
        }
        using Working? held = work;
        GitResult? pushed = await RemoteAsync(work, (stopping, asker) => repository.PushToAsync(plan, stopping, asker));
        await RefreshAsync();
        string there = $"{plan.Remote}/{plan.RemoteBranch}";
        return pushed is null ? Report(false, "Push stopped")
            : !pushed.Succeeded ? Report(false, $"{plan.Branch} was not pushed to {there}: {pushed.Message}")
            : Report(true, $"Pushed {plan.Branch} to {there}{(plan.Track ? $", which it tracks from now on" : string.Empty)}");
    }

    /// <summary>The files that differ between a branch or tag and the working tree.</summary>
    public async Task<IReadOnlyList<FileChange>> ChangesSinceAsync(BranchInfo branch) =>
        git is { } repository ? await repository.ChangesSinceAsync(branch.Ref.FullName) : [];

    /// <summary>The commits of one revision another lacks ("main..feature"), newest first.</summary>
    public async Task<IReadOnlyList<CommitInfo>> LogAsync(IReadOnlyList<string> revisions) =>
        git is { } repository ? await repository.LogAsync(revisions: revisions) : [];

    /// <summary>Creates a branch from the head and checks it out.</summary>
    public Task<VcsOutcome> NewBranchAsync(string name) =>
        SwitchAsync(repository => repository.NewBranchAsync(name), $"Created and checked out {name}", "Git checkout");

    /// <summary>Stash Changes: every uncommitted change put away under a message, new files too unless
    /// <paramref name="untracked"/> is false; the open files take the head's text again.</summary>
    public Task<VcsOutcome> StashAsync(string message, bool untracked = true) => WhyNotStash is { } unfiltered
        ? Task.FromResult(Report(false, unfiltered))
        : SwitchAsync(repository => repository.StashAsync(message.Trim(), untracked), untracked ? "Stashed the changes" : "Stashed the changes, new files left in place", "Git stash");

    /// <summary>The files a stash holds, its new files last; none outside a working tree.</summary>
    public async Task<IReadOnlyList<FileChange>> StashFilesAsync(StashInfo stash) => git is { } repository ? await repository.StashFilesAsync(stash) : [];

    /// <summary>The stash of this one's commit where the list has it now; null where it is listed no more.</summary>
    public async Task<StashInfo?> StashNowAsync(StashInfo stash) => git is { } repository ? await repository.StashNowAsync(stash) : null;

    /// <summary>A dropped stash put back at the top of the list (a drop's Undo): null once back, else why not.</summary>
    public async Task<string?> RestoreStashAsync(StashInfo stash)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return NoRepository;
        }
        GitResult stored = await repository.StoreStashAsync(stash.Commit, stash.Message);
        await RefreshAsync();
        return stored.Succeeded ? null : $"The stash \"{stash.Message}\" could not be put back: {stored.Message}";
    }

    public async Task<IReadOnlyList<StashInfo>> StashesAsync() => git is { } repository ? await repository.StashesAsync() : [];

    /// <summary>Unstash Changes: a stash's changes back in the files, the stash dropped after with
    /// <paramref name="pop"/>.</summary>
    public Task<VcsOutcome> UnstashAsync(StashInfo stash, bool pop) => SwitchAsync(async repository =>
    {
        // Git keeps no record of an unstash that stopped for conflicts: the stash names their sides until they are resolved.
        if (Conflicts.Count == 0 && await repository.StashNowAsync(stash) is { } now)
        {
            unstashed = $"stash@{{{now.Index}}}";
        }
        readsBeforeUnstash = long.MaxValue;
        try
        {
            return await repository.UnstashAsync(stash, pop);
        }
        finally
        {
            readsBeforeUnstash = readsBegun;
        }
    }, pop ? $"Unstashed {stash.Message}" : $"Applied {stash.Message}", "Git unstash", "Unstashed with conflicts");

    /// <summary>Drops a stash, the one of its commit wherever the list now has it.</summary>
    public Task<VcsOutcome> DropStashAsync(StashInfo stash) =>
        SwitchAsync(repository => repository.DropStashAsync(stash), $"Dropped {stash.Message}", "Git stash");

    /// <summary>Whether git's index holds changes beyond moves Passenger keeps there, staged by the person or another of
    /// git's tools: a commit then takes what the index holds, the Commit window listing those changes apart. Never while
    /// a merge's commit, which takes files whole, is under way.</summary>
    public bool HoldsStaged => !Merge.CommitsWhole && Changes.Any(c => c.HoldsStaged);

    /// <summary>Why files cannot be staged or unstaged: none chosen, one in conflict (resolved first), or a folder of no
    /// repository.</summary>
    public string? WhyNotStage(IReadOnlyList<FileChange> changes) =>
        git is null ? NoRepository
        : changes.Count == 0 ? "No changed file is chosen"
        : changes.FirstOrDefault(c => c.Kind is ChangeKind.Conflicted) is { } conflicted ? $"{conflicted.Name} is in conflict: resolve it first"
        : null;

    /// <summary>Stages files whole (<see cref="Git.StageAsync"/>), a rename's old name with it, or unstages them (<see
    /// cref="Git.UnstageAsync"/>), a rename keeping its move in the index, every open file saved first, then reads the
    /// status again.</summary>
    public Task<VcsOutcome> StageAsync(IReadOnlyList<FileChange> changes, bool stage) =>
        IndexChangeAsync(stage, repository => stage
            ? repository.StageAsync([.. changes.SelectMany(c => c.From is { } from ? new[] { c.Path, from } : [c.Path])])
            : repository.UnstageAsync([.. changes.Where(c => c.From is null).Select(c => c.Path)], [.. changes.Where(c => c.From is not null).Select(c => (c.From!, c.Path))]),
            Plural.Of(changes.Count, "file"));

    /// <summary>Stages some of a file's changes (<see cref="Git.StageChangesAsync"/>), then reads the status again.</summary>
    public Task<VcsOutcome> StageChangesAsync(string path, PartialCommit chosen) =>
        IndexChangeAsync(stage: true, repository => repository.StageChangesAsync(path, chosen), $"{Said(chosen.Diff, chosen.Hunks)} of {Path.GetFileName(path)}");

    /// <summary>Unstages some of a file's changes, by their lines in the last commit (<see cref="Git.UnstageChangesAsync"/>),
    /// then reads the status again.</summary>
    public Task<VcsOutcome> UnstageChangesAsync(FileChange change, FileDiff diff, IReadOnlyList<DiffHunk> changes) =>
        IndexChangeAsync(stage: false, repository => repository.UnstageChangesAsync(change.Path, change.From, changes), $"{Said(diff, changes)} of {change.Name}");

    /// <summary>Changes of a diff as a person names them: "change 2", "2 changes", or "3 lines" where some are parts of one.</summary>
    public static string Said(FileDiff diff, IReadOnlyList<DiffHunk> changes) =>
        changes.All(diff.Hunks.Contains) ? changes.Count == 1 ? $"change {diff.Hunks.ToList().IndexOf(changes[0]) + 1}" : Plural.Of(changes.Count, "change")
        : Plural.Of(changes.Sum(c => Math.Max(c.OldCount, c.NewCount)), "line");

    private async Task<VcsOutcome> IndexChangeAsync(bool stage, Func<Git, Task<GitResult>> change, string what)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        return await OneAtATimeAsync(async () =>
        {
            // Staged as the editor shows it.
            if (await SavedFirstAsync() is { } unsaved)
            {
                return Report(false, unsaved);
            }
            GitResult changed = await change(repository);
            await RefreshAsync();
            return changed.Succeeded ? Report(true, $"{(stage ? "Staged" : "Unstaged")} {what}")
                : Report(false, $"{what} could not be {(stage ? "staged" : "unstaged")}: {changed.Message}");
        }, refused => refused);
    }

    /// <summary>What git's index holds at each path (its mode and blob, or none), for putting it back as it was.</summary>
    public async Task<IReadOnlyList<(string Path, (string Mode, string Blob)? Entry)>> IndexEntriesAsync(IReadOnlyList<string> paths)
    {
        if (git is not { } repository)
        {
            return [];
        }
        string[] distinct = [.. paths.Distinct(PathNames.Comparer)];
        IReadOnlyList<(string Mode, string Blob)?> held = await repository.IndexEntriesAsync(distinct);
        return [.. distinct.Select((path, i) => (path, held[i]))];
    }

    /// <summary>Puts git's index's entries back as <paramref name="entries"/> has them, then reads the status again: null
    /// once put, else why not.</summary>
    public async Task<string?> PutIndexEntriesAsync(IReadOnlyList<(string Path, (string Mode, string Blob)? Entry)> entries)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return NoRepository;
        }
        VcsOutcome put = await OneAtATimeAsync(async () =>
        {
            GitResult written = await repository.PutIndexEntriesAsync(entries);
            await RefreshAsync();
            return new VcsOutcome(written.Succeeded, written.Succeeded ? string.Empty : written.Message);
        }, refused => refused);
        return put.Succeeded ? null : put.Message;
    }

    /// <summary>A file's text as git's index holds it, read as checking it out would write it, or null where it holds none.</summary>
    public async Task<string?> IndexTextAsync(string path) => (await VersionAsync(string.Empty, path, path))?.Text;

    /// <summary>Puts files back as the last commit has them, the unversioned ones moved to the Trash once git has put
    /// back the rest (<see cref="MoveToTrash"/>); the open ones take their text from disk again.</summary>
    public async Task<VcsOutcome> RollbackAsync(IReadOnlyList<FileChange> changes)
    {
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (changes.Count == 0)
        {
            return Report(false, "No files are chosen to roll back");
        }
        if (WhyNotRollback(changes) is { } unfiltered)
        {
            return Report(false, unfiltered);
        }
        return await OneAtATimeAsync(async () =>
        {
            if (await SavedFirstAsync() is { } unsaved)
            {
                return Report(false, unsaved);
            }
            using IDisposable? named = Activity?.Invoke("Rollback");
            GitResult rolled = await repository.RollbackAsync(changes);
            List<string> unversioned = [.. changes.Where(c => c.Kind is ChangeKind.Unversioned).Select(c => c.Path)];
            IReadOnlyList<string> stayed = unversioned.Count == 0 ? []
                : MoveToTrash is { } trash ? await trash($"Rollback {Plural.Listed([.. changes.Select(c => c.Name)])}", unversioned)
                : [.. unversioned.Select(p => $"{Path.GetFileName(p)} was left as it is: nothing here moves it to the Trash")];
            ReloadOpenFiles();
            await RefreshAsync();
            string[] said = [.. rolled.Succeeded ? [] : new[] { rolled.Message }, .. stayed];
            return said.Length == 0 ? Report(true, $"Rolled back {Plural.Of(changes.Count, "file")}") : Report(false, string.Join('\n', said));
        }, refused => refused);
    }

    /// <summary>Runs a change to the working tree unless another is still running, which it refuses, answering with what
    /// <paramref name="refused"/> makes of the refusal.</summary>
    private async Task<T> OneAtATimeAsync<T>(Func<Task<T>> change, Func<VcsOutcome, T> refused)
    {
        UiThread.Check();
        if (IsBusy)
        {
            return refused(Report(false, Busy));
        }
        IsBusy = true;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        // Held whole, the waits between its gits too (a rollback's retries): the files and the lists follow what it did.
        using GitHold held = new();
        changing = held;
        try
        {
            return await change();
        }
        finally
        {
            changing = null;
            IsBusy = false;
            BusyChanged?.Invoke(this, EventArgs.Empty);
            idle?.TrySetResult();
            idle = null;
        }
    }

    /// <summary>The open files take their text from disk again where something else changed it and they have no
    /// unsaved changes. A preview, with no text, is decoded again only when the folder watcher hears its file change.</summary>
    public void ReloadOpenFiles()
    {
        UiThread.Check();
        foreach (OpenDocument document in documents.Keys.Where(d => d.HoldsText))
        {
            document.Reload();
        }
    }

    /// <summary>Runs a change to the working tree and its files, what it changes kept in Local History under the
    /// <paramref name="activity"/>'s name; one that can stop for conflicts (an unstash, a cherry-pick) says so in
    /// <paramref name="conflicted"/>'s words when it did.</summary>
    private async Task<VcsOutcome> SwitchAsync(Func<Git, Task<GitResult>> change, string done, string activity, string? conflicted = null)
    {
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        return await OneAtATimeAsync(async () =>
        {
            if (await SavedFirstAsync() is { } unsaved)
            {
                return Report(false, unsaved);
            }
            bool already = Conflicts.Count > 0;
            using IDisposable? named = Activity?.Invoke(activity);
            GitResult switched = await change(repository);
            if (switched.Succeeded)
            {
                ReloadOpenFiles();
            }
            await RefreshAsync();
            // Stopped for conflicts, the files hold them now: they are shown as they are on disk.
            if (!switched.Succeeded && !already && Conflicts.Count > 0)
            {
                ReloadOpenFiles();
            }
            if (conflicted is not null && !already && Conflicted(conflicted) is { } stopped)
            {
                return stopped;
            }
            return Report(switched.Succeeded, switched.Succeeded ? RebaseStopped() ?? done : switched.Message);
        }, refused => refused);
    }

    /// <summary>A change to the tree that goes to a remote (a branch brought forward from its upstream, one deleted there),
    /// run as <see cref="SwitchAsync"/> runs one but held as <paramref name="id"/>'s work, which Stop ends with git and all it
    /// started, saying "<paramref name="what"/> stopped".</summary>
    private async Task<VcsOutcome> RemoteSwitchAsync(string id, string stage, string what, Func<Git, CancellationToken, GitAsker?, Task<GitResult>> change, string done, string activity)
    {
        if (WhyNotRemote is { } untrusted)
        {
            return Report(false, untrusted);
        }
        if (!Begin(id, stage, out Working? work, stoppable: true))
        {
            return Report(false, UnderWay(id));
        }
        using Working? held = work;
        return await SwitchAsync(async repository => await RemoteAsync(work, (stopping, asker) => change(repository, stopping, asker))
            ?? new GitResult(1, string.Empty, $"{what} stopped"), done, activity);
    }

    /// <summary>Accept Yours or Accept Theirs: conflicted files take that side whole (or go, where it deleted them) and
    /// are marked resolved; the open ones show it.</summary>
    public async Task<VcsOutcome> AcceptAsync(IReadOnlyList<GitConflict> conflicts, bool yours)
    {
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (conflicts.Count == 0)
        {
            return Report(false, "No files are chosen to resolve");
        }
        // Rebasing, yours is the branch being rebased: git's theirs.
        bool theirs = yours == Sides.Swapped;
        return await OneAtATimeAsync(async () =>
        {
            if (await SavedFirstAsync() is { } unsaved)
            {
                return Report(false, unsaved);
            }
            using IDisposable? named = Activity?.Invoke(yours ? "Accept Yours" : "Accept Theirs");
            GitResult accepted = await repository.AcceptAsync(conflicts, theirs);
            ReloadOpenFiles();
            await RefreshAsync();
            string names = string.Join(", ", conflicts.Select(c => c.Name));
            return Report(accepted.Succeeded, accepted.Succeeded ? $"{(yours ? "Accept Yours" : "Accept Theirs")}: {names}" : accepted.Message);
        }, refused => refused);
    }

    /// <summary>A conflicted file's three versions as the merge window lays them out: the one both sides came from, and
    /// yours on the left and theirs on the right (empty where a side deleted it), with the words for each; null where a
    /// side is binary. Each is read as checking it out would write it, in the file's encoding (<see cref="InConflict"/>),
    /// so a Windows-1252 letter is itself on every side rather than a replacement character UTF-8 makes of it.</summary>
    public async Task<MergeTexts?> MergeTextsAsync(string path)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return null;
        }
        ConflictSides sides = Sides;
        System.Text.Encoding? encoding = InConflict(path)?.Encoding;
        (byte[]? common, byte[]? ours, byte[]? theirs) = await repository.StagesAsync(path);
        if (new[] { common, ours, theirs }.Any(b => b is not null && FileText.LooksBinary(b)))
        {
            return null;
        }
        string Read(byte[]? bytes) => bytes is null ? string.Empty : FileVersion.Read(bytes, encoding, path).Text ?? string.Empty;
        return new MergeTexts(path, Read(common), Read(sides.Swapped ? theirs : ours), Read(sides.Swapped ? ours : theirs), sides);
    }

    /// <summary>The conflicted file as git left it in the working tree, read as its editor reads it (in the encoding the
    /// open file has, when that reads it): the encoding, byte order mark and line breaks the merge's result is written in.</summary>
    private FileText? InConflict(string path)
    {
        try
        {
            return File.Exists(path) ? FileText.Load(path, documents.Keys.FirstOrDefault(d => d.Path == path)?.Encoding) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The merge window's Apply: the result written to the file in the encoding, byte order mark and line breaks
    /// the working tree has it in (CRLF under core.autocrlf, as git checked it out), one gone from it made as its .editorconfig
    /// asks, and once it has landed the file marked resolved; the open files show it.</summary>
    public async Task<VcsOutcome> ResolveAsync(string path, string text)
    {
        if (git is not { } repository)
        {
            return Report(false, NoRepository);
        }
        if (Conflicts.FirstOrDefault(c => PathNames.Same(c.Path, path)) is { IsBinary: true } binary)
        {
            return Report(false, $"{binary.Name} is binary, so no text is written over it: Accept Yours or Accept Theirs keeps that side's file whole");
        }
        return await OneAtATimeAsync(async () =>
        {
            if (await SavedFirstAsync() is { } unsaved)
            {
                return Report(false, unsaved);
            }
            using IDisposable? named = Activity?.Invoke("Resolve Conflict");
            try
            {
                if (InConflict(path) is { } before)
                {
                    FileText.SaveOver(path, before, LineEndings.In(text, before.Break));
                }
                else
                {
                    FileText.SaveNew(path, text);
                }
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                return Report(false, refused.Message);
            }
            // Marked resolved only once it holds the result: git stages what the disk has.
            if (await Editors.EditorArea.LandedAsync([path], waiting => Message?.Invoke(this, waiting)) is [(_, Exception unlanded), ..])
            {
                return Report(false, unlanded.Message);
            }
            GitResult marked = await repository.MarkResolvedAsync(path);
            ReloadOpenFiles();
            await RefreshAsync();
            return Report(marked.Succeeded, marked.Succeeded ? $"{Path.GetFileName(path)} resolved" : marked.Message);
        }, refused => refused);
    }

    /// <summary>Abort Merge, Abort Rebase, Abort Cherry-Pick and Abort Revert, whichever is under way: the tree back as it
    /// was before the operation began, held as that action's work.</summary>
    public async Task<VcsOutcome> AbortAsync()
    {
        MergeOperation operation = Merge.Operation;
        if (operation.Command() is not { } name)
        {
            return Report(false, "Nothing is under way to abort");
        }
        string id = operation switch
        {
            MergeOperation.Rebase => AppActions.AbortRebase,
            MergeOperation.CherryPick => AppActions.AbortCherryPick,
            MergeOperation.Revert => AppActions.AbortRevert,
            _ => AppActions.AbortMerge,
        };
        if (!Begin(id, $"Aborting the {name}…", out Working? work))
        {
            return Report(false, UnderWay(id));
        }
        using Working? held = work;
        return await SwitchAsync(repository => repository.AbortAsync(operation), $"Abort {name} succeeded", $"Git abort {name}");
    }

    /// <summary>Continue Rebase, once every conflict is resolved, held as its action's work.</summary>
    public async Task<VcsOutcome> ContinueRebaseAsync()
    {
        if (Merge.Operation is not MergeOperation.Rebase)
        {
            return Report(false, "No rebase in progress");
        }
        if (Conflicts.Count > 0)
        {
            return Report(false, ContinueRefused);
        }
        if (!Begin(AppActions.ContinueRebase, "Continuing the rebase…", out Working? work))
        {
            return Report(false, UnderWay(AppActions.ContinueRebase));
        }
        using Working? held = work;
        return await SignedSwitchAsync((repository, signer) => repository.ContinueRebaseAsync(signer), "Rebase successful", "Git rebase", "Rebase stopped due to conflicts");
    }

    /// <summary>Why Continue Rebase cannot go on while a conflict is left.</summary>
    public const string ContinueRefused = "Resolve conflicts in order to continue rebase";

    /// <summary>A file's text as the last commit has it, or null when it has none (new, or outside the tree) or it is
    /// binary there (<see cref="HeadAsync"/>).</summary>
    public async Task<string?> HeadTextAsync(string path) => (await HeadAsync(path))?.Text;

    /// <summary>A file as the last commit has it, read as checking it out would write it and in the open file's encoding,
    /// or null when it has none (new, or outside the tree). A file opened since HEAD last moved is read on its own, in the
    /// commit the others were read in, a binary file's bytes only counted; its text is the same string until HEAD moves or
    /// the open file's encoding changes.</summary>
    public Task<FileVersion?> HeadAsync(string path)
    {
        UiThread.Check();
        if (git is not { } repository || !repository.IsInside(path) || (textsRead && textsOf is null))
        {
            return Task.FromResult<FileVersion?>(null);
        }
        if (!heads.TryGetValue(path, out HeadBytes? head))
        {
            HeadReads++;
            string revision = textsOf ?? "HEAD";
            head = new HeadBytes(repository.StoredAtAsync(revision, [path, Origin(path)]));
            heads[path] = head;
        }
        System.Text.Encoding? encoding = EncodingOf(path);
        if (head.Read is not { } read || read.In?.CodePage != encoding?.CodePage)
        {
            head.Read = read = (encoding, ReadAsync(head.Bytes, encoding, path));
        }
        return read.Version;
    }

    /// <summary>Whether git stores a file's lines alike whichever of LF and CRLF ends them, as it stores the last commit's
    /// (<see cref="Git.BreaksStoredAlikeAsync"/>): asked once of the last commit's text as read, until HEAD moves or the
    /// open file's encoding changes, never of an edit.</summary>
    public async Task<bool> BreaksStoredAlikeAsync(string path)
    {
        UiThread.Check();
        Task<FileVersion?> version = HeadAsync(path);
        if (git is not { } repository || !heads.TryGetValue(path, out HeadBytes? head))
        {
            return false;
        }
        if (head.Breaks is not { } asked || asked.Of != version)
        {
            head.Breaks = asked = (version, BreaksStoredAlikeAsync(repository, textsOf ?? "HEAD", [path, Origin(path)], path, version));
        }
        return await asked.Alike;
    }

    /// <summary>Whether git stores a file's lines alike whichever of LF and CRLF ends them, as it stores a revision's
    /// <paramref name="version"/> of it, which it has at <paramref name="stored"/>.</summary>
    public async Task<bool> BreaksStoredAlikeAsync(string revision, string stored, FileVersion version, string path)
    {
        UiThread.Check();
        return git is { } repository && await BreaksStoredAlikeAsync(repository, revision, [stored], path, Task.FromResult<FileVersion?>(version));
    }

    private static async Task<bool> BreaksStoredAlikeAsync(Git repository, string revision, IReadOnlyList<string> paths, string path, Task<FileVersion?> version) =>
        await version is { Stored: { } text } && await repository.BreaksStoredAlikeAsync(revision, paths, path, text);

    /// <summary>How a file's bytes differ from the last commit's where either is binary, which have no lines to compare;
    /// null where both are text, or the file is in neither. The disk's are read as they come, and a binary file's in the
    /// last commit, never kept, as git writes them again: a large asset costs a probe and a buffer rather than its size.</summary>
    public async Task<BinaryChange?> BinaryChangeAsync(string path)
    {
        UiThread.Check();
        Task<FileVersion?> version = HeadAsync(path);
        (Git? repository, string revision) = (git, textsOf ?? "HEAD");
        Task<StoredBytes?> committed = repository is not null && heads.TryGetValue(path, out HeadBytes? read) ? read.Bytes : Task.FromResult<StoredBytes?>(null);
        await version;
        StoredBytes? head = await committed;
        return head is { IsBinary: true } ? await repository!.BinaryChangeAsync(revision, [path, Origin(path)], path) : await Git.BinaryChangeAsync(head?.Text, path);
    }

    /// <summary>How a file's encoding or byte order mark differs from the last commit's, in words
    /// (<see cref="FileText.StoredChange"/>): the open file's as its save would write it, else the file's on disk; null
    /// where both are stored alike, or either is missing or binary.</summary>
    public async Task<string?> EncodingChangeAsync(string path)
    {
        UiThread.Check();
        Task<FileVersion?> version = HeadAsync(path);
        OpenDocument? open = Holding(path);
        (System.Text.Encoding Encoding, bool Bom)? now = open is not null ? (open.Encoding, open.HasBom)
            : await BackgroundWork.Run(() => File.Exists(path) && FileText.Load(path) is { } disk ? (disk.Encoding, disk.HasBom) : ((System.Text.Encoding, bool)?)null);
        if (await version is not { Stored: { } head } || now is not { } stored)
        {
            return null;
        }
        return await BackgroundWork.Run(() => head.StoredChange(stored.Encoding, stored.Bom));
    }

    /// <summary>A file as a revision has it, read in the open file's encoding, or null when the revision has no such
    /// file.</summary>
    public async Task<FileVersion?> VersionAsync(string revision, string path, string readAs)
    {
        UiThread.Check();
        if (git is not { } repository)
        {
            return null;
        }
        System.Text.Encoding? encoding = EncodingOf(readAs);
        return await ReadAsync(repository.StoredAtAsync(revision, [path]), encoding, readAs);
    }

    /// <summary>The encoding the open file at a path is read in, or null when none holds its text.</summary>
    private System.Text.Encoding? EncodingOf(string path) => Holding(path)?.Encoding;

    /// <summary>The open file that holds the text of a path, or null.</summary>
    private OpenDocument? Holding(string path) => documents.Keys.FirstOrDefault(d => d.HoldsText && PathNames.Same(d.Path, path));

    private static async Task<FileVersion?> ReadAsync(Task<StoredBytes?> bytes, System.Text.Encoding? encoding, string path) =>
        await bytes is { } read ? await BackgroundWork.Run(() => FileVersion.Read(read, encoding, path)) : null;

    /// <summary>A file or folder moved here (a rename, a code fix): until a commit has its files at their new paths,
    /// their change bars, diff and rollback compare with what the last commit has where they were. What the index has of
    /// it moves with it, as git mv moves it, so the status shows a rename; the next read waits for that. The moves made
    /// together (a refactoring's, an Undo's) go to the index together, in one write.</summary>
    public void Moved(string from, string to)
    {
        moves.Add((from, to));
        heads.Clear();
        if (git is not { } repository)
        {
            return;
        }
        if (unindexed is { } gathering)
        {
            gathering.Add((from, to));
            return;
        }
        List<(string From, string To)> batch = [(from, to)];
        unindexed = batch;
        indexed = MoveInIndexAsync(indexed, repository, batch);
    }

    /// <summary>The index's moves still under way, which a read waits out so it never shows a move half made.</summary>
    private Task indexed = Task.CompletedTask;

    /// <summary>Ends once every move heard so far is in the index, or Git refused it (said through <see cref="Message"/>);
    /// it never fails.</summary>
    public Task MovesIndexed
    {
        get
        {
            UiThread.Check();
            return indexed;
        }
    }

    /// <summary>The moves heard since the last batch for the index was taken, which the next takes with it.</summary>
    private List<(string From, string To)>? unindexed;

    /// <summary>One batch of moves made in the index after those before it, gathering the moves made on the window's thread
    /// until it next runs; it never fails, so the reads waiting on it go on.</summary>
    private async Task MoveInIndexAsync(Task before, Git repository, List<(string From, string To)> batch)
    {
        await Task.Yield();
        if (unindexed == batch)
        {
            unindexed = null;
        }
        await before;
        try
        {
            GitResult moved = await repository.MoveInIndexAsync(batch);
            if (!moved.Succeeded)
            {
                Message?.Invoke(this, $"Git did not take {(batch.Count == 1 ? $"{Path.GetFileName(batch[0].From)}'s move" : Plural.Of(batch.Count, "move"))}: {moved.Message}");
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    /// <summary>Where a file is in the last commit: where the status found it renamed from, else where it was before every
    /// move made here since the folder opened (or where that was renamed from, until the status reads those moves).</summary>
    public string Origin(string path)
    {
        if (renamed.TryGetValue(path, out string? from))
        {
            return from;
        }
        for (int i = moves.Count - 1; i >= 0; i--)
        {
            path = PathNames.Moved(path, moves[i].To, moves[i].From);
        }
        return renamed.GetValueOrDefault(path) ?? path;
    }

    private static Dictionary<string, string> Renames(IReadOnlyList<FileChange> changes)
    {
        Dictionary<string, string> found = new(PathNames.Comparer);
        foreach (FileChange change in changes)
        {
            if (change.From is { } from)
            {
                found[change.Path] = from;
            }
        }
        return found;
    }

    /// <summary>A status's renames taken in: each file whose place in the last commit they move is read there again, an
    /// open one's bars worked out again; none is when they move nothing (a move here the status has caught up with).</summary>
    private void TakeRenames(Dictionary<string, string> now)
    {
        if (now.Count == renamed.Count && now.All(r => renamed.TryGetValue(r.Key, out string? was) && PathNames.Same(was, r.Value)))
        {
            return;
        }
        string[] read = [.. heads.Keys];
        string[] before = [.. read.Select(Origin)];
        renamed = now;
        for (int i = 0; i < read.Length; i++)
        {
            if (PathNames.Same(before[i], Origin(read[i])))
            {
                continue;
            }
            heads.Remove(read[i]);
            foreach (OpenDocument document in documents.Keys.Where(d => PathNames.Same(d.Path, read[i])))
            {
                IdeErrors.Observe(MarkAsync(document));
            }
        }
    }

    /// <summary>Works out a file's change bars from the last commit's text against the editor's, off the window's
    /// thread; a newer edit's answer wins.</summary>
    private async Task MarkAsync(OpenDocument document)
    {
        if (!documents.TryGetValue(document, out Tracked? tracked))
        {
            return;
        }
        int generation = ++tracked.Generation;
        using (BackgroundWork.Scope())
        {
            FileDiff? diff = await DiffAsync(document);
            if (generation == tracked.Generation && documents.ContainsKey(document))
            {
                document.ChangeMarks = diff?.Marks ?? [];
            }
        }
    }

    /// <summary>An open file's text as it is now compared with the last commit's, or null when that has no such file or the
    /// file is open as its bytes. The comparison its change bars came from, or are coming from, when they are of this
    /// text, so what acts on a bar costs nothing more; else a new one, off the window's thread.</summary>
    public async Task<FileDiff?> DiffAsync(OpenDocument document)
    {
        UiThread.Check();
        if (!document.HoldsText || await HeadTextAsync(document.Path) is not { } head)
        {
            return null;
        }
        TextSnapshot text = document.Text.Snapshot;
        documents.TryGetValue(document, out Tracked? tracked);
        if (tracked?.Last is { } last && ReferenceEquals(last.Text, text) && ReferenceEquals(last.Head, head))
        {
            return await last.Diff;
        }
        // The last commit's lines are split once while its text stays the same.
        FileDiff? earlier = tracked?.Last is { Diff.IsCompletedSuccessfully: true } done && ReferenceEquals(done.Head, head) ? done.Diff.Result : null;
        Task<FileDiff> diff = CompareAsync(head, earlier, text);
        if (tracked is not null)
        {
            tracked.Last = new Compared(head, text, diff);
        }
        return await diff;
    }

    private async Task<FileDiff> CompareAsync(string head, FileDiff? earlier, TextSnapshot text)
    {
        Comparisons++;
        (FileDiff diff, long bytes) = await BackgroundWork.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            FileDiff compared = earlier?.Compare(text) ?? FileDiff.Compare(head, text);
            return (compared, GC.GetAllocatedBytesForCurrentThread() - before);
        });
        LargestComparison = Math.Max(LargestComparison, bytes);
        return diff;
    }

    /// <summary>An open file as Git follows it: one handler for its edits and one for its saves, and one wait each for its
    /// bars and its blame, all dropped with it.</summary>
    private sealed class Tracked(Debounce marks, Debounce reblame)
    {
        public Debounce Marks { get; } = marks;
        public Debounce Reblame { get; } = reblame;
        public int Generation { get; set; }
        public Compared? Last { get; set; }
        public bool Annotated { get; set; }
        public IReadOnlyList<BlameLine>? Blame { get; set; }

        /// <summary>The newest blame's number: one that lands under another was overtaken, or annotations closed.</summary>
        public int Blaming { get; set; }

        public EventHandler<Passenger.Text.DocumentChangedEventArgs>? OnChanged { get; set; }
        public EventHandler? OnSaved { get; set; }
    }

    /// <summary>The newest comparison of a file, running or done, and the texts it is between.</summary>
    private sealed record Compared(string Head, TextSnapshot Text, Task<FileDiff> Diff);

    /// <summary>A file's bytes in the last commit, and those bytes read in the encoding last asked for.</summary>
    private sealed class HeadBytes(Task<StoredBytes?> bytes)
    {
        public Task<StoredBytes?> Bytes { get; } = bytes;
        public (System.Text.Encoding? In, Task<FileVersion?> Version)? Read { get; set; }

        /// <summary>Whether git stores the file's lines alike whichever of LF and CRLF ends them, asked of the version read.</summary>
        public (Task<FileVersion?> Of, Task<bool> Alike)? Breaks { get; set; }
    }
}

/// <summary>A conflicted file's versions as the merge window shows them: the one both sides came from, yours (left) and
/// theirs (right), and the words for its sides.</summary>
public sealed record MergeTexts(string Path, string Base, string Left, string Right, ConflictSides Sides);

/// <summary>What a tag's push came to, and whether the remote refused it for having its own tag of the name.</summary>
public sealed record TagPush(VcsOutcome Outcome, bool RemoteHasAnother);

/// <summary>What an operation on the working tree came to: whether it worked, and what the person is told of it (git's own
/// words when it did not), the words the status bar is told as it lands.</summary>
public sealed record VcsOutcome(bool Succeeded, string Message)
{
    /// <summary>Why it did not work, or null when it did.</summary>
    public string? Problem => Succeeded ? null : Message;
}

/// <summary>A commit's outcome, the commit it made, and the push's outcome when one was asked for after it: a push the
/// remote refuses is a failure of its own, with the commit made all the same.</summary>
public sealed record CommitResult(VcsOutcome Commit, CommitInfo? Made, VcsOutcome? Push)
{
    public bool Succeeded => Commit.Succeeded && Push?.Succeeded != false;

    /// <summary>The commit of the moves alone made before <see cref="Made"/>, where they went first; null where they did not.</summary>
    public CommitInfo? Moves { get; init; }
}
