using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>What git asks while it reaches a remote, put to the person: a login (its user and password together, the
/// password given as git asks for it next), a password or a key's passphrase typed unseen, whether to trust a host, or
/// git's own question. Asked for an agent's call, it says so, and that the answer goes to git alone. OK closes it with
/// <see cref="Answer"/>; Cancel, Escape or a close, with none.</summary>
public sealed class GitQuestionDialog : IdeWindow
{
    private readonly GitQuestion question;
    private readonly TextBox? user;
    private readonly TextBox? secret;

    /// <param name="agent">The agent whose call git asks for, or null for the person's own action.</param>
    public GitQuestionDialog(GitQuestion question, string? agent)
    {
        this.question = question;
        bool yesNo = question.Kind == GitQuestionKind.YesNo;
        if (question.Kind == GitQuestionKind.User)
        {
            user = new TextBox { Name = "GitUser", Classes = { "search" } };
        }
        if (!yesNo)
        {
            secret = new TextBox { Name = "GitSecret", Classes = { "search" }, PasswordChar = '•' };
        }
        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8 };
        if (user is not null)
        {
            Row(fields, "User:", user);
        }
        if (secret is not null)
        {
            Row(fields, question.Kind switch
            {
                GitQuestionKind.Passphrase => "Passphrase:",
                GitQuestionKind.Other => "Answer:",
                _ => "Password:",
            }, secret);
        }

        Button ok = new() { Content = yesNo ? "Yes" : question.Kind == GitQuestionKind.User ? "Sign In" : "OK", IsDefault = agent is null, Classes = { "primary" } };
        Button cancel = new() { Content = yesNo ? "No" : "Cancel", IsCancel = true };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Decline();
        List<Control> paragraphs = [DialogFooter.Paragraph(Said(question))];
        if (fields.Children.Count > 0)
        {
            paragraphs.Add(fields);
        }
        TextBlock kept = new()
        {
            Classes = { "muted" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = (agent is null ? string.Empty : $"Asked for {agent}'s call through Passenger's MCP server: the answer goes to git, never to the agent. ")
                + (yesNo ? "ssh keeps a host it is told to trust in its known_hosts." : "It is given to git for this operation alone: Passenger keeps none of it, and git keeps it only where a credential helper is set (git config credential.helper)."),
        };
        paragraphs.Add(kept);
        DialogFooter.Question(this, Titled(question), paragraphs, DialogFooter.Of(ok, cancel));
        Opened += (_, _) => Dispatcher.UIThread.Post(() => (user ?? secret ?? (Control)ok).Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>What the person answered, once they did: null until then, and when they declined.</summary>
    public GitAnswer? Answer { get; private set; }

    /// <summary>Whether the person answered (true) or declined (false), once they did: what a question shown without
    /// waiting on it reads as it closes.</summary>
    public bool? Answered { get; private set; }

    /// <summary>The user name, where a login is asked.</summary>
    public string User
    {
        get => user?.Text ?? string.Empty;
        set => user?.Text = value;
    }

    /// <summary>OK: the answer typed (a login's user with its password), or yes; a secret typed is masked from now on
    /// wherever it would be shown.</summary>
    public void Accept()
    {
        string typed = secret?.Text ?? string.Empty;
        if (question.IsSecret || question.Kind == GitQuestionKind.User)
        {
            Containers.ContainerSecrets.Mark(typed);
        }
        Answer = question.Kind switch
        {
            GitQuestionKind.YesNo => new GitAnswer("yes"),
            GitQuestionKind.User => new GitAnswer(User.Trim(), typed),
            _ => new GitAnswer(typed),
        };
        Answered = true;
        Close(true);
    }

    /// <summary>Cancel, or No to a yes-or-no question, which git is given as its answer.</summary>
    public void Decline()
    {
        Answer = question.Kind == GitQuestionKind.YesNo ? new GitAnswer("no") : null;
        Answered = false;
        Close(false);
    }

    private static string Titled(GitQuestion question) => question.Kind switch
    {
        GitQuestionKind.User => $"Sign in to {question.Place}",
        GitQuestionKind.Password => $"Password for {(question.User is { } user ? $"{user} at " : string.Empty)}{question.Place}",
        GitQuestionKind.Passphrase => $"Passphrase for {question.Place}",
        GitQuestionKind.YesNo => $"Trust {question.Place}?",
        _ => "Git asks",
    };

    private static string Said(GitQuestion question) => question.Kind switch
    {
        GitQuestionKind.User => $"Git needs a login to reach {question.About}.",
        GitQuestionKind.Password => question.User is { } user ? $"Git needs the password of {user} to reach {question.About}." : $"Git needs a password to reach {question.About}.",
        GitQuestionKind.Passphrase => $"ssh needs the passphrase of the key {question.About} to reach the remote.",
        _ => question.Prompt.Trim(),
    };

    private static void Row(Grid grid, string label, Control field)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        TextBlock text = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
    }
}
