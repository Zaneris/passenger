using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A changed file's line, as every list of changed files shows one (the Commit window's, a commit's in the Git
/// log, the changes between a branch and the working tree, a folder's Local History): its icon, its name in its change's
/// colour (struck through when deleted), the change's letter in that colour (<see cref="LetterOf"/>, so it reads without
/// the colour), where a moved file came from ("← Shop.cs"), and its folder, which gives way first in a narrow list, then
/// where it came from, then the name, each ending in an ellipsis. Made once with its list's container and shown another
/// file in place.</summary>
public sealed class ChangedFileRow
{
    /// <summary>How far the icon sits from its list's edge where nothing comes before it.</summary>
    public const double Inset = Gaps.Normal;

    private readonly RowIcon icon = new() { Margin = new Thickness(0, 0, Gaps.Small, 0) };
    private readonly TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock letter = new() { Classes = { StyleClasses.Caption, StyleClasses.Strong }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Small, 0, 0, 0) };
    private string? letterColour;
    private readonly TextBlock came = MutedText.Of(string.Empty);
    private readonly TextBlock folder = MutedText.Of(string.Empty);
    private string? colour;

    public ChangedFileRow()
    {
        foreach (TextBlock muted in (TextBlock[])[came, folder])
        {
            muted.Classes.Add(StyleClasses.Caption);
            muted.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        }
        came.IsVisible = false;
        ShrinkingRow.SetKeepsWidth(letter, true);
        Line = new ShrinkingRow { Height = ListRow.Height, Margin = new Thickness(Inset, 0, 0, 0), Children = { icon, name, letter, came, folder } };
    }

    /// <summary>The line, <see cref="Inset"/> from its list's edge (none where a box comes before it).</summary>
    public ShrinkingRow Line { get; }

    /// <summary>Shows a file by its path and how it changed, with <paramref name="where"/> in its folder's place: its folder
    /// (<see cref="Folder"/>), or why it is as it is.</summary>
    public void Show(string path, ChangeKind kind, string where)
    {
        icon.Data = FileIcons.ForFile(path);
        icon.ForegroundKey = FileIcons.TintForFile(path);
        name.Text = Path.GetFileName(path);
        name.Resource(TextBlock.ForegroundProperty, ref colour, KindToken(kind));
        name.TextDecorations = kind is ChangeKind.Deleted ? TextDecorations.Strikethrough : null;
        letter.Text = LetterOf(kind);
        letter.Resource(TextBlock.ForegroundProperty, ref letterColour, KindToken(kind));
        ToolTip.SetTip(letter, WordOf(kind));
        AutomationProperties.SetName(letter, WordOf(kind));
        folder.Text = where;
        came.IsVisible = false;
        ToolTip.SetTip(came, null);
    }

    /// <summary>Shows a changed file of a repository, its folder from the repository's top (<paramref name="why"/> in its
    /// place where given) and, where it moved, where it came from (<see cref="Came"/>), the tip saying its whole old path.</summary>
    public void Show(FileChange change, Git? repository, string? why = null)
    {
        string? relative = repository?.Relative(change.Path);
        Show(change.Path, change.Kind, why ?? (relative is null ? string.Empty : Folder(relative)));
        if (change.From is not { } from)
        {
            return;
        }
        string was = repository?.Relative(from) ?? from;
        came.Text = "← " + Came(relative ?? change.Path, was);
        came.IsVisible = true;
        ToolTip.SetTip(came, $"{(InPlace(relative ?? change.Path, was) ? "Renamed" : "Moved")} from {was}");
    }

    /// <summary>Where a moved file came from as its line names it: its old name where it stayed in its folder, else its
    /// old path, each path from the same top folder.</summary>
    public static string Came(string path, string from) => InPlace(path, from) ? Path.GetFileName(from) : from;

    // Git's names tell case apart, so a folder renamed in its case alone is a move.
    private static bool InPlace(string path, string from) => string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(from), StringComparison.Ordinal);

    /// <summary>The folder a path from a top folder (a repository's, the one a history is of) names, with '/' between its
    /// names.</summary>
    public static string Folder(string relative) => Path.GetDirectoryName(relative) is { } folder ? PathNames.Slashed(folder) : string.Empty;

    /// <summary>The letter beside a changed file's name saying how it changed, as its colour does.</summary>
    public static string LetterOf(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Unversioned => "U",
        ChangeKind.Conflicted => "!",
        _ => "M",
    };

    /// <summary>How a file changed, in a word.</summary>
    public static string WordOf(ChangeKind kind) => kind switch
    {
        ChangeKind.Unversioned => "Unversioned",
        ChangeKind.Conflicted => "In conflict",
        _ => kind.ToString(),
    };

    /// <summary>A changed file's colour by how it changed (modified, added, deleted, new, conflicted).</summary>
    public static string KindToken(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => TokenKeys.Vcs_File_Added,
        ChangeKind.Deleted => TokenKeys.Vcs_File_Deleted,
        ChangeKind.Unversioned => TokenKeys.Vcs_File_Unversioned,
        ChangeKind.Conflicted => TokenKeys.Vcs_File_Conflicted,
        _ => TokenKeys.Vcs_File_Modified,
    };
}
