using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The question a folder opened untrusted asks once (<see cref="Passenger.Core.WorkspaceTrust"/>): whether the
/// person trusts its code, naming the folder and what trusting lets it run, with Trust, Trust Parent Folder and Open
/// Without Trusting. A folder below the top of a Git repository asks of the repository, which Trust trusts
/// (<see cref="Passenger.Core.WorkspaceTrust.FolderToTrust"/>), saying why. Trust wears the accent, yet no key answers it:
/// Enter is no answer, and Escape opens the folder without trusting it. Closed unanswered, the folder stays untrusted and
/// is asked of at its next open.</summary>
public sealed class WorkspaceTrustDialog : IdeWindow
{
    public const string Trust = "Trust";
    public const string TrustParent = "Trust Parent Folder";
    public const string Untrusted = "Open Without Trusting";

    public WorkspaceTrustDialog(string folder)
    {
        Folder = folder;
        Trusting = Passenger.Core.WorkspaceTrust.FolderToTrust(folder);
        Above = ParentOf(Trusting);
        Button trust = new() { Content = Trust, Classes = { StyleClasses.Primary } };
        Button parent = new() { Content = TrustParent, IsVisible = Above is not null };
        Button untrusted = new() { Content = Untrusted, IsCancel = true };
        trust.Click += (_, _) => Answer(TrustAnswer.Trusted);
        parent.Click += (_, _) => Answer(TrustAnswer.ParentTrusted);
        untrusted.Click += (_, _) => Answer(TrustAnswer.Untrusted);
        string name = NameOf(Trusting);
        List<Control> said = [DialogFooter.Paragraph(Welcome.RecentRow.Home(Trusting))];
        if (Passenger.Core.PathNames.IsUnder(folder, Trusting))
        {
            said.Add(DialogFooter.Paragraph(InRepository(folder, Trusting)));
        }
        said.Add(DialogFooter.Paragraph(Refusals.TrustLets.Shown));
        said.Add(DialogFooter.Paragraph(Above is not null
            ? $"Untrusted, you read and edit its files, and none of its code runs until you trust it. {TrustParent} trusts every folder in {Path.GetFileName(Above)} too."
            : "Untrusted, you read and edit its files, and none of its code runs until you trust it."));
        DialogFooter.Question(this, $"Trust the code in {name}?", said, DialogFooter.Answers(trust, parent, untrusted));
    }

    /// <summary>What the question says of a folder opened below the top of the Git repository it trusts.</summary>
    public static string InRepository(string folder, string repository) =>
        $"You opened {NameOf(repository)}/{Passenger.Core.PathNames.Relative(folder, repository)}: trusting it trusts the Git repository it is in, "
        + $"{NameOf(repository)}, whose build files (Directory.Build.props, Directory.Packages.props) {NameOf(folder)}'s projects take in.";

    /// <summary>The folder a folder's Trust Parent Folder trusts: the one it is in, but never a whole disk.</summary>
    private static string? ParentOf(string folder) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { } above && Path.GetDirectoryName(above) is not null ? above : null;

    /// <summary>The folder Trust trusts: the one asked of, or the top of the Git repository it is in.</summary>
    public string Trusting { get; }

    /// <summary>The folder Trust Parent Folder trusts, the one <see cref="Trusting"/> is in; null where that is a disk's root.</summary>
    public string? Above { get; }

    /// <summary>The folder asked of, canonical.</summary>
    public string Folder { get; }

    /// <summary>The person's answer, once they gave one; null while it asks, or closed unanswered.</summary>
    public TrustAnswer? Answered { get; private set; }

    /// <summary>A folder's name, or its path where it has none (a disk's root).</summary>
    private static string NameOf(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } own ? own : folder;

    /// <summary>The person's answer, which an agent's input never gives (<see cref="AgentAsks.PersonsAlone"/>): the question
    /// stays for the person.</summary>
    private void Answer(TrustAnswer said)
    {
        if (AgentAsks.PersonsAlone(Refusals.TrustAnsweredByThePerson.Shown))
        {
            return;
        }
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
