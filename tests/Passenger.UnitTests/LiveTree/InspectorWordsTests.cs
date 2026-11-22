using System.Globalization;
using Passenger.Inspector;

namespace Passenger.UnitTests.LiveTree;

/// <summary>The words the inspector gives a value in either framework's app, whatever the app's culture: a thickness as
/// XAML writes it.</summary>
public sealed class InspectorWordsTests
{
    [Fact]
    public void A_values_words_are_the_inspectors_whatever_the_apps_culture() =>
        A_thickness_in_a_german_app_reads_with_points_as_xaml_writes_it();

    private void A_thickness_in_a_german_app_reads_with_points_as_xaml_writes_it()
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
}
