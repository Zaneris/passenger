using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Explorer;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Rider's Show History over real working trees, as tabs of the Git window: a file's commits followed through a
/// rename, each opening what it did to the file; a folder's, only those that touched it; and those that changed the lines
/// chosen, counted as the last commit has them under unsaved edits. Compare with Branch or Tag puts another branch's text
/// beside the file's.</summary>
public sealed class HistoryTests : IDisposable
{
    private const string Shop = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("history-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_populated_git_log_keeps_its_commit_files_message_and_actions_readable_in_narrow_panes(bool light)
    {
        string directory = "src/CustomerAdministrationAndReporting/InternationalSubscriptionProcessing";
        string name = "SubscriptionRenewalConfirmationForCustomersWithDeferredPaymentTerms.txt";
        string path = directory + "/" + name;
        string subject = "Preserve customer subscription renewal confirmation and deferred payment reporting across every region";
        string branch = "feature/customer-subscription-renewal-confirmation-and-deferred-payment-reporting";
        Write(path, "Original\n");
        TestGit.Init(folder);
        Git("config", "user.name", "The international customer subscription and deferred payment reporting team");
        for (int index = 0; index < 36; index++)
        {
            Write(path, $"Revision {index}\n");
            Git("commit", "-q", "-am", $"{subject} ({index})", "-m", "The full message explains why this customer's subscription changed and preserves the complete reporting history.");
        }
        Git("branch", branch);
        MainWindow window = Open(null);
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.SetRenderScaling(2);
        try
        {
            Ui.Run(window, AppActions.ShowGit);
            GitLogView log = window.GitLog;
            Ui.WaitFor(() => log.Rows.Count == 37 && log.Branches?.Branches.Any(item => item.Name == branch) == true,
                "the real repository's complete history and long branch");
            Assert.True(log.Branches!.Select(branch));
            log.Branches.FocusRows();
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => log.Rows.Count == 37 && log.BranchFilterName == branch, "the chosen branch's complete history");
            ListBox commits = log.GetVisualDescendants().OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, log.Rows));
            log.Select(log.Rows[0].Commit);
            log.FocusRows();
            string newest = log.Selected!.Hash;
            Ui.Press(window, "DOWN");
            Ui.WaitFor(() => log.Files.Count == 1 && log.Details.Contains(log.Selected!.Hash, StringComparison.Ordinal), "the next commit's changed file and message");
            string chosen = log.Selected!.Hash;
            Assert.NotEqual(newest, chosen);
            DockLayout layout = window.DockHost.Capture();
            List<string> problems = [];
            foreach (double width in new double[] { 1200, 683, 640, 200 })
            {
                window.Width = width == 200 ? 640 : width;
                if (width == 200)
                {
                    window.DockHost.Apply(layout with
                    {
                        LeftWidth = 200,
                        Windows = layout.Windows.ToDictionary(pair => pair.Key, pair => pair.Key == "git"
                            ? pair.Value with { Anchor = ToolAnchor.LeftTop, Shown = true }
                            : pair.Value with { Shown = false }),
                    });
                }
                Ui.Settle();
                Assert.Equal(chosen, log.Selected?.Hash);
                Assert.Equal(branch, log.BranchFilterName);
                Assert.Contains(subject, log.Details, StringComparison.Ordinal);
                UiAudit.Capture(window, $"git-populated-{width:0}-{(light ? "light" : "dark")}");
                problems.AddRange(UiAudit.HorizontalOverflow(window, $"Git {width}px", log));
                TextBox message = log.GetVisualDescendants().OfType<TextBox>().Single(text => text.IsReadOnly);
                if (message.Bounds.Width < 120)
                {
                    problems.Add($"The {width}px Git pane leaves no readable commit message ({message.Bounds.Width:0}px)");
                }
                ListBox changed = log.GetVisualDescendants().OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, log.Files));
                Reveal(changed);
                ScrollViewer fileReader = changed.GetVisualDescendants().OfType<ScrollViewer>().First();
                Point at = fileReader.TranslatePoint(new Point(Math.Min(40, fileReader.Bounds.Width / 2), Math.Min(30, fileReader.Bounds.Height / 2)), window)!.Value;
                window.MouseMove(at);
                window.MouseWheel(at, new Vector(-30, 0));
                Ui.Settle();
                if (fileReader.Offset.X <= 0)
                {
                    problems.Add($"The full changed filename and directory have no horizontal reading access in the {width}px Git pane");
                }
                UiAudit.Capture(window, $"git-populated-files-{width:0}-{(light ? "light" : "dark")}");
                fileReader.Offset = default;
                ListBox branches = log.Branches.GetVisualDescendants().OfType<ListBox>().Single();
                Reveal(branches);
                ScrollViewer branchReader = branches.GetVisualDescendants().OfType<ScrollViewer>().First();
                Point overBranches = branches.TranslatePoint(new Point(40, 30), window)!.Value;
                window.MouseWheel(overBranches, new Vector(-30, 0));
                Ui.Settle();
                Assert.True(branchReader.Offset.X > 0, "The full selected branch name cannot be read horizontally");
                Assert.Equal(branch, log.Branches.Selected?.Branch?.Name);
                UiAudit.Capture(window, $"git-populated-branches-{width:0}-{(light ? "light" : "dark")}");
                branchReader.Offset = default;
                Reveal(message);
                ScrollViewer messageReader = message.GetVisualDescendants().OfType<ScrollViewer>().First();
                Point overMessage = message.TranslatePoint(new Point(40, 30), window)!.Value;
                window.MouseWheel(overMessage, new Vector(0, -30));
                Ui.Settle();
                if (messageReader.Extent.Height > messageReader.Viewport.Height)
                {
                    Assert.True(messageReader.Offset.Y > 0, "The complete commit metadata cannot be read vertically");
                }
                UiAudit.Capture(window, $"git-populated-message-{width:0}-{(light ? "light" : "dark")}");
                messageReader.Offset = default;
                Reveal(commits);
                ScrollViewer commitReader = commits.GetVisualDescendants().OfType<ScrollViewer>().First();
                Point overCommits = commits.TranslatePoint(new Point(40, 30), window)!.Value;
                window.MouseWheel(overCommits, new Vector(0, -6));
                Ui.Settle();
                Assert.True(commitReader.Offset.Y > 0, "The populated commit list cannot be read vertically");
                Assert.Equal(chosen, log.Selected?.Hash);
                Assert.True(commits.GetVisualDescendants().OfType<ListBoxItem>().Count() < log.Rows.Count,
                    "The commit list lost its finite viewport and realized every history row");
                commitReader.Offset = default;
                Button refresh = log.GetVisualDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button) as string == "Refresh");
                Reveal(refresh);
                refresh.Focus();
                int loads = log.Loads;
                Ui.Press(window, "ENTER");
                Ui.WaitFor(() => log.Loads > loads && log.Selected?.Hash == chosen && log.Details.Contains(chosen, StringComparison.Ordinal),
                    "Refresh to preserve the chosen commit and its full message");
                UiAudit.Capture(window, $"git-populated-refresh-{width:0}-{(light ? "light" : "dark")}");
            }
            window.DockHost.Apply(layout);
            Ui.Settle();
            ListBox changedFiles = log.GetVisualDescendants().OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, log.Files));
            Reveal(changedFiles);
            changedFiles.SelectedIndex = 0;
            TextBox messageInput = log.GetVisualDescendants().OfType<TextBox>().Single(text => text.IsReadOnly);
            Assert.True(messageInput.Focus());
            Ui.Press(window, "ENTER");
            Assert.Empty(window.OwnedWindows.OfType<DiffWindow>());
            Assert.True(changedFiles.ContainerFromIndex(0)!.Focus(), "The real changed-file row cannot receive keyboard focus");
            Ui.Press(window, "RIGHT");
            Assert.Empty(window.OwnedWindows.OfType<DiffWindow>());
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.OwnedWindows.OfType<DiffWindow>().Any(), "Enter on the chosen commit's real changed file to show its diff", TimeSpan.FromSeconds(5));
            DiffWindow diff = Assert.Single(window.OwnedWindows.OfType<DiffWindow>());
            Assert.Equal(Git("show", $"{chosen}:{path}", "--"), diff.View.Right.Document.Snapshot.ToString());
            diff.Close();
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

            void Reveal(Control target)
            {
                foreach (ScrollViewer reader in target.GetVisualAncestors().OfType<ScrollViewer>())
                {
                    if (reader.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                    {
                        continue;
                    }
                    Point over = reader.TranslatePoint(new Point(Math.Min(40, reader.Bounds.Width / 2), Math.Min(18, reader.Bounds.Height / 2)), window)!.Value;
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        Point left = target.TranslatePoint(default, reader)!.Value;
                        double visible = Math.Min(reader.Viewport.Width, target.Bounds.Width);
                        double amount = left.X < 0 ? -left.X : left.X + visible > reader.Viewport.Width
                            ? reader.Viewport.Width - left.X - visible : 0;
                        if (Math.Abs(amount) < 1)
                        {
                            break;
                        }
                        window.MouseWheel(over, new Vector(Math.Clamp(amount / 50, -5, 5), 0));
                        Ui.Settle();
                    }
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_files_history_follows_it_through_a_rename_and_a_double_click_on_a_commit_shows_what_it_did_to_the_file()
    {
        Write("src/Shop.cs", Shop);
        Write("Notes.txt", "notes\n");
        TestGit.Init(folder);
        Write("src/Shop.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long count");
        Git("mv", "src/Shop.cs", "src/Store.cs");
        Git("commit", "-q", "-m", "Store, not shop");
        Write("src/Store.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal).Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        Write("Notes.txt", "more notes\n");
        Git("commit", "-q", "-am", "Notes");
        MainWindow window = Open("src/Store.cs");

        // From the editor's menu, as Rider's Git submenu has it.
        ChooseFromEditorMenu(window, "Git", "Show History");
        GitLogView history = window.GitWindow.Shown;
        Ui.Landed(() => history.Rows.Count == 4, "the four commits that changed the file, under either name");
        Assert.Equal(["Twice", "Store, not shop", "Long count", "First"], history.Rows.Select(r => r.Commit.Subject));
        Assert.Equal(["Log", "Store.cs"], window.GitWindow.Titles);
        Assert.True(window.DockHost.IsShown("git"), "Show History left the Git window hidden");

        // Its first two commits are of the file under the name it had then, and the rename is one.
        Assert.Equal((Full("src/Shop.cs"), ChangeKind.Added), Change(history, "First"));
        Assert.Equal((Full("src/Shop.cs"), ChangeKind.Modified), Change(history, "Long count"));
        history.Select(Commit(history, "Store, not shop"));
        Ui.Landed(() => history.Files.Count == 1, "the rename's one file");
        Assert.Equal((Full("src/Store.cs"), ChangeKind.Renamed, Full("src/Shop.cs")), (history.Files[0].Path, history.Files[0].Kind, history.Files[0].From));

        // A double-click on a commit shows what it did to the file, under its old name too.
        DoubleClick(window, history, "Long count");
        Ui.Landed(() => window.OwnedWindows.OfType<DiffWindow>().Any(), "the commit's diff of the file");
        DiffWindow diff = window.OwnedWindows.OfType<DiffWindow>().Single();
        Assert.Equal(Shop, diff.View.Left.Document.Snapshot.ToString());
        Assert.Equal(Shop.Replace("int count", "long count", StringComparison.Ordinal), diff.View.Right.Document.Snapshot.ToString());
        Assert.Single(diff.View.Hunks);
        diff.Close();

        // Show History again for the file goes back to its tab rather than opening another.
        window.GitWindow.ShowLog();
        Assert.True(window.Perform(AppActions.ShowHistory), window.Status);
        Assert.Same(history, window.GitWindow.Shown);
        Assert.Equal(["Log", "Store.cs"], window.GitWindow.Titles);
        window.Close();
    }

    [AvaloniaFact]
    public void A_folders_history_from_the_explorer_lists_only_the_commits_that_touched_it_and_only_its_files()
    {
        Write("src/A.cs", "class A { }\n");
        Write("docs/Read.md", "read\n");
        TestGit.Init(folder);
        Write("docs/Read.md", "read more\n");
        Git("commit", "-q", "-am", "Docs only");
        Write("src/B.cs", "class B { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "B");
        Write("src/A.cs", "class A { int a; }\n");
        Write("docs/Read.md", "read again\n");
        Git("commit", "-q", "-am", "Both");
        Write("docs/Read.md", "read once more\n");
        Git("commit", "-q", "-am", "Docs again");
        MainWindow window = Open(null);

        ExplorerNode src = window.ExplorerView.Tree.Rows.First(r => r.Name == "src");
        RightClick(window, src);
        ChooseFromExplorerMenu(window, "Git", "Show History");
        GitLogView history = window.GitWindow.Shown;
        Ui.Landed(() => history.Rows.Count == 3, "the three commits that touched src");
        Assert.Equal(["Both", "B", "First"], history.Rows.Select(r => r.Commit.Subject));
        Assert.Equal(["Log", "src"], window.GitWindow.Titles);

        // A commit that changed files elsewhere too lists only the folder's.
        history.Select(Commit(history, "Both"));
        Ui.Landed(() => history.Files.Count == 1, "the one file Both changed in src");
        Assert.Equal((Full("src/A.cs"), ChangeKind.Modified), (history.Files[0].Path, history.Files[0].Kind));
        history.Select(Commit(history, "First"));
        Ui.Landed(() => history.Files.Count == 1, "the one file First added to src");
        Assert.Equal(Full("src/A.cs"), history.Files[0].Path);

        // A file's row offers its versions too; the Log stays the Log.
        ExplorerNode docs = window.ExplorerView.Tree.Rows.First(r => r.Name == "docs");
        window.ExplorerView.Tree.Expand(docs);
        Ui.Settle();
        RightClick(window, window.ExplorerView.Tree.Rows.First(r => r.Name == "Read.md"));
        MenuItem git = window.ExplorerView.OpenMenu!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Git");
        Assert.Equal(["Compare with Revision...", "Compare with Branch or Tag...", "Show History"], git.Items.OfType<MenuItem>().Select(i => i.Header as string));
        window.ExplorerView.OpenMenu.Close();
        window.GitWindow.ShowLog();
        Ui.Landed(() => window.GitLog.Rows.Count == 5, "the Log's five commits");
        window.Close();
    }

    [AvaloniaFact]
    public void Show_history_for_selection_lists_only_the_commits_that_changed_the_chosen_lines_counted_past_an_unsaved_edit()
    {
        string[] lines = [.. Enumerable.Range(1, 10).Select(i => $"line {i}")];
        Write("Lines.txt", Joined(lines));
        TestGit.Init(folder);
        // Lines 3 and 7, either side of the three chosen, change too: the editor's numbers off by one would take one in.
        foreach (int changed in new[] { 2, 3, 5, 7, 9, 6 })
        {
            lines[changed - 1] = $"line {changed}, changed";
            Write("Lines.txt", Joined(lines));
            Git("commit", "-q", "-am", $"Line {changed}");
        }
        MainWindow window = Open("Lines.txt");
        CodeEditor editor = window.EditorArea.CodeEditor;

        // A line typed above, not saved: the editor's lines 5 to 7 are the last commit's 4 to 6.
        editor.ApplyEdits([new TextEdit(0, 0, "a new first line\n")]);
        editor.GoTo(new TextPosition(4, 0));
        for (int i = 0; i < 3; i++)
        {
            Ui.Press(window, "shift+DOWN");
        }
        ChooseFromEditorMenu(window, "Git", "Show History for Selection...");
        GitLogView history = window.GitWindow.Shown;
        Ui.Landed(() => history.Rows.Count > 0, "the commits that changed the lines");
        Assert.Equal(["Line 6", "Line 5", "First"], history.Rows.Select(r => r.Commit.Subject));
        Assert.Equal(["Log", "Lines.txt, lines 5–7"], window.GitWindow.Titles);

        // Enter on a commit shows what it did to the file.
        history.Select(Commit(history, "Line 5"));
        history.FocusRows();
        Ui.Press(window, "ENTER");
        Ui.Landed(() => window.OwnedWindows.OfType<DiffWindow>().Any(), "Line 5's diff of the file");
        DiffWindow diff = window.OwnedWindows.OfType<DiffWindow>().Single();
        Assert.Contains("line 5\n", diff.View.Left.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("line 5, changed\n", diff.View.Right.Document.Snapshot.ToString(), StringComparison.Ordinal);
        diff.Close();

        // Lines only typed since the last commit have no history to show.
        editor.GoTo(new TextPosition(0, 2));
        Assert.True(window.Perform(AppActions.ShowHistoryForSelection));
        Ui.Settle();
        Assert.Equal("The lines chosen are not committed yet", window.Status);
        Assert.Equal(2, window.GitWindow.Titles.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Compare_with_branch_or_tag_puts_the_other_branchs_text_beside_the_files_as_it_is_now()
    {
        Write("Shop.cs", Shop);
        TestGit.Init(folder);
        Git("tag", "v1");
        Git("switch", "-q", "-c", "side");
        string side = Shop.Replace("count++", "count += 3", StringComparison.Ordinal);
        Write("Shop.cs", side);
        Git("commit", "-q", "-am", "Thrice");
        Git("switch", "-q", "main");
        Write("New.cs", "class New { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "New");
        MainWindow window = Open("Shop.cs");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");

        // The branches, the current one first, then the tags.
        Assert.True(window.Perform(AppActions.CompareWithBranch), window.Status);
        Ui.Settle();
        ChoicePopup popup = Assert.IsType<ChoicePopup>(window.Popup);
        Assert.Equal(["main current branch", "side local branch", "v1 tag"], popup.Rows.Select(r => $"{r.Label} {r.Detail}"));
        window.KeyTextInput("side");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.Landed(() => window.OwnedWindows.OfType<DiffWindow>().Any(), "the diff against side");
        DiffWindow diff = window.OwnedWindows.OfType<DiffWindow>().Single();
        Assert.Equal("Shop.cs: side and your version", diff.Title);
        Assert.Equal(side, diff.View.Left.Document.Snapshot.ToString());
        Assert.True(ReferenceEquals(window.EditorArea.Active!.Text, diff.View.Right.Document), "The diff's right side is a copy of the open file, not the file");
        Assert.Equal(2, diff.View.Hunks.Count);
        diff.Close();

        // A file the branch does not have says so.
        window.OpenFile(Path.Combine(folder, "New.cs"));
        Ui.Settle();
        Assert.True(window.Perform(AppActions.CompareWithBranch), window.Status);
        Ui.Settle();
        window.KeyTextInput("side");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.Landed(() => window.Status == "New.cs is not in side", "the status bar saying side has no New.cs");
        Assert.Empty(window.OwnedWindows.OfType<DiffWindow>());
        window.Close();
    }

    private MainWindow Open(string? file)
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        if (file is not null)
        {
            window.OpenFile(Path.Combine(folder, file));
            window.EditorArea.CodeEditor.Focus();
        }
        Ui.Settle();
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the folder's repository");
        return window;
    }

    private static CommitInfo Commit(GitLogView history, string subject) => history.Rows.Select(r => r.Commit).First(c => c.Subject == subject);

    /// <summary>A file's history's change in a commit: the file's path then, and how it changed.</summary>
    private static (string Path, ChangeKind Kind) Change(GitLogView history, string subject)
    {
        history.Select(Commit(history, subject));
        Ui.Settle();
        FileChange change = Assert.Single(history.Files);
        return (change.Path, change.Kind);
    }

    /// <summary>Opens the editor's menu at the caret and chooses an item of a submenu, as clicks on them would.</summary>
    private static void ChooseFromEditorMenu(MainWindow window, string submenu, string item)
    {
        window.EditorArea.CodeEditor.RequestContextMenu();
        Ui.Settle();
        ContextMenu menu = window.OpenContextMenu ?? throw new InvalidOperationException("The editor's menu did not open");
        Choose(menu, submenu, item);
    }

    private static void ChooseFromExplorerMenu(MainWindow window, string submenu, string item) =>
        Choose(window.ExplorerView.OpenMenu ?? throw new InvalidOperationException("No menu opened on the row"), submenu, item);

    private static void Choose(ContextMenu menu, string submenu, string item)
    {
        MenuItem parent = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Header == submenu)
            ?? throw new InvalidOperationException($"The menu has no {submenu}: [{string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header))}]");
        MenuItem chosen = parent.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Header == item)
            ?? throw new InvalidOperationException($"{submenu} has no {item}: [{string.Join(", ", parent.Items.OfType<MenuItem>().Select(i => i.Header))}]");
        Assert.True(chosen.IsEnabled, $"{submenu} > {item} is disabled");
        chosen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Ui.Settle();
    }

    private static void RightClick(MainWindow window, ExplorerNode row)
    {
        ListBox rows = window.ExplorerView.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, window.ExplorerView.Tree.Rows));
        Point at = Centre(window, rows, row);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Ui.Settle();
    }

    /// <summary>Double-clicks a commit of a history, as a person opens one.</summary>
    private static void DoubleClick(MainWindow window, GitLogView history, string subject)
    {
        ListBox commits = history.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, history.Rows));
        Point at = Centre(window, commits, history.Rows.First(r => r.Commit.Subject == subject));
        for (int i = 0; i < 2; i++)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }
        Ui.Settle();
    }

    private static Point Centre(MainWindow window, ListBox list, object row)
    {
        list.ScrollIntoView(row);
        Ui.Settle();
        Control item = list.ContainerFromItem(row) ?? throw new InvalidOperationException($"The list does not show {row}");
        return item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
    }

    private static string Joined(IEnumerable<string> lines) => string.Concat(lines.Select(l => l + "\n"));

    private string Full(string relative) => PathNames.Canonical(Path.Combine(folder, relative));

    private void Write(string relative, string text)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
