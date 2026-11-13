using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.Settings;

/// <summary>A Settings page of a list and what it shows of the item chosen in it (Live Templates, Postfix Completion): the
/// list down the left beside it, and on a page as narrow as stands the options' labels above their editors
/// (<see cref="OptionRow.Stacks"/>), the list above it, the page growing by the list's height.</summary>
public abstract class ListPane : DockPanel
{
    /// <summary>The list's height above what it shows.</summary>
    private const double ListHeight = 200;

    private const double Gap = 16;

    private Control? list;
    private Control? shown;
    private double width;
    private double height;

    /// <summary>Lays the list, <paramref name="listWidth"/> wide beside what it shows, in a page
    /// <paramref name="pageHeight"/> tall.</summary>
    protected void Lay(Control list, Control shown, double listWidth, double pageHeight)
    {
        this.list = list;
        this.shown = shown;
        width = listWidth;
        height = pageHeight;
        Beside(true);
    }

    /// <summary>Whether the list stands beside what it shows, as laid out last.</summary>
    public bool IsBeside => list is null || GetDock(list) == Dock.Left;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (list is not null && OptionRow.Stacks(availableSize.Width) == IsBeside)
        {
            Beside(!IsBeside);
        }
        return base.MeasureOverride(availableSize);
    }

    private void Beside(bool beside)
    {
        SetDock(list!, beside ? Dock.Left : Dock.Top);
        list!.Width = beside ? width : double.NaN;
        list.Height = beside ? double.NaN : ListHeight;
        shown!.Margin = beside ? new Thickness(Gap, 0, 0, 0) : new Thickness(0, Gap, 0, 0);
        Height = beside ? height : height + ListHeight + Gap;
    }
}
