extern alias Analyzers;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Analyzers::Passenger.Analyzers;

namespace Passenger.UnitTests.Analyzers;

/// <summary>PSG9004 (<see cref="WordsResources"/>) over a .resx and its translations given as in a build: a translation
/// whose English changed since, or whose keys are not its English's, fails the build naming just words, as does one that
/// names other arguments than its English; one waiting for review after its English changed passes, and so does one in step.</summary>
public class WordsResourcesTests
{
    private const string Folder = "/project";

    [Fact]
    public void A_translation_out_of_step_with_its_English_fails_the_build_naming_what_to_do() => Cases.Each(
    [
        ("in step", Unit("Greeting", "Hello {name}", "translated", "Bonjour {name}"), null),
        ("its English changed since", Unit("Greeting", "Hello", "translated", "Bonjour"), "Greeting's English changed: run just words, which marks its translation for review"),
        ("its keys not its English's", Unit("Farewell", "Hello {name}", "translated", "Au revoir"), "its keys are not Things.resx's, in its order: run just words"),
        ("naming other arguments", Unit("Greeting", "Hello {name}", "translated", "Bonjour {nom}"), "Greeting's translation names {nom} where its English names {name}"),
        ("waiting for review, naming other arguments", Unit("Greeting", "Hello {name}", "needs-review-translation", "Bonjour {nom}"), null),
    ], row =>
    {
        (string _, string unit, string? said) = row;
        string[] found = [.. Run(unit).Where(d => d.Id == "PSG9004").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];
        string[] expected = said is null ? [] : [$"Things.fr.xlf: {said}"];
        Assert.True(found.SequenceEqual(expected), $"PSG9004 said [{string.Join("; ", found)}], not [{string.Join("; ", expected)}]");
    }, row => row.Item1);

    private static string Unit(string id, string source, string state, string target) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2">
          <file datatype="xml" source-language="en" target-language="fr" original="../Things.resx">
            <body>
              <trans-unit id="{id}">
                <source>{source}</source>
                <target state="{state}">{target}</target>
              </trans-unit>
            </body>
          </file>
        </xliff>
        """;

    /// <summary>The generator's diagnostics over Things.resx, whose one key is Greeting, "Hello {name}", and its French.</summary>
    private static ImmutableArray<Diagnostic> Run(string french)
    {
        const string English = """<root><data name="Greeting" xml:space="preserve"><value>Hello {name}</value></data></root>""";
        AdditionalText[] files = [new Text($"{Folder}/Words/Things.resx", English), new Text($"{Folder}/Words/xlf/Things.fr.xlf", french)];
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WordsResources().AsSourceGenerator()], files, optionsProvider: new Options());
        return driver.RunGenerators(CSharpCompilation.Create("Words"), TestContext.Current.CancellationToken).GetRunResult().Diagnostics;
    }

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    /// <summary>The project's root namespace and folder, as the build gives the generator.</summary>
    private sealed class Options : AnalyzerConfigOptionsProvider
    {
        private static readonly Values Project = new(new Dictionary<string, string>
        {
            ["build_property.RootNamespace"] = "Test",
            ["build_property.MSBuildProjectDirectory"] = Folder,
        });

        private static readonly Values None = new([]);

        public override AnalyzerConfigOptions GlobalOptions => Project;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => None;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => None;
    }

    private sealed class Values(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
    }
}
