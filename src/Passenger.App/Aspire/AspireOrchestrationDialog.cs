using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>Chooses the projects for one undoable orchestration change.</summary>
public sealed class AspireOrchestrationDialog : IdeWindow
{
    private readonly IReadOnlyList<CheckChoice> choices;

    public AspireOrchestrationDialog(string solutionName, IReadOnlyList<ProjectModel> projects)
    {
        FormRows.Scope(this);
        Title = "Add Aspire Orchestration";
        Width = DialogWidths.Form;
        Height = 480;
        MinWidth = 380;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        choices = [.. projects.Where(p => p.Exists && p.IsRunnable && !p.IsAspireHost && Path.GetExtension(p.Path) == ".csproj")
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new CheckChoice(p.Name, string.Empty, false, p))];
        List<ProjectModel?> hosts = [null, .. projects.Where(p => p.Exists && p.IsAspireHost)];
        List<ProjectModel?> defaults = [null, .. projects.Where(p => p.Exists && !p.IsAspireHost && !p.IsRunnable
            && !p.IsTestProject && Path.GetExtension(p.Path) == ".csproj")
            .OrderByDescending(p => p.Name.EndsWith("ServiceDefaults", StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Name)];
        AppHost = new FormDropDown
        {
            ItemsSource = hosts.Select(p => p?.Name ?? $"New {solutionName}.AppHost").ToList(),
            SelectedIndex = 0,
        };
        ServiceDefaults = new FormDropDown
        {
            ItemsSource = defaults.Select(p => p?.Name ?? $"New {solutionName}.ServiceDefaults").ToList(),
            SelectedIndex = 0,
        };
        Add = DialogFooter.Default("Add");
        Add.IsEnabled = false;
        InstallTemplates = new Button { Content = "Install Aspire templates…" };
        Problem = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        foreach (CheckChoice choice in choices)
        {
            choice.PropertyChanged += (_, _) => Add.IsEnabled = choices.Any(c => c.IsChosen);
        }
        Add.Click += (_, _) => Close(new AspireOrchestrationChoice(
            [.. choices.Where(c => c.IsChosen).Select(c => (ProjectModel)c.Value)],
            hosts[AppHost.SelectedIndex], defaults[ServiceDefaults.SelectedIndex]));
        ServiceProjects = CheckRows.List(choices);
        StackPanel options = new()
        {
            Spacing = FormRows.Between,
            Margin = new Thickness(0, FormRows.Between, 0, 0),
            Children =
            {
                FormRows.Of("AppHost", AppHost),
                FormRows.Of("ServiceDefaults", ServiceDefaults),
                FormRows.Of(null, InstallTemplates),
            },
        };
        if (choices.Count == 0)
        {
            options.Children.Add(FormRows.Of(null, Problem));
        }
        DockPanel.SetDock(options, Dock.Bottom);
        Border rows = new() { Classes = { StyleClasses.Framed }, Child = ServiceProjects, MinHeight = 100 };
        Content = DialogFooter.Page(new DockPanel
        {
            [DialogFooter.InsetProperty] = true,
            Children = { options, FormRows.Of("Service projects", rows, top: true) },
        }, DialogFooter.OkCancel(Add, () => Close(null)));
        if (choices.Count == 0)
        {
            Problem.Text = "No C# project runs as a service: test projects and AppHosts are not services";
        }
    }

    /// <summary>The projects that run as services, each checked to be orchestrated.</summary>
    public ListBox ServiceProjects { get; }

    /// <summary>What <see cref="ServiceProjects"/> offers, by each project's name.</summary>
    public IReadOnlyList<CheckChoice> Services => choices;
    public ComboBox AppHost { get; }
    public ComboBox ServiceDefaults { get; }
    public Button Add { get; }
    public Button InstallTemplates { get; }
    public TextBlock Problem { get; }
}
