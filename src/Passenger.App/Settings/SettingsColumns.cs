using Avalonia.Controls;

namespace Passenger.App.Settings;

/// <summary>A Settings list and its detail editor share columns on a wide page and stack without recreating either.</summary>
public sealed class SettingsColumns : Grid
{
    public const double WidePageWidth = 620;

    private readonly double firstWidth;
    private readonly double firstHeight;
    private readonly double wideHeight;
    private readonly Control detail;
    private bool stacked;

    public SettingsColumns(Control list, Control detail, double firstWidth, double firstHeight, double wideHeight)
    {
        this.firstWidth = firstWidth;
        this.firstHeight = firstHeight;
        this.wideHeight = wideHeight;
        this.detail = detail;
        ColumnDefinitions = new ColumnDefinitions("*,*");
        RowDefinitions = new RowDefinitions("*,*");
        Children.Add(list);
        Children.Add(detail);
        Place();
        SizeChanged += (_, _) =>
        {
            bool narrow = Bounds.Width < WidePageWidth;
            if (stacked != narrow)
            {
                stacked = narrow;
                Place();
            }
        };
    }

    private void Place()
    {
        Height = wideHeight + (stacked ? firstHeight + 16 : 0);
        ColumnSpacing = stacked ? 0 : 16;
        RowSpacing = stacked ? 16 : 0;
        ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(firstWidth);
        ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        RowDefinitions[0].Height = stacked ? new GridLength(firstHeight) : new GridLength(1, GridUnitType.Star);
        RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SetColumn(detail, stacked ? 0 : 1);
        SetRow(detail, stacked ? 1 : 0);
    }
}
