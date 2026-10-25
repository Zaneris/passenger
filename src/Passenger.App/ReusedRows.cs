using System.ComponentModel;
using Avalonia;
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
/// is, and follows its item's changes only while it is shown (<see cref="Showing{T}"/>).</summary>
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

    /// <summary>Has a row made once show each item its list hands it: <paramref name="show"/> is given the item as it
    /// becomes the row's DataContext, as the row comes back into view after its item changed unseen, and at each change
    /// the item tells of while the row is shown, so a row the list let go of holds nothing it showed.</summary>
    public static Control Showing<T>(this Control row, Action<T> show)
        where T : class
    {
        _ = new Shown<T>(row, show);
        return row;
    }

    /// <summary>Binds a row's property to the theme's resource of a key, anew only when the key is not the one it holds
    /// (<paramref name="bound"/>), so a row handed another item makes no binding for the same key; a null key clears it.</summary>
    public static void Resource(this Control control, AvaloniaProperty property, ref string? bound, string? key)
    {
        if (key == bound)
        {
            return;
        }
        bound = key;
        if (key is null)
        {
            control.ClearValue(property);
        }
        else
        {
            control.Bind(property, control.GetResourceObservable(key));
        }
    }

    private sealed class Shown<T>
        where T : class
    {
        private readonly Control row;
        private readonly Action<T> show;
        private INotifyPropertyChanged? followed;
        private bool attached;
        private bool stale = true;

        public Shown(Control row, Action<T> show)
        {
            this.row = row;
            this.show = show;
            row.DataContextChanged += (_, _) => Show();
            row.AttachedToVisualTree += (_, _) =>
            {
                attached = true;
                if (stale)
                {
                    Show();
                }
                Follow();
            };
            row.DetachedFromVisualTree += (_, _) =>
            {
                attached = false;
                stale = true;
                Follow();
            };
        }

        private void Show()
        {
            if (row.DataContext is T item)
            {
                stale = false;
                show(item);
            }
            Follow();
        }

        private void Follow()
        {
            INotifyPropertyChanged? following = attached ? row.DataContext as INotifyPropertyChanged : null;
            if (following == followed)
            {
                return;
            }
            if (followed is not null)
            {
                followed.PropertyChanged -= OnChanged;
            }
            followed = following;
            if (following is not null)
            {
                following.PropertyChanged += OnChanged;
            }
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (row.DataContext is T item)
            {
                show(item);
            }
        }
    }
}
