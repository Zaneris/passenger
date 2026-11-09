using System.Globalization;
using Passenger.Inspector;

namespace Passenger.UnitTests.LiveTree;

/// <summary>The words the inspector gives a value in either framework's app, whatever the app's culture: a thickness as XAML
/// writes it, a property attached from another type by its owner first, and a value's words cut at their longest.</summary>
public sealed class InspectorWordsTests
{
    [Fact]
    public void A_thickness_in_a_german_app_reads_with_points_as_xaml_writes_it()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.5,0,8,0.25", Words.Thickness(1.5, 0, 8, 0.25));
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void A_property_attached_from_another_type_is_named_by_its_owner_and_a_long_value_is_cut()
    {
        Assert.Equal("Grid.Row", Words.PropertyName(typeof(Grid), "Row", attached: true));
        Assert.Equal("Width", Words.PropertyName(typeof(Grid), "Width", attached: false));
        string cut = Words.CutValue(new string('x', 400));
        Assert.True(cut.Length == 300 && cut.EndsWith("...", StringComparison.Ordinal), $"A value 400 characters long was shown as {cut.Length}: {cut[^5..]}");
        Assert.Equal("short", Words.CutValue("short"));
    }

    /// <summary>A type standing for an owner of attached properties.</summary>
    private sealed class Grid;
}
