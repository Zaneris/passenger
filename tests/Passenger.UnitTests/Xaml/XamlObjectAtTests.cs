using Passenger.Xaml;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>The element whose object an offset of a XAML file is in, which the XAML workspace chooses across the editor, the
/// preview and the running app's tree.</summary>
public class XamlObjectAtTests
{
    private const string Window = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="App.Main">
          <Window.Styles>
            <Style Selector="Button" />
          </Window.Styles>
          <StackPanel>
            <Button x:Name="Save" Content="Save" />
          </StackPanel>
        </Window>
        """;

    [Fact]
    public void An_offset_in_a_tag_or_on_its_open_angle_is_that_elements_and_one_between_tags_is_the_parents()
    {
        XmlTree tree = XmlTree.Parse(Window);
        int button = Window.IndexOf("<Button", StringComparison.Ordinal);
        Assert.Equal("Button", XamlFile.ObjectAt(tree, button)?.Name);
        Assert.Equal("Button", XamlFile.ObjectAt(tree, Window.IndexOf("Content", StringComparison.Ordinal))?.Name);
        Assert.Equal("StackPanel", XamlFile.ObjectAt(tree, button - 1)?.Name);
    }

    [Fact]
    public void An_offset_in_a_property_element_is_its_owners_and_one_past_the_root_is_no_elements()
    {
        XmlTree tree = XmlTree.Parse(Window);
        Assert.Equal("Window", XamlFile.ObjectAt(tree, Window.IndexOf("<Window.Styles>", StringComparison.Ordinal) + 3)?.Name);
        Assert.Equal("Style", XamlFile.ObjectAt(tree, Window.IndexOf("Selector", StringComparison.Ordinal))?.Name);
        Assert.Equal("App.Main", XamlFile.ClassAttribute(tree)?.Value);
        Assert.Null(XamlFile.ObjectAt(tree, Window.Length));
    }
}
