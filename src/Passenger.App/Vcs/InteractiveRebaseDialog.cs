using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App.Docking;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A commit of the branch as the Interactive Rebase dialog lists it: the plan's step, and the commit it folds into
/// where it folds into one.</summary>
public sealed record RebaseRow(PlannedCommit Step, string? Into);

/// <summary>A commit of the history the rebase makes as the dialog's preview lists it (or the commit it goes onto, at its
/// foot, <see cref="Made"/> null): its subject, where it comes from, what the rebase does to it, and, where git is foreseen
/// to stop for conflicts in making it, <see cref="Stops"/>'s words for its tag ("stops for conflicts", or "may stop for
/// conflicts" after an earlier stop) and <see cref="StopsWhy"/> naming the commit and its files.</summary>
public sealed record MadeRow(RebasedCommit? Made, string Subject, string From, IReadOnlyList<string> Tags, string? Stops = null, string? StopsWhy = null);

/// <summary>Interactively Rebase from Here: the branch's commits from the one chosen, newest first, each dragged (or moved
/// by Alt+Up and Alt+Down) into its place and given what happens to it, by its box, its menu or git's letter for it (P, R,
/// E, S, F, D; Delete drops it); the message of the commit a row makes under the list, written there for a reword or a
/// squash; and beside them the history the rebase makes, newest first over the commit it goes onto, each commit saying
/// where it comes from and whether it moved, took a new message, folded others in or stops to be edited, kept where git
/// leaves it as it is, and whether git will stop for conflicts in making it, foreseen once the preview shows. Start
/// Rebasing closes it with the plan as it stands, greyed with why while git could not run it.</summary>
public sealed class InteractiveRebaseDialog : IdeWindow
{
    private static readonly RebaseCommand[] Commands = Enum.GetValues<RebaseCommand>();

    private readonly string? onto;
    private readonly List<PlannedCommit> steps;
    private readonly ObservableCollection<RebaseRow> rows = [];
    private readonly ObservableCollection<MadeRow> made = [];
    private readonly MadeRow ground;
    private readonly ListBox list;
    private readonly ListBox history;
    private readonly TextBox message;
    private readonly TextBlock left = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap };
    private readonly Panel surface = new();
    private readonly DropOverlay drop = new();
    private readonly DragGesture<RebaseRow> drag;
    private readonly Func<RebasePlan, CancellationToken, Task<IReadOnlyList<ForeseenConflict>?>>? foresee;

    /// <summary>The stops for conflicts foreseen of each plan asked, by its <see cref="RebasePlan.ReplayKey"/>; null where
    /// git could not say.</summary>
    private readonly Dictionary<string, IReadOnlyList<ForeseenConflict>?> foreseen = new(StringComparer.Ordinal);

    /// <summary>The foresight under way and the plan it is of, cancelled as the plan changes or the dialog closes.</summary>
    private CancellationTokenSource? foreseeing;
    private string? foreseeingKey;

    /// <summary>Whether the dialog has shown, from which the preview's stops are foreseen.</summary>
    private bool shown;
    private bool touched;

    /// <summary>What the message box was last given to show, which is no one's typing: the box tells of a change a moment
    /// after it is made.</summary>
    private string? shownMessage;

    /// <param name="plan">The commits as they stand, each picked.</param>
    /// <param name="branch">The branch rebased, as the window names it.</param>
    /// <param name="ontoWords">The commit they go onto as the preview's foot shows it (its hash and subject); null where the
    /// branch's first commit is among them.</param>
    /// <param name="foresee">Where a plan will stop for conflicts, or null where git cannot say; none foresees nothing.</param>
    public InteractiveRebaseDialog(RebasePlan plan, string branch, string? ontoWords,
        Func<RebasePlan, CancellationToken, Task<IReadOnlyList<ForeseenConflict>?>>? foresee = null)
    {
        this.foresee = foresee;
        onto = plan.Base;
        steps = [.. plan.Commits];
        Title = "Interactive Rebase";
        Width = DialogWidths.Workspace;
        Height = 600;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ground = new MadeRow(null, ontoWords ?? "No commit before them",
            ontoWords is null ? "The history starts with the commits above" : "Where the commits go, as it is", []);

        list = new ListBox { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(list, Row);
        list.SelectionChanged += (_, _) => ShowChosen();
        ListKeys.Act(list, new ListActs { Remove = () => Give(RebaseCommand.Drop), Keys = Keyed });
        ListMenus.Attach(list, () => Chosen is not { } row ? [] :
        [
            .. Commands.Select(c => (Control)ListMenus.Item($"{Word(c)}", () => Give(c), row.Step.Command == c ? $"{row.Step.Commit.Short} is set to {Word(c)} already" : null)),
            new Separator(),
            ListMenus.Item("Move Up", () => Shift(-1), WhyNotShift(-1)),
            ListMenus.Item("Move Down", () => Shift(1), WhyNotShift(1)),
        ]);
        surface.Children.Add(list);
        surface.Children.Add(drop);
        drag = new DragGesture<RebaseRow>(surface, drop, ZoneAt, Dropped);
        list.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

        UpButton = ToolButtons.Of(IconKeys.ArrowUp, ActionTips.Named("Move Up", KeyWords.Of(Key.Up, KeyModifiers.Alt)), () => Shift(-1));
        DownButton = ToolButtons.Of(IconKeys.ArrowDown, ActionTips.Named("Move Down", KeyWords.Of(Key.Down, KeyModifiers.Alt)), () => Shift(1));
        message = new TextBox
        {
            [Avalonia.Automation.AutomationProperties.NameProperty] = "Message",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 104,
        };
        message.TextChanged += (_, _) => Written();

        history = new ListBox { Classes = { StyleClasses.Tree }, ItemsSource = made };
        ReusedRows.Use(history, MadeRowView);

        Grid page = new() { ColumnDefinitions = { new ColumnDefinition(3, GridUnitType.Star), new ColumnDefinition(Gaps.Group, GridUnitType.Pixel), new ColumnDefinition(2, GridUnitType.Star) } };
        page.Children.Add(Pane("Commits, newest first", Framed(surface), new StackPanel
        {
            Spacing = Gaps.Tight,
            Margin = new Thickness(0, Gaps.Wide, 0, 0),
            Children = { new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = "Message of the commit it makes" }, message },
        }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Hair, Children = { UpButton, DownButton } }));
        Control after = Pane("The history it makes", Framed(history), left, null);
        Grid.SetColumn(after, 2);
        page.Children.Add(after);

        StartButton = DialogFooter.Default("Start Rebasing");
        StartButton.Click += (_, _) => Start();
        Content = DialogFooter.Headed($"Rebase {branch} from {steps[0].Commit.Short}",
            new TextBlock
            {
                Classes = { StyleClasses.Muted },
                TextWrapping = TextWrapping.Wrap,
                Text = "Drag a commit to move it, and choose what happens to each: the history it makes shows beside them before anything runs.",
            },
            page, DialogFooter.OkCancel(StartButton, () => Close(null), problem));
        Lay();
        Opened += (_, _) =>
        {
            shown = true;
            Foresee(Plan);
            Dispatcher.UIThread.Post(() => ListKeys.Hold(list, chooseFirst: true), DispatcherPriority.Loaded);
        };
        Closed += (_, _) => foreseeing?.Cancel();
    }

    public Button StartButton { get; }

    public Button UpButton { get; }

    public Button DownButton { get; }

    /// <summary>The commits as listed, newest first.</summary>
    public IReadOnlyList<RebaseRow> Rows => rows;

    /// <summary>The history the rebase makes as the preview lists it, newest first, the commit it goes onto last.</summary>
    public IReadOnlyList<MadeRow> History => made;

    /// <summary>The commits it leaves out, as the preview says them.</summary>
    public string LeftOut => left.IsVisible ? left.Text ?? string.Empty : string.Empty;

    /// <summary>The plan as it stands.</summary>
    public RebasePlan Plan => new(onto, steps);

    /// <summary>Why Start Rebasing will not run the plan, once the person has had a hand in it; empty while it will.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>The message box, which shows the chosen commit's message and takes a new one.</summary>
    public TextBox MessageBox => message;

    /// <summary>The row chosen, which the keys, the buttons and the message box act on.</summary>
    public RebaseRow? Chosen
    {
        get => list.SelectedItem as RebaseRow;
        set => list.SelectedItem = value;
    }

    /// <summary>The row of a commit by its subject, as listed.</summary>
    public RebaseRow RowOf(string subject) => rows.First(r => r.Step.Commit.Subject == subject);

    /// <summary>The chosen row's commit given another action, as its box, its menu and its key give it.</summary>
    public void Give(RebaseCommand command)
    {
        if (Chosen is not { } row || row.Step.Command == command)
        {
            return;
        }
        int at = StepOf(row);
        steps[at] = steps[at] with { Command = command };
        touched = true;
        Lay();
        if (command is RebaseCommand.Reword)
        {
            message.Focus();
            message.SelectAll();
        }
    }

    /// <summary>The chosen row moved one place up (newer) or down (older), as Alt+Up and Alt+Down move it.</summary>
    public void Shift(int by)
    {
        if (Chosen is { } row && WhyNotShift(by) is null)
        {
            MoveTo(row, rows.IndexOf(row) + by);
        }
    }

    private string? WhyNotShift(int by) => Chosen is not { } row ? "No commit is chosen"
        : rows.IndexOf(row) + by is int to && (to < 0 || to >= rows.Count) ? $"{row.Step.Commit.Short} is the {(by < 0 ? "newest" : "oldest")} already"
        : null;

    /// <summary>A row put at a place of the list, newest first; the steps, oldest first, follow.</summary>
    private void MoveTo(RebaseRow row, int place)
    {
        int from = StepOf(row);
        int to = steps.Count - 1 - Math.Clamp(place, 0, rows.Count - 1);
        if (from == to)
        {
            return;
        }
        PlannedCommit step = steps[from];
        steps.RemoveAt(from);
        steps.Insert(to, step);
        touched = true;
        Lay();
        list.SelectedIndex = steps.Count - 1 - to;
        ListKeys.Hold(list);
    }

    /// <summary>Where a row's commit stands among the steps, oldest first.</summary>
    private int StepOf(RebaseRow row) => steps.FindIndex(s => s.Commit.Hash == row.Step.Commit.Hash);

    /// <summary>Everything shown again from the steps: each row (where it changed), the history it makes, what it leaves
    /// out, the chosen row's message and whether Start Rebasing runs it.</summary>
    private void Lay()
    {
        RebasePlan plan = Plan;
        string? chosen = Chosen?.Step.Commit.Hash;
        for (int place = 0; place < steps.Count; place++)
        {
            int at = steps.Count - 1 - place;
            int head = plan.HeadOf(at);
            RebaseRow row = new(steps[at], steps[at].Folds && head >= 0 ? steps[head].Commit.Short : null);
            if (place == rows.Count)
            {
                rows.Add(row);
            }
            else if (!Equals(rows[place], row))
            {
                rows[place] = row;
            }
        }
        if (chosen is not null && Chosen?.Step.Commit.Hash != chosen)
        {
            list.SelectedItem = rows.FirstOrDefault(r => r.Step.Commit.Hash == chosen);
        }
        Foresee(plan);
        IReadOnlyList<ForeseenConflict> stops = foreseen.GetValueOrDefault(plan.ReplayKey) ?? [];
        List<MadeRow> history = [.. plan.Outcome().Reverse().Select(c => StopsIn(new MadeRow(c, c.Subject, FromWords(c), TagsOf(c)), stops)), ground];
        for (int at = 0; at < history.Count; at++)
        {
            if (at == made.Count)
            {
                made.Add(history[at]);
            }
            else if (!Alike(made[at], history[at]))
            {
                made[at] = history[at];
            }
        }
        while (made.Count > history.Count)
        {
            made.RemoveAt(made.Count - 1);
        }
        IReadOnlyList<PlannedCommit> dropped = plan.Dropped;
        left.IsVisible = dropped.Count > 0;
        left.Text = $"Left out: {string.Join(", ", dropped.Select(c => $"{c.Commit.Short} {c.Commit.Subject}"))}";
        string? why = plan.WhyNot;
        problem.Text = touched ? why : null;
        StartButton.IsEnabled = why is null;
        UpButton.IsEnabled = WhyNotShift(-1) is null;
        DownButton.IsEnabled = WhyNotShift(1) is null;
        ShowChosen();
    }

    /// <summary>Whether two rows of the history show the same, so a key typed in the message box draws only the row it changed.</summary>
    private static bool Alike(MadeRow shown, MadeRow next) => ReferenceEquals(shown, next)
        || (shown.Made is not null && next.Made is not null && shown.Subject == next.Subject && shown.From == next.From
            && shown.Made.Kept == next.Made.Kept && shown.Tags.SequenceEqual(next.Tags) && shown.StopsWhy == next.StopsWhy);

    /// <summary>The stops for conflicts of a plan foreseen off the window's thread, once the dialog has shown and where it
    /// was not foreseen already (none where git makes each commit as it stands): the foresight of another plan under way
    /// cancelled, and the preview laid again once it lands, where the plan still stands.</summary>
    private void Foresee(RebasePlan plan)
    {
        string key = plan.ReplayKey;
        if (!shown || foresee is null || key == foreseeingKey || foreseen.ContainsKey(key))
        {
            return;
        }
        foreseeing?.Cancel();
        foreseeing = null;
        foreseeingKey = null;
        if (plan.FirstReplayed == plan.Commits.Count)
        {
            foreseen[key] = [];
            return;
        }
        CancellationTokenSource cancel = new();
        foreseeing = cancel;
        foreseeingKey = key;
        IdeErrors.Observe(ForeseeAsync(plan, key, cancel));
    }

    private async Task ForeseeAsync(RebasePlan plan, string key, CancellationTokenSource cancel)
    {
        IReadOnlyList<ForeseenConflict>? stops;
        try
        {
            stops = await foresee!(plan, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (foreseeing == cancel)
            {
                foreseeing = null;
                foreseeingKey = null;
            }
            cancel.Dispose();
        }
        foreseen[key] = stops;
        if (IsVisible && Plan.ReplayKey == key)
        {
            Lay();
        }
    }

    /// <summary>A row of the history with the stops for conflicts foreseen in making it, where there are any: its tag says
    /// whether git surely stops there, and its tip names each commit that stops and its files.</summary>
    private static MadeRow StopsIn(MadeRow row, IReadOnlyList<ForeseenConflict> stops)
    {
        List<ForeseenConflict> its = [.. row.Made!.From.SelectMany(c => stops.Where(s => s.Commit == c.Commit.Hash))];
        if (its.Count == 0)
        {
            return row;
        }
        bool sure = its.Any(s => s.Sure);
        IEnumerable<string> said = its.Select(s =>
        {
            PlannedCommit commit = row.Made.From.First(c => c.Commit.Hash == s.Commit);
            return $"{(s.Sure ? "Stops" : "May stop")} at {commit.Commit.Short} {commit.Commit.Subject} for conflicts in {FilesWords(s.Files)}";
        });
        string why = string.Join("; ", said) + (sure ? string.Empty : ", foreseen as if the conflicts before it were resolved to each commit's own files");
        return row with { Stops = sure ? "stops for conflicts" : "may stop for conflicts", StopsWhy = why };
    }

    /// <summary>The files in conflict as a sentence lists them, the first few named.</summary>
    private static string FilesWords(IReadOnlyList<string> files) =>
        files.Count <= 4 ? Plural.Listed(files) : Plural.Listed([.. files.Take(3), Plural.Of(files.Count - 3, "more file")]);

    /// <summary>Where a commit the rebase makes comes from, in words: kept as it is, or made new of its commits.</summary>
    private static string FromWords(RebasedCommit commit)
    {
        string first = commit.From[0].Commit.Short;
        if (commit.Kept)
        {
            return $"{first}, kept as it is";
        }
        IEnumerable<string> folded = commit.From.Skip(1).Select(c => c.Commit.Short);
        return commit.From.Count > 1 ? $"New, of {first} with {Plural.Listed([.. folded])} folded in" : $"New, of {first}";
    }

    private static List<string> TagsOf(RebasedCommit commit)
    {
        List<string> tags = [];
        if (commit.Moved)
        {
            tags.Add("moved");
        }
        if (commit.Reworded)
        {
            tags.Add("new message");
        }
        if (commit.From.Count > 1)
        {
            tags.Add($"{commit.From.Count - 1} folded in");
        }
        if (commit.Stops)
        {
            tags.Add("stops to edit");
        }
        return tags;
    }

    /// <summary>The message box shows the message of the commit the chosen row makes (its own, or the one it folds into);
    /// a row left out makes none.</summary>
    private void ShowChosen()
    {
        RebasePlan plan = Plan;
        int head = Chosen is { } row ? plan.HeadOf(StepOf(row)) : -1;
        message.IsEnabled = head >= 0;
        string? shown = head >= 0 ? plan.MessageOf(head) : null;
        // A box saying it already, as typed, keeps its text and caret: a line break typed at its end is the next line's start.
        if (shown is null || message.Text is null || !RebasePlan.SameMessage(message.Text, shown))
        {
            message.Text = shown;
        }
        shownMessage = message.Text;
        message.PlaceholderText = Chosen is { Step.Command: RebaseCommand.Drop } ? "Left out, it makes no commit" : null;
        UpButton.IsEnabled = WhyNotShift(-1) is null;
        DownButton.IsEnabled = WhyNotShift(1) is null;
    }

    /// <summary>The message typed for the commit the chosen row makes: the commit heading it takes it, a picked one becoming
    /// a reword; typed back to the message the plan would make, it is the plan's again.</summary>
    private void Written()
    {
        if (message.Text == shownMessage || Chosen is not { } row)
        {
            return;
        }
        RebasePlan plan = Plan;
        int at = StepOf(row);
        int head = plan.HeadOf(at);
        if (head < 0)
        {
            return;
        }
        string text = message.Text ?? string.Empty;
        bool own = RebasePlan.SameMessage(text, plan.DefaultMessageOf(head));
        PlannedCommit step = steps[head];
        steps[head] = step with
        {
            Written = own ? null : text,
            Command = !own && head == at && step.Command is RebaseCommand.Pick ? RebaseCommand.Reword : step.Command,
        };
        touched = true;
        string? chosen = row.Step.Commit.Hash;
        Lay();
        list.SelectedItem = rows.FirstOrDefault(r => r.Step.Commit.Hash == chosen);
    }

    /// <summary>Git's letter for an action given the chosen row that action, and Alt+Up and Alt+Down moving it.</summary>
    private bool Keyed(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down)
        {
            Shift(e.Key is Key.Up ? -1 : 1);
            return true;
        }
        RebaseCommand? command = e.KeyModifiers != KeyModifiers.None ? null : e.Key switch
        {
            Key.P => RebaseCommand.Pick,
            Key.R => RebaseCommand.Reword,
            Key.E => RebaseCommand.Edit,
            Key.S => RebaseCommand.Squash,
            Key.F => RebaseCommand.Fixup,
            Key.D => RebaseCommand.Drop,
            _ => null,
        };
        if (command is { } given)
        {
            Give(given);
            return true;
        }
        return false;
    }

    /// <summary>Start Rebasing: the dialog closes with the plan as it stands, where git could run it; else it says why.</summary>
    public void Start()
    {
        touched = true;
        if (Plan.WhyNot is not null)
        {
            Lay();
            return;
        }
        Close(Plan);
    }

    /// <summary>What a command is called in the list and its menu.</summary>
    public static string Word(RebaseCommand command) => command.ToString();

    /// <summary>What a command does, as its box's tip says it.</summary>
    private static string Tip(RebaseCommand command) => command switch
    {
        RebaseCommand.Pick => "Pick: keep the commit as it is",
        RebaseCommand.Reword => "Reword: keep the commit under a new message, written below the list",
        RebaseCommand.Edit => "Edit: stop after the commit, to change it and commit again before the rebase goes on",
        RebaseCommand.Squash => "Squash: fold the commit into the one under it, their messages joined",
        RebaseCommand.Fixup => "Fixup: fold the commit into the one under it, keeping that one's message",
        _ => "Drop: leave the commit and its change out",
    };

    /// <summary>A commit's row: what happens to it, its hash, its subject (struck through where it is left out) and, where it
    /// folds into another, which.</summary>
    private Control Row()
    {
        ComboBox box = new() { Classes = { StyleClasses.Cell }, Width = 104, VerticalAlignment = VerticalAlignment.Center };
        foreach (RebaseCommand command in Commands)
        {
            ComboBoxItem item = new() { Content = Word(command) };
            ToolTip.SetTip(item, Tip(command));
            box.Items.Add(item);
        }
        Avalonia.Automation.AutomationProperties.SetName(box, "Action");
        TextBlock hash = new() { Classes = { StyleClasses.Muted, StyleClasses.Code }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0) };
        TextBlock subject = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock into = new() { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0) };
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Hair, 0) };
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(hash, Dock.Left);
        DockPanel.SetDock(into, Dock.Right);
        line.Children.Add(box);
        line.Children.Add(hash);
        line.Children.Add(into);
        line.Children.Add(subject);
        RebaseRow? shown = null;
        bool setting = false;
        box.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            // A box takes the choice it was given only once it is shown, which is no one's choosing.
            if (setting || shown is null || box.SelectedIndex < 0 || Commands[box.SelectedIndex] == shown.Step.Command)
            {
                return;
            }
            Chosen = shown;
            Give(Commands[box.SelectedIndex]);
        };
        return line.Showing<RebaseRow>(row =>
        {
            shown = row;
            setting = true;
            box.SelectedIndex = Array.IndexOf(Commands, row.Step.Command);
            setting = false;
            ToolTip.SetTip(box, Tip(row.Step.Command));
            hash.Text = row.Step.Commit.Short;
            subject.Text = row.Step.Commit.Subject;
            bool gone = row.Step.Command is RebaseCommand.Drop;
            subject.TextDecorations = gone ? TextDecorations.Strikethrough : null;
            subject.Classes.Set(StyleClasses.Muted, gone);
            into.IsVisible = row.Into is not null;
            into.Text = row.Into is null ? null : $"into {row.Into}";
            Avalonia.Automation.AutomationProperties.SetName(line, string.Join(", ", ((string?[])[Word(row.Step.Command), row.Step.Commit.Short, row.Step.Commit.Subject, into.Text]).OfType<string>()));
        });
    }

    /// <summary>A commit of the history the rebase makes: a dot on the line that joins them, its subject and what the rebase
    /// does to it, and under them where it comes from; the commit it goes onto, at the foot, hollow and muted.</summary>
    private Control MadeRowView()
    {
        Lane lane = new() { Width = 20 };
        TextBlock subject = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // The tag of a stop for conflicts, in the conflicted files' colour, after the others.
        Border stops = Tags.Of(null, TokenKeys.Vcs_File_Conflicted);
        StackPanel tags = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Small, 0, 0, 0), Children = { stops } };
        DockPanel top = new();
        DockPanel.SetDock(tags, Dock.Right);
        top.Children.Add(tags);
        top.Children.Add(subject);
        TextBlock from = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
        StackPanel words = new() { VerticalAlignment = VerticalAlignment.Center, Children = { top, from } };
        DockPanel line = new() { Height = ListRow.Height * 2, Margin = new Thickness(0, 0, Gaps.Normal, 0) };
        DockPanel.SetDock(lane, Dock.Left);
        line.Children.Add(lane);
        line.Children.Add(words);
        List<Border> shownTags = [];
        return line.Showing<MadeRow>(row =>
        {
            lane.Show(row.Made is null ? Lane.Foot : ReferenceEquals(row, made.FirstOrDefault()) ? Lane.Top : Lane.Through, row.Made?.Kept ?? true);
            subject.Text = row.Subject;
            subject.Classes.Set(StyleClasses.Muted, row.Made is null);
            from.Text = row.From;
            while (shownTags.Count < row.Tags.Count)
            {
                Border tag = Tags.Of(null);
                tags.Children.Insert(tags.Children.Count - 1, tag);
                shownTags.Add(tag);
            }
            for (int i = 0; i < shownTags.Count; i++)
            {
                shownTags[i].IsVisible = i < row.Tags.Count;
                Tags.WordsOf(shownTags[i]).Text = i < row.Tags.Count ? row.Tags[i] : null;
            }
            stops.IsVisible = row.Stops is not null;
            Tags.WordsOf(stops).Text = row.Stops;
            ToolTip.SetTip(stops, row.StopsWhy);
            Avalonia.Automation.AutomationProperties.SetName(line, string.Join(", ", ((string?[])[row.Subject, row.From, .. row.Tags, row.StopsWhy]).OfType<string>()));
        });
    }

    private static Border Framed(Control content) => new() { Classes = { StyleClasses.Framed }, ClipToBounds = true, Child = content };

    /// <summary>One of the page's two panes: its title (with its buttons at its right), what it lists, and what goes under.</summary>
    private static DockPanel Pane(string title, Control content, Control under, Control? buttons)
    {
        DockPanel heading = new() { Margin = new Thickness(0, 0, 0, Gaps.Tight), MinHeight = 28 };
        if (buttons is not null)
        {
            DockPanel.SetDock(buttons, Dock.Right);
            heading.Children.Add(buttons);
        }
        heading.Children.Add(new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = title, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(under, Dock.Bottom);
        return new DockPanel { Children = { heading, under, content } };
    }

    /// <summary>A left press on a row, but on its box, may begin a drag of it.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual pressed && pressed.FindAncestorOfType<ComboBox>(includeSelf: true) is null
            && pressed.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is RebaseRow row)
        {
            drag.Begin(row, e);
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
        {
            drag.Cancel();
            return;
        }
        if (drag.Continue(e) && e.Pointer.Captured != list)
        {
            e.Pointer.Capture(list);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (drag.End(e))
        {
            e.Handled = true;
        }
        if (e.Pointer.Captured == list)
        {
            e.Pointer.Capture(null);
        }
    }

    /// <summary>The gap between rows a point of the surface is nearest: before the row whose upper half it is over, after the
    /// one whose lower half; the list's end past the last; -1 over no row.</summary>
    private int GapAt(Point at)
    {
        int gap = -1;
        foreach (Control container in list.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, surface) is not { } corner)
            {
                continue;
            }
            int index = list.IndexFromContainer(container);
            if (at.Y < corner.Y + (container.Bounds.Height / 2))
            {
                return gap < 0 || index < gap ? index : gap;
            }
            gap = Math.Max(gap, index + 1);
        }
        return gap;
    }

    /// <summary>Where a drop lights up: a line across the gap the row would go into; none where it would stay where it is.</summary>
    private DropZone? ZoneAt(RebaseRow row, Point at)
    {
        int gap = GapAt(at);
        int from = rows.IndexOf(row);
        if (gap < 0 || gap == from || gap == from + 1)
        {
            return null;
        }
        double y = 0;
        foreach (Control container in list.GetRealizedContainers())
        {
            int index = list.IndexFromContainer(container);
            if ((index == gap || index == gap - 1) && container.TranslatePoint(default, surface) is { } corner)
            {
                y = index == gap ? corner.Y : corner.Y + container.Bounds.Height;
                break;
            }
        }
        return new DropZone(new Rect(0, y - 3, surface.Bounds.Width, 6));
    }

    private void Dropped(RebaseRow row, Point at)
    {
        int gap = GapAt(at);
        int from = rows.IndexOf(row);
        if (gap < 0 || from < 0)
        {
            return;
        }
        Chosen = row;
        MoveTo(row, gap > from ? gap - 1 : gap);
    }

    /// <summary>The line through the preview's commits, a dot at each: filled for a commit the rebase makes, hollow for one
    /// git keeps as it is and for the commit they go onto, where the line ends.</summary>
    private sealed class Lane : Control
    {
        public const int Top = 0;
        public const int Through = 1;
        public const int Foot = 2;

        private int place = Through;
        private bool hollow;

        public void Show(int at, bool kept)
        {
            place = at;
            hollow = kept;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            IBrush brush = Tokens.Brush(this, place == Foot ? TokenKeys.TextMuted : TokenKeys.Graph_0);
            Pen pen = new(brush, 1.6);
            double x = Bounds.Width / 2;
            double mid = ListRow.Height / 2;
            context.DrawLine(pen, new Point(x, place == Top ? mid : 0), new Point(x, place == Foot ? mid : Bounds.Height));
            context.DrawEllipse(hollow ? Tokens.Brush(this, TokenKeys.Editor_Background) : brush, pen, new Point(x, mid), 4, 4);
        }
    }
}
