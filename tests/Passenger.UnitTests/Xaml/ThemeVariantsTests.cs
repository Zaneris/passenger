using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;

namespace Passenger.UnitTests.Xaml;

/// <summary>A theme dictionary's variant read one way where a key a variant lacks is warned of and where its fix adds it:
/// by XAML's own Key, <c>{x:Static ThemeVariant.Dark}</c> read as Dark, a dictionary keyed under another namespace's
/// prefix no variant of its group.</summary>
public sealed class ThemeVariantsTests
{
    [Fact]
    public void The_fix_adds_a_key_to_the_variant_the_warning_names_never_to_a_dictionary_keyed_under_another_namespace()
    {
        const string Palette = """
            <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:design="https://example.com/design">
                <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary design:Key="{x:Static ThemeVariant.Light}">
                    </ResourceDictionary>
                    <ResourceDictionary x:Key="Dark">
                        <SolidColorBrush x:Key="Accent" Color="#3D7BF5" />
                    </ResourceDictionary>
                    <ResourceDictionary x:Key="Light">
                        <SolidColorBrush x:Key="Muted" Color="#6C707E" />
                    </ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
            </ResourceDictionary>
            """;
        XamlFile file = XamlFiles.Indexed("Palette.axaml", Palette, AvaloniaDialect.Instance);
        XamlDiagnostic warning = Assert.Single(ThemeVariants.Of(file), d => d.Message.StartsWith("'Accent'", StringComparison.Ordinal));
        Assert.Equal("'Accent' is defined for Dark but not for Light", warning.Message);
        XamlFix fix = Assert.Single(warning.Fixes);
        Assert.Equal("Add 'Accent' to Light", fix.Title);
        string fixedText = XamlFiles.Applied(Palette, fix.Edits(Palette));
        string expected = Palette.Replace("""
                        <SolidColorBrush x:Key="Muted" Color="#6C707E" />
            """, """
                        <SolidColorBrush x:Key="Muted" Color="#6C707E" />
                        <SolidColorBrush x:Key="Accent" Color="#3D7BF5" />
            """, StringComparison.Ordinal);
        Assert.True(fixedText == expected, $"Add 'Accent' to Light left the Light dictionary the warning named without it, the warning staying however often it is taken:\n{fixedText}");
    }

    [Fact]
    public void Dictionaries_keyed_by_x_static_variants_warn_only_of_a_key_one_lacks_naming_the_variant_and_the_fix_adds_it_there()
    {
        const string Palette = """
            <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary x:Key="{x:Static ThemeVariant.Dark}">
                        <SolidColorBrush x:Key="Accent" Color="#3D7BF5" />
                        <SolidColorBrush x:Key="Muted" Color="#868A91" />
                    </ResourceDictionary>
                    <ResourceDictionary x:Key="{x:Static ThemeVariant.Light}">
                        <SolidColorBrush x:Key="Accent" Color="#3574F0" />
                    </ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
            </ResourceDictionary>
            """;
        XamlFile file = XamlFiles.Indexed("Palette.axaml", Palette, AvaloniaDialect.Instance);
        List<string> warnings = [.. ThemeVariants.Of(file).Select(d => d.Message)];
        Assert.True(warnings is ["'Muted' is defined for Dark but not for Light"],
            $"Dictionaries keyed by x:Static variants were warned of as {string.Join("; ", warnings)}, so every key they share reads as missing from variants named as written");
        XamlFileSummary own = file.Project.Files[file.Path];
        Assert.Empty(file.Project.MissingVariants(own, "Accent"));
        XamlFix fix = Assert.Single(ThemeVariants.Of(file).Single().Fixes);
        Assert.Equal("Add 'Muted' to Light", fix.Title);
        string fixedText = XamlFiles.Applied(Palette, fix.Edits(Palette));
        Assert.Contains("""
                        <SolidColorBrush x:Key="Accent" Color="#3574F0" />
                        <SolidColorBrush x:Key="Muted" Color="#868A91" />
                    </ResourceDictionary>
            """, fixedText, StringComparison.Ordinal);
    }
}
