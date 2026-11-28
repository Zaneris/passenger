using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.Explorer;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>A project as a list or a drop-down names it wherever one is chosen (Find in Files' scope, a run configuration's
/// project, Add Reference and the other check rows of projects, the NuGet and Migrations windows'): the icon of what it
/// builds in its project file's tint, then its name, cut short with an ellipsis where it has no more room, a screen reader
/// hearing its kind after it, as the Solution view draws and names its row (<see cref="FileIcons.ProjectKey"/>,
/// <see cref="ProjectKinds.Said"/>); a row it sits in is named by it. Made once with its row and shown another project, or
/// plain words, in place.</summary>
public sealed class ProjectLabel : DockPanel
{
    private readonly RowIcon icon = new() { Margin = new Thickness(0, 0, Gaps.Small, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };

    public ProjectLabel()
    {
        SetDock(icon, Dock.Left);
        Children.Add(icon);
        Children.Add(name);
    }

    /// <summary>The words it shows.</summary>
    public string? Text => name.Text;

    /// <summary>Shows a project by its name, or where <paramref name="item"/> is none, the words listed in its place.</summary>
    public void Show(object? item) => Show(item as ProjectModel, item is ProjectModel project ? project.Name : item?.ToString() ?? string.Empty);

    /// <summary>Shows words, a project's icon before them where one is given, which a screen reader hears its kind after.</summary>
    public void Show(ProjectModel? project, string words)
    {
        name.Text = words;
        icon.IsVisible = project is not null;
        if (project is null)
        {
            AutomationProperties.SetName(this, words);
            return;
        }
        ProjectKind kind = ProjectKinds.Of(project);
        icon.DataKey = FileIcons.ProjectKey(project.Path, kind);
        icon.ForegroundKey = FileIcons.FileBrushKey(project.Path);
        AutomationProperties.SetName(this, ProjectKinds.Said(words, kind));
    }
}
