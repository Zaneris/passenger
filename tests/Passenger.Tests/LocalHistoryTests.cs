using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Explorer;
using Passenger.App.History;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Rider's Local History over real files: every save and every change made elsewhere is a version, listed newest
/// first in the Local History window with its time and name ("External change"), the person's labels among them; a
/// version shows beside the file as it is now, Revert puts it back as one undoable step and a diff's arrow one change of
/// it; a folder's history lists its files' versions and brings back one deleted; a file's history follows it through a
/// rename in the Solution view; and the store keeps Rider's 5 days of activity and no more content than its limit.</summary>
public sealed class LocalHistoryTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add()\n    {\n        count++;\n    }\n\n    int Total => count;\n}\n";

    // Each save changes another line of it, so a version against now differs in as many places as saves since.
    private static readonly string Saved1 = Original.Replace("    int count;", "    long int count;", StringComparison.Ordinal);
    private static readonly string Saved2 = Saved1.Replace("        count++;", "        count++; count++;", StringComparison.Ordinal);
    private static readonly string Saved3 = Saved2.Replace("    int Total", "    public int Total", StringComparison.Ordinal);

    private readonly string folder = PathNames.Canonical(TestFolders.Create("local-history-"));
    private readonly string trash = TestFolders.Create("trash-");
    private long clock;

    public LocalHistoryTests() => Trash.Folder = trash;

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_local_history_labels_and_folder_files_remain_readable_and_revertible_in_narrow_panes(bool light)
    {
        string directory = "src/CustomerAdministrationAndReporting/SubscriptionProcessing";
        string name = "SubscriptionRenewalConfirmationForCustomersWithDeferredPaymentTerms.txt";
        string relative = directory + "/" + name;
        string path = PathNames.Canonical(Write(relative, "Before review\n"));
        string other = PathNames.Canonical(Write(directory + "/DeferredPaymentReportForInternationalSubscriptionCustomers.txt", "Other before review\n"));
        string label = "Before reviewing international customer subscription renewal confirmations and deferred payment reports";
        TestGit.Init(folder);
        MainWindow window = Open(relative);
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.SetRenderScaling(2);
        try
        {
            window.OpenFile(other);
            window.OpenFile(path);
            Ui.Settle();
            for (int index = 0; index < 18; index++)
            {
                ChooseFromEditorMenu(window, "Local History", "Put Label...");
                window.KeyTextInput($"{label} ({index})");
                Ui.Press(window, "ENTER");
                Assert.False(window.IsPopupOpen);
                SaveEdit(window, new TextEdit(0, window.EditorArea.Active!.Text.Snapshot.Length, $"Revision {index}\n"));
            }
            window.OpenFile(other);
            SaveEdit(window, new TextEdit(0, window.EditorArea.Active!.Text.Snapshot.Length, "Other after review\n"));
            window.OpenFile(path);
            HistoryTab fileHistory = ShowFromFileMenu(window, "Show History...");
            Ui.WaitFor(() => fileHistory.Rows.Count >= 36, "the real saved versions and long labels");
            fileHistory.Select(fileHistory.Rows.Single(row => row.Title == label + " (0)"));
            Ui.WaitFor(() => fileHistory.Diff?.Left.Document.Snapshot.ToString() == "Before review\n", "the labeled file version");
            List<string> problems = [];
            DockLayout layout = Audit(fileHistory, "file");
            Button revert = fileHistory.GetVisualDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button) as string == "Revert Selected and Later Changes");
            Reveal(revert);
            Assert.True(revert.Focus());
            UiAudit.Capture(window, $"local-history-file-revert-200-{(light ? "light" : "dark")}");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(path) == "Before review\n", "the reachable toolbar to revert the real file");
            window.EditorArea.CodeEditor.Focus();
            Ui.Run(window, EditorActions.Undo);
            Ui.Run(window, AppActions.Save);
            Assert.Equal("Revision 17\n", File.ReadAllText(path));
            window.DockHost.Apply(layout);
            Ui.Settle();
            HistoryTab folderHistory = ShowFromFileMenu(window, "Show Project History...");
            Ui.WaitFor(() => folderHistory.Rows.Any(row => row.Title == label + " (0)"), "the project's labeled history");
            folderHistory.Select(folderHistory.Rows.Single(row => row.Title == label + " (0)"));
            Ui.WaitFor(() => folderHistory.Files.Count == 2, "both real changed files since the selected label");
            Audit(folderHistory, "folder");
            folderHistory.SelectFile(folderHistory.Files.Single(file => PathNames.Same(file.Path, other)));
            Button revertFile = folderHistory.GetVisualDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button) as string == "Revert Selection");
            Reveal(revertFile);
            Assert.True(revertFile.Focus());
            UiAudit.Capture(window, $"local-history-folder-revert-200-{(light ? "light" : "dark")}");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => File.ReadAllText(other) == "Other before review\n", "the reachable selection action to revert only its chosen file");
            Assert.Equal("Revision 17\n", File.ReadAllText(path));
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

            DockLayout Audit(HistoryTab history, string kind)
            {
                DockLayout layout = window.DockHost.Capture();
                long chosen = history.Selected!.Set;
                bool reading = false;
                foreach (double width in new double[] { 1200, 683, 640, 200 })
                {
                    window.Width = width == 200 ? 640 : width;
                    if (width == 200)
                    {
                        window.DockHost.Apply(layout with
                        {
                            LeftWidth = 200,
                            Windows = layout.Windows.ToDictionary(pair => pair.Key, pair => pair.Key == "localHistory"
                                ? pair.Value with { Anchor = ToolAnchor.LeftTop, Shown = true }
                                : pair.Value with { Shown = false }),
                        });
                    }
                    Ui.Settle();
                    Assert.Equal(chosen, history.Selected?.Set);
                    UiAudit.Capture(window, $"local-history-{kind}-{width:0}-{(light ? "light" : "dark")}");
                    DiffView diff = history.Diff!;
                    problems.AddRange(UiAudit.HorizontalOverflow(window, $"Local History {kind} diff {width}px", diff));
                    ReadCaption(diff.GetVisualDescendants().OfType<TextBlock>().Single(text => text.FontWeight == FontWeight.SemiBold), "filename");
                    ReadCaption(diff.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text?.StartsWith(label, StringComparison.Ordinal) == true), "version");
                    if (width == 200)
                    {
                        Reveal(diff.SettingsButton);
                        Ui.ChooseFromMenu(diff.SettingsButton, "Lines");
                        Assert.Equal(DiffHighlighting.Lines, diff.Highlighting);
                        UiAudit.Capture(window, $"local-history-{kind}-diff-settings-200-{(light ? "light" : "dark")}");
                        Ui.ChooseFromMenu(diff.SettingsButton, "Words");
                        Assert.Equal(DiffHighlighting.Words, diff.Highlighting);
                    }
                    problems.AddRange(UiAudit.HorizontalOverflow(window, $"Local History toolbar {width}px", history.Children[0]));
                    ListBox activities = history.GetVisualDescendants().OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, history.Rows));
                    Reveal(activities);
                    Ui.Settle();
                    ScrollViewer activityReader = activities.GetVisualDescendants().OfType<ScrollViewer>().First();
                    if (reading)
                    {
                        Assert.True(activityReader.Offset.Y > 0, "Resizing jumped away from the activity being read");
                    }
                    problems.AddRange(UiAudit.HorizontalOverflow(window, $"Local History {kind} activity {width}px", activities));
                    ReadAcross(activities, "labels");
                    activityReader.Offset = default;
                    Ui.Settle();
                    Point rows = activities.TranslatePoint(new Point(Math.Min(40, activities.Bounds.Width / 2), Math.Min(30, activities.Bounds.Height / 2)), window)!.Value;
                    window.MouseWheel(rows, new Vector(0, -8));
                    Ui.Settle();
                    Assert.True(activityReader.Offset.Y > 0, "The remaining history rows cannot be reached vertically");
                    reading = true;
                    Assert.Equal(chosen, history.Selected?.Set);
                    Assert.True(activities.GetRealizedContainers().Count() < history.Rows.Count, "The activity list stopped virtualizing the real history");
                    if (history.IsFolder)
                    {
                        ListBox files = history.GetVisualDescendants().OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, history.Files));
                        Reveal(files);
                        Ui.Settle();
                        problems.AddRange(UiAudit.HorizontalOverflow(window, $"Local History folder files {width}px", files));
                        ReadAcross(files, "files");
                    }

                    void ReadAcross(ListBox list, string part)
                    {
                        Reveal(list);
                        ScrollViewer reader = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                        reader.Offset = new Vector(0, reader.Offset.Y);
                        Ui.Settle();
                        Point over = list.TranslatePoint(new Point(Math.Min(40, list.Bounds.Width / 2), Math.Min(30, list.Bounds.Height / 2)), window)!.Value;
                        window.MouseWheel(over, new Vector(-30, 0));
                        Ui.Settle();
                        if (reader.Offset.X <= 0)
                        {
                            problems.Add($"The complete {part} have no horizontal reading access in the {kind} {width}px history");
                        }
                        UiAudit.Capture(window, $"local-history-{kind}-{part}-{width:0}-{(light ? "light" : "dark")}");
                        reader.Offset = default;
                    }

                    void ReadCaption(TextBlock caption, string part)
                    {
                        ScrollViewer? reader = caption.GetVisualAncestors().TakeWhile(parent => parent != diff).OfType<ScrollViewer>().FirstOrDefault();
                        if (reader is null)
                        {
                            return;
                        }
                        Reveal(reader);
                        reader.Offset = default;
                        Ui.Settle();
                        if (reader.Extent.Width > reader.Viewport.Width + 1)
                        {
                            Point at = reader.TranslatePoint(new Point(Math.Min(40, reader.Bounds.Width / 2), reader.Bounds.Height / 2), window)!.Value;
                            window.MouseWheel(at, new Vector(-30, 0));
                            Ui.Settle();
                            Assert.True(reader.Offset.X > 0, $"The diff {part} does not scroll to its complete text");
                            ScrollBar bar = reader.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Horizontal);
                            Thumb thumb = bar.GetVisualDescendants().OfType<Thumb>().First();
                            TextLine line = Assert.Single(caption.TextLayout.TextLines);
                            double textBottom = caption.TranslatePoint(default, reader)!.Value.Y + line.Height + line.OverhangAfter;
                            double thumbTop = thumb.TranslatePoint(default, reader)!.Value.Y;
                            Assert.True(textBottom <= thumbTop + 0.5, $"The diff {part} horizontal thumb paints over its caption: ink ends at {textBottom:0.0}px, thumb starts at {thumbTop:0.0}px");
                        }
                        Assert.True(caption.TextLayout.Width <= caption.Bounds.Width + 1, $"The diff {part} still clips its text inside the horizontal reader");
                        Reveal(reader, rightEdge: true);
                        UiAudit.Capture(window, $"local-history-{kind}-diff-{part}-{width:0}-{(light ? "light" : "dark")}");
                        reader.Offset = default;
                    }
                }
                return layout;
            }

            void Reveal(Control target, bool rightEdge = false)
            {
                Point anchor = rightEdge ? new Point(target.Bounds.Width - 1, 0) : default;
                foreach (ScrollViewer reader in target.GetVisualAncestors().OfType<ScrollViewer>())
                {
                    if (reader.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                    {
                        continue;
                    }
                    Point over = reader.TranslatePoint(new Point(Math.Min(40, reader.Bounds.Width / 2), Math.Min(18, reader.Bounds.Height / 2)), window)!.Value;
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        Point at = target.TranslatePoint(anchor, reader)!.Value;
                        double visible = rightEdge ? 1 : Math.Min(reader.Viewport.Width, target.Bounds.Width);
                        double amount = at.X < 0 ? -at.X : at.X + visible > reader.Viewport.Width ? reader.Viewport.Width - at.X - visible : 0;
                        if (Math.Abs(amount) < 1)
                        {
                            break;
                        }
                        window.MouseWheel(over, new Vector(Math.Clamp(amount / 50, -5, 5), 0));
                        Ui.Settle();
                    }
                    Point final = target.TranslatePoint(anchor, reader)!.Value;
                    Assert.True(final.X >= -1 && final.X + (rightEdge ? 1 : Math.Min(reader.Viewport.Width, target.Bounds.Width)) <= reader.Viewport.Width + 1,
                        "Horizontal input could not reveal the Local History control");
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Three_saves_and_a_change_made_elsewhere_are_four_versions_newest_first_the_last_named_an_external_change()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);

        // Another program writes the file; the open file takes it, and the history keeps it as Rider names it.
        string elsewhere = Saved3.Replace("class Shop", "sealed class Shop", StringComparison.Ordinal);
        File.WriteAllText(shop, elsewhere);
        Ui.WaitFor(() => Text(window) == elsewhere, "the open file to take the change made elsewhere");

        HistoryTab history = ShowFromFileMenu(window, "Show History...");
        Assert.True(window.DockHost.IsShown("localHistory"), "Show History left the Local History window hidden");
        Assert.Equal(["Shop.cs"], window.LocalHistoryWindow.Titles);
        Ui.Landed(() => history.Rows.Count == 4, "the four versions: three saves and the change made elsewhere");
        Assert.Equal(["External change", "Change", "Change", "Change"], history.Rows.Select(r => r.Title));
        Assert.True(history.Rows.Zip(history.Rows.Skip(1)).All(p => p.First.Time >= p.Second.Time), "The versions are not listed newest first");

        // Opened, the history shows its newest row: the file before the change made elsewhere, beside the file now.
        Assert.Same(history.Rows[0], history.Selected);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved3, "the file as saved last, before the external change");
        Assert.Equal([new DiffHunk(0, 1, 0, 1)], history.Diff!.Hunks);
        window.Close();
    }

    [AvaloniaFact]
    public void An_old_versions_diff_shows_what_changed_since_beside_the_open_file()
    {
        Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);

        // From the editor's menu, Local History, as Rider's has it.
        ChooseFromEditorMenu(window, "Local History", "Show History...");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count == 3, "the three saves");
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the oldest version, the file before its first save");
        Assert.Same(window.EditorArea.Active!.Text, history.Diff!.Right.Document);
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(6, 1, 6, 1), new DiffHunk(9, 1, 9, 1)], history.Diff.Hunks);

        // The version between shows only the changes made after it.
        history.Select(history.Rows[1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved1, "the version after the first save");
        Assert.Equal([new DiffHunk(6, 1, 6, 1), new DiffHunk(9, 1, 9, 1)], history.Diff!.Hunks);
        window.Close();
    }

    [AvaloniaFact]
    public void Revert_puts_a_version_back_as_one_undoable_step_and_a_diffs_arrow_puts_back_one_change()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);
        HistoryTab history = ShowFromFileMenu(window, "Show History...");
        Ui.Landed(() => history.Rows.Count == 3, "the three saves");

        // The version after the first save comes back, written to the file, under Rider's name for a revert.
        history.Select(history.Rows[1]);
        Ui.Settle();
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.Equal(Saved1, Text(window));
        Assert.Equal(Saved1, File.ReadAllText(shop));
        Assert.False(window.EditorArea.Active!.IsModified, "The revert was not written to the file");
        Ui.Landed(() => history.Rows.Count == 4 && history.Rows[0].Title.StartsWith("Revert to ", StringComparison.Ordinal), "the revert kept as a version of its own");

        // One undo takes the whole revert back.
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Saved3, Text(window));

        // Against the first version, the arrow on one change puts back that change alone.
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original && history.Diff.Hunks.Count == 3, "the first version's three changes");
        history.Diff!.Revert(history.Diff.Hunks[1]);
        Ui.Settle();
        Assert.Equal(Saved3.Replace("count++; count++;", "count++;", StringComparison.Ordinal), Text(window));
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(9, 1, 9, 1)], history.Diff.Hunks);
        window.Close();
    }

    [AvaloniaFact]
    public void Put_label_puts_a_label_among_the_versions_that_shows_the_file_as_it_was_then()
    {
        Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        Save(window, 2, 4, "long ");
        ChooseFromEditorMenu(window, "Local History", "Put Label...");
        Assert.IsType<NamePopup>(window.Popup);
        window.KeyTextInput("Before cleanup");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, "The label's name was refused");
        Assert.Equal("Label Before cleanup created", window.Status);
        Save(window, 6, 8, "count++; ");

        HistoryTab history = ShowFromFileMenu(window, "Show History...");
        Ui.Landed(() => history.Rows.Count == 3, "the two saves and the label between them");
        Assert.Equal(["Change", "Before cleanup", "Change"], history.Rows.Select(r => r.Title));
        Assert.True(history.Rows[1].IsLabel, "The label is listed as a change, not a label");
        history.Select(history.Rows[1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved1, "the file as it was when the label was put");
        window.Close();
    }

    [AvaloniaFact]
    public void What_a_git_checkout_changes_is_kept_under_Riders_name_for_it_not_as_an_external_change()
    {
        string shop = Write("Shop.cs", Original);
        TestGit.Init(folder);
        TestGit.Run(folder, "switch", "-q", "-c", "side");
        File.WriteAllText(shop, Saved1);
        TestGit.Run(folder, "commit", "-q", "-am", "Long");
        TestGit.Run(folder, "switch", "-q", "main");
        MainWindow window = Open("Shop.cs");
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the folder's repository");

        Task<VcsOutcome> checkout = window.Vcs.CheckoutAsync("side");
        Ui.WaitFor(() => checkout.IsCompleted && Text(window) == Saved1, "side checked out and the open file following it");
        Assert.True(checkout.Result.Succeeded, checkout.Result.Message);
        HistoryTab history = ShowFromFileMenu(window, "Show History...");
        Ui.WaitFor(() => history.Rows.Count == 1, "the checkout's change to the file");
        Assert.Equal("Git checkout", history.Rows[0].Title);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as main had it");
        window.Close();
    }

    [AvaloniaFact]
    public void A_folders_history_lists_its_files_versions_and_brings_back_a_file_deleted_in_the_explorer()
    {
        string a = Write("src/A.cs", "class A\n{\n}\n");
        string b = Write("src/B.cs", "class B\n{\n}\n");
        Write("Notes.txt", "notes\n");
        MainWindow window = Open("src/A.cs");
        Save(window, 0, 0, "sealed ");
        window.OpenFile(b);
        Save(window, 0, 0, "sealed ");
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Save(window, 0, 5, " kept");
        const string savedB = "sealed class B\n{\n}\n";
        Assert.Equal(savedB, File.ReadAllText(b));

        // Deleted from the Explorer, as a person deletes it.
        window.Confirm = (_, _, _) => Task.FromResult(true);
        RightClick(window, Show(window, b));
        ChooseFromMenu(window, "Edit", "Delete...");
        Ui.WaitFor(() => !File.Exists(b), "B.cs deleted");

        // The folder's history from its row: its files' changes only, the newest first.
        RightClick(window, Show(window, Path.Combine(folder, "src")));
        ChooseFromMenu(window, null, "Show Local History");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Assert.Equal("src", window.LocalHistoryWindow.Titles.Single());
        Ui.Landed(() => history.Rows.Count == 3, "A.cs's save, B.cs's save and B.cs's deletion");
        Assert.Equal(["Delete B.cs", "Modify B.cs", "Modify A.cs"], history.Rows.Select(r => r.Title));

        // Before the deletion, B.cs was there as saved: its row shows it, and Revert brings it back.
        Ui.Landed(() => history.Files.Count == 1, "the file the deletion took");
        Assert.Equal((PathNames.Canonical(b), ChangeKind.Deleted), (history.Files[0].Path, history.Files[0].Kind));
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == savedB, "B.cs as it was deleted");
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.Equal(savedB, File.ReadAllText(b));

        // Before A.cs's save, both files were as they were first seen.
        history.Select(history.Rows.First(r => r.Title == "Modify A.cs"));
        Ui.Landed(() => history.Files.Count == 2, "A.cs and B.cs changed since");
        Assert.Equal([(PathNames.Canonical(a), ChangeKind.Modified), (PathNames.Canonical(b), ChangeKind.Modified)], history.Files.Select(f => (f.Path, f.Kind)));
        window.Close();
    }

    [AvaloniaFact]
    public void A_files_history_follows_it_through_a_rename_in_the_solution_view()
    {
        Write("Shop/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop/Shop.csproj\" />\n</Solution>\n");
        string order = Write("Shop/Order.cs", Original);
        MainWindow window = Open("Shop/Order.cs");
        Save(window, 2, 4, "long ");

        RightClick(window, Show(window, order));
        ChooseFromMenu(window, "Edit", "Rename...");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Purchase.cs");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, $"The new name was refused: {popup.Problem}");
        string purchase = Path.Combine(folder, "Shop", "Purchase.cs");
        Assert.True(File.Exists(purchase));
        Save(window, 6, 8, "count++; ");

        RightClick(window, Show(window, purchase));
        ChooseFromMenu(window, null, "Show Local History");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count == 3, "the saves under both names and the rename between them");
        Assert.Equal(["Change", "Rename Order.cs to Purchase.cs", "Change"], history.Rows.Select(r => r.Title));
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as it was under its old name");
        window.Close();
    }

    [AvaloniaFact]
    public void The_store_keeps_days_of_activity_as_Riders_does_and_no_more_content_than_its_limit()
    {
        // Twelve saves six hours apart are three days of work: a store keeping one day keeps the last four of them, past
        // the pruning it does as each day turns and as it opens again.
        string kept = TestFolders.Create("history-state-");
        Write("Shop.cs", Original);
        Set(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        HistoryKeeping day = new(Days: 1) { Clock = Now };
        MainWindow window = Open("Shop.cs", kept, day);
        for (int save = 1; save <= 12; save++)
        {
            Set(Now() + TimeSpan.FromHours(6));
            SaveEdit(window, new TextEdit(0, 0, $"// {save}\n"));
        }
        HistoryTab history = ShowFromFileMenu(window, "Show History...");
        Ui.Landed(() => history.Rows.Count is > 0 and < 12, "the oldest saves pruned as the days turned");
        window.Close();

        MainWindow again = Open("Shop.cs", kept, day);
        history = ShowFromFileMenu(again, "Show History...");
        Ui.Landed(() => history.Rows.Count == 4, "the last day's four saves");
        history.Select(history.Rows[^1]);
        string eighth = string.Concat(Enumerable.Range(1, 8).Reverse().Select(n => $"// {n}\n")) + Original;
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == eighth, "the oldest kept version, the file after the eighth save");
        Assert.Equal(4, File.ReadAllLines(Path.Combine(again.LocalHistory!.Root, "changes.log")).Length);
        again.Close();

        // Versions of too much content: the oldest go until what is left fits, the newest always kept.
        string small = TestFolders.Create("history-state-");
        HistoryKeeping bytes = new(Days: 5, Bytes: 16_000) { Clock = Now };
        MainWindow third = Open("Shop.cs", small, bytes);
        Random random = new(9);
        for (int save = 1; save <= 8; save++)
        {
            // Each a day apart, so the store prunes as each day turns.
            Set(Now() + TimeSpan.FromHours(25));
            string noise = Convert.ToHexString(random.GetItems<byte>(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 3000));
            SaveEdit(third, new TextEdit(0, third.EditorArea.CodeEditor.Document.Snapshot.Length, $"// {save}\n{noise}\n"));
        }
        history = ShowFromFileMenu(third, "Show History...");
        Ui.Landed(() => history.Rows.Count is >= 1 and < 8, "the oldest versions pruned past the store's limit");
        Assert.StartsWith("// 7\n", Version(history, 0), StringComparison.Ordinal);
        third.Close();
    }

    private DateTimeOffset Now() => new(Interlocked.Read(ref clock), TimeSpan.Zero);

    private void Set(DateTimeOffset now) => Interlocked.Exchange(ref clock, now.UtcTicks);

    private static string Version(HistoryTab history, int row)
    {
        history.Select(history.Rows[row]);
        Ui.Settle();
        return history.Diff!.Left.Document.Snapshot.ToString();
    }

    /// <summary>Three saves of three edits, each to a line of its own.</summary>
    private static void SaveThree(MainWindow window)
    {
        Save(window, 2, 4, "long ");
        Save(window, 6, 8, "count++; ");
        Save(window, 9, 4, "public ");
        Assert.Equal(Saved3, Text(window));
    }

    /// <summary>Types at a place in the file shown and saves it with Save's keys.</summary>
    private static void Save(MainWindow window, int line, int column, string text)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.GoTo(new TextPosition(line, column));
        window.KeyTextInput(text);
        Ui.Settle();
        Ui.Run(window, AppActions.Save);
        Assert.False(window.EditorArea.Active!.IsModified, "The save did not write the file");
    }

    /// <summary>An edit made in the file shown, as a paste would make it, then saved with Save's keys.</summary>
    private static void SaveEdit(MainWindow window, TextEdit edit)
    {
        window.EditorArea.CodeEditor.Focus();
        window.EditorArea.CodeEditor.ApplyEdits([edit]);
        Ui.Settle();
        Ui.Run(window, AppActions.Save);
        Assert.False(window.EditorArea.Active!.IsModified, "The save did not write the file");
    }

    private MainWindow Open(string file, string? state = null, HistoryKeeping? keeping = null)
    {
        MainWindow window = state is null ? new() { Width = 1200, Height = 800 } : new() { Width = 1200, Height = 800, StateFolder = state, HistoryKeeping = keeping! };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, file));
        window.EditorArea.CodeEditor.Focus();
        Ui.Settle();
        return window;
    }

    /// <summary>File, Local History, and one of its items, as the menu bar runs it.</summary>
    private static HistoryTab ShowFromFileMenu(MainWindow window, string item)
    {
        NativeMenu menu = Ui.Submenu(Ui.Submenu(window.MenuBar, "File"), "Local History");
        NativeMenuItem chosen = menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == item)
            ?? throw new InvalidOperationException($"File, Local History has no {item}: [{string.Join(", ", menu.Items.OfType<NativeMenuItem>().Select(i => i.Header))}]");
        Assert.True(chosen.IsEnabled, $"File, Local History, {item} is disabled");
        chosen.Command!.Execute(null);
        Ui.Settle();
        return window.LocalHistoryWindow.Shown ?? throw new InvalidOperationException($"{item} showed no history: {window.Status}");
    }

    /// <summary>Opens the editor's menu at the caret and chooses an item of a submenu, as clicks on them would.</summary>
    private static void ChooseFromEditorMenu(MainWindow window, string submenu, string item)
    {
        window.EditorArea.CodeEditor.RequestContextMenu();
        Ui.Settle();
        ContextMenu menu = window.OpenContextMenu ?? throw new InvalidOperationException("The editor's menu did not open");
        MenuItem parent = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Header == submenu)
            ?? throw new InvalidOperationException($"The menu has no {submenu}: [{string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header))}]");
        Click(menu, parent.Items.OfType<MenuItem>(), item, submenu);
    }

    private static void ChooseFromMenu(MainWindow window, string? submenu, string item)
    {
        ContextMenu menu = window.ExplorerView.OpenMenu ?? throw new InvalidOperationException("No menu opened on the row");
        IEnumerable<MenuItem> items = menu.Items.OfType<MenuItem>();
        if (submenu is not null)
        {
            items = (items.FirstOrDefault(i => (string?)i.Header == submenu)
                ?? throw new InvalidOperationException($"The row's menu has no {submenu}: [{string.Join(", ", items.Select(i => i.Header))}]")).Items.OfType<MenuItem>();
        }
        Click(menu, items, item, submenu ?? "The row's menu");
    }

    private static void Click(ContextMenu menu, IEnumerable<MenuItem> items, string item, string where)
    {
        MenuItem chosen = items.FirstOrDefault(i => (string?)i.Header == item)
            ?? throw new InvalidOperationException($"{where} has no {item}: [{string.Join(", ", items.Select(i => i.Header))}]");
        Assert.True(chosen.IsEnabled, $"{where}, {item} is disabled");
        chosen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Ui.Settle();
    }

    /// <summary>Opens the Explorer's folders down to a file or folder, as a person does to get to it, and gives its row.</summary>
    private static ExplorerNode Show(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(PathNames.Canonical(path));
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == PathNames.Canonical(path))
            ?? throw new InvalidOperationException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    private static void RightClick(MainWindow window, ExplorerNode row)
    {
        ListBox rows = window.ExplorerView.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, window.ExplorerView.Tree.Rows));
        rows.ScrollIntoView(row);
        Ui.Settle();
        Control item = rows.ContainerFromItem(row) ?? throw new InvalidOperationException($"The Explorer does not show {row.Name}");
        Point at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Ui.Settle();
    }

    private static string Text(MainWindow window) => window.EditorArea.Active!.Text.Snapshot.ToString();

    private string Write(string relative, string text)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
