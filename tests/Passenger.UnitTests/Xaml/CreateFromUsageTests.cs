using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>Create from usage in XAML: the member a binding lacks made in the very type it binds, and a converter a
/// resource key names made as a class that compiles.</summary>
public class CreateFromUsageTests
{
    private const string ViewModels = """
        namespace A
        {
            public class MainViewModel
            {
                public string Title { get; set; }
            }
        }

        namespace B
        {
            public class MainViewModel
            {
                public string Title { get; set; }
            }
        }

        """;

    [Fact]
    public void A_property_a_binding_lacks_is_made_in_the_type_of_its_full_name_where_its_file_declares_another_of_the_same_name()
    {
        const string Window = """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="using:B" x:DataType="vm:MainViewModel">
                <TextBlock Text="{Binding Subtitle}" />
            </Window>
            """;
        XamlFile file = XamlFiles.Read("MainWindow.axaml", Window, AvaloniaDialect.Instance, ("MainViewModel.cs", ViewModels));
        ClrType owner = Assert.IsType<ClrType>(file.Types.Find("B.MainViewModel"));
        XmlAttribute text = file.Tree.Elements.Single(e => e.LocalName == "TextBlock").Attribute("Text")!;
        XamlFix fix = Assert.IsType<XamlFix>(CreateFromUsage.Property(file, owner, "Subtitle", text, last: true));
        Assert.Equal(XamlFiles.PathOf("MainViewModel.cs"), fix.Path);

        string made = XamlFiles.Applied(ViewModels, fix.Edits(ViewModels));
        string[] spaces = made.Split("namespace B", StringSplitOptions.None);
        Assert.True(spaces.Length == 2 && !spaces[0].Contains("Subtitle", StringComparison.Ordinal) && spaces[1].Contains("public object Subtitle { get; set; }", StringComparison.Ordinal),
            $"Create property for a binding of B.MainViewModel did not make it in B's class, the binding still finding nothing:\n{made}");
    }

    [Theory]
    [InlineData("class", "@class")]
    [InlineData("event", "@event")]
    [InlineData("Upper", "Upper")]
    public void A_converter_made_for_a_key_that_is_a_csharp_keyword_names_its_class_as_csharp_escapes_it_and_compiles(string key, string declared)
    {
        string window = "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
            + $"    <TextBlock Text=\"{{Binding Title, Converter={{StaticResource {key}}}}}\" />\n</Window>\n";
        XamlFile file = XamlFiles.Read("MainWindow.axaml", window, AvaloniaDialect.Instance);
        XamlFix fix = Assert.IsType<XamlFix>(CreateFromUsage.Converter(file, key));
        XamlFileChange made = Assert.Single(fix.Others);
        Assert.Equal(XamlFiles.PathOf(key + ".cs"), made.Path);
        Assert.Contains($"public class {declared} : IValueConverter", made.Made, StringComparison.Ordinal);
        IReadOnlyList<Diagnostic> errors = Errors(made.Made!);
        Assert.True(errors.Count == 0, $"The converter made for the key '{key}' does not compile:\n{string.Join("\n", errors)}\n{made.Made}");
        Assert.Contains($"<local:{key} x:Key=\"{key}\" />", XamlFiles.Applied(window, fix.Edits(window)), StringComparison.Ordinal);
    }

    /// <summary>The errors a file of C# compiles with, Avalonia's IValueConverter declared beside it.</summary>
    private static IReadOnlyList<Diagnostic> Errors(string code)
    {
        const string Converters = """
            namespace Avalonia.Data.Converters;

            public interface IValueConverter
            {
                object? Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture);

                object? ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture);
            }
            """;
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Made", [CSharpSyntaxTree.ParseText(code), CSharpSyntaxTree.ParseText(Converters)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }
}
