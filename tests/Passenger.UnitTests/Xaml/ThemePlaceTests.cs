using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;

namespace Passenger.UnitTests.Xaml;

/// <summary>Where a theme library's XAML writes a resource's key, a template part's name and a type's control theme, and
/// the files it merges in, read from its markup however each value is quoted or spaced round its equals sign.</summary>
public sealed class ThemePlaceTests
{
    private const string Theme = """
        <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:xaml="http://schemas.microsoft.com/winfx/2006/xaml">
          <ResourceDictionary.MergedDictionaries>
            <MergeResourceInclude Source = 'Accents/Base.xaml' />
            <ResourceInclude Source="avares://Avalonia.Themes.Fluent/Accents/Colors.xaml" />
          </ResourceDictionary.MergedDictionaries>
          <StaticResource xaml:Key='ButtonBackground' ResourceKey="ControlFillColorDefaultBrush" />
          <StaticResource xaml:Key = "ButtonForeground" ResourceKey="TextFillColorPrimaryBrush" />
          <ControlTheme xaml:Key = '{xaml:Type Button}' TargetType = 'Button'>
            <Setter Property="Template">
              <ControlTemplate>
                <ContentPresenter xaml:Name = 'PART_ContentPresenter' Content="{TemplateBinding Content}" />
              </ControlTemplate>
            </Setter>
          </ControlTheme>
          <ControlTheme xaml:Key="RepeatButtonTheme" TargetType = 'RepeatButton' />
        </ResourceDictionary>
        """;

    private static readonly ClrType Button = new("Avalonia.Controls", "Button", ClrTypeKind.Class, new TypeOrigin("Avalonia.Controls", null, null), _ => ClrTypeDetails.None);

    private static readonly ClrType RepeatButton = new("Avalonia.Controls", "RepeatButton", ClrTypeKind.Class, new TypeOrigin("Avalonia.Controls", null, null), _ => ClrTypeDetails.None);

    [Fact]
    public void A_resource_keyed_in_single_quotes_or_spaced_round_its_equals_is_found_at_its_key()
    {
        foreach (string key in (string[])["ButtonBackground", "ButtonForeground"])
        {
            int? at = XamlNavigation.ThemePlace(new XamlTarget(XamlTargetKind.Theme, $"{key} in the theme") { Part = key }, Theme);
            Assert.True(at == Theme.IndexOf(key, StringComparison.Ordinal), $"The theme's resource {key} was found at {at?.ToString() ?? "no place"}, not at its key: Go to Declaration leaves it unfound");
        }
    }

    [Fact]
    public void A_template_part_named_in_single_quotes_and_a_control_theme_targeted_so_are_found_at_their_name_and_tag()
    {
        int? part = XamlNavigation.ThemePlace(new XamlTarget(XamlTargetKind.Theme, "PART_ContentPresenter in Button's template") { Type = Button, Part = "PART_ContentPresenter" }, Theme);
        Assert.True(part == Theme.IndexOf("PART_ContentPresenter", StringComparison.Ordinal), $"Button's template part was found at {part?.ToString() ?? "no place"}, not at its name");
        int? button = XamlNavigation.ThemePlace(new XamlTarget(XamlTargetKind.Theme, "Button's control theme") { Type = Button }, Theme);
        Assert.True(button == Theme.IndexOf("ControlTheme", StringComparison.Ordinal), $"Button's control theme, keyed by its type, was found at {button?.ToString() ?? "no place"}, not at its tag");
        int? repeat = XamlNavigation.ThemePlace(new XamlTarget(XamlTargetKind.Theme, "RepeatButton's control theme") { Type = RepeatButton }, Theme);
        Assert.True(repeat == Theme.LastIndexOf("ControlTheme", StringComparison.Ordinal), $"RepeatButton's control theme, targeted alone, was found at {repeat?.ToString() ?? "no place"}, not at its tag");
    }

    [Fact]
    public void A_key_the_theme_does_not_write_is_found_nowhere()
    {
        Assert.Null(XamlNavigation.ThemePlace(new XamlTarget(XamlTargetKind.Theme, "Missing in the theme") { Part = "Missing" }, Theme));
        Assert.Null(XamlNavigation.ThemePlace(new XamlTarget(XamlTargetKind.Theme, "PART_ContentPresenter in the theme") { Part = "PART_ContentPresenter" }, Theme));
    }

    [Fact]
    public void The_files_a_theme_merges_in_are_read_however_their_source_is_quoted()
    {
        string[] includes = [.. XamlNavigation.ThemeIncludes(Theme)];
        Assert.True(includes.SequenceEqual(["Accents/Base.xaml", "avares://Avalonia.Themes.Fluent/Accents/Colors.xaml"]),
            $"The theme's merged files were read as {string.Join(", ", includes)}: a file merged in single quotes is not looked in");
    }
}
