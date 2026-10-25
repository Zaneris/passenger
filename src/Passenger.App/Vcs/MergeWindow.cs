using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>The merge window over the Conflicts dialog: the three-way merge, with Accept Left and Accept Right (the
/// whole file as that side has it), Cancel and Apply Changes (the result), either of which writes the file and marks it
/// resolved. Applying with changes left asks first, as cancelling a merge begun does; Escape cancels. The merge's own keys
/// go to it wherever in the window the keys are, ahead of the IDE's.</summary>
public sealed class MergeWindow : Window, IOwnKeys
{
    private readonly Func<string, Task<bool>> resolve;
    private readonly Func<string, string, string, string, Task<bool>> ask;
    private readonly Button[] buttons;
    private bool finished;

    /// <param name="resolve">Writes a text to the file and marks it resolved, saying whether it did.</param>
    /// <param name="ask">Asks the person: a title, the question, the button that agrees and the one that does not.</param>
    public MergeWindow(string title, MergeView view, Func<string, Task<bool>> resolve, Func<string, string, string, string, Task<bool>> ask)
    {
        this.resolve = resolve;
        this.ask = ask;
        Title = title;
        Width = 1300;
        Height = 800;
        MinWidth = 800;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        View = view;

        Button acceptLeft = Action("Accept Left", () => AcceptAsync(MergeSide.Left));
        Button acceptRight = Action("Accept Right", () => AcceptAsync(MergeSide.Right));
        Button cancel = Action("Cancel", CancelAsync);
        Button apply = Action("Apply Changes", ApplyAsync);
        apply.Classes.Add("primary");
        buttons = [acceptLeft, acceptRight, cancel, apply];
        StackPanel sides = new() { Classes = { "footer" }, Children = { acceptLeft, acceptRight } };
        Border bar = DialogFooter.Bar(DialogFooter.Of(apply, cancel), sides);
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Bottom);
        Content = new DockPanel { Children = { bar, view } };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                IdeErrors.Observe(CancelAsync());
                e.Handled = true;
            }
        };
        Opened += (_, _) => view.GoToFirst();
        // Closed from its title bar with the merge begun, it asks first as Cancel does.
        Closing += (_, e) =>
        {
            if (!finished && e.CloseReason == WindowCloseReason.WindowClosing && View.IsModified)
            {
                e.Cancel = true;
                IdeErrors.Observe(CancelAsync());
            }
        };
    }

    public MergeView View { get; }

    public bool TakesKey(KeyEventArgs e) => MergeView.TakesKey(e);

    /// <summary>Whether it closed with the file written and marked resolved.</summary>
    public bool Resolved { get; private set; }

    /// <summary>Apply Changes: the result written and the file marked resolved, asked first when changes are left
    /// unprocessed. Whether it was.</summary>
    public async Task<bool> ApplyAsync()
    {
        int changes = View.UnresolvedChanges;
        int conflicts = View.UnresolvedConflicts;
        if (changes + conflicts > 0 && !await ask("Apply Changes", Unprocessed(changes, conflicts), "Yes", "No"))
        {
            return false;
        }
        return await FinishAsync(View.ResultText);
    }

    /// <summary>Accept Left or Accept Right: the file as that side has it, whole, asked first when the result was changed.</summary>
    public async Task<bool> AcceptAsync(MergeSide side)
    {
        string what = side == MergeSide.Left ? "Accept Left" : "Accept Right";
        if (View.IsModified && !await ask(what, $"There are unsaved changes in the result file. Discard changes and {what.ToLowerInvariant()} anyway?",
            $"Discard Changes and {what}", "Continue Merge"))
        {
            return false;
        }
        return await FinishAsync(side == MergeSide.Left ? View.Texts.Left : View.Texts.Right);
    }

    /// <summary>Cancel: the file left in conflict, asked first when the merge was begun.</summary>
    public async Task CancelAsync()
    {
        if (View.IsModified && !await ask("Cancel File Merge", "Are you sure you want to exit without applying changes?", "Yes", "No"))
        {
            return;
        }
        finished = true;
        Close(false);
    }

    private async Task<bool> FinishAsync(string text)
    {
        foreach (Button button in buttons)
        {
            button.IsEnabled = false;
        }
        bool written = await resolve(text);
        foreach (Button button in buttons)
        {
            button.IsEnabled = true;
        }
        if (!written)
        {
            return false;
        }
        Resolved = true;
        finished = true;
        // The Conflicts dialog under it closes as its last file is resolved, and this with it.
        if (IsVisible)
        {
            Close(true);
        }
        return true;
    }

    /// <summary>The words for what Apply Changes leaves behind.</summary>
    private static string Unprocessed(int changes, int conflicts)
    {
        List<string> left = [];
        if (changes > 0)
        {
            left.Add(changes == 1 ? "one change" : $"{changes} changes");
        }
        if (conflicts > 0)
        {
            left.Add(conflicts == 1 ? "one conflict" : $"{conflicts} conflicts");
        }
        string verb = (changes > 0 ? changes : conflicts) == 1 ? "is" : "are";
        return $"There {verb} {string.Join(" and ", left)} left unprocessed.\nSave changes and mark the conflict resolved anyway?";
    }

    private static Button Action(string label, Func<Task> run)
    {
        Button button = new() { Content = label };
        button.Click += (_, _) => IdeErrors.Observe(run());
        return button;
    }
}
