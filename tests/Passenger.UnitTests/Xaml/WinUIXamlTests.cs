using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>WinUI's XAML read against stand-ins shaped as its C#/WinRT projection is (each dependency property a static
/// <c>XxxProperty</c> property, the content property named by <c>[ContentProperty(Name = …)]</c>, no xmlns mapped by any
/// attribute): its elements, properties, attached properties and events; <c>{x:Bind}</c>'s path read from the page, its
/// named elements and a data template's <c>x:DataType</c> as its compiler reads it, completed, checked and leading to
/// what it names; <c>{ThemeResource}</c> and its other markup extensions; and its own directives.</summary>
public sealed class WinUIXamlTests
{
    /// <summary>The projection's shape, of the types the cases name.</summary>
    private const string Framework = """
        namespace Microsoft.UI.Xaml
        {
            public class DependencyObject { }
            public sealed class DependencyProperty { }
            public class UIElement : DependencyObject
            {
                public static DependencyProperty VisibilityProperty { get; }
                public Visibility Visibility { get; set; }
            }
            public enum Visibility { Visible, Collapsed }
            public class FrameworkElement : UIElement
            {
                public static DependencyProperty WidthProperty { get; }
                public double Width { get; set; }
                public static DependencyProperty TagProperty { get; }
                public object Tag { get; set; }
                public ResourceDictionary Resources { get; set; }
            }
            public class ResourceDictionary : DependencyObject { }
            public class DataTemplate : DependencyObject { }
        }

        namespace Microsoft.UI.Xaml.Markup
        {
            public sealed class ContentPropertyAttribute : System.Attribute
            {
                public string Name;
            }
            public class MarkupExtension { }
        }

        namespace Microsoft.UI.Xaml.Data
        {
            public enum BindingMode { OneWay = 1, OneTime = 2, TwoWay = 3 }
            public class Binding
            {
                public string Path { get; set; }
                public BindingMode Mode { get; set; }
            }
        }

        namespace Microsoft.UI.Xaml.Controls
        {
            using Microsoft.UI.Xaml;
            using Microsoft.UI.Xaml.Markup;

            [ContentProperty(Name = "Children")]
            public class Panel : FrameworkElement
            {
                public System.Collections.Generic.IList<UIElement> Children { get; }
            }
            public class StackPanel : Panel { }
            public class Grid : Panel
            {
                public static DependencyProperty RowProperty { get; }
                public static int GetRow(FrameworkElement element) => 0;
                public static void SetRow(FrameworkElement element, int value) { }
            }
            [ContentProperty(Name = "Content")]
            public class ContentControl : FrameworkElement
            {
                public static DependencyProperty ContentProperty { get; }
                public object Content { get; set; }
            }
            public class Button : ContentControl
            {
                public event System.EventHandler Click;
            }
            public class TextBlock : FrameworkElement
            {
                public static DependencyProperty TextProperty { get; }
                public string Text { get; set; }
            }
            public class ItemsControl : FrameworkElement
            {
                public static DependencyProperty ItemTemplateProperty { get; }
                public DataTemplate ItemTemplate { get; set; }
            }
            public class UserControl : ContentControl { }
            public class Page : UserControl { }
        }
        """;

    private const string Code = """
        namespace App;

        public partial class MainPage : Microsoft.UI.Xaml.Controls.Page
        {
            private string greeting = "Hello";

            public Shop Shop { get; } = new();

            private string Shout(string text, int times) => text;

            private void Save() { }
        }

        public class Shop
        {
            public string Name { get; set; }

            public Item[] Items { get; }

            public object Any { get; }

            private string Secret { get; }
        }

        public class Item
        {
            public string Title { get; set; }
        }

        public static class Format
        {
            public static string Money(string text) => text;
        }
        """;

    private const string Page = """
        <Page x:Class="App.MainPage"
              xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:local="using:App">
            <StackPanel>
                <TextBlock x:Name="Title" Text="{x:Bind greeting}" Grid.Row="1" />
                <TextBlock Text="{x:Bind Shop.Name, Mode=OneWay}" Tag="{x:Bind Title.Text}" />
                <TextBlock Text="{x:Bind Shout(Shop.Name, 2), Mode=OneWay}" />
                <TextBlock Text="{x:Bind local:Format.Money(Shop.Name)}" Tag="{x:Bind ((local:Item)Shop.Any).Title}" />
                <Button Content="Go" Click="{x:Bind Save}" />
                <ItemsControl>
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="local:Item">
                            <TextBlock Text="{x:Bind Title}" x:Phase="1" />
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </StackPanel>
        </Page>
        """;

    private static XamlFile Read(string page) => XamlFiles.Read("MainPage.xaml", page, WinUIDialect.Instance, ("Framework.cs", Framework), ("MainPage.xaml.cs", Code));

    [Fact]
    public void Winuis_xaml_reads_its_projection_and_its_x_bind_as_its_compiler_reads_them() => Cases.All(
        ("the presentation namespace maps every microsoft ui xaml namespace and properties are the projections static registrations", Properties_are_the_projections_static_registrations),
        ("a page that compiles is marked with nothing", A_page_that_compiles_is_marked_with_nothing),
        ("an x bind step its start lacks is marked where it stands and nothing else is", () => Cases.Each<(string, string, string)>(
        [
            ("{x:Bind greeting}", "{x:Bind greetng}", "greetng"),
            ("{x:Bind Shop.Name, Mode=OneWay}", "{x:Bind Shop.Nmae, Mode=OneWay}", "Nmae"),
            ("{x:Bind Shout(Shop.Name, 2), Mode=OneWay}", "{x:Bind Shout(Shop.Nme, 2), Mode=OneWay}", "Nme"),
            ("{x:Bind Title}\" x:Phase", "{x:Bind Titel}\" x:Phase", "Titel"),
            ("{x:Bind ((local:Item)Shop.Any).Title}", "{x:Bind ((local:Item)Shop.Any).Titl}", "Titl"),
        ], row => An_x_bind_step_its_start_lacks_is_marked_where_it_stands(row.Item1, row.Item2, row.Item3))),
        ("a data templates x bind reads only what is not private of its data type and needs one", A_data_templates_x_bind_reads_only_what_is_not_private_of_its_data_type_and_needs_one),
        ("x bind completes the pages members its named elements a steps members and a calls argument", X_bind_completes_the_pages_members_its_named_elements_a_steps_members_and_a_calls_argument),
        ("go to declaration from x bind leads to the member the element and the cast type", Go_to_declaration_from_x_bind_leads_to_the_member_the_element_and_the_cast_type),
        ("winuis own directives are checked and its intrinsic extensions known", Winuis_own_directives_are_checked_and_its_intrinsic_extensions_known),
        ("a markup values brackets hold its commas and braces", A_markup_values_brackets_hold_its_commas_and_braces));

    private void Properties_are_the_projections_static_registrations()
    {
        XamlFile file = Read(Page);
        Assert.Contains("Microsoft.UI.Xaml.Controls", file.ClrNamespacesOf(WpfDialect.Uri));
        Assert.Contains("Microsoft.UI.Xaml.Data", file.ClrNamespacesOf(WpfDialect.Uri));
        Passenger.Xaml.Metadata.ClrType text = file.Types.Find("Microsoft.UI.Xaml.Controls.TextBlock")!;
        XamlProperty? width = WinUIDialect.Instance.Property(file.Types, text, "Width");
        Assert.True(width is { Kind: XamlPropertyKind.Dependency, ClrProperty: not null }, "Width, registered by a static property, was not read as a dependency property");
        Assert.True(WinUIDialect.Instance.Attached(file.Types, file.Types.Find("Microsoft.UI.Xaml.Controls.Grid")!, "Row") is { IsAttached: true },
            "Grid.Row, registered by a static property with GetRow and SetRow, was not read as an attached property");
        Assert.Equal("Children", WinUIDialect.Instance.ContentProperty(file.Types, file.Types.Find("Microsoft.UI.Xaml.Controls.StackPanel")!)?.Name);
    }

    private void A_page_that_compiles_is_marked_with_nothing()
    {
        IReadOnlyList<XamlDiagnostic> found = XamlDiagnostics.Of(Read(Page));
        Assert.True(found.Count == 0, $"The page, which compiles, is marked: {string.Join("; ", found.Select(d => d.Message))}");
    }

    private void An_x_bind_step_its_start_lacks_is_marked_where_it_stands(string written, string misspelt, string step)
    {
        Assert.Contains(written, Page, StringComparison.Ordinal);
        string page = Page.Replace(written, misspelt, StringComparison.Ordinal);
        XamlFile file = Read(page);
        XamlDiagnostic marked = Assert.Single(XamlDiagnostics.Of(file));
        Assert.Equal(XamlSeverity.Error, marked.Severity);
        Assert.Equal(step, file.Text[marked.Range.Start..marked.Range.End]);
    }

    private void A_data_templates_x_bind_reads_only_what_is_not_private_of_its_data_type_and_needs_one()
    {
        XamlFile secret = Read(Page.Replace("<DataTemplate x:DataType=\"local:Item\">", "<DataTemplate x:DataType=\"local:Shop\">", StringComparison.Ordinal)
            .Replace("{x:Bind Title}", "{x:Bind Secret}", StringComparison.Ordinal));
        Assert.Equal("Cannot resolve property 'Secret' on Shop", Assert.Single(XamlDiagnostics.Of(secret)).Message);
        XamlFile untyped = Read(Page.Replace(" x:DataType=\"local:Item\"", string.Empty, StringComparison.Ordinal));
        XamlDiagnostic refused = Assert.Single(XamlDiagnostics.Of(untyped));
        Assert.Contains("x:DataType", refused.Message, StringComparison.Ordinal);
        Assert.Equal("x:Bind", untyped.Text[refused.Range.Start..refused.Range.End]);
    }

    private void X_bind_completes_the_pages_members_its_named_elements_a_steps_members_and_a_calls_argument()
    {
        XamlFile file = Read(Page);
        string[] Offered(string after, int back = 0)
        {
            int at = file.Text.IndexOf(after, StringComparison.Ordinal) + after.Length - back;
            return [.. XamlCompletion.At(file, at)?.Items.Select(i => i.Label) ?? []];
        }
        string[] start = Offered("{x:Bind greeting", "greeting".Length);
        Assert.True(start.Contains("greeting") && start.Contains("Shop") && start.Contains("Shout") && start.Contains("Title"),
            $"The page's members, private ones too, and its named elements were not all offered: {string.Join(", ", start)}");
        string[] shop = Offered("{x:Bind Shop.Name", "Name".Length);
        Assert.True(shop.Contains("Name") && shop.Contains("Items") && !shop.Contains("Secret") && !shop.Contains("greeting"),
            $"Shop's members, its private one left out, were not what was offered after Shop.: {string.Join(", ", shop)}");
        string[] argument = Offered("{x:Bind Shout(Shop.Name", "Shop.Name".Length);
        Assert.True(argument.Contains("Shop") && argument.Contains("greeting"), $"A call's argument was not offered the page's members: {string.Join(", ", argument)}");
        string[] mode = Offered("Mode=OneWay}\" Tag", "OneWay}\" Tag".Length);
        Assert.Equal(["OneTime", "OneWay", "TwoWay"], mode.Order(StringComparer.Ordinal));
        string[] item = Offered("<TextBlock Text=\"{x:Bind Title}\" x:Phase", "Title}\" x:Phase".Length);
        Assert.True(item.Contains("Title") && !item.Contains("greeting"), $"The template's data type's members were not what was offered: {string.Join(", ", item)}");
    }

    private void Go_to_declaration_from_x_bind_leads_to_the_member_the_element_and_the_cast_type()
    {
        XamlFile file = Read(Page);
        XamlTarget Target(string after)
        {
            int at = file.Text.IndexOf(after, StringComparison.Ordinal) + after.Length - 1;
            XamlSymbol? symbol = XamlNavigation.At(file, at);
            Assert.True(symbol is not null, $"Nothing was found under the caret after '{after}'");
            return symbol.Targets[0];
        }
        Assert.Equal("Shop.Name", Target("{x:Bind Shop.Name").Name);
        Assert.Equal("MainPage.Shout", Target("{x:Bind Shout").Name);
        Assert.Equal("MainPage.Save", Target("Click=\"{x:Bind Save").Name);
        Assert.Equal(XamlTargetKind.Element, Target("Tag=\"{x:Bind Title").Kind);
        Assert.Equal("App.Item", Target("((local:Item").Name);
        Assert.Equal("Format.Money", Target("local:Format.Money").Name);
        Assert.Equal(2, XamlNavigation.NameUses(file, "Title").Count);
    }

    private void Winuis_own_directives_are_checked_and_its_intrinsic_extensions_known()
    {
        XamlFile file = Read(Page.Replace("<TextBlock x:Name=\"Title\"", "<TextBlock x:Name=\"Title\" x:Load=\"Maybe\" Tag=\"{ThemeResource Missing}\"", StringComparison.Ordinal)
            .Replace("x:Phase=\"1\"", "x:Phase=\"first\"", StringComparison.Ordinal));
        // A library's resources come from its application, which this project has none of: the key is left unmarked, its extension known.
        string[] said = [.. XamlDiagnostics.Of(file).Select(d => $"{file.Text[d.Range.Start..d.Range.End]}: {d.Message}")];
        Assert.True(said.Length == 2 && said.Any(s => s.StartsWith("Maybe:", StringComparison.Ordinal)) && said.Any(s => s.StartsWith("first:", StringComparison.Ordinal)),
            $"x:Load's and x:Phase's mistakes were not each marked, or more was: {string.Join("; ", said)}");
        XamlFile unnamed = Read(Page.Replace("<TextBlock Text=\"{x:Bind Shout(", "<TextBlock x:Load=\"False\" Text=\"{x:Bind Shout(", StringComparison.Ordinal));
        XamlDiagnostic named = Assert.Single(XamlDiagnostics.Of(unnamed));
        Assert.Equal("x:Load", unnamed.Text[named.Range.Start..named.Range.End]);
        int brace = file.Text.IndexOf("{ThemeResource", StringComparison.Ordinal) + 1;
        string[] extensions = [.. XamlCompletion.At(file, brace)?.Items.Select(i => i.Label) ?? []];
        Assert.True(extensions.Contains("ThemeResource") && extensions.Contains("StaticResource") && extensions.Contains("x:Bind") && extensions.Contains("Binding"),
            $"WinUI's markup extensions were not offered after a brace: {string.Join(", ", extensions)}");
        int directive = file.Text.IndexOf("x:Load", StringComparison.Ordinal) + 2;
        string[] directives = [.. XamlCompletion.At(file, directive)?.Items.Select(i => i.Label) ?? []];
        Assert.True(directives.Contains("Load") && directives.Contains("Phase") && directives.Contains("DefaultBindMode"),
            $"WinUI's own directives were not offered: {string.Join(", ", directives)}");
    }

    private void A_markup_values_brackets_hold_its_commas_and_braces()
    {
        const string Value = "{x:Bind Shout(Name, ', }'), Mode=OneWay}";
        MarkupExtension read = MarkupExtension.Parse(Value, 0, Value.Length)!;
        Assert.Equal(["Shout(Name, ', }')", "OneWay"], read.Arguments.Select(a => a.Value));
        Assert.True(read.IsClosed, "The extension was not read to its closing brace");
    }
}
