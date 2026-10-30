using Passenger.Xaml;
using Passenger.Xaml.Dialects;
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
}
