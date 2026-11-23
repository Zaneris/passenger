using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;

namespace Passenger.Tests;

public sealed partial class VcsTests
{
    /// <summary>Group By › Directory lists the Commit window's changes under their folders, a folder holding one folder alone
    /// as one row with it; a folder's box, at any depth, takes every file under it out of the commit or puts it in, a
    /// folder holding some chosen files partly ticked; a folder closed hides its files and keeps them chosen; Group By
    /// again lists them flat, and the folder keeps the grouping once it is opened again.</summary>
    private void Group_by_directory_lists_changes_under_their_folders_and_a_folders_box_chooses_every_file_under_it()
    {
        // tools.cs sorts after the folders' files flat as well as grouped, so the rows keep their order as the grouping turns.
        string[] files = ["src/App/Main.cs", "src/App/View.cs", "src/Lib/Deep/Util.cs", "docs/guide/intro.md", "tools.cs"];
        foreach (string file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, file))!);
            File.WriteAllText(Path.Combine(folder, file), "one\n");
        }
        Git("add", "-A");
        Git("commit", "-q", "-m", "Files");
        foreach (string file in files)
        {
            File.AppendAllText(Path.Combine(folder, file), "two\n");
        }
        string state = TestFolders.Create("group-state-");
        try
        {
            MainWindow window = OpenWithState(state);
            CommitView commit = window.CommitView;
            Ui.Landed(() => commit.Rows.OfType<CommitGroup>().Any(g => g.Name == "Changes" && g.Rows.Count == 5), "the five changed files under Changes");
            Assert.Empty(commit.Rows.OfType<CommitFolder>());

            GroupBy(commit);
            Ui.Landed(() => commit.Rows.OfType<CommitFolder>().Any(), "the changes listed under their folders");
            Assert.Equal(
                ["Changes", "  docs/guide", "    intro.md", "  src", "    App", "      Main.cs", "      View.cs", "    Lib/Deep", "      Util.cs", "  tools.cs"],
                commit.Rows.Select(Shown));

            ListBox list = Find(commit, commit.Rows);
            Assert.True(!Words(list, commit, "Util.cs").Contains("src/Lib/Deep"), $"Util.cs under its folders still says its folder beside its name: {Words(list, commit, "Util.cs")}");
            Ui.Click(Box(list, Folder(commit, "src")));
            Assert.Equal(["intro.md", "tools.cs"], ChosenNames(commit));
            Assert.True(Folder(commit, "src").IsChosen == false && Folder(commit, "App").IsChosen == false, "Unticking src left a folder under it ticked");
            Assert.Null(commit.Rows.OfType<CommitGroup>().Single().IsChosen);

            Ui.Click(Box(list, Folder(commit, "App")));
            Assert.Equal(["Main.cs", "View.cs", "intro.md", "tools.cs"], ChosenNames(commit));
            Assert.True(Folder(commit, "src").IsChosen is null, "src, holding App's chosen files and Util.cs left out, is not partly ticked");

            Ui.Click(Ui.Row(list, Folder(commit, "src")).GetVisualDescendants().OfType<RowExpander>().Single());
            Assert.DoesNotContain(commit.Rows, r => r is CommitRow { Change.Name: "Main.cs" });
            Assert.Equal(["Main.cs", "View.cs", "intro.md", "tools.cs"], ChosenNames(commit));

            GroupBy(commit);
            Ui.Landed(() => !commit.Rows.OfType<CommitFolder>().Any(), "the changes listed flat again");
            Assert.Equal(["Changes", "  intro.md", "  Main.cs", "  View.cs", "  Util.cs", "  tools.cs"], commit.Rows.Select(Shown));
            Assert.True(Words(list, commit, "intro.md").Contains("docs/guide"), $"intro.md listed flat again does not say its folder beside its name: {Words(list, commit, "intro.md")}");
            Assert.Equal(["Main.cs", "View.cs", "intro.md", "tools.cs"], ChosenNames(commit));

            GroupBy(commit);
            Ui.Landed(() => commit.Rows.OfType<CommitFolder>().Any(), "the changes under their folders again");
            window.Close();

            MainWindow again = OpenWithState(state);
            Ui.Landed(() => again.CommitView.Rows.OfType<CommitFolder>().Any(), "the changes still grouped by directory once the folder is opened again");
            again.Close();
        }
        finally
        {
            TestFolders.Delete(state);
        }

        static string Shown(object row) => row switch
        {
            CommitGroup group => group.Name,
            CommitFolder folder => new string(' ', 2 * folder.Depth) + folder.Name,
            CommitRow file => new string(' ', 2 * file.Depth) + file.Change.Name,
            _ => row.ToString() ?? string.Empty,
        };
    }

    /// <summary>The Commit window's Group By › Directory, chosen as a person chooses it.</summary>
    private static void GroupBy(CommitView commit)
    {
        Ui.Click(commit.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Group By"));
        Ui.Choose(commit.GroupByMenu, "Directory");
    }

    private static CommitFolder Folder(CommitView commit, string name) => commit.Rows.OfType<CommitFolder>().Single(f => f.Name == name);

    /// <summary>The words a file's row of the Commit window shows: its name, its letter and its folder.</summary>
    private static string Words(ListBox list, CommitView commit, string name) => string.Join(" ",
        Ui.Row(list, commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == name)).GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text));

    private static CheckBox Box(ListBox list, object row) => Ui.Row(list, row).GetVisualDescendants().OfType<CheckBox>().First(b => b.IsVisible);

    private static string[] ChosenNames(CommitView commit) => [.. commit.Chosen.Select(c => c.Name).Order(StringComparer.Ordinal)];

    private MainWindow OpenWithState(string state)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, StateFolder = state };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        window.Perform(AppActions.ShowCommit);
        return window;
    }
}
