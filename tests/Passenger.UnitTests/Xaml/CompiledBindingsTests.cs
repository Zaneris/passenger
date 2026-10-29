using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>Which binding paths start at the data context, and so need a type to start from where Avalonia compiles them
/// (AVLN2100), and which bindings name a source of their own, as its 12.1 compiler reads them.</summary>
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
    [InlineData("((vm:Shop)Item).Name")]
    [InlineData("((vm:Shop)Item)")]
    [InlineData("!((vm:Shop)Item).Closed")]
    public void A_path_with_no_source_of_its_own_starts_at_the_data_context(string? path) =>
        Assert.True(CompiledBindings.StartsAtDataContext(path), $"'{path}' read as starting elsewhere, so its binding goes unmarked where the build refuses it");

    [Theory]
    [InlineData("#Other.Text")]
    [InlineData("$parent[StackPanel].Tag")]
    [InlineData("$self.Tag")]
    [InlineData("(Grid.Row)")]
    [InlineData("(vm:Shop).Name")]
    [InlineData("(vm:Shop)")]
    [InlineData("((vm:Shop)(Grid.Row))")]
    [InlineData("!")]
    public void A_path_from_an_element_a_relative_source_an_attached_property_or_the_data_context_cast_does_not(string path) =>
        Assert.False(CompiledBindings.StartsAtDataContext(path), $"'{path}' read as starting at the data context, so its binding is marked where the build takes it");

    [Theory]
    [InlineData("{Binding Text, ElementName=Other}")]
    [InlineData("{Binding Length, Source={StaticResource Word}}")]
    [InlineData("{Binding Greeting, DataType=vm:MainViewModel}")]
    [InlineData("{Binding Tag, RelativeSource={RelativeSource Self}}")]
    [InlineData("{Binding Tag, RelativeSource={RelativeSource Mode=Self}}")]
    [InlineData("{Binding Tag, RelativeSource={RelativeSource FindAncestor, AncestorType=StackPanel}}")]
    public void A_binding_naming_an_element_a_source_a_data_type_or_another_relative_source_names_its_own(string binding) =>
        Assert.True(XamlFile.NamesSource(Parse(binding)), $"{binding} read as starting at the data context, so it is marked where the build takes it");

    [Theory]
    [InlineData("{Binding Greeting}")]
    [InlineData("{Binding}")]
    [InlineData("{Binding RelativeSource={RelativeSource DataContext}}")]
    [InlineData("{Binding Greeting, RelativeSource={RelativeSource Mode=DataContext}}")]
    [InlineData("{CompiledBinding Path=Greeting, RelativeSource={RelativeSource DataContext}}")]
    [InlineData("{Binding Greeting, Mode=OneWay, Converter={StaticResource Upper}}")]
    public void A_binding_to_its_data_context_names_no_source_of_its_own(string binding) =>
        Assert.False(XamlFile.NamesSource(Parse(binding)), $"{binding} read as naming a source of its own, so it goes unmarked where the build refuses it");

    private static MarkupExtension Parse(string text) => MarkupExtension.Parse(text, 0, text.Length) ?? throw new ArgumentException($"{text} is no markup extension");
}
