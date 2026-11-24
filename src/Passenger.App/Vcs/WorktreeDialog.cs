using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>What New Worktree… opens on: the branch or tag it starts from (null for HEAD), the repository's branches, and
/// its main working tree's folder, beside which a new one goes.</summary>
public sealed record WorktreeStart(BranchInfo? From, IReadOnlyList<BranchInfo> Branches, string Main);

/// <summary>What New Worktree… closed with: the working tree to add, and whether to open it in a window of its own.</summary>
public sealed record WorktreeChoice(WorktreePlan Plan, bool Open);

/// <summary>New Worktree…: another checkout of the repository in a folder of its own, of a branch checked out nowhere else
/// or of a new one made from a revision (looked up as typed), in a folder beside the repository named for it unless the
/// person names another; and under them what it will do before anything runs, in words and in git's: what a worktree is,
/// that git keeps a branch in one at a time, and where it opens. Create Worktree closes it with the plan, greyed with why
/// while git would refuse it.</summary>
public sealed class WorktreeDialog : IdeWindow
{
    private readonly Func<string, Task<CommitInfo?>> commitOf;
    private readonly WorktreeStart start;
    private readonly string[] free;
    private readonly Segments<bool> kind = new();
    private readonly FormDropDown existing;
    private readonly TextBox name;
    private readonly TextBox from;
    private readonly TextBlock fromNote = FormRows.Note();
    private readonly TextBox folder;
    private readonly TextBlock folderNote = FormRows.Note(path: true);
    private readonly CheckBox open = new() { Content = "Open it in a new window", IsChecked = true };
    private readonly Grid existingRow;
    private readonly Grid nameRow;
    private readonly Grid fromRow;
    private readonly StackPanel said = new() { Spacing = Gaps.Tight };
    private readonly TextBlock gitWords = new() { Classes = { StyleClasses.Code, StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap };
    private readonly Debounce looking;
    private bool touched;

    /// <summary>The folder the person named, which a branch chosen no longer names for it.</summary>
    private bool folderNamed;

    /// <summary>What the revision last looked up names.</summary>
    private (string At, CommitInfo? Commit) looked = (string.Empty, null);

    /// <param name="start">What it opens on.</param>
    /// <param name="commitOf">The commit a revision names, or null.</param>
    public WorktreeDialog(WorktreeStart start, Func<string, Task<CommitInfo?>> commitOf)
    {
        this.start = start;
        this.commitOf = commitOf;
        free = [.. start.Branches.Where(b => b.Kind == RefKind.Local && b.Worktree is null).Select(b => b.Name)];
        FormRows.Scope(this);
        Title = "New Worktree";
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        looking = new Debounce(TypingPauses.GitNames, LookAsync);

        kind.Add(false, "A branch there is", "ExistingBranch");
        kind.Add(true, "A new branch", "NewBranch");
        ToolButtons.Grey(kind[false], free.Length > 0 ? null : "Every branch is checked out in a worktree already");
        bool startsFree = start.From is { Kind: RefKind.Local, Worktree: null };
        kind.Choice = !startsFree;
        kind.Chosen += (_, _) => Typed();
        existing = new FormDropDown { ItemsSource = free, SelectedIndex = startsFree ? Array.IndexOf(free, start.From!.Name) : free.Length > 0 ? 0 : -1 };
        existing.SelectionChanged += (_, _) => Typed();
        name = new TextBox
        {
            Text = start.From is { Kind: RefKind.Remote } remote ? remote.Name[(remote.Name.IndexOf('/', StringComparison.Ordinal) + 1)..] : string.Empty,
            PlaceholderText = "feature/login",
        };
        name.TextChanged += (_, _) => Typed();
        from = new TextBox { Text = start.From is { } branch ? branch.Ref.GitName ?? branch.Ref.FullName : "HEAD" };
        from.TextChanged += (_, _) => Typed();
        folder = new TextBox();
        folder.TextChanged += (_, _) =>
        {
            folderNamed |= folder.Text != Beside();
            Typed();
        };
        open.IsCheckedChanged += (_, _) => Lay();

        existingRow = FormRows.Of("Branch", existing);
        nameRow = FormRows.Of("Branch", name);
        fromRow = FormRows.Of("From", from, [fromNote]);
        StackPanel fields = new()
        {
            Spacing = FormRows.Between,
            Children =
            {
                FormRows.Of("Checks out", kind),
                existingRow,
                nameRow,
                fromRow,
                FormRows.Of("Folder", BrowseRow.Of(folder, BrowseAsync), [folderNote]),
                FormRows.Of(null, open),
            },
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
        CreateButton = DialogFooter.Default("Create Worktree");
        CreateButton.Click += (_, _) => IdeErrors.Observe(StartAsync());
        Content = DialogFooter.Headed(Title,
            new TextBlock
            {
                Classes = { StyleClasses.Muted },
                TextWrapping = TextWrapping.Wrap,
                Text = "A worktree is another checkout of this repository in a folder of its own, so two branches are worked on at once without stashing or switching.",
            },
            new StackPanel { Spacing = Gaps.Group, Children = { fields, preview } },
            DialogFooter.OkCancel(CreateButton, () => Close(null), problem));
        folder.Text = Beside();
        folderNamed = false;
        touched = false;
        Lay();
        Opened += (_, _) =>
        {
            looking.Restart();
            Dispatcher.UIThread.Post(() => (kind.Choice ? name : (Control)existing).Focus(), DispatcherPriority.Loaded);
        };
    }

    public Button CreateButton { get; }

    /// <summary>A branch there is, or a new branch.</summary>
    public Segments<bool> KindBox => kind;

    public ComboBox ExistingBox => existing;

    public TextBox NameBox => name;

    public TextBox FromBox => from;

    public TextBox FolderBox => folder;

    public CheckBox OpenBox => open;

    /// <summary>What it will do, a sentence each, as the preview says it.</summary>
    public IReadOnlyList<string> WhatItDoes => [.. said.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>What it does in git's words.</summary>
    public string GitWords => gitWords.Text ?? string.Empty;

    /// <summary>Why Create Worktree will not make it, once the person has had a hand in it; empty while it will.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>Create Worktree: the revision looked up as it stands, then the choice taken where git would make it, else
    /// why not said.</summary>
    public async Task StartAsync()
    {
        touched = true;
        if (kind.Choice && looked.At != From)
        {
            looking.Stop();
            await LookAsync();
        }
        if (Planned() is { } plan)
        {
            Close(new WorktreeChoice(plan, open.IsChecked == true));
        }
    }

    private string Branch => kind.Choice ? (name.Text ?? string.Empty).Trim() : existing.SelectedItem as string ?? string.Empty;

    private string From => (from.Text ?? string.Empty).Trim();

    private string Folder => (folder.Text ?? string.Empty).Trim();

    /// <summary>The folder beside the main working tree named for the branch.</summary>
    private string Beside() => Branch.Length > 0 ? Worktrees.Beside(start.Main, Branch) : string.Empty;

    private void Typed()
    {
        touched = true;
        if (!folderNamed && folder.Text != Beside())
        {
            folder.Text = Beside();
            folderNamed = false;
        }
        looking.Restart();
        Lay();
    }

    private async Task LookAsync()
    {
        string asked = From;
        CommitInfo? commit = asked.Length > 0 ? await commitOf(asked) : null;
        if (asked != From)
        {
            return;
        }
        looked = (asked, commit);
        Lay();
    }

    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the worktree" });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
        {
            folder.Text = path;
        }
    }

    /// <summary>The plan as the dialog stands where git would carry it out; null, with why said, where not.</summary>
    private WorktreePlan? Planned()
    {
        string? why = WhyNot();
        problem.Text = touched ? why : null;
        CreateButton.IsEnabled = why is null;
        return why is null ? new WorktreePlan(Path.GetFullPath(Folder), Branch, kind.Choice ? looked.Commit!.Hash : null) : null;
    }

    private string? WhyNot()
    {
        if (!kind.Choice && Branch.Length == 0)
        {
            return "Choose the branch to check out";
        }
        if (kind.Choice)
        {
            if (RefNames.WhyNot(Branch, "branch") is { } badName)
            {
                return badName;
            }
            if (start.Branches.Any(b => b.Kind == RefKind.Local && b.Name == Branch))
            {
                return $"{Branch} is a branch already: choose it as a branch there is";
            }
            if (From.Length == 0)
            {
                return "Say which commit the branch starts from: a hash, a branch or HEAD";
            }
            if (looked.At != From)
            {
                return "Looking up the commit…";
            }
            if (looked.Commit is null)
            {
                return $"{From} names no commit here";
            }
        }
        if (Folder.Length == 0)
        {
            return "Say which folder the worktree goes in";
        }
        if (!Path.IsPathFullyQualified(Folder))
        {
            return "Give the folder's whole path";
        }
        return Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any() ? $"{PathNames.Shown(Folder, null)} holds files already" : null;
    }

    /// <summary>The rows the choice of branch needs, then what it will do said again.</summary>
    private void Lay()
    {
        bool fresh = kind.Choice;
        existingRow.IsVisible = !fresh;
        nameRow.IsVisible = fresh;
        fromRow.IsVisible = fresh;
        bool current = looked.At == From;
        fromNote.Text = !fresh || !current || From.Length == 0 ? null : looked.Commit is { } commit ? $"{commit.Short} {commit.Subject}" : $"{From} names no commit here";
        folderNote.Text = Folder.Length > 0 && Path.IsPathFullyQualified(Folder) ? PathNames.Shown(Folder, null) : null;
        WorktreePlan? plan = Planned();
        said.Children.Clear();
        foreach (string sentence in Sentences(plan))
        {
            said.Children.Add(new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap });
        }
        gitWords.Text = plan is null ? null : GitSaid(plan);
        gitWords.IsVisible = plan is not null;
    }

    /// <summary>The preview's sentences: the branch made where it is new, the checkout in its folder and what a worktree
    /// shares, git's one worktree a branch, and where it opens.</summary>
    private IEnumerable<string> Sentences(WorktreePlan? plan)
    {
        if (plan is null)
        {
            yield return "Nothing yet: what stands in the way is said below.";
            yield break;
        }
        string where = IsBeside(plan.Folder) ? $"{Path.GetFileName(plan.Folder)}, a folder beside this repository's" : PathNames.Shown(plan.Folder, null);
        yield return plan.From is null
            ? $"Checks {plan.Branch} out in {where}: a second working tree of this repository, with files and staged changes of its own, sharing every commit, branch, tag and stash with this one."
            : $"Makes the branch {plan.Branch} from {looked.Commit!.Short} ({looked.Commit.Subject}) and checks it out in {where}: a second working tree of this repository, with files and staged changes of its own, sharing every commit, branch, tag and stash with this one.";
        yield return $"While {plan.Branch} is checked out there, no other worktree can check it out: git keeps a branch in one worktree at a time.";
        yield return open.IsChecked == true
            ? "Opens it in a new window, trusted as this repository is."
            : "Leaves it closed: Worktrees in the Git log's branches opens it.";
    }

    /// <summary>Whether a folder is beside the main working tree, in the folder that holds it.</summary>
    private bool IsBeside(string chosen)
    {
        string main = Path.TrimEndingDirectorySeparator(start.Main);
        return PathNames.Same(Path.GetDirectoryName(chosen) ?? chosen, Path.GetDirectoryName(main) ?? main);
    }

    /// <summary>The plan in git's own words, a folder beside the main working tree named from it.</summary>
    private string GitSaid(WorktreePlan plan)
    {
        string place = IsBeside(plan.Folder) ? $"../{Path.GetFileName(plan.Folder)}" : plan.Folder;
        return plan.From is null
            ? $"git worktree add {place} {plan.Branch}"
            : $"git worktree add -b {plan.Branch} {place} {(From == looked.Commit?.Hash ? looked.Commit!.Short : From)}";
    }
}
