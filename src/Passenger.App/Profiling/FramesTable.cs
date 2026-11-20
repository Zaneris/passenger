using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App.ToolWindows;
using Passenger.Editor;

namespace Passenger.App.Profiling;

/// <summary>What the two Frames pages (an Avalonia app's, a Godot game's) are built of: the frames' chart in its strip under a
/// legend of its colours, the tree table under them, each row a chevron and a name with its numbers in their columns, muted
/// where they are nothing, and what the page says in the table's place while it shows no frames.</summary>
internal static class FramesTable
{
    /// <summary>The chart, scrolled sideways where its frames are wider than the page.</summary>
    public static ScrollViewer Strip(FrameChart chart) => new()
    {
        Content = chart,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>The chart's colours named, each a swatch of its token's brush; the muted one, what the rest leave, faint.</summary>
    public static StackPanel Legend(IEnumerable<(string Label, string Brush)> parts)
    {
        StackPanel legend = new() { [DockPanel.DockProperty] = Dock.Left, Orientation = Orientation.Horizontal, Spacing = Gaps.Wide, Margin = new Thickness(Gaps.Wide, Gaps.Hair, Gaps.Wide, 0) };
        foreach ((string label, string brush) in parts)
        {
            Border swatch = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Opacity = brush == TokenKeys.TextMuted ? FramePart.Faint : 1 };
            swatch.Bind(Border.BackgroundProperty, swatch.GetResourceObservable(brush));
            legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, Children = { swatch, Muted(label) } });
        }
        return legend;
    }

    /// <summary>The tree table of <paramref name="rows"/>, each made once by <paramref name="row"/> and stretched to the
    /// table's width so its numbers stand under their titles.</summary>
    public static TreeList Table(IEnumerable rows, Func<Control> row)
    {
        TreeList table = new() { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(table, row);
        table.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        });
        return table;
    }

    /// <summary>What the page says in its table's place while it shows no frames.</summary>
    public static EmptyState Placeholder(string text) => new(text);

    /// <summary>A row's chevron and name, with its other words (<paramref name="beside"/>) after the name; a press on the
    /// chevron toggles the row's <typeparamref name="T"/>, which the row holds as its data.</summary>
    public static (StackPanel Named, RowExpander Chevron, TextBlock Name) Named<T>(Action<T> toggle, params Control[] beside)
    {
        RowExpander chevron = RowExpander.Of(toggle);
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        StackPanel named = new() { Orientation = Orientation.Horizontal, Children = { chevron, name } };
        named.Children.AddRange(beside);
        return (named, chevron, name);
    }

    /// <summary>A number's cell, right-aligned in the code font.</summary>
    public static TextBlock Number() =>
        new TextBlock { Classes = { StyleClasses.Code }, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A number in its cell, muted where it is nothing.</summary>
    public static void Put(TextBlock cell, string text)
    {
        cell.Text = text;
        cell.Classes.Set(StyleClasses.Muted, text is "0" or "0.0 ms");
    }

    /// <summary>The pages' small muted words.</summary>
    public static TextBlock Muted(string text)
    {
        return MutedText.Caption(text);
    }
}
