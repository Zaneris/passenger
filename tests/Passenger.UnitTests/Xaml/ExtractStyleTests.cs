using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>Extract Style into a resource dictionary of another file: what the setters it moves name by a prefix is
/// named there by the namespaces that file declares, those it lacks declared on its root.</summary>
public class ExtractStyleTests
{
    private const string Code = """
        namespace App
        {
            public static class Sizes
            {
                public const double Wide = 120;
            }
        }

        namespace App.Controls
        {
            public class Box
            {
                public double Width { get; set; }

                public double Height { get; set; }
            }
        }

        """;

    private const string Window = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:App"
                xmlns:c="clr-namespace:App.Controls">
            <c:Box Width="{x:Static local:Sizes.Wide}" Height="20" />
        </Window>
        """;

    [Theory]
    [InlineData("", "local", "c")]
    [InlineData(" xmlns:app=\"clr-namespace:App\" xmlns:local=\"clr-namespace:Other\"", "app", "c")]
    [InlineData(" xmlns:ctl=\"clr-namespace:App.Controls\" xmlns:xaml=\"http://schemas.microsoft.com/winfx/2006/xaml\"", "local", "ctl")]
    public void A_setter_value_naming_a_type_by_a_prefix_is_written_with_the_prefix_the_receiving_dictionary_declares_its_namespace_with(
        string declared, string prefix, string controls)
    {
        string dictionary = $"<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"{declared}>\n</ResourceDictionary>\n";
        XamlFile file = XamlFiles.Read("MainWindow.xaml", Window, WpfDialect.Instance, ("Code.cs", Code));
        (StyleSource? source, string? why) = XamlRefactorings.StyleAt(file, Window.IndexOf("<c:Box", StringComparison.Ordinal) + 2);
        Assert.True(source is not null, why);
        IReadOnlyList<XamlFileChange> changes = XamlRefactorings.ExtractStyle(file, source, "WideBox", ["Width", "Height"],
            new ResourcePlace(XamlFiles.PathOf("Styles.xaml"), "Styles.xaml <ResourceDictionary>"));

        XamlFileChange styles = Assert.Single(changes, c => c.Path == XamlFiles.PathOf("Styles.xaml"));
        string made = XamlFiles.Applied(dictionary, styles.Edits!(dictionary));
        string x = declared.Contains("xmlns:xaml", StringComparison.Ordinal) ? "xaml" : "x";
        Assert.True(made.Contains($"<Setter Property=\"Width\" Value=\"{{{x}:Static {prefix}:Sizes.Wide}}\" />", StringComparison.Ordinal),
            $"The moved setter does not name Sizes by a prefix the dictionary declares, so the dictionary no longer builds:\n{made}");
        XmlElement root = XmlTree.Parse(made).Root!;
        foreach ((string uri, string declaredAs) in ((string, string)[])[("clr-namespace:App", prefix), ("clr-namespace:App.Controls", controls), (XamlDialect.XamlUri, x)])
        {
            Assert.True(root.Attributes.Where(a => a.Prefix == "xmlns" && a.Value == uri).Select(a => a.LocalName).SequenceEqual([declaredAs]),
                $"The dictionary does not declare {uri} once, as {declaredAs}:\n{made}");
        }
        Assert.Contains($"TargetType=\"{controls}:Box\"", made, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"20\" />", made, StringComparison.Ordinal);
    }
}
