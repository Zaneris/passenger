using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.CSharp;

namespace Passenger.App.Settings;

/// <summary>A template's Availability configuration: under its C# caption, a check box for each place it may be
/// offered, by its long name; a place that has no meaning for what the template is used in (a shortcut typed at a
/// line's start, Surround With in a type member) is greyed. OK hands the places back.</summary>
public sealed class AvailabilityDialog : IdeWindow
{
    private readonly Dictionary<TemplateAvailability, CheckBox> boxes = [];

    public AvailabilityDialog(TemplateAvailability where, TemplateUse use)
    {
        Title = "Availability configuration";
        Width = 520;
        Height = 360;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StackPanel places = new() { Spacing = 6, Margin = new Thickness(16, 12) };
        places.Children.Add(new TextBlock { Text = "C#", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach ((TemplateAvailability place, _, string name) in LiveTemplatesPane.Places)
        {
            CheckBox box = new() { Content = name, IsChecked = where.HasFlag(place), IsEnabled = Applies(place, use) };
            boxes[place] = box;
            places.Children.Add(box);
        }
        OkButton = DialogFooter.Default();
        OkButton.Click += (_, _) => Close(true);
        Content = DialogFooter.Page(new ScrollViewer { Content = places }, DialogFooter.OkCancel(OkButton, () => Close(false)));
    }

    public Button OkButton { get; }

    /// <summary>The places checked, those greyed left out.</summary>
    public TemplateAvailability Availability =>
        boxes.Where(b => b.Value.IsChecked == true && b.Value.IsEnabled).Aggregate(TemplateAvailability.None, (all, b) => all | b.Key);

    /// <summary>A place's check box.</summary>
    public CheckBox Box(TemplateAvailability place) => boxes[place];

    /// <summary>Whether a place means anything for what a template is used in: a shortcut is typed where a statement, a
    /// type member or a type goes; Surround With wraps statements, an expression, lines, types or anything.</summary>
    private static bool Applies(TemplateAvailability place, TemplateUse use) => place switch
    {
        TemplateAvailability.Statements or TemplateAvailability.TypesAndNamespaces => true,
        TemplateAvailability.TypeMembers => use != TemplateUse.Surround,
        _ => use != TemplateUse.Generation,
    };
}
