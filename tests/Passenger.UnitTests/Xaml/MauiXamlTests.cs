using Microsoft.CodeAnalysis.CSharp;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>What MAUI's XAML reads that Avalonia's and WPF's do not: a file that declares no namespace has MAUI's implicit
/// ones (its default the global namespace, which maps MAUI's own and what the project maps to it; <c>x:</c> XAML 2009's),
/// bindings compile where an <c>x:DataType</c> names a type, a value in braces that starts with no markup extension is a
/// C# expression, and the Windows head's WinUI XAML is no MAUI XAML.</summary>
public sealed class MauiXamlTests
{
    private const string Page = """
        <ContentPage x:Class="App.MainPage" x:DataType="local:MainViewModel" xmlns:local="clr-namespace:App">
            <Label x:Name="Title" Text="{Binding Greeting}" />
            <VerticalStackLayout x:DataType="{x:Null}">
                <Label Text="{Binding Greeting}" />
            </VerticalStackLayout>
        </ContentPage>
        """;

    [Fact]
    public void A_file_that_declares_no_namespace_reads_xaml_2009s_directives_and_mauis_global_namespace_by_default()
    {
        XmlTree tree = XmlTree.Parse(Page);
        XmlElement root = tree.Root!;
        Assert.Equal(XamlDialect.Xaml2009Uri, XamlFile.DeclaredUri(root, "x"));
        Assert.Equal(MauiDialect.GlobalUri, XamlFile.DeclaredUri(root, string.Empty));
        Assert.Equal("App.MainPage", XamlFile.ClassAttribute(tree)?.Value);
        Assert.Equal("Title", XamlFile.NameAttribute(root.Children[0])?.Value);
        XamlFileSummary summary = XamlFileSummary.Of("MainPage.xaml", tree, (_, _) => null);
        Assert.Equal("App.MainPage", summary.Class);
        Assert.Equal("Title", Assert.Single(summary.Names).Name);
    }

    [Fact]
    public void A_file_that_declares_its_default_namespace_leaves_a_prefix_it_does_not_declare_undeclared()
    {
        XmlTree tree = XmlTree.Parse("<Window xmlns=\"https://github.com/avaloniaui\" x:Class=\"App.MainWindow\" />");
        Assert.Null(XamlFile.DeclaredUri(tree.Root, "x"));
        Assert.Null(XamlFile.ClassAttribute(tree));
    }

    [Fact]
    public void Mauis_global_namespace_maps_its_own_namespaces_and_those_the_project_maps_to_it()
    {
        const string Mapped = """
            [assembly: Microsoft.Maui.Controls.XmlnsDefinition("http://schemas.microsoft.com/dotnet/2021/maui", "App.Controls")]
            [assembly: Microsoft.Maui.Controls.XmlnsDefinition("http://schemas.microsoft.com/dotnet/maui/global", "App.Pages")]

            namespace App.Controls
            {
                public class Card
                {
                }
            }

            namespace App.Pages
            {
                public class Home
                {
                }
            }
            """;
        SourceTypes source = SourceTypes.Read(XamlFiles.PathOf("Mapped.cs"), Mapped, "App", CSharpParseOptions.Default);
        TypeSystem types = new([], [source], "App", implied: MauiDialect.Instance.ImpliedDefinitions("App"));
        Assert.Equal(["App.Controls", "App.Pages"], types.ClrNamespacesOf(MauiDialect.GlobalUri).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(types.XmlnsDefinitions, d => d.ClrNamespace.Contains("://", StringComparison.Ordinal));
    }

    [Fact]
    public void Bindings_compile_where_an_x_data_type_names_a_type_and_not_below_one_of_x_null()
    {
        XamlFile file = XamlFiles.Read("MainPage.xaml", Page, MauiDialect.Instance);
        XmlElement[] labels = [.. file.Tree.Elements.Where(e => e.LocalName == "Label")];
        Assert.True(file.CompilesBindingsAt(labels[0]), "A binding under the page's x:DataType was read as not compiled");
        Assert.False(file.CompilesBindingsAt(labels[1]), "A binding under x:DataType=\"{x:Null}\" was read as compiled");
    }

    [Theory]
    [InlineData("{Greeting}", true)]
    [InlineData("{Owner.Name}", true)]
    [InlineData("{$'{First} {Last}'}", true)]
    [InlineData("{(s, e) => Count++}", true)]
    [InlineData("{Count GT 0}", true)]
    [InlineData("{= Binding}", true)]
    [InlineData("{this.(Grid.Row)}", true)]
    [InlineData("{Binding Greeting}", false)]
    [InlineData("{StaticResource Primary}", false)]
    [InlineData("{x:Static local:Motto.Text}", false)]
    [InlineData("{}{Literal}", false)]
    [InlineData("Plain", false)]
    public void A_value_in_braces_that_starts_with_no_markup_extension_is_a_csharp_expression(string value, bool expression)
    {
        HashSet<string> extensions = ["Binding", "StaticResource"];
        Assert.Equal(expression, MauiDialect.Instance.IsExpression(value, extensions.Contains));
        Assert.False(WpfDialect.Instance.IsExpression(value, extensions.Contains), $"WPF read {value} as a C# expression");
    }

    [Fact]
    public void The_windows_heads_winui_xaml_is_left_out_of_mauis()
    {
        Assert.True(MauiDialect.Instance.LeavesOut("Platforms/Windows/App.xaml"));
        Assert.False(MauiDialect.Instance.LeavesOut("App.xaml"));
        Assert.False(MauiDialect.Instance.LeavesOut("Platforms/WindowsHelpers/Card.xaml"));
        Assert.False(WpfDialect.Instance.LeavesOut("Platforms/Windows/App.xaml"));
    }
}
