using Avalonia.Controls;

namespace Passenger.App;

/// <summary>Asks before something that cannot be undone: what happens, what it happens to when there is a list of it
/// (Rollback's files), the button that does it and the one that does not (Cancel, or a choice of its own, as the Android
/// licences' Decline), closing with whether the person agreed. Escape answers no.</summary>
public sealed class ConfirmDialog : IdeWindow
{
    private readonly Button confirm;

    public ConfirmDialog(string title, string text, string yes, string no = "Cancel", Control? detail = null)
    {
        confirm = DialogFooter.Default(yes);
        confirm.Click += (_, _) => Answer(true);
        DialogFooter.Question(this, title, [DialogFooter.Paragraph(text), .. detail is null ? [] : new[] { detail }],
            DialogFooter.Of(confirm, DialogFooter.Cancel(() => Answer(false), no)));
    }

    /// <summary>Whether Enter agrees, as it does to a question the person's own action asked; never to one that came on
    /// its own (an agent's), which a key meant for something else must not answer.</summary>
    public bool EnterAgrees
    {
        get => confirm.IsDefault;
        init => confirm.IsDefault = value;
    }

    /// <summary>Whether the person agreed, once they answered: what a question shown without waiting on it reads as it
    /// closes.</summary>
    public bool? Answered { get; private set; }

    /// <summary>Why only the person answers it (an install's, a change of their shell's), which no agent's input reaches
    /// (<see cref="MainWindow.KeptFromAgents"/>); null for a question an agent's input may answer.</summary>
    public string? PersonsAlone { get; init; }

    /// <summary>The answer, which an agent's input never gives to a question only the person answers
    /// (<see cref="AgentAsks.PersonsAlone"/>): it stays asking.</summary>
    private void Answer(bool agreed)
    {
        if (PersonsAlone is { } why && AgentAsks.PersonsAlone(why))
        {
            return;
        }
        Answered = agreed;
        Close(agreed);
    }
}
