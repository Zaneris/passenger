using Avalonia.Controls;

namespace Passenger.App;

/// <summary>Asks before something that cannot be undone: what happens, what it happens to when there is a list of it
/// (Rollback's files), the button that does it and the one that does not (Cancel, or a choice of its own, as File Cache
/// Conflict's Keep Memory Changes), closing with whether the person agreed. Escape answers no.</summary>
public sealed class ConfirmDialog : Window
{
    private readonly Button confirm;

    public ConfirmDialog(string title, string text, string yes, string no = "Cancel", Control? detail = null)
    {
        confirm = new() { Content = yes, IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = no, IsCancel = true };
        confirm.Click += (_, _) => Answer(true);
        cancel.Click += (_, _) => Answer(false);
        DialogFooter.Question(this, title, [DialogFooter.Paragraph(text), .. detail is null ? [] : new[] { detail }], DialogFooter.Of(confirm, cancel));
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

    private void Answer(bool agreed)
    {
        Answered = agreed;
        Close(agreed);
    }
}
