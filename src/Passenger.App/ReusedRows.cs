using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Styling;

namespace Passenger.App;

/// <summary>A list whose row is its container's own template, made once with the container and handed each item the list
/// shows there as its DataContext, so scrolling makes no row again: a list clears an item template's row from its
/// container as the item scrolls away and builds another for the next. A row given here shows whatever its DataContext
/// is, and follows its item's changes only while it is shown.</summary>
public static class ReusedRows
{
    public static void Use(ListBox list, Func<Control> row)
    {
        Style style = new(x => x.OfType<ListBoxItem>());
        style.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((_, _) => new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            [!ContentPresenter.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
            [!ContentPresenter.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty),
            [!ContentPresenter.BorderThicknessProperty] = new TemplateBinding(TemplatedControl.BorderThicknessProperty),
            [!ContentPresenter.CornerRadiusProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty),
            [!ContentPresenter.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
            [!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty),
            [!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty),
            Content = row(),
        })));
        list.Styles.Add(style);
    }
}
