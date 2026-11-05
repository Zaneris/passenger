using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>What a XAML element sets itself, as the XAML workspace's preview shows it while no app runs: each property its
/// attributes and property elements set, a binding's path and a resource's key said, XAML's own directives and the
/// designer's left out.</summary>
public class XamlSetValuesTests
{
    private const string Window = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                mc:Ignorable="d" d:DesignWidth="800" x:Class="App.Main" Title="Main">
          <Window.Styles>
            <Style Selector="Button" />
            <Style Selector="TextBlock" />
          </Window.Styles>
          <Button x:Name="Go" Content="{Binding Caption, Mode=OneWay}" Background="{StaticResource Accent}" Grid.Row="1" Tag="{x:Static Main.Tag}" />
        </Window>
        """;

    [Fact]
    public void An_elements_attributes_are_its_local_values_bindings_and_resources_its_directives_left_out()
    {
        XmlTree tree = XmlTree.Parse(Window);
        XmlElement button = tree.Elements.Single(e => e.Name == "Button");
        Assert.Equal(
            [
                new XamlSetValue("Content", "{Binding Caption, Mode=OneWay}", XamlValueKind.Binding, "Caption"),
                new XamlSetValue("Background", "{StaticResource Accent}", XamlValueKind.Resource, "Accent"),
                new XamlSetValue("Grid.Row", "1", XamlValueKind.Local, null),
                new XamlSetValue("Tag", "{x:Static Main.Tag}", XamlValueKind.Extension, "x:Static"),
            ],
            XamlSetValues.Of(tree, button));
    }

    [Fact]
    public void A_property_element_is_its_owners_value_by_what_it_holds_and_the_designers_attributes_are_left_out()
    {
        XmlTree tree = XmlTree.Parse(Window);
        Assert.Equal(
            [
                new XamlSetValue("Title", "Main", XamlValueKind.Local, null),
                new XamlSetValue("Styles", "<Style> and 1 more", XamlValueKind.Element, null),
            ],
            XamlSetValues.Of(tree, tree.Root!));
    }
}
