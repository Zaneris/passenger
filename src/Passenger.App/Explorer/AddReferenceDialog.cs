using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>A project the Add Reference dialog offers: checked while the project references it, and not offered when it
/// references the project itself, directly or through others (a reference back would be a cycle MSBuild refuses).</summary>
public sealed class ReferenceChoice(ProjectModel project, string where, bool chosen, bool circular) : Observable, ICheckChoice
{
    private bool isChosen = chosen;

    public string Path => Project.Path;

    public string Name => Project.Name;

    public ProjectModel Project { get; } = project;

    /// <summary>Where it is, and why it cannot be chosen when it cannot.</summary>
    public string Detail { get; } = circular ? $"{where} (references this project)" : where;

    public bool CanChoose { get; } = !circular;

    public bool IsChosen
    {
        get => isChosen;
        set => Set(ref isChosen, value && CanChoose);
    }
}

/// <summary>Add Reference for a project: the solution's other projects, each with a check box, those it references
/// already checked. Space checks or unchecks the row chosen, Enter is OK and Escape cancels; it closes with the
/// projects checked, or null when cancelled.</summary>
public sealed class AddReferenceDialog : IdeWindow
{
    private readonly ListBox list;

    public AddReferenceDialog(string project, IReadOnlyList<ReferenceChoice> choices)
    {
        Choices = choices;
        Title = "Add Reference";
        Width = DialogWidths.Form;
        Height = 420;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Enter takes the choices as the dialog's default button does.
        list = CheckRows.List(choices, Accept);
        list.SelectedItem = choices.FirstOrDefault(c => c.CanChoose);
        Border rows = new() { Classes = { StyleClasses.Framed }, Child = list };

        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Accept();
        TextBlock heading = new()
        {
            [DockPanel.DockProperty] = Dock.Top,
            Classes = { StyleClasses.PaneTitle },
            Text = $"Projects {project} references",
            Margin = new Thickness(0, 0, 0, Gaps.Wide),
        };
        Content = DialogFooter.Page(new DockPanel { [DialogFooter.InsetProperty] = true, Children = { heading, rows } }, DialogFooter.OkCancel(ok, () => Close(null)));
        Opened += (_, _) => Dispatcher.UIThread.Post(() => ListKeys.Hold(list), DispatcherPriority.Loaded);
    }

    public IReadOnlyList<ReferenceChoice> Choices { get; }

    /// <summary>Closes as OK does, with the projects checked.</summary>
    public void Accept() => Close((IReadOnlyList<string>)[.. Choices.Where(c => c.IsChosen).Select(c => c.Path)]);
}
