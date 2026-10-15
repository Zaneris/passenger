using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App.CSharp;
using Passenger.Languages.CSharp;

namespace Passenger.App.Settings;

/// <summary>Rider's Editor | General | Postfix Completion page, laid out as IntelliJ's: Enable postfix completion and Show
/// postfix templates in completion autopopup over the templates under their language, each with the check box that
/// turns it on or off, and beside them the chosen one's Description, Before and After. Each change goes back to the
/// settings being edited at once, the templates turned off kept by name.</summary>
public sealed class PostfixTemplatesPane : DockPanel
{
    private const string Language = "C#";

    private readonly Action<Func<AppSettings, AppSettings>> change;
    private readonly Dictionary<string, bool> turned;
    private readonly ListBox tree = new() { Classes = { "tree" } };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox before = Code();
    private readonly TextBox after = Code();
    private readonly Control about;

    public PostfixTemplatesPane(AppSettings current, Action<Func<AppSettings, AppSettings>> change)
    {
        turned = new(current.PostfixTemplates);
        this.change = change;
        EnableBox = new CheckBox { Content = "Enable postfix completion", IsChecked = current.PostfixCompletion };
        AutopopupBox = new CheckBox { Content = "Show postfix templates in completion autopopup", IsChecked = current.PostfixInCompletion, IsEnabled = current.PostfixCompletion };
        EnableBox.IsCheckedChanged += (_, _) =>
        {
            bool on = EnableBox.IsChecked == true;
            change(s => s with { PostfixCompletion = on });
            AutopopupBox.IsEnabled = on;
            tree.IsEnabled = on;
        };
        AutopopupBox.IsCheckedChanged += (_, _) => change(s => s with { PostfixInCompletion = AutopopupBox.IsChecked == true });
        tree.IsEnabled = current.PostfixCompletion;
        tree.ItemTemplate = new FuncDataTemplate<PostfixTemplate?>((template, _) => RowView(template), supportsRecycling: false);
        tree.ItemsSource = (PostfixTemplate?[])[null, .. PostfixTemplates.All];
        tree.SelectionChanged += (_, _) => Describe();

        Border framed = new() { Classes = { "framed" }, Child = tree };
        about = new StackPanel
        {
            Spacing = 6,
            Children = { new TextBlock { Text = "Description:" }, description, new TextBlock { Text = "Before:", Margin = new Thickness(0, 8, 0, 0) }, before,
                new TextBlock { Text = "After:", Margin = new Thickness(0, 8, 0, 0) }, after },
        };
        StackPanel options = new() { Spacing = 6, Margin = new Thickness(0, 0, 0, 10), Children = { EnableBox, AutopopupBox } };
        SetDock(options, Dock.Top);
        Children.Add(options);
        Children.Add(new SettingsColumns(framed, about, 300, 240, 440));
        tree.SelectedIndex = 1;
    }

    /// <summary>Rider's Enable postfix completion.</summary>
    public CheckBox EnableBox { get; }

    /// <summary>Rider's Show postfix templates in completion autopopup.</summary>
    public CheckBox AutopopupBox { get; }

    /// <summary>Chooses a postfix template in the list by name, scrolled into view; false when there is none.</summary>
    public bool Choose(string name)
    {
        if (PostfixTemplates.All.FirstOrDefault(t => t.Name == name) is not { } template)
        {
            return false;
        }
        tree.SelectedItem = template;
        tree.ScrollIntoView(template);
        return true;
    }

    /// <summary>A postfix template's check box, which turns it on or off.</summary>
    public CheckBox? EnabledBox(string name)
    {
        int at = PostfixTemplates.All.ToList().FindIndex(t => t.Name == name);
        return at < 0 ? null : (tree.ContainerFromIndex(at + 1) as ListBoxItem)?.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
    }

    private bool IsOn(PostfixTemplate template) => turned.GetValueOrDefault(template.Name, true);

    private void Turn(IEnumerable<PostfixTemplate> which, bool on)
    {
        foreach (PostfixTemplate template in which)
        {
            if (on)
            {
                turned.Remove(template.Name);
            }
            else
            {
                turned[template.Name] = false;
            }
        }
        Dictionary<string, bool> off = new(turned);
        change(s => s with { PostfixTemplates = off });
        int keep = tree.SelectedIndex;
        tree.ItemsSource = (PostfixTemplate?[])[null, .. PostfixTemplates.All];
        tree.SelectedIndex = keep;
    }

    /// <summary>The chosen template's description and what it does to an expression, as IntelliJ's Before and After.</summary>
    private void Describe()
    {
        about.IsVisible = tree.SelectedItem is PostfixTemplate;
        if (tree.SelectedItem is PostfixTemplate template)
        {
            description.Text = template.Description + (template.Statement ? ", where the expression is a whole statement." : ".");
            before.Text = $"expr.{template.Name}";
            after.Text = template.Example;
        }
    }

    private Control RowView(PostfixTemplate? template)
    {
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(template is null ? 0 : 20, 0, 0, 0) };
        if (template is null)
        {
            CheckBox all = new() { IsChecked = PostfixTemplates.All.All(IsOn) ? true : PostfixTemplates.All.Any(IsOn) ? null : false, VerticalAlignment = VerticalAlignment.Center };
            all.IsCheckedChanged += (_, _) => Turn(PostfixTemplates.All, all.IsChecked == true);
            line.Children.Add(all);
            line.Children.Add(new TextBlock { Text = Language, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            return line;
        }
        CheckBox on = new() { IsChecked = IsOn(template), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        on.IsCheckedChanged += (_, _) => Turn([template], on.IsChecked == true);
        TextBlock name = new() { Text = "." + template.Name, FontFamily = Fonts.Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        SetDock(on, Dock.Left);
        SetDock(name, Dock.Left);
        // The example takes what the row leaves, so a long one ends in an ellipsis rather than cut at the frame.
        return new DockPanel
        {
            Margin = line.Margin,
            Children = { on, name, new TextBlock { Text = template.Example, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } },
        };
    }

    private static TextBox Code() => new() { IsReadOnly = true, FontFamily = Fonts.Mono, FontSize = 12.5, Classes = { "search" }, Focusable = false };
}
