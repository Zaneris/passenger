using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.Aspire;

public sealed class AspireServiceDialog : IdeWindow
{
    public AspireServiceDialog(IReadOnlyList<AspireSelection> services)
    {
        Title = "Profile Aspire Service";
        Width = 540;
        Height = 360;
        MinWidth = 400;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Services = new ListBox { ItemsSource = services };
        ReusedRows.Use(Services, () =>
        {
            TextBlock name = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            return name.Showing<AspireSelection>(selection => name.Text = selection.Resource.ToString());
        });
        Profile = DialogFooter.Default("Profile");
        Profile.IsEnabled = false;
        Services.SelectionChanged += (_, _) => Profile.IsEnabled = Services.SelectedItem is AspireSelection;
        Profile.Click += (_, _) => Close(Services.SelectedItem as AspireSelection);
        StackPanel heading = new()
        {
            Spacing = 8,
            Margin = new Thickness(20, 16),
            Children = { new TextBlock { Text = "Choose a running .NET service", Classes = { "muted" } } },
        };
        DockPanel page = new();
        DockPanel.SetDock(heading, Dock.Top);
        page.Children.Add(heading);
        page.Children.Add(new Border { Classes = { "framed" }, Margin = new Thickness(20, 0, 20, 16), Child = Services });
        Content = DialogFooter.Page(page, DialogFooter.OkCancel(Profile, () => Close(null)));
        Services.SelectedIndex = services.Count > 0 ? 0 : -1;
    }

    public ListBox Services { get; }
    public Button Profile { get; }
}
