using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Explorer;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A changed file's line, as every list of changed files shows one (the Commit window's, a commit's in the Git
/// log, the changes between a branch and the working tree, a folder's Local History): its icon, its name in its change's
/// colour (struck through when deleted) and its folder, which gives way first in a narrow list, then the name, each
/// ending in an ellipsis. Made once with its list's container and shown another file in place.</summary>
public sealed class ChangedFileRow
{
    /// <summary>How far the icon sits from its list's edge where nothing comes before it.</summary>
    public const double Inset = 8;

    private readonly RowIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock folder = MutedText.Of(string.Empty);
    private string? colour;

    public ChangedFileRow()
    {
        folder.FontSize = 12;
        folder.Margin = new Thickness(8, 0, 0, 0);
        Line = new ShrinkingRow { Height = ListRow.Height, Margin = new Thickness(Inset, 0, 0, 0), Children = { icon, name, folder } };
    }

    /// <summary>The line, <see cref="Inset"/> from its list's edge (none where a box comes before it).</summary>
    public ShrinkingRow Line { get; }

    /// <summary>Shows a file by its path and how it changed, with <paramref name="where"/> in its folder's place: its folder
    /// (<see cref="Folder"/>), or why it is as it is.</summary>
    public void Show(string path, ChangeKind kind, string where)
    {
        icon.Data = FileIcons.ForFile(path);
        icon.Foreground = FileIcons.BrushForFile(path);
        name.Text = Path.GetFileName(path);
        name.Resource(TextBlock.ForegroundProperty, ref colour, KindToken(kind));
        name.TextDecorations = kind is ChangeKind.Deleted ? TextDecorations.Strikethrough : null;
        folder.Text = where;
    }

    /// <summary>The folder a path from a top folder (a repository's, the one a history is of) names, with '/' between its
    /// names.</summary>
    public static string Folder(string relative) => Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;

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
