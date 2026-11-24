using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>The icon and tint a file or folder is drawn with, each as the theme's key, which the one drawing it finds as it
/// draws, so it follows a theme laid since (an icon of the theme's own in Passenger's place among them): <see
/// cref="FileIconSet"/>'s shape and colour for its name, a folder the set draws nothing apart for as a folder (one the SDK
/// reads with its badge, open or not), drawn only while Settings shows plain folders' icons (<see cref="ShowPlainFolders"/>),
/// and a solution folder as a filled one, so it never passes for a folder on disk. A project takes its kind's icon (<see
/// cref="KindIcon"/>) in its project file's tint, wherever it is listed (<see cref="ProjectKey"/>). A file's or folder's key
/// names it beside Passenger's own key (<see cref="Named"/>), so the theme's VS Code file icon theme, where it lays one, draws
/// it by its name as it is drawn (<see cref="TryFind"/>, answering for the application through <see cref="NamedIcons"/>).</summary>
public static class FileIcons
{
    private const string FilePlace = "file:";
    private const string FolderPlace = "folder:";
    private const string OpenPlace = "open:";

    /// <summary>The resource the application holds while Settings shows a plain folder's chevron alone: its change has every
    /// icon drawn again.</summary>
    private static readonly object PlainFoldersHidden = new();

    /// <summary>The key of a row's icon: a file's and a folder's named by its name (<see cref="Named"/>).</summary>
    public static string KeyFor(ExplorerNode node) => KindIcon(node.ProjectKind) is { } kind
        ? kind
        : node.Kind switch
        {
            NodeKind.File => FileKey(node.Path ?? node.Name),
            NodeKind.Folder => Named(node.IsExpanded ? OpenPlace : FolderPlace, Look(node) is { } look ? Shaped(look) : node.SdkFolder switch
            {
                SdkFolder.Properties => IconKeys.PropertiesFolder,
                SdkFolder.WebRoot => IconKeys.WebRootFolder,
                _ => PlainFolder(node.IsExpanded),
            }, null, node.Name),
            NodeKind.SolutionFolder => node.IsExpanded ? IconKeys.SolutionFolderOpen : IconKeys.SolutionFolder,
            NodeKind.Framework => IconKeys.Reference,
            _ => Look(node) is { } look ? Shaped(look) : IconKeys.Explorer,
        };

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
    public static string FolderKey(string name, bool open) =>
        Named(open ? OpenPlace : FolderPlace, FileIconSet.ForFolder(name) is { } look ? Shaped(look) : PlainFolder(open), null, name);

    /// <summary>The key of a folder's icon's tint by its name.</summary>
    public static string FolderBrushKey(string name) => FileIconSet.ForFolder(name) is { } look ? Tint(look) : TokenKeys.File_Folder;

    /// <summary>The key of a file's icon by its name and its folder's.</summary>
    public static string FileKey(string path) =>
        Named(FilePlace, Shaped(FileIconSet.ForFile(Path.GetFileName(path))), Path.GetFileName(Path.GetDirectoryName(path)), Path.GetFileName(path));

    /// <summary>The icon a key names where it is drawn under a variant, found in the resources of <paramref name="owner"/>
    /// (the application): for a named file or folder, the VS Code file icon theme's laid for the variant
    /// (<see cref="IconThemeIcons"/>) where it names the file or folder, else Passenger's own where Passenger draws it apart,
    /// else the icon theme's for any file or folder, else Passenger's; a plain folder none while Settings hides plain
    /// folders' icons (found, drawing nothing). False for a key that names no file or folder.</summary>
    public static bool TryFind(IResourceHost owner, string key, ThemeVariant? variant, out object? icon)
    {
        icon = null;
        if (NamedKey.Of(key) is not { } named)
        {
            return false;
        }
        IconThemeIcons? set = variant is not null && owner.TryGetResource(IconThemeIcons.Key, variant, out object? laid) ? laid as IconThemeIcons : null;
        (IconPicture? theirs, bool own) = named.In(set);
        if (theirs is not null && own)
        {
            icon = theirs;
            return true;
        }
        if (named.Plain && named.Place != FilePlace && !ShowsPlainFolders(owner))
        {
            return true;
        }
        if (named.Plain && theirs is not null)
        {
            icon = theirs;
            return true;
        }
        return owner.TryGetResource(named.Own, variant, out icon);
    }

    /// <summary>A file's or folder's key: where it stands (<see cref="FilePlace"/>, <see cref="FolderPlace"/>,
    /// <see cref="OpenPlace"/>), Passenger's own key, its folder's name and its name, which neither holds a <c>/</c>.</summary>
    private static string Named(string place, string own, string? folder, string name) => $"{place}{own}:{folder}/{name}";

    /// <summary>A named key as read, kept for as long as the key it was read from, so an icon drawn again reads nothing; with
    /// the answer of the icon theme it was last found in.</summary>
    private sealed class NamedKey(string place, string own, string? folder, string name)
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, NamedKey> Read = [];
        private IconThemeIcons? foundIn;
        private (IconPicture?, bool) found;

        public string Place => place;

        public string Own => own;

        /// <summary>Whether Passenger draws it as any file or folder, nothing apart.</summary>
        public bool Plain { get; } = place == FilePlace ? own == Shaped(FileIconSet.Plain) : own == PlainFolder(place == OpenPlace);

        public static NamedKey? Of(string key)
        {
            string? place = key.StartsWith(FilePlace, StringComparison.Ordinal) ? FilePlace
                : key.StartsWith(FolderPlace, StringComparison.Ordinal) ? FolderPlace
                : key.StartsWith(OpenPlace, StringComparison.Ordinal) ? OpenPlace : null;
            if (place is null)
            {
                return null;
            }
            if (Read.TryGetValue(key, out NamedKey? named))
            {
                return named;
            }
            int colon = key.IndexOf(':', place.Length);
            int slash = colon < 0 ? -1 : key.IndexOf('/', colon + 1);
            if (colon < 0 || slash < 0)
            {
                return null;
            }
            named = new NamedKey(place, key[place.Length..colon], slash == colon + 1 ? null : key[(colon + 1)..slash], key[(slash + 1)..]);
            Read.AddOrUpdate(key, named);
            return named;
        }

        /// <summary>Its icon in an icon theme, and whether the theme names it; none for no theme.</summary>
        public (IconPicture? Icon, bool Named) In(IconThemeIcons? set)
        {
            if (set is null)
            {
                return (null, false);
            }
            if (!ReferenceEquals(set, foundIn))
            {
                found = place == FilePlace ? set.File(name, folder) : set.Folder(name, place == OpenPlace);
                foundIn = set;
            }
            return found;
        }
    }

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
