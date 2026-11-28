using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>What the Push dialog opens on: the local branch, the remote and branch there it tracks (null for none), the
/// remotes, and whether it opens on another remote than the one tracked (Push to…) or where Push… sends the branch.</summary>
public sealed record PushStart(string Branch, string? UpstreamRemote, string? UpstreamBranch, IReadOnlyList<string> Remotes, bool Elsewhere = false)
{
    /// <summary>What it tracks, as remote/branch; null for none.</summary>
    public string? Upstream => UpstreamRemote is null || UpstreamBranch is null ? null : $"{UpstreamRemote}/{UpstreamBranch}";

    /// <summary>The remote it opens on: for Push to…, another than the one tracked where there is one; for Push…, the one
    /// tracked, else origin, else the first; null where there is none.</summary>
    public string? Remote => Remotes.Count == 0 ? null
        : Elsewhere ? (Remotes.Count > 1 && UpstreamRemote is { } tracked ? Remotes.First(r => r != tracked) : Remotes[0])
        : UpstreamRemote is { } own && Remotes.Contains(own) ? own
        : Remotes.Contains("origin") ? "origin" : Remotes[0];

    /// <summary>The branch there it opens on: Push…'s the one tracked, else one of the branch's own name.</summary>
    public string RemoteBranch => !Elsewhere && Remote == UpstreamRemote && UpstreamBranch is { } tracked ? tracked : Branch;
}

/// <summary>The Push dialog, of Push… and Push to…: a local branch pushed to any remote, under its own name or another, and
/// whether it tracks that branch from then on; under them what it will do before anything runs, read from the remote as
/// last fetched: the commits it sends, listed, a branch it makes there, commits the remote has that git would refuse to
/// push over, and what the branch tracks after, in words and in git's. Push closes it with the plan, greyed with why
/// while git would refuse it; Force Push, apart at the footer's left, closes it with the plan forced under a lease, greyed
/// with why while there is nothing of the remote's to replace.</summary>
public sealed class PushDialog : IdeWindow
{
    /// <summary>How many of the commits it sends show at once; the list scrolls to the rest.</summary>
    private const int RowsShown = 8;

    private readonly PushStart start;
    private readonly Func<string, string, string, Task<PushPreview?>> previewOf;
    private readonly FormDropDown remote;
    private readonly TextBox remoteBranch;
    private readonly CheckBox track;
    private readonly TextBlock sendsTitle = new() { Classes = { StyleClasses.PaneTitle } };
    private readonly ListBox sends = new() { Classes = { StyleClasses.Tree }, Focusable = false, MaxHeight = ListRow.Height * RowsShown };
    private readonly StackPanel sendsPane;
    private readonly StackPanel said = new() { Spacing = Gaps.Tight };
    private readonly TextBlock gitWords = new() { Classes = { StyleClasses.Code, StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap };
    private readonly Debounce looking;
    private bool touched;

    /// <summary>The remote and branch last looked up, and what a push there would send.</summary>
    private (string Remote, string Branch, PushPreview? Preview) looked = (string.Empty, string.Empty, null);

    /// <param name="previewOf">What pushing the branch to a remote's branch would send, as last fetched.</param>
    /// <param name="first">What a push to the remote and branch it opens on sends, read before it opened; null to read it
    /// as it opens.</param>
    public PushDialog(PushStart start, Func<string, string, string, Task<PushPreview?>> previewOf, PushPreview? first = null)
    {
        this.start = start;
        this.previewOf = previewOf;
        FormRows.Scope(this);
        Title = $"Push {start.Branch}";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        looking = new Debounce(TypingPauses.GitNames, LookAsync);
        remote = new FormDropDown { ItemsSource = start.Remotes, SelectedIndex = start.Remote is { } opened ? start.Remotes.ToList().IndexOf(opened) : -1 };
        remoteBranch = new TextBox { Text = start.RemoteBranch };
        track = new CheckBox { Content = "Track it from now on", IsChecked = start.UpstreamRemote is null };
        if (first is not null && start.Remote is { } read)
        {
            looked = (read, start.RemoteBranch, first);
        }
        remote.SelectionChanged += (_, _) =>
        {
            touched = true;
            looking.Restart();
            Lay();
        };
        TypedText.Hear(remoteBranch, () =>
        {
            touched = true;
            looking.Restart();
            Lay();
        });
        track.IsCheckedChanged += (_, _) => Lay();
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children = { FormRows.Of("To", remote), FormRows.Of("As", remoteBranch), FormRows.Of(null, track) },
        };
        ReusedRows.Use(sends, Row);
        Grid.SetIsSharedSizeScope(sends, true);
        sendsPane = new StackPanel
        {
            Spacing = Gaps.Tight,
            Children = { sendsTitle, new Border { Classes = { StyleClasses.Framed }, ClipToBounds = true, Child = sends } },
        };
        StackPanel preview = new()
        {
            Spacing = Gaps.Tight,
            Children =
            {
                new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = "What it does" },
                new Border { Classes = { StyleClasses.Framed }, Padding = new Thickness(Gaps.Normal), Child = new StackPanel { Spacing = Gaps.Normal, Children = { said, gitWords } } },
            },
        };
        PushButton = DialogFooter.Default("Push");
        PushButton.Click += (_, _) => IdeErrors.Observe(StartAsync());
        ForceButton = new Button { Content = "Force Push" };
        ForceButton.Click += (_, _) => IdeErrors.Observe(StartAsync(force: true));
        DockPanel left = new() { Children = { new StackPanel { Classes = { StyleClasses.Footer }, Children = { ForceButton }, [DockPanel.DockProperty] = Dock.Left }, problem } };
        problem.Margin = new Thickness(Gaps.Group, 0, 0, 0);
        problem.VerticalAlignment = VerticalAlignment.Center;
        string tracks = start.Upstream is { } upstream ? $"{start.Branch} tracks {upstream}, where Push… sends it." : $"{start.Branch} tracks no remote's branch yet.";
        Content = DialogFooter.Headed(Title,
            new TextBlock
            {
                Classes = { StyleClasses.Muted },
                TextWrapping = TextWrapping.Wrap,
                Text = $"Sends the commits of {start.Branch} to a branch of any remote. {tracks}",
            },
            new StackPanel { Spacing = Gaps.Group, Children = { fields, sendsPane, preview } },
            DialogFooter.OkCancel(PushButton, () => Close(null), left));
        Lay();
        Opened += (_, _) =>
        {
            if (!Current)
            {
                looking.Restart();
            }
        };
    }

    public Button PushButton { get; }

    /// <summary>Force Push: the remote's branch replaced by this one, only while it stands as last fetched.</summary>
    public Button ForceButton { get; }

    /// <summary>The remote it goes to.</summary>
    public ComboBox RemoteBox => remote;

    /// <summary>The branch it goes to there.</summary>
    public TextBox BranchBox => remoteBranch;

    public CheckBox TrackBox => track;

    /// <summary>The commits it sends as listed, newest first; none while it sends none or is still reading.</summary>
    public IReadOnlyList<CommitInfo> Sends => Current && looked.Preview is { } preview ? preview.Sends : [];

    /// <summary>What it will do, a sentence each, as the preview says it.</summary>
    public IReadOnlyList<string> WhatItDoes => [.. said.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>What it does in git's words.</summary>
    public string GitWords => gitWords.Text ?? string.Empty;

    /// <summary>Why Push will not push, once the person has had a hand in it; empty while it will.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>The plan it closed with; null until then, and when given up.</summary>
    public PushPlan? Plan { get; private set; }

    /// <summary>Push, or Force Push where <paramref name="force"/>: the remote's branch looked up as it stands, then the plan
    /// taken where git would push, else why not said.</summary>
    public async Task StartAsync(bool force = false)
    {
        touched = true;
        if (!Current)
        {
            looking.Stop();
            await LookAsync();
        }
        if (Planned() is { } plan && (!force || WhyNotForce() is null))
        {
            Plan = plan with { Force = force };
            Close(Plan);
        }
    }

    private string Remote => remote.SelectedIndex >= 0 && remote.SelectedIndex < start.Remotes.Count ? start.Remotes[remote.SelectedIndex] : string.Empty;

    private string RemoteBranch => (remoteBranch.Text ?? string.Empty).Trim();

    private bool Current => looked.Remote == Remote && looked.Branch == RemoteBranch;

    private bool Tracks => track.IsChecked == true;

    /// <summary>Whether the plan's branch is the one the local branch tracks already.</summary>
    private bool TrackedAlready => start.UpstreamRemote == Remote && start.UpstreamBranch == RemoteBranch;

    /// <summary>What a push to the remote's branch named now sends, then the preview again; a newer lookup's answer stands.</summary>
    private async Task LookAsync()
    {
        string asked = Remote;
        string branch = RemoteBranch;
        PushPreview? preview = asked.Length > 0 && RefNames.WhyNot(branch, "branch") is null ? await previewOf(start.Branch, asked, branch) : null;
        if (asked != Remote || branch != RemoteBranch)
        {
            return;
        }
        looked = (asked, branch, preview);
        Lay();
    }

    /// <summary>The plan as the dialog stands where git would push it; null, with why said, where not.</summary>
    private PushPlan? Planned()
    {
        string? why = WhyNot();
        problem.Text = touched ? why : null;
        PushButton.IsEnabled = why is null;
        ToolButtons.Grey(ForceButton, why ?? WhyNotForce());
        return why is null ? new PushPlan(start.Branch, Remote, RemoteBranch, Tracks && !TrackedAlready) : null;
    }

    private string? WhyNot() =>
        start.Remotes.Count == 0 ? "The repository has no remote: Manage Remotes…, in the Git menu, adds one"
        : RefNames.WhyNot(RemoteBranch, "branch") is { } badName ? badName
        : !Current ? "Reading what the remote has, as last fetched…"
        : null;

    /// <summary>Why Force Push has nothing to do where Push would push: nothing of the remote's branch is replaced, as last
    /// fetched, so it would push as Push does.</summary>
    private string? WhyNotForce() =>
        looked.Preview is not { } preview ? null
        : !preview.RemoteHasBranch ? $"{Remote} has no branch {RemoteBranch} to replace, as last fetched: Push makes it"
        : preview.RemoteAhead == 0 ? $"{Remote}/{RemoteBranch} has no commit {start.Branch} lacks, as last fetched: Push sends it"
        : null;

    /// <summary>The track box said for the remote's branch chosen, then the commits it sends and what it will do said again.</summary>
    private void Lay()
    {
        bool trackedAlready = TrackedAlready;
        track.IsEnabled = !trackedAlready;
        track.Content = trackedAlready ? $"{start.Branch} tracks {Remote}/{RemoteBranch} already" : $"Track {Remote}/{RemoteBranch} from now on";
        PushPlan? plan = Planned();
        PushPreview? preview = plan is null ? null : looked.Preview;
        sends.ItemsSource = preview?.Sends ?? [];
        sendsPane.IsVisible = preview is { Sends.Count: > 0 };
        sendsTitle.Text = preview is null ? null : preview.Sends.Count < preview.SendsCount
            ? $"The newest {Numbers.Said(preview.Sends.Count)} of the {Plural.Of(preview.SendsCount, "commit")} it sends"
            : $"The {Plural.Of(preview.SendsCount, "commit")} it sends, newest first";
        said.Children.Clear();
        foreach (string sentence in Sentences(plan))
        {
            said.Children.Add(new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap });
        }
        gitWords.Text = plan is null ? null : GitSaid(plan);
        gitWords.IsVisible = plan is not null;
    }

    /// <summary>The preview's sentences: what it sends and makes, what stands in its way, and what the branch tracks after.</summary>
    private IEnumerable<string> Sentences(PushPlan? plan)
    {
        if (plan is null || looked.Preview is not { } preview)
        {
            yield return plan is null && start.Remotes.Count > 0 && !Current ? "Reading what the remote has…" : "Nothing yet: what stands in the way is said below.";
            yield break;
        }
        string there = $"{plan.Remote}/{plan.RemoteBranch}";
        string sends = Plural.Of(preview.SendsCount, "commit");
        yield return preview.RemoteHasBranch
            ? preview.SendsCount == 0 ? $"Sends nothing: {there} has every commit of {plan.Branch} already, as last fetched." : $"Sends {sends} to {there}, as last fetched."
            : preview.SendsCount == 0 ? $"Makes the branch {plan.RemoteBranch} on {plan.Remote} at {plan.Branch}'s commit, which {plan.Remote} has already."
            : $"Makes the branch {plan.RemoteBranch} on {plan.Remote}, sending {sends} it lacks, as last fetched.";
        if (preview.RemoteAhead > 0)
        {
            yield return $"{there} has {Plural.Of(preview.RemoteAhead, "commit")} {plan.Branch} lacks, as after a rebase or an amend: git refuses Push until "
                + $"{Plural.Agreeing(preview.RemoteAhead, "it is", "they are")} brought in (merge or rebase onto {there}), and Force Push replaces "
                + $"{Plural.Agreeing(preview.RemoteAhead, "it", "them")}, only while {there} stands as last fetched.";
        }
        yield return plan.Track ? $"{plan.Branch} tracks {there} from now on: Push… and Update Project go there."
            : start.Upstream is { } upstream ? $"{plan.Branch} keeps tracking {upstream}."
            : $"{plan.Branch} still tracks nothing: Push… asks where to send it.";
    }

    /// <summary>The plan in git's own words.</summary>
    private static string GitSaid(PushPlan plan) =>
        $"git push{(plan.Track ? " -u" : string.Empty)} {plan.Remote} {(plan.RemoteBranch == plan.Branch ? plan.Branch : $"{plan.Branch}:{plan.RemoteBranch}")}";

    /// <summary>A commit it sends: its short hash, subject and author, the hashes and authors lined up across the rows.</summary>
    private static Control Row()
    {
        TextBlock hash = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0), Classes = { StyleClasses.Muted } };
        TextBlock subject = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 1 };
        TextBlock author = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, Gaps.Normal, 0), Classes = { StyleClasses.Muted }, [Grid.ColumnProperty] = 2 };
        // Each column as wide as its widest row's, through the list's shared size scope.
        ColumnDefinitions columns = [new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Hash" }, new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Author" }];
        Grid line = new() { ColumnDefinitions = columns, Height = ListRow.Height, Children = { hash, subject, author } };
        return line.Showing<CommitInfo>(commit =>
        {
            hash.Text = commit.Short;
            subject.Text = commit.Subject;
            author.Text = commit.Author;
            AutomationProperties.SetName(line, $"{commit.Short}, {commit.Subject}, {commit.Author}");
        });
    }
}
