using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>The side-by-side diff: the last commit's text on the left (read-only), the file as it is on the right (the
/// open file's own text, so an edit there is an edit to it), each change tinted on both sides and joined by a band
/// between them, the words that changed inside its lines marked stronger, the two kept level as either scrolls. A band's
/// arrow puts that change back in the open file's text, or writes it to a file not open, as Rollback Lines does to the
/// change at the caret (<see cref="RollBackAtCaretAsync"/>); F7 and Shift+F7 (F8 and Shift+F8 too off the Mac) go to the
/// next and previous one; F4 asks for its file at the caret's line (Jump to Source); Settings (its keys
/// <see cref="AppActions.DiffSettings"/>'s) chooses how differences are highlighted, by words at first; Find searches the
/// side with the caret (<see cref="ShowFind"/>). A side whose version has no such file (a file new since it, or gone) shows
/// a pane saying so in place of its lines (<see cref="NoFile"/>). Made by <see cref="DiffPanes"/>.</summary>
public sealed class DiffView : DockPanel, IOwnKeys
{
    /// <summary>How long editing pauses before the differences are worked out again.</summary>
    private static readonly TimeSpan RediffDelay = TimeSpan.FromMilliseconds(120);

    private readonly DiffStrip bands;
    /// <summary>The boxes beside the changes in view for whether each goes into the next commit, the change each stands
    /// beside, and whether each change of the diff shown goes in.</summary>
    private readonly Canvas hunkBoxes = new() { Width = 28, ClipToBounds = true, IsVisible = false };
    private readonly List<int> boxedHunks = [];
    private bool[] hunksTaken = [];
    private Func<FileDiff, bool[]?>? hunksChosen;
    private Action<FileDiff, int, bool>? chooseHunk;
    private bool settingHunkBoxes;

    private readonly TextBlock count;
    private readonly Button settings;
    private readonly Debounce rediff;
    private FileDiff shown;
    private DiffHighlighting highlighting;
    private int comparing;
    private int wording;
    private int separating;
    private bool following;
    private string? separators;
    private string? note;

    /// <summary>What each side is, above it.</summary>
    private readonly (string Left, string Right) named;

    /// <summary>Each side's carets and scroll as the view left its window, until it is put back in one.</summary>
    private (SelectionSet Left, Vector LeftScroll, SelectionSet Right, Vector RightScroll)? away;

    /// <param name="diff">The last commit's text (empty for a file it does not have) compared with the file's.</param>
    /// <param name="mine">The file's text now: the open file's document, or a copy when it is not open.</param>
    /// <param name="editable">Whether the right side is the open file, which can be edited and have changes put back.</param>
    /// <param name="titles">What the sides are, above them: the last commit and the file by default.</param>
    /// <param name="highlighting">How differences inside changed lines are marked: words by default.</param>
    public DiffView(string name, FileDiff diff, TextDocument mine, bool editable, (string Left, string Right)? titles = null,
        DiffHighlighting highlighting = DiffHighlighting.Words, MergeSide? noFile = null)
    {
        Subject = name;
        shown = diff;
        this.highlighting = highlighting;
        IsEditable = editable;
        NoFile = noFile;
        Left = new CodeEditor { IsReadOnly = true, ChangeBackgrounds = true };
        TextDocument head = new(diff.Head);
        Left.Show(head, SelectionSet.Single(0), default);
        Right = new CodeEditor { IsReadOnly = !editable, ChangeBackgrounds = true };
        Right.Show(mine, SelectionSet.Single(0), default);
        DiffSigns.Give(Left);
        DiffSigns.Give(Right);
        named = (titles?.Left ?? "HEAD", titles?.Right ?? (editable ? "Your version" : "Your version (read-only)"));
        bands = new DiffStrip(Bands, MergeSide.Left, Left, () => RollsBack, ignores: false,
            (band, stage) => (stage ? $"{(HeldAt(band.Index) == true ? "Unstage" : "Stage")} change {band.Index + 1}" : $"Roll back change {band.Index + 1} to {named.Left}", null),
            (band, stage, _) =>
            {
                if (!stage)
                {
                    Revert(Hunks[band.Index]);
                }
                else if (HeldAt(band.Index) is { } held)
                {
                    StageRequested?.Invoke(this, ([Hunks[band.Index]], !held));
                }
            }, AppActions.RollbackLines, band => HeldAt(band.Index), AppActions.Stage) { Width = 36 };

        Grid sides = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        Left.ReadOnlyWhy = Refusals.ReadOnly(named.Left);
        Right.ReadOnlyWhy = editable ? null : Refusals.ReadOnly(named.Right);
        sides.Children.Add(DiffLines.Title(named.Left, 0));
        sides.Children.Add(DiffLines.Title(named.Right, 3));
        Grid.SetRow(Left, 1);
        Grid.SetRow(bands, 1);
        Grid.SetColumn(bands, 1);
        Grid.SetRow(Right, 1);
        Grid.SetColumn(Right, 3);
        Grid.SetColumn(hunkBoxes, 2);
        Grid.SetRow(hunkBoxes, 1);
        hunkBoxes.SizeChanged += (_, _) => PlaceHunkBoxes();
        sides.Children.Add(hunkBoxes);
        sides.Children.Add(Left);
        sides.Children.Add(bands);
        sides.Children.Add(Right);
        leftNone = NoFilePane(Left, "New file");
        rightNone = NoFilePane(Right, "Deleted file");
        sides.Children.Add(leftNone);
        sides.Children.Add(rightNone);

        count = new TextBlock();
        settings = ToolButtons.Of(AppActions.DiffSettings, IconKeys.Settings, "Settings", _ => ShowSettings());
        settings.ContextMenu = SettingsMenu();
        Button jump = ToolButtons.Of(IconKeys.Edit, ActionTips.Named("Jump to Source", KeyWords.Of(ListKeys.SourceKey)), AskForSource);
        jump.Name = AppActions.DiffJumpToSource;
        Children.Add(DiffLines.Bar(name, Step, count, [jump], settings));
        Find = new Editors.FindBar { [DockProperty] = Dock.Top };
        Children.Add(Find);
        Children.Add(sides);
        // An open Find follows the side the person works in.
        Left.GotFocus += (_, _) => FollowFind(Left, opened: false);
        Right.GotFocus += (_, _) => FollowFind(Right, opened: false);

        Left.ScrollChanged += (_, _) => Follow(Left, Right, toLeft: false);
        Right.ScrollChanged += (_, _) => Follow(Right, Left, toLeft: true);
        rediff = new Debounce(RediffDelay, CompareAsync);
        mine.Changed += OnMineChanged;
        // Out of its window, the diff lets go of the file's text, which may outlive it by far, and drops a late comparison.
        DetachedFromVisualTree += (_, _) =>
        {
            rediff.Stop();
            Find.Detach();
            mine.Changed -= OnMineChanged;
            comparing++;
            wording++;
            away = (Left.Selections, Left.ScrollOffset, Right.Selections, Right.ScrollOffset);
            Left.Detach();
            Right.Detach();
        };
        // Put back in one (its tab's group moved by a split), it shows the texts again where it left them.
        AttachedToVisualTree += (_, _) =>
        {
            if (away is not { } left)
            {
                return;
            }
            away = null;
            Left.Show(head, left.Left, left.LeftScroll);
            Right.Show(mine, left.Right, left.RightScroll);
            mine.Changed += OnMineChanged;
            Show(shown);
            IdeErrors.Observe(CompareAsync());
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DiffLines.Step(e) is not 0 and int step)
            {
                Step(step);
                e.Handled = true;
            }
            else if (IsJump(e))
            {
                AskForSource();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Show(diff);
        IdeErrors.Observe(CompareAsync());
    }

    /// <summary>A box beside each change for whether it goes into the next commit: <paramref name="chosen"/> asked once for
    /// each diff shown and each change of the choice, null while no change is chosen apart (the boxes hidden),
    /// <paramref name="choose"/> told of a box the person turns.</summary>
    public void ChooseForCommit(Func<FileDiff, bool[]?> chosen, Action<FileDiff, int, bool> choose)
    {
        hunksChosen = chosen;
        chooseHunk = choose;
        RefreshCommitChoices();
    }

    /// <summary>The boxes set again from the choice, after it changed.</summary>
    public void RefreshCommitChoices()
    {
        if (hunksChosen is null || settingHunkBoxes)
        {
            return;
        }
        bool[]? taken = hunksChosen(shown);
        hunkBoxes.IsVisible = taken is not null;
        hunksTaken = taken ?? [];
        PlaceHunkBoxes();
    }

    /// <summary>A stage button beside each change, a plus that stages it and a minus that unstages it as git's index holds
    /// it whole: <paramref name="held"/> asked which changes of each diff shown the index holds whole (null where it can't
    /// say), again on <see cref="RefreshStaged"/>.</summary>
    public void StageWith(Func<FileDiff, Task<bool[]?>> held)
    {
        heldIn = held;
        RefreshStaged();
    }

    private Func<FileDiff, Task<bool[]?>>? heldIn;
    private (FileDiff Of, bool[] Held)? staged;
    private int stagings;

    /// <summary>Whether the diff stages its changes (<see cref="StageWith"/>).</summary>
    public bool Stages => heldIn is not null;

    /// <summary>Asks again which changes git's index holds, after it changed.</summary>
    public void RefreshStaged() => IdeErrors.Observe(AskStagedAsync());

    private async Task AskStagedAsync()
    {
        if (heldIn is null)
        {
            return;
        }
        FileDiff of = shown;
        int generation = ++stagings;
        bool[]? held = await heldIn(of);
        if (generation == stagings && ReferenceEquals(of, shown))
        {
            staged = held is null ? null : (of, held);
            bands.Refresh();
            StagedShown?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Which changes git's index holds, worked out for the differences shown.</summary>
    public event EventHandler? StagedShown;

    /// <summary>Whether git's index holds a change of the differences shown whole; null while that is not known (or the diff
    /// stages nothing).</summary>
    public bool? HeldAt(int index) => staged is { } known && ReferenceEquals(known.Of, shown) && index < known.Held.Length ? known.Held[index] : null;

    /// <summary>Changes to stage (true) or unstage, by a change's stage button or Stage's keys.</summary>
    public event EventHandler<(IReadOnlyList<DiffHunk> Changes, bool Stage)>? StageRequested;

    /// <summary>The changes the side with the keys has its selections over (the file's side unless the last commit's has
    /// them), each change a selection's lines touch, else the one at its caret, as the differences stand.</summary>
    public IReadOnlyList<DiffHunk> ChangesAtCarets()
    {
        bool head = Left.IsKeyboardFocusWithin;
        CodeEditor side = head ? Left : Right;
        TextSnapshot text = side.Document.Snapshot;
        HashSet<DiffHunk> found = [];
        foreach (Selection selection in side.Selections.Items)
        {
            int first = text.GetLineFromOffset(selection.Start);
            int last = Math.Max(first, text.GetLineFromOffset(selection.End) - (selection.End > selection.Start && text.GetLineStart(text.GetLineFromOffset(selection.End)) == selection.End ? 1 : 0));
            for (int line = first; line <= last; line++)
            {
                if ((head ? shown.HeadHunkAt(line) : shown.HunkAt(line)) is { } hunk)
                {
                    found.Add(hunk);
                }
            }
        }
        return [.. Hunks.Where(found.Contains)];
    }

    /// <summary>What Stage takes at the carets of the side with the keys: the lines a selection spans, each its own part of
    /// a change (<see cref="PartialCommit.LinesChosen"/>), and the whole change at a caret with nothing selected.</summary>
    public IReadOnlyList<DiffHunk> ChosenAtCarets()
    {
        bool head = Left.IsKeyboardFocusWithin;
        CodeEditor side = head ? Left : Right;
        TextSnapshot text = side.Document.Snapshot;
        List<(int First, int Last)> file = [];
        List<(int First, int Last)> last = [];
        foreach (Selection selection in side.Selections.Items)
        {
            int first = text.GetLineFromOffset(selection.Start);
            if (selection.Length > 0)
            {
                int end = text.GetLineFromOffset(selection.End);
                // A selection ending at a line's start takes none of that line.
                (head ? last : file).Add((first, Math.Max(first, text.GetLineStart(end) == selection.End ? end - 1 : end)));
            }
            else if ((head ? shown.HeadHunkAt(first) : shown.HunkAt(first)) is { } hunk)
            {
                // The whole change: every line of the file it has, or for one that only deletes lines a line beside them.
                file.Add(hunk.NewCount == 0 ? (Math.Max(0, hunk.NewStart - 1), hunk.NewStart) : (hunk.NewStart, hunk.NewStart + hunk.NewCount - 1));
            }
        }
        return PartialCommit.LinesChosen(shown, file, last);
    }

    /// <summary>Asks to stage the changes or lines at the carets (<see cref="ChosenAtCarets"/>), or to unstage them where
    /// git's index holds every change they are of whole (Stage's keys, turning as the change's own button turns): why not,
    /// else null once asked.</summary>
    public string? StageAtCarets(bool? stage = null)
    {
        if (!Stages)
        {
            return "Only a diff of the working tree against the last commit stages its changes";
        }
        if (ChosenAtCarets() is not { Count: > 0 } changes)
        {
            return "No change here to stage";
        }
        bool[] held = [.. ChangesAtCarets().Select(c => HeldAt(Hunks.ToList().IndexOf(c)) == true)];
        StageRequested?.Invoke(this, (changes, stage ?? !(held.Length > 0 && held.All(h => h))));
        return null;
    }

    /// <summary>A box beside the first line of each change in view, as the file's side scrolls: only the changes in view
    /// have one, so a diff of thousands of changes scrolls as lightly as one of a few.</summary>
    private void PlaceHunkBoxes()
    {
        if (hunksChosen is null)
        {
            return;
        }
        double line = Right.LineHeight;
        double scrolled = Right.ScrollOffset.Y;
        int index = line > 0 ? Sorted.FirstAtOrAfter(Hunks, static h => h.NewStart, (int)(scrolled / line)) : Hunks.Count;
        int used = 0;
        settingHunkBoxes = true;
        try
        {
            for (; index < Hunks.Count && index < hunksTaken.Length; index++)
            {
                double top = (Hunks[index].NewStart * line) - scrolled;
                if (top >= hunkBoxes.Bounds.Height)
                {
                    break;
                }
                CheckBox box = used < hunkBoxes.Children.Count ? (CheckBox)hunkBoxes.Children[used] : HunkBox(used);
                if (used == boxedHunks.Count)
                {
                    boxedHunks.Add(index);
                }
                boxedHunks[used] = index;
                ToolButtons.Tip(box, $"Include change {index + 1} in commit");
                box.IsChecked = hunksTaken[index];
                box.IsVisible = true;
                Canvas.SetTop(box, top + ((line - box.Height) / 2));
                used++;
            }
            for (int i = used; i < hunkBoxes.Children.Count; i++)
            {
                hunkBoxes.Children[i].IsVisible = false;
            }
        }
        finally
        {
            settingHunkBoxes = false;
        }
    }

    /// <summary>A box for the changes in view, the <paramref name="slot"/>th from the top, which chooses whichever change it
    /// stands beside.</summary>
    private CheckBox HunkBox(int slot)
    {
        CheckBox box = new() { MinHeight = 0, Height = 24 };
        box.IsCheckedChanged += (_, _) =>
        {
            if (!settingHunkBoxes && slot < boxedHunks.Count)
            {
                chooseHunk?.Invoke(shown, boxedHunks[slot], box.IsChecked == true);
            }
        };
        hunkBoxes.Children.Add(box);
        return box;
    }

    /// <summary>Find over the diff: it searches the side with the caret, the other side's matches marked too, F3 and Shift+F3
    /// stepping through those of the side searched.</summary>
    public Editors.FindBar Find { get; }

    /// <summary>Opens Find on the side with the keys, the file's unless the last commit's has them; a side that is read-only
    /// greys its replace row, saying so.</summary>
    public void ShowFind(bool replace)
    {
        FollowFind(Left.IsKeyboardFocusWithin ? Left : Right, opened: true);
        Find.Open(replace);
    }

    private void FollowFind(CodeEditor side, bool opened)
    {
        if (opened || Find.IsVisible)
        {
            Find.Attach(side, side == Left ? Right : Left, null, side == Right && IsEditable ? null : Refusals.ReadOnly(side == Left ? named.Left : named.Right));
        }
    }

    /// <summary>The last commit's side.</summary>
    public CodeEditor Left { get; }

    /// <summary>The file's side.</summary>
    public CodeEditor Right { get; }

    /// <summary>The side whose version has no such file, null where both have it: the left's for a file new since its
    /// version, the right's for one gone (while its text is empty, so a file come back on disk shows).</summary>
    public MergeSide? NoFile { get; }

    /// <summary>What shows in each side's place while its version has no such file.</summary>
    private readonly Border leftNone;
    private readonly Border rightNone;

    /// <summary>The side the keys go to as the diff opens or is shown again: the file's, unless it has no such file.</summary>
    public CodeEditor Keyed => Right.IsVisible ? Right : Left;

    /// <summary>A side's pane where its version has no such file: the side's ground, saying so, no line taken for its text.</summary>
    private static Border NoFilePane(CodeEditor side, string words)
    {
        Border pane = new() { Child = new ToolWindows.EmptyState(words), IsVisible = false };
        pane.Bind(Border.BackgroundProperty, side.GetResourceObservable(TokenKeys.Editor_Background));
        Grid.SetRow(pane, 1);
        Grid.SetColumn(pane, Grid.GetColumn(side));
        return pane;
    }

    /// <summary>Each side's lines, or in their place the pane saying its version has no such file.</summary>
    private void ShowFiles()
    {
        bool leftNew = NoFile == MergeSide.Left;
        bool rightGone = NoFile == MergeSide.Right && Right.Document.Snapshot.Length == 0;
        Left.IsVisible = !leftNew;
        leftNone.IsVisible = leftNew;
        Right.IsVisible = !rightGone;
        rightNone.IsVisible = rightGone;
    }

    /// <summary>Next and Previous Difference's keys, ahead of what the IDE binds them to elsewhere (Smart Step Into's
    /// Shift+F7), and Jump to Source's.</summary>
    public bool TakesKey(KeyEventArgs e) => DiffLines.Step(e) != 0 || IsJump(e);

    private static bool IsJump(KeyEventArgs e) => e.Key == ListKeys.SourceKey && e.KeyModifiers == KeyModifiers.None;

    /// <summary>The file the right side is a version of, which Jump to Source opens.</summary>
    public string? Source { get; init; }

    /// <summary>Jump to Source asked for, by F4, the toolbar's button or its action.</summary>
    public event EventHandler? SourceRequested;

    /// <summary>Jump to Source: asks whoever shows the diff for its file at the caret.</summary>
    public void AskForSource() => SourceRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Where Jump to Source lands in the file: the right side's caret, or the left's carried across the
    /// differences above it while the left has the keys.</summary>
    public TextPosition SourcePlace => Left.IsKeyboardFocusWithin
        ? new TextPosition(Math.Min((int)MapLine(Left.CaretPosition.Line, toLeft: false), Math.Max(0, Right.Document.Snapshot.LineCount - 1)), 0)
        : Right.CaretPosition;

    public bool IsEditable { get; }

    /// <summary>Where the file's side is a copy of a file not open, how a change put back is written to the file: given the
    /// text it was found in, the edit that puts it back and the change named ("change 2"), null once written, else why not.
    /// Null where the file's side is the open file's own text, or a version nothing is put back to (a commit's).</summary>
    public Func<TextSnapshot, TextEdit, string, Task<string?>>? PutsBackOnDisk
    {
        get => putsBackOnDisk;
        set
        {
            putsBackOnDisk = value;
            bands.Refresh();
        }
    }

    private Func<TextSnapshot, TextEdit, string, Task<string?>>? putsBackOnDisk;

    /// <summary>Whether a change can be put back: in the open file's text, or written to a file not open.</summary>
    public bool RollsBack => IsEditable || putsBackOnDisk is not null;

    /// <summary>Why no change of the diff can be put back: its file's side is a version of its own.</summary>
    public string? WhyNotRollBack => RollsBack ? null : Refusals.ReadOnly(named.Right);

    /// <summary>The copy of a file not open takes its text from disk (<paramref name="text"/>, null where it can't be read)
    /// where that differs, after something wrote it: a change put back, its Undo, another program.</summary>
    public void FollowDisk(string? text)
    {
        if (IsEditable || text is null || away is not null || text == Right.Document.Snapshot.ToString())
        {
            return;
        }
        taking = true;
        try
        {
            Right.Document.SetText(text, Right.Selections);
        }
        finally
        {
            taking = false;
        }
    }

    /// <summary>The copy is taking its text from disk, no edit of this view's.</summary>
    private bool taking;

    /// <summary>The file changed from this view: typed in on its side, or a difference put back by its band.</summary>
    public event EventHandler? Edited;

    /// <summary>A difference is being put back by the band, an edit of this view's own whatever has the keys.</summary>
    private bool reverting;

    /// <summary>How differences are highlighted (Highlighting Differences), Words at first.</summary>
    public DiffHighlighting Highlighting
    {
        get => highlighting;
        set
        {
            if (value == highlighting)
            {
                return;
            }
            highlighting = value;
            settings.ContextMenu = SettingsMenu();
            Show(shown);
            HighlightingChanged?.Invoke(this, value);
        }
    }

    /// <summary>The person chose another way to highlight differences, which the next diff opens with.</summary>
    public event EventHandler<DiffHighlighting>? HighlightingChanged;

    /// <summary>The words marked inside each side's changed lines, once worked out.</summary>
    public InlineChanges Words => new(Left.InlineChanges, Right.InlineChanges);

    /// <summary>The toolbar's Settings, whose menu is Highlighting Differences.</summary>
    public Button SettingsButton => settings;

    /// <summary>The differences as they stand.</summary>
    public IReadOnlyList<DiffHunk> Hunks => shown.Hunks;

    /// <summary>The comparison shown, whose texts the differences are of.</summary>
    public FileDiff Compared => shown;

    /// <summary>Puts one difference back as the other side has it (the band's arrow), unless the file was edited since the
    /// differences were found, which may have moved it (<see cref="RevertAsync"/>).</summary>
    public void Revert(DiffHunk hunk) => IdeErrors.Observe(RevertAsync(hunk));

    /// <summary>Puts one difference back as the other side has it: an edit of the open file its Undo takes back, or written to
    /// a file not open (<see cref="PutsBackOnDisk"/>), the copy taking it. Null once put back, else why not (a version of its
    /// own, edited since the differences were found, which may have moved it, or the write refused).</summary>
    public async Task<string?> RevertAsync(DiffHunk hunk)
    {
        if (WhyNotRollBack is { } readOnly)
        {
            return readOnly;
        }
        TextSnapshot compared = shown.Text;
        if (!ReferenceEquals(Right.Document.Snapshot, compared) || !Hunks.Contains(hunk))
        {
            return "The file changed since its differences were found: roll the change back again";
        }
        TextEdit edit = shown.Restore(hunk);
        if (!IsEditable)
        {
            if (await putsBackOnDisk!(compared, edit, $"change {Hunks.ToList().IndexOf(hunk) + 1}") is { } refused)
            {
                return refused;
            }
            // The copy shows what was written, unless it took the disk's text meanwhile.
            if (!ReferenceEquals(Right.Document.Snapshot, compared))
            {
                return null;
            }
        }
        reverting = true;
        try
        {
            if (IsEditable)
            {
                Right.ApplyEdits([edit]);
            }
            else
            {
                SelectionSet carets = Right.Selections;
                Right.Document.Edit([edit], carets, TextEditing.MapThrough(carets, [edit]));
            }
        }
        finally
        {
            reverting = false;
        }
        await CompareAsync();
        return null;
    }

    /// <summary>Rollback Lines in the diff: the change at the caret of the side with the keys (the file's, unless the last
    /// commit's has them) put back as the other side has it, once the differences are of the file's text as it is now.
    /// What came of it, in words.</summary>
    public async Task<(bool RolledBack, string Said)> RollBackAtCaretAsync()
    {
        if (WhyNotRollBack is { } readOnly)
        {
            return (false, readOnly);
        }
        bool head = Left.IsKeyboardFocusWithin;
        // Typed in just before, the differences shown are of an older text, whose changes may stand elsewhere now.
        while (!ReferenceEquals(Right.Document.Snapshot, shown.Text) && away is null)
        {
            await CompareAsync();
        }
        if (away is not null)
        {
            return (false, "The diff was closed");
        }
        if ((head ? shown.HeadHunkAt(Left.CaretPosition.Line) : shown.HunkAt(Right.CaretPosition.Line)) is not { } hunk)
        {
            return (false, NoChangeHere);
        }
        int index = Hunks.ToList().IndexOf(hunk) + 1;
        return await RevertAsync(hunk) is { } refused ? (false, refused) : (true, $"Rolled back change {index} to {named.Left}");
    }

    /// <summary>What Rollback Lines says with no change at the caret.</summary>
    public const string NoChangeHere = "No change here to roll back";

    /// <summary>The next or previous difference from the right side's caret, shown on both sides.</summary>
    public void Step(int step)
    {
        if (Hunks.Count == 0)
        {
            return;
        }
        int line = Right.CaretPosition.Line;
        DiffHunk target = step > 0
            ? Hunks.FirstOrDefault(h => h.NewStart > line, Hunks[0])
            : Hunks.LastOrDefault(h => h.NewStart + Math.Max(1, h.NewCount) <= line, Hunks[^1]);
        Right.GoTo(new TextPosition(Math.Min(target.NewStart, Right.Document.Snapshot.LineCount - 1), 0));
    }

    private void OnMineChanged(object? sender, DocumentChangedEventArgs e)
    {
        rediff.Restart();
        if (reverting || Right.IsKeyboardFocusWithin && !taking)
        {
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Compares the file's text as it is now, off the window's thread, unless the differences shown are of it;
    /// a newer text's answer wins.</summary>
    private async Task CompareAsync()
    {
        TextSnapshot text = Right.Document.Snapshot;
        int generation = ++comparing;
        if (ReferenceEquals(text, shown.Text))
        {
            return;
        }
        FileDiff from = shown;
        FileDiff diff = await BackgroundWork.Run(() => from.Compare(text));
        if (generation == comparing)
        {
            Show(diff);
        }
    }

    private void Show(FileDiff diff)
    {
        shown = diff;
        ShowFiles();
        RefreshCommitChoices();
        RefreshStaged();
        bool off = highlighting == DiffHighlighting.None;
        Left.SetChangeMarks(off ? [] : [.. Hunks.Select(h => h.OldCount == 0 ? new ChangeMark(h.OldStart, 0, ChangeMarkKind.Added)
            : h.NewCount == 0 ? new ChangeMark(h.OldStart, h.OldCount, ChangeMarkKind.Deleted)
            : new ChangeMark(h.OldStart, h.OldCount, ChangeMarkKind.Modified))]);
        Right.SetChangeMarks(off ? [] : diff.Marks);
        Count();
        bands.Refresh();
        IdeErrors.Observe(ShowWordsAsync(diff));
        IdeErrors.Observe(ShowSeparatorsAsync(diff));
    }

    /// <summary>What else differs than the lines, which the count says after theirs: the file's encoding or byte order
    /// mark and its mode, as their changes say them.</summary>
    public string? Note
    {
        get => note;
        set
        {
            note = value;
            Count();
        }
    }

    /// <summary>What the bar says of the differences: how many, then what differs besides the lines, alone where the
    /// lines do not.</summary>
    public string Said => count.Text ?? string.Empty;

    /// <summary>What is compared, as the bar names it.</summary>
    public string Subject { get; }

    /// <summary>The differences as plain text (Read as Text): what the bar says of them, then each change by where it stands
    /// on both sides, the lines the left side has marked "-" and those the right has "+".</summary>
    public string ChangesAsText()
    {
        TextSnapshot left = Left.Document.Snapshot;
        TextSnapshot right = shown.Text;
        System.Text.StringBuilder text = new(Said);
        for (int i = 0; i < Hunks.Count; i++)
        {
            DiffHunk hunk = Hunks[i];
            string where = hunk.OldCount == 0 ? $"{Lines(hunk.NewStart, hunk.NewCount)} of {named.Right} added"
                : hunk.NewCount == 0 ? $"{Lines(hunk.OldStart, hunk.OldCount)} of {named.Left} removed"
                : $"{Lines(hunk.OldStart, hunk.OldCount)} of {named.Left} changed to {Lines(hunk.NewStart, hunk.NewCount)} of {named.Right}";
            text.Append("\n\nChange ").Append(i + 1).Append(" of ").Append(Hunks.Count).Append(": ").Append(where);
            Append(left, hunk.OldStart, hunk.OldCount, "- ");
            Append(right, hunk.NewStart, hunk.NewCount, "+ ");
        }
        return text.ToString();

        static string Lines(int start, int count) => count == 1 ? $"Line {start + 1}" : $"Lines {start + 1} to {start + count}";

        void Append(TextSnapshot side, int start, int count, string mark)
        {
            for (int line = start; line < Math.Min(start + count, side.LineCount); line++)
            {
                LineSpan span = side.GetLine(line);
                text.Append('\n').Append(mark).Append(side.GetText(span.Start, span.Length));
            }
        }
    }

    private void Count()
    {
        string lines = highlighting == DiffHighlighting.None ? "Differences highlighting is off" : Hunks.Count switch
        {
            0 => "No differences",
            1 => "1 difference",
            _ => $"{Numbers.Said(Hunks.Count)} differences",
        };
        string[] besides = [.. new[] { separators, note }.OfType<string>()];
        count.Text = besides.Length == 0 ? lines : string.Join("; ", Hunks.Count == 0 && highlighting != DiffHighlighting.None ? besides : [lines, .. besides]);
    }

    /// <summary>Says how the lines alike on both sides changed their separators, worked out off the window's thread; a
    /// newer comparison's answer wins.</summary>
    private async Task ShowSeparatorsAsync(FileDiff diff)
    {
        int generation = ++separating;
        string? changed = await BackgroundWork.Run(() => diff.SeparatorChanges());
        if (generation == separating)
        {
            separators = changed;
            Count();
        }
    }

    /// <summary>Marks the words that differ inside the changed lines, worked out off the window's thread; a newer
    /// comparison's (or highlighting's) answer wins, and the file's side takes them only while its text is still the one
    /// compared: an edit since moved them, and the comparison after it marks them again.</summary>
    private async Task ShowWordsAsync(FileDiff diff)
    {
        int generation = ++wording;
        DiffHighlighting how = highlighting;
        if (how is not (DiffHighlighting.Words or DiffHighlighting.Characters))
        {
            Left.SetInlineChanges([]);
            Right.SetInlineChanges([]);
            return;
        }
        InlineChanges changes = await BackgroundWork.Run(() => diff.InnerChanges(how));
        if (generation != wording)
        {
            return;
        }
        Left.SetInlineChanges(changes.Left);
        if (ReferenceEquals(Right.Document.Snapshot, diff.Text))
        {
            Right.SetInlineChanges(changes.Right);
        }
    }

    /// <summary>Highlighting Differences' ways, as the diff's Settings and Settings' Version Control page name them.</summary>
    public static readonly IReadOnlyList<(DiffHighlighting Value, string Name)> HighlightingWays =
        [(DiffHighlighting.Lines, "Lines"), (DiffHighlighting.Words, "Words"), (DiffHighlighting.Characters, "Characters"), (DiffHighlighting.None, "None")];

    /// <summary>The diff's settings: Highlighting Differences, the way in use ticked.</summary>
    private ContextMenu SettingsMenu()
    {
        List<Control> items = [ToolWindows.ListMenus.Heading("Highlighting Differences")];
        foreach ((DiffHighlighting way, string title) in HighlightingWays)
        {
            items.Add(MenuChoice.Of(title, way == highlighting, () => Highlighting = way));
        }
        return new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight };
    }

    /// <summary>Opens Settings' menu, how differences are highlighted.</summary>
    public void ShowSettings() => settings.ContextMenu?.Open(settings);

    /// <summary>Scrolls one side to stand level with the other: the line at the top of one maps through the
    /// differences above it to the other's.</summary>
    private void Follow(CodeEditor from, CodeEditor to, bool toLeft)
    {
        bands.Refresh();
        PlaceHunkBoxes();
        if (following)
        {
            return;
        }
        following = true;
        DiffLines.ScrollTo(to, MapLine(DiffLines.Top(from), toLeft), from);
        following = false;
    }

    /// <summary>Where a line of one side (fractional, from zero) stands on the other.</summary>
    public double MapLine(double line, bool toLeft) => DiffLines.Map(line, Hunks.Count, i =>
    {
        DiffHunk h = Hunks[i];
        return toLeft ? new LinesAcross(h.NewStart, h.NewCount, h.OldStart, h.OldCount) : new LinesAcross(h.OldStart, h.OldCount, h.NewStart, h.NewCount);
    });

    /// <summary>Each difference's band, from its lines on the left to its lines on the right; none while highlighting is off.</summary>
    private IEnumerable<DiffBand> Bands()
    {
        if (highlighting == DiffHighlighting.None)
        {
            yield break;
        }
        for (int i = 0; i < Hunks.Count; i++)
        {
            DiffHunk hunk = Hunks[i];
            double leftTop = (hunk.OldStart * Left.LineHeight) - Left.ScrollOffset.Y;
            double rightTop = (hunk.NewStart * Right.LineHeight) - Right.ScrollOffset.Y;
            ChangeMarkKind kind = hunk.OldCount == 0 ? ChangeMarkKind.Added : hunk.NewCount == 0 ? ChangeMarkKind.Deleted : ChangeMarkKind.Modified;
            yield return new DiffBand(i, leftTop, leftTop + (hunk.OldCount * Left.LineHeight), rightTop, rightTop + (hunk.NewCount * Right.LineHeight), kind);
        }
    }
}
