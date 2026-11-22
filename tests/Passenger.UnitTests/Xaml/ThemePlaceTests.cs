using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;

namespace Passenger.UnitTests.Xaml;

/// <summary>Where a theme library's XAML writes a resource's key, read from its markup however the value is quoted or
/// spaced round its equals sign; and its root, the XAML compiled into its class, found in the index of the assembly's
/// Avalonia resources.</summary>
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
    public void A_theme_librarys_keys_and_compiled_root_are_found_however_written() => Cases.All(
        ("a resource keyed in single quotes or spaced round its equals is found at its key", A_resource_keyed_in_single_quotes_or_spaced_round_its_equals_is_found_at_its_key),
        ("the xaml compiled into a class is read from avalonias resources past the entries before it", The_xaml_compiled_into_a_class_is_read_from_avalonias_resources_past_the_entries_before_it));

    private void A_resource_keyed_in_single_quotes_or_spaced_round_its_equals_is_found_at_its_key()
    {
        foreach (string key in (string[])["ButtonBackground", "ButtonForeground"])
        {
            int? at = XamlNavigation.ThemePlace(new XamlTarget(XamlTargetKind.Theme, $"{key} in the theme") { Part = key }, Theme);
            Assert.True(at == Theme.IndexOf(key, StringComparison.Ordinal), $"The theme's resource {key} was found at {at?.ToString() ?? "no place"}, not at its key: Go to Declaration leaves it unfound");
        }
    }

    private void The_xaml_compiled_into_a_class_is_read_from_avalonias_resources_past_the_entries_before_it()
    {
        const string Info = "<AvaloniaResourceXamlInfo xmlns=\"http://schemas.datacontract.org/2004/07/Avalonia.Markup.Xaml.PortableXaml\">"
            + "<ClassToResourcePathIndex xmlns:a=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\"><a:KeyValueOfstringstring>"
            + "<a:Key>Avalonia.Themes.Fluent.FluentTheme</a:Key><a:Value>/FluentTheme.xaml</a:Value></a:KeyValueOfstringstring></ClassToResourcePathIndex></AvaloniaResourceXamlInfo>";
        byte[] font = new byte[1000];
        byte[] info = System.Text.Encoding.UTF8.GetBytes(Info);
        using MemoryStream index = new();
        using (BinaryWriter writer = new(index, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(2);
            writer.Write(2);
            writer.Write("/Assets/Inter-Bold.ttf");
            writer.Write(0);
            writer.Write(font.Length);
            writer.Write("/!AvaloniaResourceXamlInfo");
            writer.Write(font.Length);
            writer.Write(info.Length);
        }
        // As an assembly holds them: their length, then the index's size, the index and the entries, after bytes of its own.
        using MemoryStream resources = new();
        using (BinaryWriter writer = new(resources, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(new byte[16]);
            writer.Write(4 + (int)index.Length + font.Length + info.Length);
            writer.Write((int)index.Length);
            writer.Write(index.ToArray());
            writer.Write(font);
            writer.Write(info);
        }
        resources.Position = 16;
        IReadOnlyList<string> files = AssemblyTypes.XamlClassFiles(resources);
        Assert.True(files.SequenceEqual(["/FluentTheme.xaml"]), $"The theme's root was read as {string.Join(", ", files)}: its resources are not looked in");
        resources.Position = 16 + 4;
        Assert.Empty(AssemblyTypes.XamlClassFiles(resources));
    }
}
