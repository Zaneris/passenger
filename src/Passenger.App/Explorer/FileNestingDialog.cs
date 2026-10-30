using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Explorer;

/// <summary>File Nesting, from the Solution view's options: its check box, "Show files with the same names as nested",
/// over the nesting rules, each parent file suffix with the child file suffixes its files take under them (greyed while
/// the box is clear). OK hands the box back.</summary>
public sealed class FileNestingDialog : IdeWindow
{
    public FileNestingDialog(bool nest)
    {
        Title = "File Nesting";
        Width = 720;
        Height = 480;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NestBox = new CheckBox { Content = "Show files with the same names as nested", IsChecked = nest };

        Grid table = new() { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(8, 4) };
        void Row(string parent, string children, bool heading = false)
        {
            int row = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach ((string text, int column) in (IEnumerable<(string, int)>)[(parent, 0), (children, 1)])
            {
                TextBlock cell = new()
                {
                    Text = text,
                    Margin = new Thickness(0, 3, 12, 3),
                    TextWrapping = TextWrapping.WrapWithOverflow,
                    FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal,
                    [Grid.RowProperty] = row,
                    [Grid.ColumnProperty] = column,
                };
                if (heading)
                {
                    cell.Classes.Add("muted");
                }
                table.Children.Add(cell);
            }
        }
        Row("Parent File Suffix", "Child File Suffix", heading: true);
        foreach (IGrouping<string, (string Parent, string Child)> rule in FileNesting.Defaults.GroupBy(r => r.Parent, StringComparer.OrdinalIgnoreCase))
        {
            Row(rule.Key, string.Join("; ", rule.Select(r => r.Child)));
        }
        Row(FileNesting.EnvironmentParent, FileNesting.EnvironmentChildren);
        Rules = table;
        Border framed = new() { Classes = { "framed" }, Child = new ScrollViewer { Content = table } };
        Rules.IsEnabled = nest;
        NestBox.IsCheckedChanged += (_, _) => Rules.IsEnabled = NestBox.IsChecked == true;

        DockPanel body = new() { Margin = new Thickness(16, 14, 16, 8) };
        TextBlock title = new() { Text = "Nesting rules:", Margin = new Thickness(0, 12, 0, 6) };
        DockPanel.SetDock(NestBox, Dock.Top);
        DockPanel.SetDock(title, Dock.Top);
        body.Children.Add(NestBox);
        body.Children.Add(title);
        body.Children.Add(framed);

        OkButton = new Button { Content = "OK", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        OkButton.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        StackPanel buttons = DialogFooter.Of(OkButton, cancel);
        Border bottom = DialogFooter.Bar(buttons);
        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Border page = new() { Child = root };
        Content = page;
    }

    /// <summary>"Show files with the same names as nested".</summary>
    public CheckBox NestBox { get; }

    /// <summary>The rules, shown greyed while the box is clear.</summary>
    public Control Rules { get; }

    public Button OkButton { get; }

    public bool Nest => NestBox.IsChecked == true;
}
