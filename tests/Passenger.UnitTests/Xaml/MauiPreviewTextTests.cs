using Passenger.Xaml.Features;

namespace Passenger.UnitTests.Xaml;

/// <summary>A MAUI file's XAML as its previewer is sent it: MAUI's implicit namespaces written after the last attribute of
/// a root's start tag that declares no default namespace, each prefix the root declares itself left as it is, every line and every place
/// before the tag's end where it was; a root that declares its default namespace, or a file the loader must refuse, sent as
/// it is.</summary>
public sealed class MauiPreviewTextTests
{
    private const string Global = "http://schemas.microsoft.com/dotnet/maui/global";
    private const string X = "http://schemas.microsoft.com/winfx/2009/xaml";
    private const string Design = "http://schemas.microsoft.com/dotnet/2021/maui/design";

    [Fact]
    public void A_root_that_declares_no_namespace_takes_mauis_implicit_ones_at_its_start_tags_end()
    {
        const string Page = "<ContentPage x:Class=\"App.MainPage\"\n             Title=\"Main\">\n    <Label d:Text=\"Hi\" />\n</ContentPage>\n";
        string declared = MauiPreviewText.Declared(Page);
        Assert.Equal(Page.Replace("Title=\"Main\">", $"Title=\"Main\" xmlns=\"{Global}\" xmlns:x=\"{X}\" xmlns:d=\"{Design}\">", StringComparison.Ordinal), declared);
        Assert.Equal(Page.Split('\n').Length, declared.Split('\n').Length);
        Assert.Equal(Page.IndexOf("Title", StringComparison.Ordinal), declared.IndexOf("Title", StringComparison.Ordinal));
    }

    [Fact]
    public void A_prefix_the_root_declares_itself_keeps_its_own_and_a_self_closing_root_takes_them_after_its_last_attribute()
    {
        const string View = "<ContentView xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" />";
        Assert.Equal($"<ContentView xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns=\"{Global}\" xmlns:d=\"{Design}\" />", MauiPreviewText.Declared(View));
    }

    [Fact]
    public void An_open_merged_dictionary_is_written_inline_with_its_own_namespaces_and_one_not_open_or_of_another_assembly_is_left()
    {
        const string App = """
            <Application x:Class="App.App">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="Resources/Styles/Colors.xaml" />
                            <ResourceDictionary Source="Resources/Styles/Styles.xaml" />
                            <ResourceDictionary Source="Themes/Dark.xaml;assembly=Other" />
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </Application.Resources>
            </Application>
            """;
        const string Colors = "<?xml version=\"1.0\" ?>\n<ResourceDictionary xmlns:local=\"clr-namespace:App\" x:Class=\"App.Colors\">\n    <Color x:Key=\"Primary\">#E2A33F</Color>\n</ResourceDictionary>\n";
        List<string> asked = [];
        string written = MauiPreviewText.WithOpenDictionaries(App, source =>
        {
            asked.Add(source);
            return source == "Resources/Styles/Colors.xaml" ? Colors : null;
        });
        Assert.Equal(["Resources/Styles/Colors.xaml", "Resources/Styles/Styles.xaml"], asked);
        Assert.Equal(App.Replace("<ResourceDictionary Source=\"Resources/Styles/Colors.xaml\" />",
            "<ResourceDictionary xmlns:local=\"clr-namespace:App\">\n    <Color x:Key=\"Primary\">#E2A33F</Color>\n</ResourceDictionary>", StringComparison.Ordinal), written);
    }

    [Fact]
    public void A_root_that_declares_its_default_namespace_or_a_file_the_loader_must_refuse_is_sent_as_it_is()
    {
        const string Declared = "<ContentPage xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" x:Class=\"App.MainPage\" />";
        Assert.Same(Declared, MauiPreviewText.Declared(Declared));
        Assert.Equal("<ContentPage Title=\"x\"", MauiPreviewText.Declared("<ContentPage Title=\"x\""));
        Assert.Equal("", MauiPreviewText.Declared(""));
    }
}
