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
        ("a colours casts deeper than a stack holds say no colour and end nothing", A_colours_casts_deeper_than_a_stack_holds_say_no_colour_and_end_nothing),
        ("the c sharp colour finder makes nothing of a file naming no colour however large and finds each in one", The_csharp_colour_finder_makes_nothing_of_a_file_naming_no_colour_however_large_and_finds_each_in_one));

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

    /// <summary>The finder runs after every parse of every open C# file: one naming no colour or brush type costs nothing at
    /// any size, where walking its tree made every node of it and a string of every call's receiver.</summary>
    private void The_csharp_colour_finder_makes_nothing_of_a_file_naming_no_colour_however_large_and_finds_each_in_one()
    {
        static string Code(int members) =>
            "class C\n{\n" + string.Concat(Enumerable.Range(0, members).Select(i => $"    int M{i}(int a) => System.Math.Max(a, {i}) + this.Other.Call(a);\n")) + "}\n";
        SyntaxNode small = CSharpParsing.Parse(Code(10)).GetRoot();
        SyntaxNode large = CSharpParsing.Parse(Code(5_000)).GetRoot();
        Assert.Empty(Colours.InCSharp(small));
        Assert.Empty(Colours.InCSharp(large));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Colours.InCSharp(small);
        long few = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        Colours.InCSharp(large);
        long many = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(few == 0 && many == 0, $"Finding no colour allocated {few:N0} bytes in a file of 10 members and {many:N0} in one of 5,000, after every parse of every open C# file");

        SyntaxNode coloured = CSharpParsing.Parse(Code(10) + "class D { object c = Avalonia.Media.Color.FromRgb(1, 2, 3); object n = Colors.Red; object b = Brush.Parse(\"#102030\"); }").GetRoot();
        Assert.Equal([ColourForm.Call, ColourForm.Named, ColourForm.Text], Colours.InCSharp(coloured).Select(c => c.Form));
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
