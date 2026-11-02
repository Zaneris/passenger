using Passenger.Inspector;

namespace Passenger.UnitTests.Xaml;

/// <summary>What XAML Hot Reload reads of two texts of a XAML file's root: its type and x:Class, the properties a new text no
/// longer sets (cleared before the app populates the root again), those it sets as before (whose live value stands, but for
/// a markup extension, which may read what changed), a property element compared whole, and a text that does not read
/// refused at its line.</summary>
public sealed class XamlRootTests
{
    private const string Before = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:d="http://schemas.microsoft.com/expression/blend/2008" d:DesignWidth="400"
                x:Class="App.MainWindow" Title="App" Width="480" Background="{DynamicResource Ground}" Opened="OnOpened">
            <Window.DataContext>
                <local:Model xmlns:local="clr-namespace:App" />
            </Window.DataContext>
            <Window.Styles>
                <Style Selector="Button" />
            </Window.Styles>
            <TextBlock Text="Hello" />
        </Window>
        """;

    [Fact]
    public void A_root_is_read_as_its_type_its_class_its_own_attributes_and_its_property_elements()
    {
        XamlRoot root = XamlRoot.Read(Before);
        Assert.Equal(("Window", "App.MainWindow"), (root.Type, root.Class));
        Assert.Equal(["Title", "Width", "Background", "Opened"], root.Attributes.Keys);
        Assert.Equal(["DataContext", "Styles"], root.Elements.Keys);
    }

    [Fact]
    public void What_the_new_text_no_longer_sets_on_the_root_is_dropped_and_what_it_sets_as_before_is_kept_but_for_a_markup_extension()
    {
        XamlRoot before = XamlRoot.Read(Before);
        XamlRoot now = XamlRoot.Read(Before.Replace(" Opened=\"OnOpened\"", "", StringComparison.Ordinal).Replace("Title=\"App\"", "Title=\"App 2\"", StringComparison.Ordinal)
            .Replace("<Style Selector=\"Button\" />", "<Style Selector=\"TextBlock\" />", StringComparison.Ordinal));
        Assert.Equal(["Opened"], before.Dropped(now));
        Assert.True(before.Keeps(now, "Width"), "Width, set as before, should keep what the app holds in it");
        Assert.True(before.Keeps(now, "DataContext"), "The DataContext's element, as before, should keep the model the app holds");
        Assert.False(before.Keeps(now, "Title"), "Title, set to another value, should take the new one");
        Assert.False(before.Keeps(now, "Background"), "A markup extension should be read again rather than kept");
        Assert.False(before.Keeps(now, "Styles"), "Styles changed in their element should take the new ones");
        Assert.False(before.Keeps(now, "Height"), "A property neither text sets is no XAML's to keep");
    }

    [Fact]
    public void A_text_that_does_not_read_as_xml_is_refused_at_its_line_and_column()
    {
        UnreadXaml unread = Assert.Throws<UnreadXaml>(() => XamlRoot.Read(Before.Replace("</Window.Styles>", "</Window.Style>", StringComparison.Ordinal)));
        Assert.Equal(9, unread.Line);
    }

    [Fact]
    public void The_attribute_a_compiler_names_by_its_line_and_column_is_found_by_its_name_and_value()
    {
        Assert.Equal(("Opened", "OnOpened"), XamlRoot.AttributeAt(Before, 3, 96));
        Assert.Null(XamlRoot.AttributeAt(Before, 3, 95));
    }
}
