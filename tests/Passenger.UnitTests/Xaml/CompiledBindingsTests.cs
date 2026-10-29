using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>Which binding paths start at the data context, and so need a type to start from where Avalonia compiles them
/// (AVLN2100), which it refuses from a data context it has no type for (AVLN2000), and which bindings name a source of
/// their own, as its 12.1 compiler reads them.</summary>
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

    [Theory]
    [InlineData("Name")]
    [InlineData("Owner.Name")]
    [InlineData("!Flag")]
    [InlineData("[0]")]
    [InlineData("Greeting^")]
    [InlineData("((vm:Person)Owner).Name")]
    public void A_step_from_a_data_context_the_compiler_has_no_type_for_is_refused(string path) =>
        Assert.True(CompiledBindings.Refused(CompiledContext.Untyped, path), $"'{path}' taken from a data context of no type, so it goes unmarked where the build refuses it");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" . ")]
    [InlineData("(vm:Person).Name")]
    [InlineData("#Other.Text")]
    [InlineData("$parent.Tag")]
    public void The_data_context_itself_its_cast_or_a_path_from_elsewhere_is_taken_from_one_of_no_type(string? path) =>
        Assert.False(CompiledBindings.Refused(CompiledContext.Untyped, path), $"'{path}' refused from a data context of no type, so it is marked where the build takes it");

    [Theory]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("Name")]
    [InlineData("((vm:Person)Owner).Name")]
    public void Any_path_from_the_data_context_is_refused_where_nothing_gives_it_a_type_and_none_where_something_does(string? path)
    {
        Assert.True(CompiledBindings.Refused(CompiledContext.None, path), $"'{path}' taken with no data type, so it goes unmarked where the build refuses it");
        Assert.False(CompiledBindings.Refused(CompiledContext.Typed, path), $"'{path}' refused from a typed data context, so it is marked where the build takes it");
    }

    [Theory]
    [InlineData("{Binding Owner, DataType=vm:Person}")]
    [InlineData("{Binding Owner, DataType={x:Type vm:Person}}")]
    [InlineData("{Binding Greeting, Source={x:Static vm:Statics.Vm}}")]
    [InlineData("{Binding Greeting, RelativeSource={RelativeSource DataContext}}")]
    public void An_elements_own_data_context_binding_starts_above_it_whatever_data_type_or_source_it_names(string binding) =>
        Assert.False(XamlFile.NamesSource(Parse(binding), dataContext: true), $"{binding} as a DataContext read as starting elsewhere, so it goes unmarked where the build refuses it");

    [Theory]
    [InlineData("{Binding Text, ElementName=Other}")]
    [InlineData("{Binding Tag, RelativeSource={RelativeSource Self}}")]
    public void An_elements_own_data_context_binding_from_an_element_or_a_relative_source_starts_there(string binding) =>
        Assert.True(XamlFile.NamesSource(Parse(binding), dataContext: true), $"{binding} as a DataContext read as starting at the one above, so it is marked where the build takes it");

    private static MarkupExtension Parse(string text) => MarkupExtension.Parse(text, 0, text.Length) ?? throw new ArgumentException($"{text} is no markup extension");
}
