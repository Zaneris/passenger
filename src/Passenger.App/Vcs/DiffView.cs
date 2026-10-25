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
/// arrow puts that change back; F7 and Shift+F7 (F8 and Shift+F8 too off the Mac) go to the next and previous one; Settings
/// (its keys <see cref="AppActions.DiffSettings"/>'s) chooses how differences are highlighted, by words at first. Made by
/// <see cref="DiffPanes"/>.</summary>
public sealed class DiffView : DockPanel, IOwnKeys
{
    /// <summary>How long editing pauses before the differences are worked out again.</summary>
    private static readonly TimeSpan RediffDelay = TimeSpan.FromMilliseconds(120);

    private readonly DiffStrip bands;
    private readonly TextBlock count;
    private readonly Button settings;
    private readonly Debounce rediff;
    private FileDiff shown;
    private DiffHighlighting highlighting;
    private int comparing;
    private int wording;
    private bool following;

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
        Left.Show(new TextDocument(diff.Head), SelectionSet.Single(0), default);
        Right = new CodeEditor { IsReadOnly = !editable, ChangeBackgrounds = true };
        Right.Show(mine, SelectionSet.Single(0), default);
        bands = new DiffStrip(Bands, MergeSide.Left, Left, () => IsEditable, ignores: false, (band, _, _) => Revert(Hunks[band.Index])) { Width = 36 };

        Grid sides = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        sides.Children.Add(DiffLines.Title(titles?.Left ?? "HEAD", 0));
        sides.Children.Add(DiffLines.Title(titles?.Right ?? (editable ? "Your version" : "Your version (read-only)"), 2));
        Grid.SetRow(Left, 1);
        Grid.SetRow(bands, 1);
        Grid.SetColumn(bands, 1);
        Grid.SetRow(Right, 1);
        Grid.SetColumn(Right, 2);
        sides.Children.Add(Left);
        sides.Children.Add(bands);
        sides.Children.Add(Right);

        count = new TextBlock();
        settings = ToolButtons.Of(AppActions.DiffSettings, IconKeys.Settings, "Settings", _ => ShowSettings());
        settings.ContextMenu = SettingsMenu();
        Children.Add(DiffLines.Bar(name, Step, count, [], settings));
        Children.Add(sides);

        Left.ScrollChanged += (_, _) => Follow(Left, Right, toLeft: false);
        Right.ScrollChanged += (_, _) => Follow(Right, Left, toLeft: true);
        rediff = new Debounce(RediffDelay, CompareAsync);
        mine.Changed += OnMineChanged;
        // Closed, the diff lets go of the file's text and colours, which may outlive it by far, and drops a late comparison.
        DetachedFromVisualTree += (_, _) =>
        {
            rediff.Stop();
            mine.Changed -= OnMineChanged;
            comparing++;
            wording++;
            Left.Detach();
            Right.Detach();
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DiffLines.Step(e) is not 0 and int step)
            {
                Step(step);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Show(diff);
        IdeErrors.Observe(CompareAsync());
    }

    /// <summary>The last commit's side.</summary>
    public CodeEditor Left { get; }

    /// <summary>The file's side.</summary>
    public CodeEditor Right { get; }

    /// <summary>Next and Previous Difference's keys, ahead of what the IDE binds them to elsewhere (Smart Step Into's
    /// Shift+F7).</summary>
    public bool TakesKey(KeyEventArgs e) => DiffLines.Step(e) != 0;

    public bool IsEditable { get; }

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
        Right.ApplyEdits([shown.Restore(hunk)]);
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

    private void OnMineChanged(object? sender, DocumentChangedEventArgs e) => rediff.Restart();

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
        bool off = highlighting == DiffHighlighting.None;
        Left.SetChangeMarks(off ? [] : [.. Hunks.Select(h => h.OldCount == 0 ? new ChangeMark(h.OldStart, 0, ChangeMarkKind.Added)
            : h.NewCount == 0 ? new ChangeMark(h.OldStart, h.OldCount, ChangeMarkKind.Deleted)
            : new ChangeMark(h.OldStart, h.OldCount, ChangeMarkKind.Modified))]);
        Right.SetChangeMarks(off ? [] : diff.Marks);
        count.Text = off ? "Differences highlighting is off" : Hunks.Count switch
        {
            0 => "No differences",
            1 => "1 difference",
            _ => $"{Hunks.Count} differences",
        };
        bands.InvalidateVisual();
        IdeErrors.Observe(ShowWordsAsync(diff));
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

    /// <summary>The diff's settings: Highlighting Differences, the way in use ticked.</summary>
    private ContextMenu SettingsMenu()
    {
        List<Control> items = [new MenuItem { Header = "Highlighting Differences", IsEnabled = false }];
        (DiffHighlighting Way, string Title)[] ways =
            [(DiffHighlighting.Lines, "Lines"), (DiffHighlighting.Words, "Words"), (DiffHighlighting.Characters, "Characters"), (DiffHighlighting.None, "None")];
        foreach ((DiffHighlighting way, string title) in ways)
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
