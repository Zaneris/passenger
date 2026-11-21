using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>The icon and tint a file or folder is drawn with: <see cref="FileIconSet"/>'s shape and colour for its name, a
/// folder the set draws nothing apart for as a folder (one the SDK reads with its badge, open or not), and a solution
/// folder as a filled one, so it never passes for a folder on disk. A project takes its kind's icon (<see cref="KindIcon"/>)
/// in its project file's tint, wherever it is listed (<see cref="ForProject"/>).</summary>
public static class FileIcons
{
    public static Geometry For(ExplorerNode node) => KindIcon(node.ProjectKind) is { } kind
        ? Find<Geometry>(kind)
        : Look(node) is { } look
        ? Find<Geometry>(Shaped(look))
        : Find<Geometry>(node.Kind switch
        {
            NodeKind.SolutionFolder => node.IsExpanded ? IconKeys.SolutionFolderOpen : IconKeys.SolutionFolder,
            NodeKind.Framework => IconKeys.Reference,
            _ => node.SdkFolder switch
            {
                SdkFolder.Properties => IconKeys.PropertiesFolder,
                SdkFolder.WebRoot => IconKeys.WebRootFolder,
                _ => node.IsExpanded ? IconKeys.FolderOpen : IconKeys.Explorer,
            },
        });

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

    /// <summary>A project's icon wherever a list names it: its kind's, else its project file's.</summary>
    public static Geometry ForProject(string path, ProjectKind kind) => KindIcon(kind) is { } key ? Find<Geometry>(key) : ForFile(path);

    /// <summary>The resource of a shape of the file icons.</summary>
    private static string Shaped(FileIconLook look) => $"FileIcon.{look.Shape}";

    public static IBrush BrushFor(ExplorerNode node) => Look(node) is { } look
        ? Tint(look)
        : Find<IBrush>(node.Kind switch
        {
            NodeKind.SolutionFolder => TokenKeys.File_SolutionFolder,
            NodeKind.Framework => TokenKeys.File_Plain,
            _ => TokenKeys.File_Folder,
        });

    /// <summary>A folder's icon by its name, as the Explorer draws a folder on disk.</summary>
    public static Geometry ForFolder(string name, bool open) => FileIconSet.ForFolder(name) is { } look
        ? Find<Geometry>(Shaped(look))
        : Find<Geometry>(open ? IconKeys.FolderOpen : IconKeys.Explorer);

    public static IBrush BrushForFolder(string name) => FileIconSet.ForFolder(name) is { } look ? Tint(look) : Find<IBrush>(TokenKeys.File_Folder);

    public static Geometry ForFile(string path) => Find<Geometry>(Shaped(FileIconSet.ForFile(Path.GetFileName(path))));

    public static IBrush BrushForFile(string path) => Tint(FileIconSet.ForFile(Path.GetFileName(path)));

    /// <summary>A diff's tab's icon, in its file's tint.</summary>
    public static Geometry ForDiff => Find<Geometry>(IconKeys.Diff);

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

    private static IBrush Tint(FileIconLook look) => Find<IBrush>(look.Colour is { } colour ? $"Passenger.FileIcon.{colour}" : TokenKeys.File_Plain);

    private static T Find<T>(string key) where T : class =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is T found
            ? found
            : throw new InvalidOperationException($"The theme has no {key}.");
}
