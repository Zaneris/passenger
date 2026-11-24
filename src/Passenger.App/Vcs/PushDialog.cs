using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>What Push to… opens on: the local branch, the remote and branch there it tracks (null for none), and the
/// remotes.</summary>
public sealed record PushStart(string Branch, string? UpstreamRemote, string? UpstreamBranch, IReadOnlyList<string> Remotes)
{
    /// <summary>What it tracks, as remote/branch; null for none.</summary>
    public string? Upstream => UpstreamRemote is null || UpstreamBranch is null ? null : $"{UpstreamRemote}/{UpstreamBranch}";
}

/// <summary>Push to…: a local branch pushed to any remote, under its own name or another, and whether it tracks that
/// branch from then on; under them what it will do before anything runs, read from the remote as last fetched: the
/// commits it sends, a branch it makes there, commits the remote has that git would refuse to push over, and what the
/// branch tracks after, in words and in git's. Push closes it with the plan, greyed with why while git would refuse it.</summary>
public sealed class PushDialog : IdeWindow
{
    /// <summary>How many of the commits it sends the preview names; the rest are counted.</summary>
    private const int CommitsNamed = 3;

    private readonly PushStart start;
    private readonly Func<string, string, string, Task<PushPreview?>> previewOf;
    private readonly FormDropDown remote;
    private readonly TextBox remoteBranch;
    private readonly CheckBox track;
    private readonly StackPanel said = new() { Spacing = Gaps.Tight };
    private readonly TextBlock gitWords = new() { Classes = { StyleClasses.Code, StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap };
    private readonly Debounce looking;
    private bool touched;

    /// <summary>The remote and branch last looked up, and what a push there would send.</summary>
    private (string Remote, string Branch, PushPreview? Preview) looked = (string.Empty, string.Empty, null);

    /// <param name="previewOf">What pushing the branch to a remote's branch would send, as last fetched.</param>
    public PushDialog(PushStart start, Func<string, string, string, Task<PushPreview?>> previewOf)
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
        // Another remote than the one it tracks to begin with: the one it tracks is Push…'s.
        int first = start.Remotes.Count > 1 && start.UpstreamRemote is { } tracked ? start.Remotes.ToList().FindIndex(r => r != tracked) : 0;
        remote = new FormDropDown { ItemsSource = start.Remotes, SelectedIndex = Math.Max(first, 0) };
        remoteBranch = new TextBox { Text = start.Branch };
        track = new CheckBox { Content = "Track it from now on", IsChecked = start.UpstreamRemote is null };
        remote.SelectionChanged += (_, _) =>
        {
            touched = true;
            looking.Restart();
            Lay();
        };
        remoteBranch.TextChanged += (_, _) =>
        {
            touched = true;
            looking.Restart();
            Lay();
        };
        track.IsCheckedChanged += (_, _) => Lay();
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children = { FormRows.Of("To", remote), FormRows.Of("As", remoteBranch), FormRows.Of(null, track) },
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
        string tracks = start.Upstream is { } upstream ? $"{start.Branch} tracks {upstream}, where Push… sends it." : $"{start.Branch} tracks no remote's branch yet.";
        Content = DialogFooter.Headed(Title,
            new TextBlock
            {
                Classes = { StyleClasses.Muted },
                TextWrapping = TextWrapping.Wrap,
                Text = $"Sends the commits of {start.Branch} to a branch of any remote. {tracks}",
            },
            new StackPanel { Spacing = Gaps.Group, Children = { fields, preview } },
            DialogFooter.OkCancel(PushButton, () => Close(null), problem));
        Lay();
        Opened += (_, _) => looking.Restart();
    }

    public Button PushButton { get; }

    /// <summary>The remote it goes to.</summary>
    public ComboBox RemoteBox => remote;

    /// <summary>The branch it goes to there.</summary>
    public TextBox BranchBox => remoteBranch;

    public CheckBox TrackBox => track;

    /// <summary>What it will do, a sentence each, as the preview says it.</summary>
    public IReadOnlyList<string> WhatItDoes => [.. said.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>What it does in git's words.</summary>
    public string GitWords => gitWords.Text ?? string.Empty;

    /// <summary>Why Push will not push, once the person has had a hand in it; empty while it will.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>The plan it closed with; null until then, and when given up.</summary>
    public PushPlan? Plan { get; private set; }

    /// <summary>Push: the remote's branch looked up as it stands, then the plan taken where git would push, else why not
    /// said.</summary>
    public async Task StartAsync()
    {
        touched = true;
        if (!Current)
        {
            looking.Stop();
            await LookAsync();
        }
        if (Planned() is { } plan)
        {
            Plan = plan;
            Close(plan);
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
        return why is null ? new PushPlan(start.Branch, Remote, RemoteBranch, Tracks && !TrackedAlready) : null;
    }

    private string? WhyNot() =>
        start.Remotes.Count == 0 ? "The repository has no remote: Manage Remotes…, in the Git menu, adds one"
        : RefNames.WhyNot(RemoteBranch, "branch") is { } badName ? badName
        : !Current ? "Reading what the remote has, as last fetched…"
        : null;

    /// <summary>The track box said for the remote's branch chosen, then what it will do said again.</summary>
    private void Lay()
    {
        bool trackedAlready = TrackedAlready;
        track.IsEnabled = !trackedAlready;
        track.Content = trackedAlready ? $"{start.Branch} tracks {Remote}/{RemoteBranch} already" : $"Track {Remote}/{RemoteBranch} from now on";
        PushPlan? plan = Planned();
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
        string named = string.Join(", ", preview.Sends.Take(CommitsNamed).Select(c => $"{c.Short} {c.Subject}"));
        string more = preview.Sends.Count > CommitsNamed ? $", and {Plural.Of(preview.Sends.Count - CommitsNamed, "more")}" : string.Empty;
        string sends = preview.Sends.Count == 0 ? string.Empty : $"{Plural.Of(preview.Sends.Count, "commit")} ({named}{more})";
        yield return preview.RemoteHasBranch
            ? preview.Sends.Count == 0 ? $"Sends nothing: {there} has every commit of {plan.Branch} already, as last fetched." : $"Sends {sends} to {there}, as last fetched."
            : preview.Sends.Count == 0 ? $"Makes the branch {plan.RemoteBranch} on {plan.Remote} at {plan.Branch}'s commit, which {plan.Remote} has already."
            : $"Makes the branch {plan.RemoteBranch} on {plan.Remote}, sending {sends} it lacks, as last fetched.";
        if (preview.RemoteAhead > 0)
        {
            yield return $"{there} has {Plural.Of(preview.RemoteAhead, "commit")} {plan.Branch} lacks: git refuses the push until they are brought in (merge or rebase onto {there}).";
        }
        yield return plan.Track ? $"{plan.Branch} tracks {there} from now on: Push… and Update Project go there."
            : start.Upstream is { } upstream ? $"{plan.Branch} keeps tracking {upstream}."
            : $"{plan.Branch} still tracks nothing: Push… asks where to send it.";
    }

    /// <summary>The plan in git's own words.</summary>
    private static string GitSaid(PushPlan plan) =>
        $"git push{(plan.Track ? " -u" : string.Empty)} {plan.Remote} {(plan.RemoteBranch == plan.Branch ? plan.Branch : $"{plan.Branch}:{plan.RemoteBranch}")}";
}
