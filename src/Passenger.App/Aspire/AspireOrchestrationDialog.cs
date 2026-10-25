using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>Chooses the projects for one undoable orchestration change.</summary>
public sealed class AspireOrchestrationDialog : Window
{
    private readonly IReadOnlyList<(ProjectModel Project, CheckBox Check)> choices;

    public AspireOrchestrationDialog(string solutionName, IReadOnlyList<ProjectModel> projects)
    {
        Title = "Add Aspire Orchestration";
        Width = 520;
        Height = 480;
        MinWidth = 380;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        choices = [.. projects.Where(p => p.Exists && p.IsRunnable && !p.IsAspireHost && Path.GetExtension(p.Path) == ".csproj")
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p, new CheckBox { Content = p.Name }))];
        ServiceProjects = new ListBox
        {
            ItemsSource = choices.Select(c => c.Check).ToList(),
            MinHeight = 100,
        };
        List<ProjectModel?> hosts = [null, .. projects.Where(p => p.Exists && p.IsAspireHost)];
        List<ProjectModel?> defaults = [null, .. projects.Where(p => p.Exists && !p.IsAspireHost && !p.IsRunnable
            && !p.IsTestProject && Path.GetExtension(p.Path) == ".csproj")
            .OrderByDescending(p => p.Name.EndsWith("ServiceDefaults", StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Name)];
        AppHost = new ComboBox
        {
            ItemsSource = hosts.Select(p => p?.Name ?? $"New {solutionName}.AppHost").ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ServiceDefaults = new ComboBox
        {
            ItemsSource = defaults.Select(p => p?.Name ?? $"New {solutionName}.ServiceDefaults").ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Add = new Button { Content = "Add", IsDefault = true, IsEnabled = false, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        InstallTemplates = new Button { Content = "Install Aspire templates…" };
        Problem = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        foreach ((ProjectModel _, CheckBox check) in choices)
        {
            check.IsCheckedChanged += (_, _) => Add.IsEnabled = choices.Any(c => c.Check.IsChecked == true);
        }
        Add.Click += (_, _) => Close(new AspireOrchestrationChoice(
            [.. choices.Where(c => c.Check.IsChecked == true).Select(c => c.Project)],
            hosts[AppHost.SelectedIndex], defaults[ServiceDefaults.SelectedIndex]));
        cancel.Click += (_, _) => Close(null);
        StackPanel options = new()
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "AppHost" }, AppHost,
                new TextBlock { Text = "ServiceDefaults" }, ServiceDefaults,
                InstallTemplates, Problem,
            },
        };
        DockPanel.SetDock(options, Dock.Bottom);
        TextBlock heading = new() { Text = "Service projects", Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top);
        Border rows = new() { Classes = { "framed" }, Child = ServiceProjects, Margin = new Thickness(0, 0, 0, 12) };
        Content = DialogFooter.Page(new DockPanel
        {
            Margin = new Thickness(16),
            Children = { heading, options, rows },
        }, DialogFooter.Bar(DialogFooter.Of(Add, cancel)));
        if (choices.Count == 0)
        {
            Problem.Text = "No C# project runs as a service: test projects and AppHosts are not services";
        }
    }

    public ListBox ServiceProjects { get; }
    public ComboBox AppHost { get; }
    public ComboBox ServiceDefaults { get; }
    public Button Add { get; }
    public Button InstallTemplates { get; }
    public TextBlock Problem { get; }
}
