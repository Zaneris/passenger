using Passenger.Inspector;

namespace Passenger.UnitTests.Xaml;

/// <summary>What XAML Hot Reload reads of two texts of a XAML file's root: its type and x:Class, the properties a new
/// text no longer sets (cleared before the app populates the root again), those it sets as before (whose live value
/// stands, but for a markup extension, which may read what changed), the entries its resources write by key, what the
/// text writes into the root's collections, against which what the app put there itself is told, and a text that does
/// not read refused at its line.</summary>
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
    public void A_root_is_read_as_its_type_class_attributes_and_resources_and_what_the_new_text_drops_goes() => Cases.All(
        ("a root is read as its type its class its own attributes and its property elements", A_root_is_read_as_its_type_its_class_its_own_attributes_and_its_property_elements),
        ("what the new text no longer sets on the root is dropped and what it sets as before is kept but for a markup extension", What_the_new_text_no_longer_sets_on_the_root_is_dropped_and_what_it_sets_as_before_is_kept_but_for_a_markup_extension),
        ("the entries a roots resources write are read by key a dictionary inside them as themselves", The_entries_a_roots_resources_write_are_read_by_key_a_dictionary_inside_them_as_themselves),
        ("an entry is written by a text that keys it by that name or type and by no other", An_entry_is_written_by_a_text_that_keys_it_by_that_name_or_type_and_by_no_other),
        ("a text that does not read as xml is refused at its line and column", A_text_that_does_not_read_as_xml_is_refused_at_its_line_and_column));

    private void A_root_is_read_as_its_type_its_class_its_own_attributes_and_its_property_elements()
    {
        XamlRoot root = XamlRoot.Read(Before);
        Assert.Equal(("Window", "App.MainWindow"), (root.Type, root.Class));
        Assert.Equal(["Title", "Width", "Background", "Opened"], root.Attributes.Keys);
        Assert.Equal(["DataContext", "Styles"], root.Elements.Keys);
    }

    private void What_the_new_text_no_longer_sets_on_the_root_is_dropped_and_what_it_sets_as_before_is_kept_but_for_a_markup_extension()
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

    private void The_entries_a_roots_resources_write_are_read_by_key_a_dictionary_inside_them_as_themselves()
    {
        XamlResources resources = XamlRoot.Read("""
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:App">
                <Window.Resources>
                    <ResourceDictionary>
                        <SolidColorBrush x:Key="Accent" Color="Red" />
                        <ResourceDictionary x:Key="Nested" />
                        <Style TargetType="Button" />
                        <Style TargetType="{x:Type TextBox}" />
                        <DataTemplate DataType="{x:Type local:Model}" />
                        <ControlTheme x:Key="{x:Type local:Label}" />
                        <SolidColorBrush x:Key="{x:Static local:Keys.Ground}" Color="Red" />
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="One.xaml" />
                            <ResourceDictionary Source="Two.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                        <ResourceDictionary.ThemeDictionaries>
                            <ResourceDictionary x:Key="Dark" />
                        </ResourceDictionary.ThemeDictionaries>
                    </ResourceDictionary>
                </Window.Resources>
            </Window>
            """).Resources;
        Assert.Equal(["Accent", "Nested"], resources.Keys.Order());
        Assert.Equal(["Button", "Label", "Model", "TextBox"], resources.Types.Order());
        Assert.Equal(2, resources.Merged);
        Assert.Equal(["Dark"], resources.Themes);
    }

    private void An_entry_is_written_by_a_text_that_keys_it_by_that_name_or_type_and_by_no_other()
    {
        XamlResources resources = XamlRoot.Read("""
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <UserControl.Resources>
                    <SolidColorBrush x:Key="Accent" Color="Red" />
                    <Style TargetType="String" />
                </UserControl.Resources>
            </UserControl>
            """).Resources;
        Assert.True(resources.Writes("Accent"), "The text keys a brush Accent");
        Assert.False(resources.Writes("accent"), "A resource's key tells letter case apart");
        Assert.True(resources.Writes(typeof(string)), "The text writes a style for the type");
        Assert.False(resources.Writes(typeof(int)), "The text writes nothing for that type");
        Assert.False(resources.Writes(1), "A key of another kind is no key the text can name");
    }

    [Fact]
    public void Of_a_collection_the_texts_items_and_the_apps_keep_their_places_by_where_each_came_from() =>
        Of_a_collection_what_the_text_did_not_write_stays_before_and_after_what_it_did_by_where_each_came_from();

    private void Of_a_collection_what_the_text_did_not_write_stays_before_and_after_what_it_did_by_where_each_came_from()
    {
        string own = "own";
        string early = "early";
        string written = "written";
        string included = "included";
        Func<string, XamlOrigin> origin = item => item == written ? XamlOrigin.ThisFile : item == included ? XamlOrigin.AnotherFile : XamlOrigin.Unrecorded;

        Assert.Equal("early | own", Shown(XamlRoot.Own([early, written, included, own], 2, origin)));
        Assert.Equal(" | own early", Shown(XamlRoot.Own([written, own, early], 1, origin)));
        Assert.Equal("own early | ", Shown(XamlRoot.Own([own, early, written], 1, origin)));
    }

    /// <summary>What stays before what the text wrote, and what after.</summary>
    private static string Shown((List<string> First, List<string> Last) own) => $"{string.Join(' ', own.First)} | {string.Join(' ', own.Last)}";

    private void A_text_that_does_not_read_as_xml_is_refused_at_its_line_and_column()
    {
        UnreadXaml unread = Assert.Throws<UnreadXaml>(() => XamlRoot.Read(Before.Replace("</Window.Styles>", "</Window.Style>", StringComparison.Ordinal)));
        Assert.Equal(9, unread.Line);
    }

    /// <summary>A root whose one handler's name holds marks.</summary>
    internal sealed class Greeter
    {
        public int Greeted { get; private set; }

        public void नमस्ते(object? sender, EventArgs e) => Greeted++;
    }
}
