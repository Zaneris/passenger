using Avalonia;
using Avalonia.Controls;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>The icon and tint a file or folder is drawn with, each as the theme's key, which the one drawing it finds as it
/// draws, so it follows a theme laid since (an icon of the theme's own in Passenger's place among them): <see
/// cref="FileIconSet"/>'s shape and colour for its name, a folder the set draws nothing apart for as a folder (one the SDK
/// reads with its badge, open or not), drawn only while Settings shows plain folders' icons (<see cref="ShowPlainFolders"/>),
/// and a solution folder as a filled one, so it never passes for a folder on disk. A project takes its kind's icon (<see
/// cref="KindIcon"/>) in its project file's tint, wherever it is listed (<see cref="ProjectKey"/>).</summary>
public static class FileIcons
{
    /// <summary>The resource the application holds while Settings shows a plain folder's chevron alone: its change has every
    /// icon drawn again.</summary>
    private static readonly object PlainFoldersHidden = new();

    /// <summary>The key of a row's icon.</summary>
    public static string KeyFor(ExplorerNode node) => KindIcon(node.ProjectKind) is { } kind
        ? kind
        : Look(node) is { } look
        ? Shaped(look)
        : node.Kind switch
        {
            NodeKind.SolutionFolder => node.IsExpanded ? IconKeys.SolutionFolderOpen : IconKeys.SolutionFolder,
            NodeKind.Framework => IconKeys.Reference,
            _ => node.SdkFolder switch
            {
                SdkFolder.Properties => IconKeys.PropertiesFolder,
                SdkFolder.WebRoot => IconKeys.WebRootFolder,
                _ => PlainFolder(node.IsExpanded),
            },
        };

    /// <summary>Whether a row's icon is a plain folder's, the set and the SDK drawing nothing apart for it.</summary>
    public static bool IsPlainFolder(ExplorerNode node) =>
        KindIcon(node.ProjectKind) is null && Look(node) is null && node.Kind is not (NodeKind.SolutionFolder or NodeKind.Framework) && node.SdkFolder is null;

    /// <summary>The icon each kind of project is drawn with, the one place a kind gets its look: the icon of the IDE's
    /// window for it where it has one, a file's of its kind where one is, else its own; null for a project drawn as its
    /// project file.</summary>
    private static string? KindIcon(ProjectKind kind) => kind switch
    {
        ProjectKind.Compose => Shaped(FileIconSet.Compose),
        ProjectKind.Shared => IconKeys.SharedProject,
        ProjectKind.AspireHost => IconKeys.Aspire,
        ProjectKind.Test => IconKeys.Tests,
        ProjectKind.GodotGame => Shaped(FileIconSet.Godot),
        ProjectKind.Functions => IconKeys.FunctionsApp,
        ProjectKind.Maui => IconKeys.MauiApp,
        ProjectKind.Mobile => IconKeys.MobileApp,
        ProjectKind.Blazor => IconKeys.BlazorApp,
        ProjectKind.Avalonia => IconKeys.AvaloniaApp,
        ProjectKind.Wpf => IconKeys.WpfApp,
        ProjectKind.WindowsForms => IconKeys.FormsApp,
        ProjectKind.WebApp => IconKeys.WebApp,
        ProjectKind.WebApi => IconKeys.Endpoints,
        ProjectKind.Desktop => IconKeys.DesktopApp,
        ProjectKind.Console => IconKeys.Terminal,
        ProjectKind.Library => IconKeys.ClassLibrary,
        _ => null,
    };

    /// <summary>The key of a project's icon wherever a list names it: its kind's, else its project file's.</summary>
    public static string ProjectKey(string path, ProjectKind kind) => KindIcon(kind) ?? FileKey(path);

    /// <summary>The resource of a shape of the file icons.</summary>
    private static string Shaped(FileIconLook look) => $"FileIcon.{look.Shape}";

    /// <summary>The key of a row's icon's tint.</summary>
    public static string BrushKeyFor(ExplorerNode node) => Look(node) is { } look
        ? Tint(look)
        : node.Kind switch
        {
            NodeKind.SolutionFolder => TokenKeys.File_SolutionFolder,
            NodeKind.Framework => TokenKeys.File_Plain,
            _ => TokenKeys.File_Folder,
        };

    /// <summary>The key of a folder's icon by its name, as the Explorer draws a folder on disk.</summary>
    public static string FolderKey(string name, bool open) => FileIconSet.ForFolder(name) is { } look ? Shaped(look) : PlainFolder(open);

    /// <summary>Whether a folder of a name is a plain one, the set drawing nothing apart for it.</summary>
    public static bool IsPlainFolder(string name) => FileIconSet.ForFolder(name) is null;

    /// <summary>The key of a folder's icon's tint by its name.</summary>
    public static string FolderBrushKey(string name) => FileIconSet.ForFolder(name) is { } look ? Tint(look) : TokenKeys.File_Folder;

    /// <summary>The key of a file's icon by its name.</summary>
    public static string FileKey(string path) => Shaped(FileIconSet.ForFile(Path.GetFileName(path)));

    /// <summary>The key of a file's icon's tint by its name.</summary>
    public static string FileBrushKey(string path) => Tint(FileIconSet.ForFile(Path.GetFileName(path)));

    /// <summary>The key of a diff's tab's icon, in its file's tint.</summary>
    public static string DiffKey => IconKeys.Diff;

    /// <summary>Whether a plain folder's icon is drawn where an icon's control finds its resources: Settings shows a plain
    /// folder's chevron alone otherwise.</summary>
    public static bool ShowsPlainFolders(IResourceHost host) => !host.TryFindResource(PlainFoldersHidden, out _);

    /// <summary>Shows plain folders' icons, or their chevrons alone, in every window, each icon drawn again.</summary>
    public static void ShowPlainFolders(Application app, bool shown)
    {
        if (shown)
        {
            app.Resources.Remove(PlainFoldersHidden);
        }
        else
        {
            app.Resources[PlainFoldersHidden] = true;
        }
    }

    private static string PlainFolder(bool open) => open ? IconKeys.FolderOpen : IconKeys.Explorer;

    /// <summary>A row's look in the set, or null for what it draws as a folder or a link: a plain folder, a solution
    /// folder and a target framework.</summary>
    private static FileIconLook? Look(ExplorerNode node) => node.Kind switch
    {
        NodeKind.Folder => FileIconSet.ForFolder(node.Name),
        NodeKind.SolutionFolder or NodeKind.Framework => null,
        NodeKind.Dependencies or NodeKind.Package => FileIconSet.Packages,
        NodeKind.ProjectReference or NodeKind.SharedProject => FileIconSet.ForFile(Path.GetFileName(node.Referenced ?? node.Name)),
        _ => FileIconSet.ForFile(Path.GetFileName(node.Path ?? node.Name)),
    };

    private static string Tint(FileIconLook look) => look.Colour is { } colour ? ThemeResources.KeyOf($"FileIcon.{colour}") : TokenKeys.File_Plain;
}
