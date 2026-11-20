using Avalonia;
using Avalonia.Controls;
using Passenger.App.CSharp;
using Passenger.Xaml.Features;

namespace Passenger.App.Xaml;

/// <summary>Extract XAML Resource: the new resource's name, where it goes (this file's resources, the application's, a
/// resource dictionary the file reaches), and whether the value's other uses in the file take it too.</summary>
public sealed class ExtractResourceDialog : RefactoringDialog
{
    private readonly IReadOnlyList<ResourcePlace> places;
    private readonly Func<string, string?> problem;
    private readonly Func<string, ResourcePlace, bool, Task<string?>> make;

    public ExtractResourceDialog(ExtractableValue value, IReadOnlyList<ResourcePlace> places, Func<string, string?> problem,
        Func<string, ResourcePlace, bool, Task<string?>> make)
        : base("Extract XAML Resource", "Extract resource", $"Extract the {value.Property} value '{value.Value}' to a resource and use it through StaticResource", DialogWidths.Form)
    {
        this.places = places;
        this.problem = problem;
        this.make = make;
        NameBox = Field(value.Suggested);
        NameBox.TextChanged += (_, _) => Changed();
        PlaceBox = new FormDropDown { ItemsSource = places.Select(p => p.Title).ToList(), SelectedIndex = 0 };
        EveryUse = new CheckBox { Content = $"Replace all {value.Uses.Count} occurrences in this file", IsChecked = true, IsVisible = value.Others > 0 };
        Options = new StackPanel
        {
            Spacing = FormRows.Between,
            Children = { Row("Resource name", NameBox), Row("Create in", PlaceBox), Row(null, EveryUse) },
        };
        Changed();
        FocusOnOpen(NameBox);
    }

    public TextBox NameBox { get; }

    public ComboBox PlaceBox { get; }

    /// <summary>Replace all occurrences, shown when the value is written more than once.</summary>
    public CheckBox EveryUse { get; }

    /// <summary>Where the places offered go, as the list names them.</summary>
    public IReadOnlyList<string> Places => [.. places.Select(p => p.Title)];

    protected override string? Check() => problem(NameBox.Text ?? string.Empty);

    protected override Task<string?> MakeAsync() =>
        make(NameBox.Text ?? string.Empty, places[Math.Max(0, PlaceBox.SelectedIndex)], EveryUse.IsVisible && EveryUse.IsChecked == true);
}

/// <summary>Extract XAML Style: the style's name (a WPF key, or the class an Avalonia style selects by), where it goes,
/// and which of the element's properties become its setters, a binding left out at first.</summary>
public sealed class ExtractStyleDialog : RefactoringDialog
{
    private readonly IReadOnlyList<ResourcePlace> places;
    private readonly Func<string, string?> problem;
    private readonly Func<string, IReadOnlyList<string>, ResourcePlace, Task<string?>> make;

    public ExtractStyleDialog(StyleSource source, IReadOnlyList<ResourcePlace> places, Func<string, string?> problem,
        Func<string, IReadOnlyList<string>, ResourcePlace, Task<string?>> make)
        : base("Extract XAML Style", "Extract style", source.ByClass
            ? $"Extract properties of '{source.ElementName}' to a style that selects it by a class it takes"
            : $"Extract properties of '{source.ElementName}' to a style it names", DialogWidths.Form)
    {
        this.places = places;
        this.problem = problem;
        this.make = make;
        NameBox = Field(source.Suggested);
        NameBox.TextChanged += (_, _) => Changed();
        PlaceBox = new FormDropDown { ItemsSource = places.Select(p => p.Title).ToList(), SelectedIndex = 0 };
        StackPanel list = new() { Spacing = Gaps.Tight };
        foreach (StyleProperty property in source.Properties)
        {
            CheckBox box = new() { Content = $"{property.Name} = {property.Written}", IsChecked = !property.IsBinding, Tag = property.Name };
            box.IsCheckedChanged += (_, _) => Changed();
            Properties.Add(box);
            list.Children.Add(box);
        }
        Options = new StackPanel
        {
            Spacing = FormRows.Between,
            Children =
            {
                Row(source.ByClass ? "Style class" : "Style name", NameBox),
                Row("Create in", PlaceBox),
                Row("Properties to extract", list, top: true),
            },
        };
        Changed();
        FocusOnOpen(NameBox);
    }

    public TextBox NameBox { get; }

    public ComboBox PlaceBox { get; }

    /// <summary>A check box for each property, by its name and value.</summary>
    public List<CheckBox> Properties { get; } = [];

    protected override string? Check() =>
        Properties.All(p => p.IsChecked != true) ? "At least one property must be selected" : problem(NameBox.Text ?? string.Empty);

    protected override Task<string?> MakeAsync() =>
        make(NameBox.Text ?? string.Empty, [.. Properties.Where(p => p.IsChecked == true).Select(p => (string)p.Tag!)], places[Math.Max(0, PlaceBox.SelectedIndex)]);
}
