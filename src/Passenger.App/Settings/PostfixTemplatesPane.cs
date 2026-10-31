using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.Editor;
using Passenger.Languages.CSharp;

namespace Passenger.App.Settings;

/// <summary>The Editor | General | Postfix Completion page: Enable postfix completion and Show postfix templates in
/// completion autopopup over the templates under their language, each with the check box that turns it on or off, and
/// beside them the chosen one's Description, Before and After. Each change goes back to the settings being edited at
/// once, the templates turned off kept by name.</summary>
public sealed class PostfixTemplatesPane : DockPanel
{
    private const string Language = "C#";

    private readonly Action<Func<AppSettings, AppSettings>> change;
    private readonly Dictionary<string, bool> turned;
    private readonly ListBox tree = new() { Classes = { StyleClasses.Tree } };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox before = Code();
    private readonly TextBox after = Code();
    private readonly Control about;

    public PostfixTemplatesPane(AppSettings current, Action<Func<AppSettings, AppSettings>> change)
    {
        turned = new(current.PostfixTemplates);
        this.change = change;
        Height = 520;
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
        ReusedRows.Use(tree, RowView);
        tree.ItemsSource = (object[])[Language, .. PostfixTemplates.All];
        tree.SelectionChanged += (_, _) => Describe();

        Border framed = new() { Classes = { StyleClasses.Framed }, Child = tree, Width = 300 };
        about = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(16, 0, 0, 0),
            Children = { new TextBlock { Text = "Description:" }, description, new TextBlock { Text = "Before:", Margin = new Thickness(0, 8, 0, 0) }, before,
                new TextBlock { Text = "After:", Margin = new Thickness(0, 8, 0, 0) }, after },
        };
        StackPanel options = new() { Spacing = 6, Margin = new Thickness(0, 0, 0, 10), Children = { EnableBox, AutopopupBox } };
        SetDock(options, Dock.Top);
        SetDock(framed, Dock.Left);
        Children.Add(options);
        Children.Add(framed);
        Children.Add(about);
        tree.SelectedIndex = 1;
    }

    /// <summary>Enable postfix completion.</summary>
    public CheckBox EnableBox { get; }

    /// <summary>Show postfix templates in completion autopopup.</summary>
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
        return at < 0 ? null : (tree.ContainerFromIndex(at + 1) as ListBoxItem)?.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(box => box.IsEffectivelyVisible);
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
        tree.ItemsSource = (object[])[Language, .. PostfixTemplates.All];
        tree.SelectedIndex = keep;
    }

    /// <summary>The chosen template's description and what it does to an expression, Before and After.</summary>
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

    /// <summary>A row for whichever item the list hands its container: the language's, its box turning every template on
    /// or off (neither while some are on), or a template's, its box turning it on or off, its name and its example.</summary>
    private Control RowView()
    {
        CheckBox all = new() { VerticalAlignment = VerticalAlignment.Center };
        StackPanel language = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { all, new TextBlock { Text = Language, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } },
        };
        CheckBox on = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        TextBlock name = new() { FontFamily = CodeEditor.CodeFont, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        TextBlock example = new() { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        SetDock(on, Dock.Left);
        SetDock(name, Dock.Left);
        // The example takes what the row leaves, so a long one ends in an ellipsis rather than cut at the frame.
        DockPanel line = new() { Margin = new Thickness(20, 0, 0, 0), Children = { on, name, example } };
        Panel row = new() { Children = { language, line } };
        bool setting = false;
        all.IsCheckedChanged += (_, _) =>
        {
            if (!setting)
            {
                Turn(PostfixTemplates.All, all.IsChecked == true);
            }
        };
        on.IsCheckedChanged += (_, _) =>
        {
            if (!setting && row.DataContext is PostfixTemplate template)
            {
                Turn([template], on.IsChecked == true);
            }
        };
        return row.Showing<object>(item =>
        {
            setting = true;
            language.IsVisible = item is not PostfixTemplate;
            line.IsVisible = item is PostfixTemplate;
            if (item is PostfixTemplate template)
            {
                on.IsChecked = IsOn(template);
                name.Text = "." + template.Name;
                example.Text = template.Example;
            }
            else
            {
                all.IsChecked = PostfixTemplates.All.All(IsOn) ? true : PostfixTemplates.All.Any(IsOn) ? null : false;
            }
            setting = false;
        });
    }

    private static TextBox Code() => new() { IsReadOnly = true, FontFamily = CodeEditor.CodeFont, FontSize = 12.5, Classes = { StyleClasses.Search }, Focusable = false };
}
