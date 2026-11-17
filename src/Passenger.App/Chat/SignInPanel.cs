using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;

namespace Passenger.App.Chat;

/// <summary>Signing the agent in, as one clear flow in the Chat window. Signed out, it says so, what signing in does (the
/// agent's own login, in the browser; Passenger never sees it), and offers one way first (the agent's first: a Claude
/// subscription) with the others under it. Signing in, it walks the steps (the browser opens; Authorize there; Passenger
/// connects), each a ring while it is the one now, a check once done and a cross where it failed, with Cancel and the
/// Terminal tab the sign-in runs in.</summary>
public sealed class SignInPanel : StackPanel
{
    private readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock said = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly StackPanel choices = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel steps = new() { Spacing = 10, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel stepActions = new() { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    private readonly List<(Panel Mark, TextBlock Words, TextBlock Detail)> stepRows = [];

    public SignInPanel()
    {
        Spacing = 12;
        MaxWidth = PopupFrame.Narrow;
        HorizontalAlignment = HorizontalAlignment.Center;
        Button cancel = new() { Classes = { StyleClasses.TextLink }, Content = "Cancel sign-in" };
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Button terminal = new() { Classes = { StyleClasses.Link }, Content = "Show in Terminal" };
        ToolTip.SetTip(terminal, "The sign-in's own tab: its link, if the browser did not open, and how it ends");
        terminal.Click += (_, _) => TerminalRequested?.Invoke(this, EventArgs.Empty);
        stepActions.Children.Add(terminal);
        stepActions.Children.Add(cancel);
        foreach ((string words, string detail) in new[]
        {
            ("Your browser opens claude.ai", "If it does not, Show in Terminal has the link."),
            ("Click Authorize in the browser", "Sign in there with your Claude account if it asks, then come back here."),
            ("Passenger connects to Claude", "Your chats start once Claude Code says you are signed in."),
        })
        {
            Panel mark = new() { Width = 16, Height = 16, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            TextBlock line = new() { Text = words, TextWrapping = TextWrapping.Wrap };
            TextBlock under = new() { Classes = { StyleClasses.Muted }, FontSize = 11, Text = detail, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(mark, Dock.Left);
            steps.Children.Add(new DockPanel { Children = { mark, new StackPanel { Spacing = 2, Children = { line, under } } } });
            stepRows.Add((mark, line, under));
        }
        Children.Add(title);
        Children.Add(said);
        Children.Add(choices);
        Children.Add(steps);
        Children.Add(stepActions);
    }

    /// <summary>One of the agent's ways to sign in was chosen.</summary>
    public event EventHandler<AuthMethod>? SignInRequested;

    /// <summary>Cancel was pressed while signing in.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Show in Terminal was pressed while signing in.</summary>
    public event EventHandler? TerminalRequested;

    /// <summary>What it says now, its title then its words.</summary>
    public string Said => $"{title.Text}\n{said.Text}";

    /// <summary>The step it is on, from zero; -1 while it offers the ways to sign in.</summary>
    public int Step { get; private set; } = -1;

    /// <summary>Signed out: the ways to sign in, the agent's first the button, the others links under it.</summary>
    public void Offer(IReadOnlyList<AuthMethod> methods, string? why = null, bool error = false)
    {
        Step = -1;
        title.Text = "Sign in to Claude";
        said.Text = why ?? "Chat uses your own Claude Code login. You sign in in your browser, and Passenger never sees your password or keys.";
        said.Classes.Set(StyleClasses.Error, error);
        said.Classes.Set(StyleClasses.Muted, !error);
        choices.Children.Clear();
        for (int i = 0; i < methods.Count; i++)
        {
            AuthMethod method = methods[i];
            Button choose = i == 0
                ? new Button { Classes = { StyleClasses.Primary }, Content = $"Continue with {method.Name}", HorizontalAlignment = HorizontalAlignment.Center }
                : new Button { Classes = { StyleClasses.Link }, Content = $"Use {method.Name} instead", HorizontalAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(choose, method.Description ?? method.Name);
            choose.Click += (_, _) => SignInRequested?.Invoke(this, method);
            choices.Children.Add(choose);
        }
        if (methods.Count == 0)
        {
            said.Text += " Run claude /login in a terminal, then Connect.";
        }
        choices.IsVisible = true;
        steps.IsVisible = stepActions.IsVisible = false;
    }

    /// <summary>Signing in with a way the agent offers: the steps, the browser's step the one now.</summary>
    public void SigningIn(string method)
    {
        title.Text = $"Signing in with {method}";
        said.Text = "Follow the steps; this panel moves on by itself.";
        said.Classes.Set(StyleClasses.Error, false);
        said.Classes.Set(StyleClasses.Muted, true);
        choices.IsVisible = false;
        steps.IsVisible = stepActions.IsVisible = true;
        // The browser opens as the sign-in starts, so authorizing there is what waits.
        MoveTo(1);
    }

    /// <summary>The browser's sign-in finished: Passenger connects.</summary>
    public void Connecting()
    {
        said.Text = "Signed in. Connecting to Claude…";
        stepActions.IsVisible = false;
        MoveTo(2);
    }

    private void MoveTo(int step)
    {
        Step = step;
        for (int i = 0; i < stepRows.Count; i++)
        {
            (Panel mark, TextBlock words, _) = stepRows[i];
            mark.Children.Clear();
            mark.Children.Add(i < step
                ? new RowIcon { DataKey = IconKeys.Check, ForegroundKey = TokenKeys.Vcs_Added, Width = 14, Height = 14 }
                : i == step
                    ? new WorkingRing { Width = 12, Height = 12 }
                    : new TextBlock { Classes = { StyleClasses.Muted }, Text = "○", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            words.FontWeight = i == step ? FontWeight.SemiBold : FontWeight.Normal;
            words.Classes.Set(StyleClasses.Muted, i > step);
        }
    }
}
