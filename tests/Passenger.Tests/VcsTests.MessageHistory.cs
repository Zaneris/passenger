using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;

namespace Passenger.Tests;

public sealed partial class VcsTests
{
    /// <summary>The Commit window keeps the messages committed from it in the folder's state, newest first and each once:
    /// with none, its history is refused saying why; the history's keys in the message box and its button list them, each
    /// by its first line, a longer one saying how many lines more; the one chosen goes in the box in place of what was typed,
    /// is changed and committed again, coming first; and the folder opened again offers them still.</summary>
    private void The_commit_windows_message_history_offers_each_message_committed_newest_first_to_change_and_commit_again()
    {
        string state = TestFolders.Create("messages-state-");
        try
        {
            MainWindow window = OpenWithState(state);
            CommitView commit = window.CommitView;
            Assert.Equal(Refusals.NoCommitMessages.Shown, window.Actions.WhyNot(AppActions.CommitMessageHistory, window.Actions.Context())?.Shown);
            foreach (string message in (string[])["Add the till", "Fix #12: round totals\n\nThe till rounded down.", "Add the till"])
            {
                Committed(window, message);
            }

            commit.Message = "typed meanwhile";
            commit.FocusMessage();
            Ui.Settle();
            string keys = window.Actions.Keymap.ShortcutsFor(AppActions.KeysIn(AppActions.CommitMessageHistory))[0].First.Notation();
            Ui.Press(window, keys);
            ChoicePopup popup = Assert.IsType<ChoicePopup>(window.Popup);
            Assert.True(popup.Rows.Select(r => $"{r.Label}|{r.Detail}").SequenceEqual(["Add the till|", "Fix #12: round totals|+2 lines"]),
                $"{keys} in the commit message listed [{string.Join("; ", popup.Rows.Select(r => $"{r.Label} {r.Detail}"))}], not each message committed once, newest first");
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            Assert.Equal("Fix #12: round totals\n\nThe till rounded down.", commit.Message);
            commit.Message = commit.Message.Replace("#12", "#13", StringComparison.Ordinal);
            Committed(window, null);
            window.Close();

            MainWindow again = OpenWithState(state);
            Ui.Click(again.CommitView.GetVisualDescendants().OfType<Button>().Single(b => b.Name == AppActions.CommitMessageHistory));
            ChoicePopup kept = Assert.IsType<ChoicePopup>(again.Popup);
            Assert.True(kept.Rows.Select(r => r.Label).SequenceEqual(["Fix #13: round totals", "Add the till", "Fix #12: round totals"]),
                $"The folder opened again offers [{string.Join("; ", kept.Rows.Select(r => r.Label))}], not the messages it kept, newest first");
            Ui.Press(again, "ESCAPE");
            again.Close();
        }
        finally
        {
            TestFolders.Delete(state);
        }

        // A change to Shop.cs, the Commit window's changes read again, committed from it with a message, else the one in its box.
        void Committed(MainWindow window, string? message)
        {
            File.AppendAllText(Path.Combine(folder, "Shop.cs"), $"// {Guid.NewGuid():N}\n");
            CommitView commit = window.CommitView;
            Assert.True(window.Perform(AppActions.CommitRefresh), window.Status);
            Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Shop.cs"), "Shop.cs changed in the Commit window");
            commit.Message = message ?? commit.Message;
            string subject = commit.Message.Split('\n')[0];
            Ui.ClickButton(commit, "Commit");
            Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == subject, $"the commit '{subject}'");
        }
    }
}
