using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.Aspire;

public sealed class AspireServiceDialog : IdeWindow
{
    public AspireServiceDialog(IReadOnlyList<AspireSelection> services)
    {
        Title = "Profile Aspire Service";
        Width = DialogWidths.Form;
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
        TextBlock heading = new() { Text = "Choose a running .NET service", Classes = { StyleClasses.Muted }, Margin = new Thickness(0, 0, 0, Gaps.Wide) };
        DockPanel page = new() { [DialogFooter.InsetProperty] = true };
        DockPanel.SetDock(heading, Dock.Top);
        page.Children.Add(heading);
        page.Children.Add(new Border { Classes = { StyleClasses.Framed }, Child = Services });
        Content = DialogFooter.Page(page, DialogFooter.OkCancel(Profile, () => Close(null)));
        Services.SelectedIndex = services.Count > 0 ? 0 : -1;
    }

    public ListBox Services { get; }
    public Button Profile { get; }
}
