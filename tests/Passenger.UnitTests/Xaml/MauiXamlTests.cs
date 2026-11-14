using Microsoft.CodeAnalysis.CSharp;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
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

    [Theory]
    [InlineData("{Greeting}", "Greeting")]
    [InlineData(" {= Owner.Name } ", "Owner.Name")]
    [InlineData("{Count GT 0 AND Count lte 9}", "Count > 0 && Count <= 9")]
    [InlineData("{$'{First} {Last}'}", "$\"{First} {Last}\"")]
    [InlineData("{'it\\'s' + 'say \"hi\"'}", "\"it's\" + \"say \\\"hi\\\"\"")]
    [InlineData("{A &amp;&amp; B}", "A && B")]
    [InlineData("{'GT' + Total}", "\"GT\" + Total")]
    [InlineData("{Owner.", "Owner.")]
    [InlineData("{$'{IsOn ? 'on' : 'off'} now'}", "$\"{(IsOn ? \"on\" : \"off\")} now\"")]
    [InlineData("{$'{Name ?? 'none'} {Price:F2} {A?.B}'}", "$\"{Name ?? \"none\"} {Price:F2} {A?.B}\"")]
    [InlineData("{IsOn ? 'on' : 'off'}", "IsOn ? \"on\" : \"off\"")]
    public void An_expressions_code_is_read_as_mauis_source_generator_reads_it(string value, string code)
    {
        Assert.Equal(code, MauiExpressionCode.Of(value)?.Code);
    }

    [Fact]
    public void A_prefixed_type_is_written_by_its_csharp_name_and_an_attached_property_read_as_its_getters_call()
    {
        Dictionary<string, string> types = new() { ["local:Helper"] = "global::App.Helper" };
        MauiExpressionCode code = MauiExpressionCode.Of("{local:Helper.Shout(Name) + label.(Grid.Row) + \"local:Helper\"}", types)!;
        Assert.Equal("global::App.Helper.Shout(Name) + Grid.GetRow(label) + \"local:Helper\"", code.Code);
        Assert.Equal(["local:Helper"], MauiExpressionCode.PrefixedNames(MauiExpressionCode.Of("{local:Helper.Shout(x:Null) + \"vm:Person\"}")!.Code));
        int row = code.Code.IndexOf("Row", StringComparison.Ordinal);
        Assert.Equal("{local:Helper.Shout(Name) + label.(Grid.Row)".IndexOf("Row", StringComparison.Ordinal), code.WrittenAt(row));
        int label = code.Code.IndexOf("label", StringComparison.Ordinal);
        Assert.Equal("{local:Helper.Shout(Name) + ".Length, code.WrittenAt(label));
        Assert.Equal("{local:".Length, code.WrittenAt(code.Code.IndexOf("Helper", StringComparison.Ordinal)));
        Assert.Equal("{this.(Grid.Row)}", "{" + MauiExpressionCode.Of("{this.(Grid.Row)}")!.Code.Replace("Grid.GetRow(this)", "this.(Grid.Row)", StringComparison.Ordinal) + "}");
    }

    [Fact]
    public void Each_place_of_an_expressions_code_is_a_place_of_its_value_as_written()
    {
        MauiExpressionCode code = MauiExpressionCode.Of("{A &amp;&amp; B GT 'c'}")!;
        Assert.Equal("A && B > \"c\"", code.Code);
        Assert.Equal(1, code.Start);
        Assert.Equal(22, code.End);
        Assert.Equal(14, code.WrittenAt(code.Code.IndexOf('B', StringComparison.Ordinal)));
        Assert.Equal(code.Code.IndexOf('B', StringComparison.Ordinal), code.CodeAt(14));
        Assert.Equal(16, code.WrittenAt(code.Code.IndexOf('>', StringComparison.Ordinal)));
        Assert.Equal(code.Code.IndexOf('&', StringComparison.Ordinal), code.CodeAt(3));
        Assert.Null(MauiExpressionCode.Of("{}"));
        Assert.Null(MauiExpressionCode.Of("Plain"));
    }

    [Fact]
    public void A_caret_in_an_expression_being_typed_finds_it_with_the_page_its_x_data_type_and_whether_its_first_name_is_still_open()
    {
        const string Typed = """
            <ContentPage x:Class="App.MainPage" x:DataType="local:MainViewModel" xmlns:local="clr-namespace:App" Title="{">
                <Label Text="{Owner.Na" Clicked="{(s, e) => Count++}" />
                <Label Text="{Gr}" />
                <Label Text="Plain" Style="{{StaticResource Headline}" />
            </ContentPage>
            """;
        const string Code = """
            namespace App
            {
                public partial class MainPage { }

                public class MainViewModel
                {
                    public string Greeting { get; set; } = "";
                }
            }
            """;
        XamlFile file = XamlFiles.Read("MainPage.xaml", Typed, MauiDialect.Instance, ("MainPage.cs", Code));
        (XamlExpression owner, bool ownerHead) = XamlExpressions.At(file, Typed.IndexOf("Owner.Na", StringComparison.Ordinal) + "Owner.Na".Length)!.Value;
        Assert.False(ownerHead, "A caret past the expression's first name was read as in it");
        Assert.Equal("Owner.Na", owner.Code.Code);
        Assert.Equal("App.MainPage", owner.RootType);
        Assert.Equal("App.MainViewModel", owner.DataType);
        (XamlExpression greeting, bool greetingHead) = XamlExpressions.At(file, Typed.IndexOf("{Gr", StringComparison.Ordinal) + 2)!.Value;
        Assert.True(greetingHead, "A caret in the expression's first name was read as past it");
        Assert.Equal("Gr", greeting.Code.Code);
        Assert.Null(XamlExpressions.At(file, Typed.IndexOf("Plain", StringComparison.Ordinal) + 2));
        Assert.Null(XamlExpressions.At(file, Typed.IndexOf("{StaticResource ", StringComparison.Ordinal) + "{StaticResource ".Length));
        (XamlExpression title, bool titleHead) = XamlExpressions.At(file, Typed.IndexOf("Title=\"{", StringComparison.Ordinal) + "Title=\"{".Length)!.Value;
        Assert.True(titleHead, "A caret right after an expression's brace was read as past its first name");
        Assert.Equal("App.MainViewModel", title.DataType);
        Assert.Equal(["{Gr}"], XamlExpressions.In(file).Select(e => e.Attribute.Value).Where(v => v.StartsWith("{G", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_namespaces_target_platform_is_read_and_a_project_builds_for_it_only_where_its_framework_names_that_platform()
    {
        Assert.Equal("Android", MauiDialect.Instance.TargetPlatformOf("clr-namespace:Android.Widget;assembly=Mono.Android;targetPlatform=Android"));
        Assert.Null(MauiDialect.Instance.TargetPlatformOf("clr-namespace:App.Views"));
        Assert.Null(WpfDialect.Instance.TargetPlatformOf("clr-namespace:Android.Widget;targetPlatform=Android"));
        Assert.True(MauiDialect.Instance.BuildsFor("net11.0-android", "Android"));
        Assert.True(MauiDialect.Instance.BuildsFor("net11.0-ios27.0", "iOS"));
        Assert.True(MauiDialect.Instance.BuildsFor("net11.0-windows10.0.19041.0", "WinUI"));
        Assert.False(MauiDialect.Instance.BuildsFor("net11.0-android", "iOS"));
        Assert.False(MauiDialect.Instance.BuildsFor("net11.0", "Android"));
        Assert.False(MauiDialect.Instance.BuildsFor(null, "Android"));
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
