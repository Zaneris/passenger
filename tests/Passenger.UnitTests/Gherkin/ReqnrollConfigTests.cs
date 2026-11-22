using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

public sealed class ReqnrollConfigTests
{
    [Fact]
    public void Reqnrolls_configuration_names_the_feature_language_binding_culture_and_skeleton_style_as_its_runtime_reads_them() => Cases.All(
        ("reqnrolls file names the features language the binding culture and the skeleton style", () =>
        {
            ReqnrollConfig config = ReqnrollConfig.Parse(
                """
                {
                  "$schema": "https://schemas.reqnroll.net/reqnroll-config-latest.json",
                  // comments are allowed
                  "language": { "feature": "de-AT", "binding": "en-US" },
                  "bindingCulture": { "name": "de-DE" },
                  "trace": { "stepDefinitionSkeletonStyle": "AsyncRegexAttribute" },
                  "bindingAssemblies": [ { "assembly": "Shared.Steps" } ],
                }
                """, specFlow: false);

            Assert.Equal("de-AT", config.FeatureLanguage);
            Assert.Equal("de-DE", config.BindingCulture);
            Assert.Equal(SkeletonStyle.AsyncRegex, config.Skeleton);
            Assert.Equal(["Shared.Steps"], config.BindingAssemblies);
            Assert.False(config.IsSpecFlow);
        }),
        ("with nothing said reqnroll writes cucumber expressions and specflow regular expressions", () =>
        {
            Assert.Equal(SkeletonStyle.CucumberExpression, ReqnrollConfig.Parse("{}", specFlow: false).Skeleton);
            Assert.Equal(SkeletonStyle.Regex, ReqnrollConfig.Parse("{}", specFlow: true).Skeleton);
            Assert.Null(ReqnrollConfig.Parse("not json", specFlow: false).FeatureLanguage);
        }),
        ("a projects folder is read for reqnroll json before specflow json", () =>
        {
            string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;
            try
            {
                Assert.Null(ReqnrollConfig.In(folder).FeatureLanguage);
                File.WriteAllText(Path.Combine(folder, "specflow.json"), """{ "language": { "feature": "fr" } }""");
                Assert.Equal(("fr", true), (ReqnrollConfig.In(folder).FeatureLanguage, ReqnrollConfig.In(folder).IsSpecFlow));
                File.WriteAllText(Path.Combine(folder, "reqnroll.json"), """{ "language": { "feature": "nl" } }""");
                Assert.Equal(("nl", false), (ReqnrollConfig.In(folder).FeatureLanguage, ReqnrollConfig.In(folder).IsSpecFlow));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }),
        ("a folder with no file writes steps as its framework does", () =>
        {
            ReqnrollConfig none = ReqnrollConfig.In(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

            Assert.Equal((SkeletonStyle.CucumberExpression, SkeletonStyle.Regex), (none.SkeletonFor(specFlow: false), none.SkeletonFor(specFlow: true)));
            Assert.Equal(SkeletonStyle.AsyncRegex, ReqnrollConfig.Parse("""{ "trace": { "stepDefinitionSkeletonStyle": "AsyncRegexAttribute" } }""", false) with { Written = true } is var said ? said.SkeletonFor(specFlow: false) : default);
        }),
        ("a file read as no feature says the language it is read in whence its feature words and how to name its own", () =>
        {
            const string English = "@calc\nFeature: Calculator\n\nScenario: S\n  Given one\n";
            const string Naming = "name the language it is written in on its first line, as # language: en names English and # language: de German, or the language "
                + "the project's features are written in as language.feature in its ";

            ReqnrollConfig austrian = ReqnrollConfig.Parse("""{ "language": { "feature": "de-AT" } }""", specFlow: false) with { Written = true };
            Assert.Equal("Reqnroll reads this file in de-AT, the language.feature of its project's reqnroll.json, where a feature begins with Funktionalität: or Funktion:, "
                + "and finds none: " + Naming + "reqnroll.json", austrian.NoFeatureWhy(Unread(English, austrian)));

            ReqnrollConfig quiet = ReqnrollConfig.Parse("""{ "bindingAssemblies": [] }""", specFlow: false) with { Written = true };
            Assert.Equal("Reqnroll reads this file in en-US, as its project's reqnroll.json names no language.feature, where a feature begins with Feature:, Business Need: "
                + "or Ability:, and finds none: " + Naming + "reqnroll.json", quiet.NoFeatureWhy(Unread("Funktionalität: Laden\n", quiet)));

            ReqnrollConfig none = ReqnrollConfig.Parse("{}", specFlow: false);
            Assert.StartsWith("Reqnroll reads this file in en-US, as its project has no reqnroll.json naming a language.feature, where",
                none.NoFeatureWhy(Unread("Funktionalität: Laden\n", none)), StringComparison.Ordinal);

            ReqnrollConfig specFlow = ReqnrollConfig.Parse("""{ "language": { "feature": "fr-FR" } }""", specFlow: true) with { Written = true };
            Assert.StartsWith("SpecFlow reads this file in fr-FR, the language.feature of its project's specflow.json, where a feature begins with Fonctionnalité:",
                specFlow.NoFeatureWhy(Unread(English, specFlow)), StringComparison.Ordinal);
            Assert.EndsWith("as language.feature in its specflow.json", specFlow.NoFeatureWhy(Unread(English, specFlow)), StringComparison.Ordinal);

            Assert.Equal("Reqnroll reads this file in de, as its # language: line names, where a feature begins with Funktionalität: or Funktion:, and finds none: "
                + "name the language it is written in on that line, as # language: en names English and # language: de German",
                quiet.NoFeatureWhy(Unread("# language: de\n" + English, quiet)));
        }),
        ("specFlows file names its cultures as reqnrolls does", () =>
        {
            ReqnrollConfig config = ReqnrollConfig.Parse("""{ "language": { "feature": "de-AT" }, "bindingCulture": { "name": " " } }""", specFlow: true);

            Assert.Equal<(string?, string?)>(("de-AT", null), (config.FeatureLanguage, config.BindingCulture));
            Assert.Equal("de-AT", config.BindingCultureOf(FeatureReading.Read("Funktionalität: F\n", GherkinDialects.Default(config.FeatureLanguage))));
        }),
        ("a feature's arguments are converted in the culture Reqnroll's runtime takes", () => Cases.Each(
        [
            ("{}", "Feature: F", "en-US"),
            ("{}", "# language: de-AT\nFunktionalität: F", "de-AT"),
            ("""{ "language": { "feature": "de-AT" } }""", "# language: de\nFunktionalität: F", "de"),
            ("""{ "language": { "feature": "de-AT", "binding": "en-GB" } }""", "Funktionalität: F", "en-GB"),
            ("""{ "language": { "binding": "en-GB" }, "bindingCulture": { "name": "nl-NL" } }""", "Feature: F", "nl-NL"),
            ("""{ "bindingCulture": { "name": "" } }""", "# language: fr\nFonctionnalité: F", "fr"),
        ], row => CultureTaken(row.Item1, row.Item2, row.Item3))));

    /// <summary>The culture a feature's steps' arguments are converted in, for each shape of configuration and each way a
    /// feature names its language, as Reqnroll 3.3.4 itself answered when asked (its JsonConfigurationLoader reading the
    /// configuration, Gherkin's reader with Reqnroll's dialect provider the feature, its FeatureInfo taking the language its
    /// generator writes, and FeatureContext's binding culture from them): the binding culture named, else the feature's own
    /// language line's, else the project's feature language as written, else en-US.</summary>
    private static void CultureTaken(string json, string feature, string culture)
    {
        ReqnrollConfig config = ReqnrollConfig.Parse(json, specFlow: false);
        FeatureReading reading = FeatureReading.Read(feature + "\n", GherkinDialects.Default(config.FeatureLanguage));

        Assert.Empty(reading.Errors);
        Assert.Equal(culture, ProjectSteps.CultureOf(config.BindingCultureOf(reading)).Name);
    }

    /// <summary>A file's text read as its project's configuration has it read, its reader finding no feature in it.</summary>
    private static FeatureReading Unread(string text, ReqnrollConfig config)
    {
        FeatureReading reading = FeatureReading.Read(text, GherkinDialects.Default(config.FeatureLanguage));
        Assert.Contains(reading.Errors, e => e.NoFeature);
        return reading;
    }

}
