using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The question a folder opened untrusted asks once (<see cref="Passenger.Core.WorkspaceTrust"/>): whether the
/// person trusts its code, naming the folder and what trusting lets it run, with Trust, Trust Parent Folder and Open
/// Without Trusting. Trust wears the accent, yet no key answers it: Enter is no answer, and Escape opens the folder
/// without trusting it. Closed unanswered, the folder stays untrusted and is asked of at its next open.</summary>
public sealed class WorkspaceTrustDialog : IdeWindow
{
    public const string Trust = "Trust";
    public const string TrustParent = "Trust Parent Folder";
    public const string Untrusted = "Open Without Trusting";

    public WorkspaceTrustDialog(string folder)
    {
        Folder = folder;
        Button trust = new() { Content = Trust, Classes = { StyleClasses.Primary } };
        string? above = ParentOf(folder);
        Button parent = new() { Content = TrustParent, IsVisible = above is not null };
        Button untrusted = new() { Content = Untrusted, IsCancel = true };
        trust.Click += (_, _) => Answer(TrustAnswer.Trusted);
        parent.Click += (_, _) => Answer(TrustAnswer.ParentTrusted);
        untrusted.Click += (_, _) => Answer(TrustAnswer.Untrusted);
        string name = Path.GetFileName(folder) is { Length: > 0 } own ? own : folder;
        DialogFooter.Question(this, $"Trust the code in {name}?",
        [
            DialogFooter.Paragraph(Welcome.RecentRow.Home(folder)),
            DialogFooter.Paragraph(Refusals.TrustLets),
            DialogFooter.Paragraph(above is not null
                ? $"Untrusted, you read and edit its files, and none of its code runs until you trust it. {TrustParent} trusts every folder in {Path.GetFileName(above)} too."
                : "Untrusted, you read and edit its files, and none of its code runs until you trust it."),
        ], DialogFooter.Answers(trust, parent, untrusted));
    }

    /// <summary>The folder a folder's Trust Parent Folder trusts: the one it is in, but never a whole disk.</summary>
    public static string? ParentOf(string folder) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { } above && Path.GetDirectoryName(above) is not null ? above : null;

    /// <summary>The folder asked of, canonical.</summary>
    public string Folder { get; }

    /// <summary>The person's answer, once they gave one; null while it asks, or closed unanswered.</summary>
    public TrustAnswer? Answered { get; private set; }

    private void Answer(TrustAnswer said)
    {
        Answered = said;
        Close(said);
    }
}

/// <summary>What the person answered of a folder's trust.</summary>
public enum TrustAnswer
{
    Trusted,
    ParentTrusted,
    Untrusted,
}
