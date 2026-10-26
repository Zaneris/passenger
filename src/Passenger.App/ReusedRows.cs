using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>A list whose row is its container's own template, made once with the container and handed each item the list
/// shows there as its DataContext, so scrolling makes no row again: a list clears an item template's row from its
/// container as the item scrolls away and builds another for the next. A row given here shows whatever its DataContext
/// is, and follows its item's changes only while it is shown (<see cref="Showing{T}"/>).</summary>
public static class ReusedRows
{
    public static void Use(ListBox list, Func<Control> row) => Contain<ListBoxItem>(list, row, foreground: false);

    /// <summary>A drop-down's choices as a list's rows, and its choice shown closed in one more row from <paramref
    /// name="row"/>, kept for the box and handed each choice made; the box shows nothing while none is chosen.</summary>
    public static void Use(ComboBox box, Func<Control> row)
    {
        Contain<ComboBoxItem>(box, row, foreground: true);
        box.SelectionBoxItemTemplate = new Chosen(row);
    }

    /// <summary>A strip of tabs, or any items control whose items stand in plain presenters: each item's row is made once,
    /// the first time a presenter shows it, and handed it as its DataContext.</summary>
    public static void Use(ItemsControl strip, Func<Control> tab) => strip.ItemTemplate = new Kept(tab);

    /// <summary>A list's containers given a template of the list's own whose presenter holds a row made once, standing
    /// where the theme's template has its presenter (the drop-down's binding its foreground too, as the theme's does).</summary>
    private static void Contain<TContainer>(ItemsControl list, Func<Control> row, bool foreground)
        where TContainer : ContentControl
    {
        Style style = new(x => x.OfType<TContainer>());
        style.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<TContainer>((_, _) =>
        {
            ContentPresenter presenter = new()
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
            };
            if (foreground)
            {
                presenter[!ContentPresenter.ForegroundProperty] = new TemplateBinding(TemplatedControl.ForegroundProperty);
            }
            return presenter;
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

    /// <summary>A strip's rows, one made for each item the first time a presenter shows it and handed back each time
    /// after, a presenter attached again among them (which asks for its row anew).</summary>
    private sealed class Kept(Func<Control> make) : IRecyclingDataTemplate
    {
        private readonly ConditionalWeakTable<object, Control> rows = [];

        public Control? Build(object? param) => Build(param, null);

        public Control? Build(object? data, Control? existing)
        {
            if (existing is not null || data is null)
            {
                return existing;
            }
            if (!rows.TryGetValue(data, out Control? row) || !Free(row, data))
            {
                row = make();
                rows.AddOrUpdate(data, row);
            }
            return row;
        }

        public bool Match(object? data) => data is not null;
    }

    /// <summary>A drop-down's closed box: one row kept for it, made again only once the box's template is made anew, and
    /// none while nothing is chosen.</summary>
    private sealed class Chosen(Func<Control> make) : IRecyclingDataTemplate
    {
        private Control? row;

        public Control? Build(object? param) => Build(param, null);

        public Control? Build(object? data, Control? existing) => existing ?? (row is not null && Free(row, data) ? row : row = make());

        public bool Match(object? data) => data is not null;
    }

    /// <summary>Whether a row kept can be handed to the presenter asking for it to show an item: it is in no presenter, or
    /// in one in the tree showing that item, which is the one asking.</summary>
    private static bool Free(Control row, object? data) =>
        row.GetVisualParent() is not { } parent || (parent is ContentPresenter { Content: { } shown } && ((ILogical)parent).IsAttachedToLogicalTree && Equals(shown, data));

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
