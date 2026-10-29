using Passenger.Xaml.Features;

namespace Passenger.UnitTests.Xaml;

/// <summary>Which binding paths start at the data context, and so need a type to start from where Avalonia compiles them
/// (AVLN2100), as its 12.1 compiler reads them.</summary>
public class CompiledBindingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(" . ")]
    [InlineData("Title")]
    [InlineData("Owner.Name")]
    [InlineData("!Flag")]
    [InlineData("!!Flag")]
    [InlineData("[0]")]
    [InlineData("Greeting^")]
    [InlineData("_hidden")]
    public void A_path_with_no_source_of_its_own_starts_at_the_data_context(string? path) =>
        Assert.True(CompiledBindings.StartsAtDataContext(path), $"'{path}' read as starting elsewhere, so its binding goes unmarked where the build refuses it");

    [Theory]
    [InlineData("#Other.Text")]
    [InlineData("$parent[StackPanel].Tag")]
    [InlineData("$self.Tag")]
    [InlineData("(Grid.Row)")]
    [InlineData("!")]
    public void A_path_from_an_element_a_relative_source_or_an_attached_property_does_not(string path) =>
        Assert.False(CompiledBindings.StartsAtDataContext(path), $"'{path}' read as starting at the data context, so its binding is marked where the build takes it");
}
