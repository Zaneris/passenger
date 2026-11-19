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

    [Fact]
    public void A_property_is_made_in_a_type_whose_namespace_has_more_parts_than_a_stack_holds()
    {
        string space = "a" + string.Concat(Enumerable.Repeat(".a", 30_000));
        string models = $"namespace {space}\n{{\n    public class Order\n    {{\n        public string Title {{ get; set; }}\n    }}\n}}\n";
        string window = $"<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:vm=\"using:{space}\" x:DataType=\"vm:Order\">"
            + "<TextBlock Text=\"{Binding Subtitle}\" /></Window>";
        SmallStack.Run(() =>
        {
            XamlFile file = XamlFiles.Read("MainWindow.axaml", window, AvaloniaDialect.Instance, ("Models.cs", models));
            ClrType owner = Assert.IsType<ClrType>(file.Types.Find(space + ".Order"));
            XmlAttribute text = file.Tree.Elements.Single(e => e.LocalName == "TextBlock").Attribute("Text")!;
            XamlFix fix = Assert.IsType<XamlFix>(CreateFromUsage.Property(file, owner, "Subtitle", text, last: true));
            Assert.Contains("public object Subtitle { get; set; }", XamlFiles.Applied(models, fix.Edits(models)), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("Shop", "@event", "Shop.event", "using:Shop", "vm:event")]
    [InlineData("Shop.@class", "Order", "Shop.class.Order", "using:Shop.class", "vm:Order")]
    public void A_property_a_binding_lacks_is_made_in_its_type_where_the_type_or_its_namespace_is_named_with_an_escaped_keyword(
        string space, string declared, string fullName, string uri, string dataType)
    {
        string models = $$"""
            namespace {{space}}
            {
                public class {{declared}}
                {
                    public string Title { get; set; }
                }
            }

            """;
        string window = $"""
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="{uri}" x:DataType="{dataType}">
                <TextBlock Text="{"{"}Binding Subtitle{"}"}" />
            </Window>
            """;
        XamlFile file = XamlFiles.Read("MainWindow.axaml", window, AvaloniaDialect.Instance, ("Models.cs", models));
        ClrType owner = Assert.IsType<ClrType>(file.Types.Find(fullName));
        XmlAttribute text = file.Tree.Elements.Single(e => e.LocalName == "TextBlock").Attribute("Text")!;
        XamlFix? fix = CreateFromUsage.Property(file, owner, "Subtitle", text, last: true);
        Assert.True(fix is not null, $"Create property for a binding of {fullName}, declared as {declared} in {space}, offered nothing, so the binding still finds nothing");
        string made = XamlFiles.Applied(models, fix.Edits(models));
        Assert.True(made.Contains("public object Subtitle { get; set; }", StringComparison.Ordinal),
            $"Create property for a binding of {fullName} did not make it in {declared}'s class:\n{made}");
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

    /// <summary>A converter's key holding marks names its class and file whole, as C# reads a name, and what it makes compiles.</summary>
    [Fact]
    public void A_converter_made_for_a_key_holding_marks_names_its_class_and_file_whole()
    {
        const string Key = "मूल्यConverter";
        string window = "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
            + $"    <TextBlock Text=\"{{Binding Price, Converter={{StaticResource {Key}}}}}\" />\n</Window>\n";
        XamlFile file = XamlFiles.Read("MainWindow.axaml", window, AvaloniaDialect.Instance);
        XamlFix fix = Assert.IsType<XamlFix>(CreateFromUsage.Converter(file, Key));
        XamlFileChange made = Assert.Single(fix.Others);
        Assert.True(made.Path == XamlFiles.PathOf(Key + ".cs"), $"The converter for {Key} was made as {Path.GetFileName(made.Path)}, its name cut at its marks");
        Assert.Contains($"public class {Key} : IValueConverter", made.Made, StringComparison.Ordinal);
        IReadOnlyList<Diagnostic> errors = Errors(made.Made!);
        Assert.True(errors.Count == 0, $"The converter made for the key '{Key}' does not compile:\n{string.Join("\n", errors)}\n{made.Made}");
        Assert.Contains($"<local:{Key} x:Key=\"{Key}\" />", XamlFiles.Applied(window, fix.Edits(window)), StringComparison.Ordinal);
    }

    /// <summary>A converter made in a project whose other file declares global usings of the namespaces it takes writes no
    /// using of its own for them, and compiles with the project's.</summary>
    [Fact]
    public void A_converter_writes_no_using_a_global_using_in_another_file_of_the_project_brings_in()
    {
        const string Usings = "global using System;\nglobal using System.Globalization;\nglobal using Avalonia.Data.Converters;\n";
        string window = "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
            + "    <TextBlock Text=\"{Binding Title, Converter={StaticResource ShoutConverter}}\" />\n</Window>\n";
        XamlFile file = XamlFiles.Read("MainWindow.axaml", window, AvaloniaDialect.Instance, ("GlobalUsings.cs", Usings));
        string made = Assert.Single(Assert.IsType<XamlFix>(CreateFromUsage.Converter(file, "ShoutConverter")).Others).Made!;
        Assert.True(!made.Contains("using ", StringComparison.Ordinal),
            $"The converter wrote usings the project's other file brings in for every file, which the compiler marks unneeded:\n{made}");
        IReadOnlyList<Diagnostic> errors = Errors(made, Usings);
        Assert.True(errors.Count == 0, $"The converter made without the usings does not compile with the project's global ones:\n{string.Join("\n", errors)}\n{made}");
    }

    /// <summary>A converter's namespace is written as its view's code-behind writes its own, block or file-scoped, the
    /// code-behind's namespace named with marks.</summary>
    [Theory]
    [InlineData("namespace गिनती\n{\n    public partial class MainWindow\n    {\n    }\n}\n", false)]
    [InlineData("namespace गिनती;\n\npublic partial class MainWindow\n{\n}\n", true)]
    public void A_converter_writes_its_namespace_as_a_code_behind_named_with_marks_writes_its_own(string behind, bool scoped)
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("passenger-unit-");
        try
        {
            string view = Path.Combine(folder.FullName, "MainWindow.axaml");
            File.WriteAllText(view + ".cs", behind);
            string window = "<Window xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
                + "    <TextBlock Text=\"{Binding Title, Converter={StaticResource ShoutConverter}}\" />\n</Window>\n";
            XamlFile file = new(view, XmlTree.Parse(window), XamlFiles.Project(AvaloniaDialect.Instance));
            string made = Assert.Single(Assert.IsType<XamlFix>(CreateFromUsage.Converter(file, "ShoutConverter")).Others).Made!;
            Assert.True(made.Contains(scoped ? ";\n\npublic class ShoutConverter" : "\n{\n    public class ShoutConverter", StringComparison.Ordinal),
                $"The converter's namespace is not written {(scoped ? "file-scoped" : "in a block")} as its code-behind writes its own:\n{made}");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>The errors a file of C# compiles with, Avalonia's IValueConverter declared beside it, and the project's other
    /// files' code when given.</summary>
    private static IReadOnlyList<Diagnostic> Errors(string code, string others = "")
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
        CSharpCompilation compilation = CSharpCompilation.Create("Made", [CSharpSyntaxTree.ParseText(code), CSharpSyntaxTree.ParseText(Converters), CSharpSyntaxTree.ParseText(others)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }
}
