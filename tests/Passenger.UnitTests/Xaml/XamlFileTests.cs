using Microsoft.CodeAnalysis;
using Passenger.Languages.CSharp;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>What a XAML file says of its code-behind, read by the namespaces its prefixes are declared for.</summary>
public class XamlFileTests
{
    private const string CodeBehind = "namespace Shop;\n\npublic partial class MainWindow\n{\n}\n";

    [Theory]
    [InlineData("x")]
    [InlineData("xaml")]
    public void The_code_behinds_class_is_read_from_x_class_whatever_prefix_xamls_language_is_declared_with(string prefix)
    {
        string text = $"<Window xmlns=\"https://github.com/avaloniaui\" xmlns:{prefix}=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
            + $"        {prefix}:Class=\"Shop.MainWindow\" {prefix}:Name=\"Shell\" />\n";
        XamlFile file = XamlFiles.Read("MainWindow.axaml", text, AvaloniaDialect.Instance, ("MainWindow.axaml.cs", CodeBehind));
        Assert.True(file.ClassName == "Shop.MainWindow", $"{prefix}:Class read as naming {file.ClassName ?? "no class"}, so the file's code-behind, its "
            + "bindings' data context, renames and its named elements' fields go unfound");
        Assert.Equal("Shop.MainWindow", file.ClassType?.FullName);
        Assert.Equal("Shop.MainWindow", XamlFile.ClassAttribute(XmlTree.Parse(text))?.Value);
        Assert.Equal("Shell", XamlFile.NameAttribute(file.Tree.Root!)?.Value);
    }

    [Fact]
    public void An_x_prefix_declared_for_another_namespace_names_no_class()
    {
        const string Text = "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"using:Shop\" x:Class=\"Shop.MainWindow\" />\n";
        XamlFile file = XamlFiles.Read("MainWindow.axaml", Text, AvaloniaDialect.Instance, ("MainWindow.axaml.cs", CodeBehind));
        Assert.True(file.ClassName is null, $"x:Class with x declared for a CLR namespace read as naming {file.ClassName}, a class XAML's compilers never make it");
        Assert.Null(XamlFile.ClassAttribute(file.Tree));
    }

    [Fact]
    public void A_nested_style_with_no_type_asks_each_style_above_it_once()
    {
        string text = string.Concat(Enumerable.Repeat("<Style Selector=\"^:a\">", 64)) + string.Concat(Enumerable.Repeat("</Style>", 64));
        XamlFile file = XamlFiles.Read("Styles.axaml", text, AvaloniaDialect.Instance);
        Assert.Null(file.StyleTargetAt(file.Tree.Elements[^1]));
    }

    [Fact]
    public void A_type_deeper_than_a_stack_holds_reads_as_far_as_a_reference_goes_and_ends_nothing()
    {
        string text = "class C { " + string.Concat(Enumerable.Repeat("A<", 100_000)) + "int" + new string('>', 100_000) + " x; }";
        SmallStack.Run(() =>
        {
            ClrType type = Assert.Single(SourceTypes.Read("/App/C.cs", text, "App", CSharpParsing.Default).Types);
            Assert.Equal("x", Assert.Single(type.Members).Name);
        });
    }

    [Fact]
    public void A_colours_casts_deeper_than_a_stack_holds_say_no_colour_and_end_nothing()
    {
        string casts = string.Concat(Enumerable.Repeat("(byte)", 100_000));
        SmallStack.Run(() =>
        {
            SyntaxNode root = CSharpParsing.Parse($"class C {{ object c = Color.FromRgb({casts}1, 2, 3); }}").GetRoot();
            Assert.Empty(Colours.InCSharp(root));
        });
    }
}
