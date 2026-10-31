using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>A Details tab's page: headings over framed tables of names and what each is, every table's names in one
/// column as wide as the widest of any so the values line up down the page, and in their place a word of why there is
/// nothing to show (being read, a failure). Shown again with nothing it shows changed, its tables stay as they are, so a
/// click on a link in them lands however often what they show is read again.</summary>
public class DetailsTables : DockPanel
{
    private readonly StackPanel sections = new() { Margin = new Thickness(8, 0, 8, 8) };
    private readonly TextBlock said = new() { Classes = { "emptyText" } };

    /// <summary>The tables a <see cref="Show"/> is making, and what they show in words; null outside one.</summary>
    private List<Control>? making;
    private System.Text.StringBuilder? words;

    /// <summary>What the tables shown show, in words; null while none are.</summary>
    private string? shownWords;

    /// <summary>The widest a name may be: the column of names takes at most its share of a narrow page, so the values keep
    /// room to be read rather than wrapping a letter or two to a line.</summary>
    private double namesMost = NamesMost;

    private const double NamesMost = 260;

    public DetailsTables()
    {
        Grid.SetIsSharedSizeScope(sections, true);
        Children.Add(new Panel { Children = { new ScrollViewer { Content = sections }, said } });
        said.IsVisible = false;
    }

    /// <summary>What is said in the tables' place, or empty while tables (or nothing) are shown.</summary>
    public string Said => said.IsVisible ? said.Text ?? string.Empty : string.Empty;

    /// <summary>Takes the tables away and says why in their place; null says nothing.</summary>
    public void Say(string? why)
    {
        sections.Children.Clear();
        shownWords = null;
        said.Text = why ?? string.Empty;
        said.IsVisible = why is not null;
    }

    /// <summary>Shows the tables <paramref name="fill"/> makes by its <see cref="Section"/>s in place of those shown, unless
    /// they show what those show, which then stay (a link's press under way kept).</summary>
    public void Show(Action fill)
    {
        making = [];
        words = new System.Text.StringBuilder();
        try
        {
            fill();
            said.IsVisible = false;
            string now = words.ToString();
            if (now != shownWords)
            {
                sections.Children.Clear();
                foreach (Control made in making)
                {
                    sections.Children.Add(made);
                }
                shownWords = now;
            }
        }
        finally
        {
            making = null;
            words = null;
        }
    }

    /// <summary>A heading over a framed table of names and what each is, the names in the page's one column of names.</summary>
    public void Section(string title, IReadOnlyList<(string Name, Control Value)> rows)
    {
        words?.Append(title).Append('\u0001');
        TextBlock heading = new() { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        Grid table = new()
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "DetailsNames" }, new ColumnDefinition(GridLength.Star) },
            Margin = new Thickness(8, 0),
        };
        for (int i = 0; i < rows.Count; i++)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock name = new()
            {
                Text = rows[i].Name, Classes = { "muted", "detailsName" }, MaxWidth = namesMost, MinHeight = ListRow.Height, Margin = new Thickness(0, 0, 16, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0, 4),
                [Grid.RowProperty] = i,
            };
            ToolTip.SetTip(name, rows[i].Name);
            Control value = rows[i].Value;
            words?.Append(rows[i].Name).Append('\u0002').Append(Described(value)).Append('\u0003');
            value[Grid.RowProperty] = i;
            value[Grid.ColumnProperty] = 1;
            table.Children.Add(name);
            table.Children.Add(value);
        }
        Border framed = new() { Classes = { "framed" }, Padding = new Thickness(0, 2), Child = table };
        if (making is { } made)
        {
            made.Add(heading);
            made.Add(framed);
        }
        else
        {
            sections.Children.Add(heading);
            sections.Children.Add(framed);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // A table's page's margins and frame (34) and a name's own margin (16) aside, the names take at most two fifths.
        double most = double.IsInfinity(availableSize.Width) ? NamesMost : Math.Clamp((availableSize.Width - 34) * 0.4 - 16, 48, NamesMost);
        if (most != namesMost)
        {
            namesMost = most;
            foreach (TextBlock name in sections.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("detailsName")))
            {
                name.MaxWidth = most;
            }
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>What a value shows in words: its texts, a link's words and its tip.</summary>
    private static string Described(Control value) => string.Join('\u0004', value.GetLogicalDescendants().Prepend((ILogical)value)
        .SelectMany(part => (string?[])[part switch { TextBlock text => text.Text, ContentControl { Content: string content } => content, _ => null },
            part is Control shown ? ToolTip.GetTip(shown) as string : null, (part as Control)?.Name])
        .OfType<string>());

    /// <summary>A value to read and copy, wrapped rather than cut; a program's words in the editor's font.</summary>
    public static SelectableTextBlock Words(string text, bool code = false, string? brush = null)
    {
        SelectableTextBlock words = new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, MinHeight = ListRow.Height, Padding = new Thickness(0, 4), VerticalAlignment = VerticalAlignment.Top,
        };
        if (code)
        {
            words.FontFamily = CodeEditor.CodeFont;
            words.FontSize = 12;
        }
        if (brush is not null)
        {
            words.Bind(TextBlock.ForegroundProperty, words.GetResourceObservable(brush));
        }
        return words;
    }

    /// <summary>A value that opens something (an address in the browser, a file in the editor), as a named link.</summary>
    public static Button Link(string words, string tip, Action open, string name)
    {
        Button link = new()
        {
            Classes = { "link" }, Content = words, Name = name, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, MinHeight = ListRow.Height,
        };
        ToolTip.SetTip(link, tip);
        link.Click += (_, _) => open();
        return link;
    }

    /// <summary>A state in words beside its health's tag, as the lists show it.</summary>
    public static Control State(string state, string? health)
    {
        StackPanel shown = new() { Orientation = Orientation.Horizontal, Spacing = 8, MinHeight = ListRow.Height };
        shown.Children.Add(new TextBlock { Text = state, VerticalAlignment = VerticalAlignment.Center });
        if (health is not null)
        {
            shown.Children.Add(Containers.ContainerDetailsPane.HealthTag(health));
        }
        return shown;
    }
}
