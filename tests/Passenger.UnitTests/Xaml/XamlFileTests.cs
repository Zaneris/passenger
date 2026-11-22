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

    [Fact]
    public void A_xaml_file_declares_its_class_types_and_settable_properties_and_deep_or_broken_text_ends_nothing() => Cases.All(
        ("the code behinds class is read from x class whatever prefix xamls language is declared with", () => Cases.Each<string>(
        [
            "x",
            "xaml",
        ], The_code_behinds_class_is_read_from_x_class_whatever_prefix_xamls_language_is_declared_with)),
        ("a nested style with no type asks each style above it once", A_nested_style_with_no_type_asks_each_style_above_it_once),
        ("a type nested as deep as passenger reads reads as far as a reference goes and ends nothing", A_type_nested_as_deep_as_passenger_reads_reads_as_far_as_a_reference_goes_and_ends_nothing),
        ("a file whose strings leave brackets open still declares its types", A_file_whose_strings_leave_brackets_open_still_declares_its_types),
        ("a colours casts deeper than a stack holds say no colour and end nothing", A_colours_casts_deeper_than_a_stack_holds_say_no_colour_and_end_nothing));

    private void The_code_behinds_class_is_read_from_x_class_whatever_prefix_xamls_language_is_declared_with(string prefix)
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

    private void A_nested_style_with_no_type_asks_each_style_above_it_once()
    {
        string text = string.Concat(Enumerable.Repeat("<Style Selector=\"^:a\">", 64)) + string.Concat(Enumerable.Repeat("</Style>", 64));
        XamlFile file = XamlFiles.Read("Styles.axaml", text, AvaloniaDialect.Instance);
        Assert.Null(file.StyleTargetAt(file.Tree.Elements[^1]));
    }

    private void A_type_nested_as_deep_as_passenger_reads_reads_as_far_as_a_reference_goes_and_ends_nothing()
    {
        string text = "class C { " + string.Concat(Enumerable.Repeat("A<", 900)) + "int" + new string('>', 900) + " x; }";
        SmallStack.Run(() =>
        {
            ClrType type = Assert.Single(SourceTypes.Read("/App/C.cs", text, "App", CSharpParsing.Default).Types);
            Assert.Equal("x", Assert.Single(type.Members).Name);
        });
    }

    private void A_file_whose_strings_leave_brackets_open_still_declares_its_types()
    {
        string text = "class C { string[] x = { " + string.Concat(Enumerable.Repeat("\"(\", ", 5_000)) + "}; }";
        SmallStack.Run(() => Assert.Equal("C", Assert.Single(SourceTypes.Read("/App/C.cs", text, "App", CSharpParsing.Default).Types).Name));
    }

    private void A_colours_casts_deeper_than_a_stack_holds_say_no_colour_and_end_nothing()
    {
        string casts = string.Concat(Enumerable.Repeat("(byte)", 100_000));
        SmallStack.Run(() =>
        {
            SyntaxNode root = CSharpParsing.Parse($"class C {{ object c = Color.FromRgb({casts}1, 2, 3); }}").GetRoot();
            Assert.Empty(Colours.InCSharp(root));
        });
    }
}
