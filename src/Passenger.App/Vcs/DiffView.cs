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
/// arrow puts that change back; F7 and Shift+F7 (F8 and Shift+F8 too off the Mac) go to the next and previous one; F4 asks
/// for its file at the caret's line (Jump to Source); Settings (its keys <see cref="AppActions.DiffSettings"/>'s) chooses
/// how differences are highlighted, by words at first; Find searches the side with the caret (<see cref="ShowFind"/>).
/// Made by <see cref="DiffPanes"/>.</summary>
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
    private Func<FileDiff, bool[]>? hunksChosen;
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
        DiffHighlighting highlighting = DiffHighlighting.Words)
    {
        shown = diff;
        this.highlighting = highlighting;
        IsEditable = editable;
        Left = new CodeEditor { IsReadOnly = true, ChangeBackgrounds = true };
        TextDocument head = new(diff.Head);
        Left.Show(head, SelectionSet.Single(0), default);
        Right = new CodeEditor { IsReadOnly = !editable, ChangeBackgrounds = true };
        Right.Show(mine, SelectionSet.Single(0), default);
        bands = new DiffStrip(Bands, MergeSide.Left, Left, () => IsEditable, ignores: false, (band, _, _) => Revert(Hunks[band.Index])) { Width = 36 };

        Grid sides = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        named = (titles?.Left ?? "HEAD", titles?.Right ?? (editable ? "Your version" : "Your version (read-only)"));
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

        count = new TextBlock();
        settings = ToolButtons.Of(AppActions.DiffSettings, IconKeys.Settings, "Settings", _ => ShowSettings());
        settings.ContextMenu = SettingsMenu();
        Button jump = ToolButtons.Of(IconKeys.Edit, "Jump to Source (F4)", AskForSource);
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
    /// each diff shown and each change of the choice, <paramref name="choose"/> told of a box the person turns.</summary>
    public void ChooseForCommit(Func<FileDiff, bool[]> chosen, Action<FileDiff, int, bool> choose)
    {
        hunksChosen = chosen;
        chooseHunk = choose;
        hunkBoxes.IsVisible = true;
        RefreshCommitChoices();
    }

    /// <summary>The boxes set again from the choice, after it changed.</summary>
    public void RefreshCommitChoices()
    {
        if (hunksChosen is null || settingHunkBoxes)
        {
            return;
        }
        hunksTaken = hunksChosen(shown);
        PlaceHunkBoxes();
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
                Avalonia.Automation.AutomationProperties.SetName(box, $"Include change {index + 1} in commit");
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
        ToolTip.SetTip(box, "Include this change in commit");
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

    /// <summary>Next and Previous Difference's keys, ahead of what the IDE binds them to elsewhere (Smart Step Into's
    /// Shift+F7), and Jump to Source's.</summary>
    public bool TakesKey(KeyEventArgs e) => DiffLines.Step(e) != 0 || IsJump(e);

    private static bool IsJump(KeyEventArgs e) => e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None;

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

    /// <summary>Puts one difference back as the last commit has it (the band's arrow), unless the file was edited since
    /// the differences were found, which may have moved it.</summary>
    public void Revert(DiffHunk hunk)
    {
        if (!IsEditable || !ReferenceEquals(Right.Document.Snapshot, shown.Text))
        {
            return;
        }
        reverting = true;
        try
        {
            Right.ApplyEdits([shown.Restore(hunk)]);
        }
        finally
        {
            reverting = false;
        }
        IdeErrors.Observe(CompareAsync());
    }

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
        if (reverting || Right.IsKeyboardFocusWithin)
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
        RefreshCommitChoices();
        bool off = highlighting == DiffHighlighting.None;
        Left.SetChangeMarks(off ? [] : [.. Hunks.Select(h => h.OldCount == 0 ? new ChangeMark(h.OldStart, 0, ChangeMarkKind.Added)
            : h.NewCount == 0 ? new ChangeMark(h.OldStart, h.OldCount, ChangeMarkKind.Deleted)
            : new ChangeMark(h.OldStart, h.OldCount, ChangeMarkKind.Modified))]);
        Right.SetChangeMarks(off ? [] : diff.Marks);
        Count();
        bands.InvalidateVisual();
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

    private void Count()
    {
        string lines = highlighting == DiffHighlighting.None ? "Differences highlighting is off" : Hunks.Count switch
        {
            0 => "No differences",
            1 => "1 difference",
            _ => $"{Hunks.Count} differences",
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
        List<Control> items = [new MenuItem { Header = "Highlighting Differences", IsEnabled = false }];
        foreach ((DiffHighlighting way, string title) in HighlightingWays)
        {
            MenuItem item = new() { Header = title, ToggleType = MenuItemToggleType.Radio, IsChecked = way == highlighting };
            item.Click += (_, _) => Highlighting = way;
            items.Add(item);
        }
        return new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight };
    }

    /// <summary>Opens Settings' menu, how differences are highlighted.</summary>
    public void ShowSettings() => settings.ContextMenu?.Open(settings);

    /// <summary>Scrolls one side to stand level with the other: the line at the top of one maps through the
    /// differences above it to the other's.</summary>
    private void Follow(CodeEditor from, CodeEditor to, bool toLeft)
    {
        bands.InvalidateVisual();
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
