using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Explorer;

/// <summary>File Nesting, from the Solution view's options: its check box, "Show files with the same names as nested",
/// over the nesting rules, each parent file suffix with the child file suffixes its files take under them (greyed while
/// the box is clear). OK hands the box back.</summary>
public sealed class FileNestingDialog : IdeWindow
{
    public FileNestingDialog(bool nest)
    {
        ShownWords.Set(this, TitleProperty, ExplorerWords.FileNesting.TrimmedEnd('…'));
        Width = DialogWidths.Wide;
        Height = 480;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NestBox = new CheckBox { [ShownWords.ContentProperty] = ExplorerWords.NestSameNames, IsChecked = nest };

        Grid table = new() { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(Gaps.Normal, Gaps.Tight) };
        void Row(Words parent, Words children, bool heading = false)
        {
            int row = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach ((Words text, int column) in (IEnumerable<(Words, int)>)[(parent, 0), (children, 1)])
            {
                TextBlock cell = new()
                {
                    [ShownWords.TextProperty] = text,
                    Margin = new Thickness(0, Gaps.Hair, Gaps.Wide, Gaps.Hair),
                    TextWrapping = TextWrapping.Wrap,
                    [Grid.RowProperty] = row,
                    [Grid.ColumnProperty] = column,
                };
                if (heading)
                {
                    cell.Classes.Add(StyleClasses.Muted);
                    cell.Classes.Add(StyleClasses.Strong);
                }
                table.Children.Add(cell);
            }
        }
        Row(ExplorerWords.ParentSuffix, ExplorerWords.ChildSuffix, heading: true);
        foreach (IGrouping<string, (string Parent, string Child)> rule in FileNesting.Defaults.GroupBy(r => r.Parent, StringComparer.OrdinalIgnoreCase))
        {
            Row(Words.Verbatim(rule.Key), Words.Verbatim(string.Join("; ", rule.Select(r => r.Child))));
        }
        Row(Words.Verbatim(FileNesting.EnvironmentParent), Words.Verbatim(FileNesting.EnvironmentChildren));
        Row(Words.Verbatim(FileNesting.ComposeParent), Words.Verbatim(FileNesting.ComposeChildren));
        Rules = table;
        Border framed = new() { Classes = { StyleClasses.Framed }, Child = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        Rules.IsEnabled = nest;
        NestBox.IsCheckedChanged += (_, _) => Rules.IsEnabled = NestBox.IsChecked == true;

        DockPanel body = new() { [DialogFooter.InsetProperty] = true };
        TextBlock title = new() { [ShownWords.TextProperty] = ExplorerWords.NestingRules, Margin = new Thickness(0, Gaps.Wide, 0, Gaps.Small) };
        DockPanel.SetDock(NestBox, Dock.Top);
        DockPanel.SetDock(title, Dock.Top);
        body.Children.Add(NestBox);
        body.Children.Add(title);
        body.Children.Add(framed);

        OkButton = DialogFooter.Default();
        OkButton.Click += (_, _) => Close(true);
        Content = DialogFooter.Page(body, DialogFooter.OkCancel(OkButton, () => Close(false)));
    }

    /// <summary>"Show files with the same names as nested".</summary>
    public CheckBox NestBox { get; }

    /// <summary>The rules, shown greyed while the box is clear.</summary>
    public Control Rules { get; }

    public Button OkButton { get; }

    public bool Nest => NestBox.IsChecked == true;
}
