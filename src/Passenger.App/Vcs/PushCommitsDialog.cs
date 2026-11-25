using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>The Push dialog, as Rider's Push Commits: where the branch goes (the remote's branch it tracks, or one of
/// several remotes for a branch that tracks none), the commits it sends, and where the remote's branch has commits the
/// branch lacks (a rebase's, an amend's), that a push is refused and Force Push replaces them. Push is the default; Force
/// Push stands apart at the footer's left, as Rider keeps it apart under Push. Either closes it with <see cref="Chosen"/>.</summary>
public sealed class PushCommitsDialog : IdeWindow
{
    private readonly PushCommitsPlan plan;
    private readonly FormDropDown? remotes;

    public PushCommitsDialog(PushCommitsPlan plan)
    {
        this.plan = plan;
        BranchInfo branch = plan.Branch;
        Title = "Push Commits";
        Width = DialogWidths.Form;
        Height = 420;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        FormRows.Scope(this);
        TextBlock target = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        Control? choice = null;
        if (plan.Remotes.Count > 1)
        {
            remotes = new FormDropDown { Name = "PushRemote" };
            foreach (string remote in plan.Remotes)
            {
                remotes.Items.Add(remote);
            }
            remotes.SelectedItem = plan.Remotes.Contains("origin") ? "origin" : plan.Remotes[0];
            remotes.SelectionChanged += (_, _) => target.Text = Target;
            choice = FormRows.Of("Push to", remotes);
            choice.Margin = new Thickness(0, 0, 0, Gaps.Wide);
            DockPanel.SetDock(choice, Dock.Top);
        }
        target.Text = Target;

        ListBox list = new() { Classes = { StyleClasses.Tree }, ItemsSource = plan.Outgoing, Focusable = false };
        ReusedRows.Use(list, Row);
        Grid.SetIsSharedSizeScope(list, true);
        ToolWindows.EmptyState empty = new(branch.Upstream is { } tracked ? $"No commits to push: {tracked} has every commit of {branch.Name}"
            : $"Every commit of {branch.Name} is on a remote already: Push makes the branch there")
        {
            IsVisible = plan.Outgoing.Count == 0,
        };
        Border table = new()
        {
            Classes = { StyleClasses.Framed },
            ClipToBounds = true,
            Child = new Panel { Children = { list, empty } },
        };
        TextBlock behind = new()
        {
            Text = Rejected,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = plan.Behind > 0,
            Margin = new Thickness(0, Gaps.Wide, 0, 0),
            [DockPanel.DockProperty] = Dock.Bottom,
        };
        behind.Classes.Add(StyleClasses.Muted);

        DockPanel page = new();
        if (choice is not null)
        {
            page.Children.Add(choice);
        }
        page.Children.Add(behind);
        page.Children.Add(table);

        Button push = DialogFooter.Default("Push");
        push.Click += (_, _) => Choose(force: false);
        Button force = new() { Content = "Force Push" };
        force.Click += (_, _) => Choose(force: true);
        TextBlock about = new() { Text = plan.Outgoing.Count >= PushCommitsPlan.MostListed ? $"The first {Numbers.Said(PushCommitsPlan.MostListed)} commits it sends" : $"{Plural.Of(plan.Outgoing.Count, "commit")} to push", Classes = { StyleClasses.Muted } };
        Content = DialogFooter.Headed("Push Commits", new StackPanel { Spacing = Gaps.Tight, Children = { target, about } },
            page,
            DialogFooter.Bar(DialogFooter.Of(push, DialogFooter.Cancel(() => Close(false))), force));
    }

    /// <summary>Where the branch goes, as the header says it: the branch it tracks, or a new one on the remote chosen.</summary>
    public string Target => plan.Branch.Upstream is { } upstream ? $"{plan.Branch.Name} → {upstream}"
        : $"{plan.Branch.Name} → {Remote ?? plan.Remotes.FirstOrDefault()}/{plan.Branch.Name} (new)";

    /// <summary>The words under the commits where the remote's branch has commits this one lacks.</summary>
    public string Rejected => plan.Behind == 0 ? string.Empty
        : $"{plan.Branch.Upstream} has {Plural.Of(plan.Behind, "commit")} {plan.Branch.Name} lacks, as after a rebase or an amend: Push is refused, "
            + $"and Force Push replaces {(plan.Behind == 1 ? "it" : "them")}, only while the remote's branch is still as last fetched.";

    /// <summary>The commits listed, newest first.</summary>
    public IReadOnlyList<CommitInfo> Outgoing => plan.Outgoing;

    /// <summary>The remotes offered for a branch that tracks none, of several; empty otherwise.</summary>
    public IReadOnlyList<string> Remotes => remotes is null ? [] : plan.Remotes;

    /// <summary>The remote chosen for a branch that tracks none; null where it tracks one, or the remote is the only one.</summary>
    public string? Remote
    {
        get => remotes?.SelectedItem as string;
        set
        {
            if (remotes is not null)
            {
                remotes.SelectedItem = value;
            }
        }
    }

    /// <summary>What was chosen once Push or Force Push closed it: whether forced, and the remote; null until then, and when
    /// cancelled.</summary>
    public (bool Force, string? Remote)? Chosen { get; private set; }

    /// <summary>Push, or Force Push where <paramref name="force"/>, as a click on its button does.</summary>
    public void Choose(bool force)
    {
        Chosen = (force, Remote);
        Close(true);
    }

    private static Control Row()
    {
        TextBlock hash = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0) };
        hash.Classes.Add(StyleClasses.Muted);
        TextBlock subject = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 1 };
        TextBlock author = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, Gaps.Normal, 0), [Grid.ColumnProperty] = 2 };
        author.Classes.Add(StyleClasses.Muted);
        // The hashes and authors line up across the rows, each column as wide as its widest (the list's shared size scope).
        ColumnDefinitions columns = [new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Hash" }, new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Author" }];
        Grid line = new() { ColumnDefinitions = columns, Height = ListRow.Height, Children = { hash, subject, author } };
        return line.Showing<CommitInfo>(commit =>
        {
            hash.Text = commit.Short;
            subject.Text = commit.Subject;
            author.Text = commit.Author;
        });
    }
}
