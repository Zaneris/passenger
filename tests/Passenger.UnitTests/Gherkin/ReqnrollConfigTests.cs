using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

public sealed class ReqnrollConfigTests
{
    [Fact]
    public void Reqnrolls_file_names_the_features_language_the_binding_culture_and_the_skeleton_style()
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
    }

    [Fact]
    public void With_nothing_said_reqnroll_writes_cucumber_expressions_and_specflow_regular_expressions()
    {
        Assert.Equal(SkeletonStyle.CucumberExpression, ReqnrollConfig.Parse("{}", specFlow: false).Skeleton);
        Assert.Equal(SkeletonStyle.Regex, ReqnrollConfig.Parse("{}", specFlow: true).Skeleton);
        Assert.Null(ReqnrollConfig.Parse("not json", specFlow: false).FeatureLanguage);
    }

    [Fact]
    public void A_projects_folder_is_read_for_reqnroll_json_before_specflow_json()
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
    }

    [Fact]
    public void A_folder_with_no_file_writes_steps_as_its_framework_does()
    {
        ReqnrollConfig none = ReqnrollConfig.In(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

        Assert.Equal((SkeletonStyle.CucumberExpression, SkeletonStyle.Regex), (none.SkeletonFor(specFlow: false), none.SkeletonFor(specFlow: true)));
        Assert.Equal(SkeletonStyle.AsyncRegex, ReqnrollConfig.Parse("""{ "trace": { "stepDefinitionSkeletonStyle": "AsyncRegexAttribute" } }""", false) with { Written = true } is var said ? said.SkeletonFor(specFlow: false) : default);
    }

    /// <summary>The culture a feature's steps' arguments are converted in, for each shape of configuration and each way a
    /// feature names its language, as Reqnroll 3.3.4 itself answered when asked (its JsonConfigurationLoader reading the
    /// configuration, Gherkin's reader with Reqnroll's dialect provider the feature, its FeatureInfo taking the language its
    /// generator writes, and FeatureContext's binding culture from them): the binding culture named, else the feature's own
    /// language line's, else the project's feature language as written, else en-US.</summary>
    [Theory]
    [InlineData("{}", "Feature: F", "en-US")]
    [InlineData("{}", "# language: de\nFunktionalität: F", "de")]
    [InlineData("{}", "# language: de-AT\nFunktionalität: F", "de-AT")]
    [InlineData("{}", "# language: en-au\nPretty much: F", "en-AU")]
    [InlineData("""{ "language": { "feature": "de-AT" } }""", "Funktionalität: F", "de-AT")]
    [InlineData("""{ "language": { "feature": "de-AT" } }""", "# language: de\nFunktionalität: F", "de")]
    [InlineData("""{ "language": { "feature": "de-AT" } }""", "# language: de-CH\nFunktionalität: F", "de-CH")]
    [InlineData("""{ "language": { "feature": "de-AT" } }""", "# language: en\nFeature: F", "en")]
    [InlineData("""{ "language": { "feature": "de" } }""", "Funktionalität: F", "de")]
    [InlineData("""{ "language": { "feature": "en-GB" } }""", "Feature: F", "en-GB")]
    [InlineData("""{ "language": { "feature": "" } }""", "Feature: F", "en-US")]
    [InlineData("""{ "language": { "binding": "fr-FR" } }""", "# language: de\nFunktionalität: F", "fr-FR")]
    [InlineData("""{ "language": { "feature": "de-AT", "binding": "en-GB" } }""", "Funktionalität: F", "en-GB")]
    [InlineData("""{ "language": { "feature": "de-AT" }, "bindingCulture": { "name": "nl-NL" } }""", "# language: en\nFeature: F", "nl-NL")]
    [InlineData("""{ "language": { "binding": "en-GB" }, "bindingCulture": { "name": "nl-NL" } }""", "Feature: F", "nl-NL")]
    [InlineData("""{ "bindingCulture": { "name": "" } }""", "Feature: F", "en-US")]
    [InlineData("""{ "bindingCulture": { "name": "" } }""", "# language: fr\nFonctionnalité: F", "fr")]
    public void A_features_arguments_are_converted_in_the_culture_reqnrolls_runtime_takes(string json, string feature, string culture)
    {
        ReqnrollConfig config = ReqnrollConfig.Parse(json, specFlow: false);
        FeatureReading reading = FeatureReading.Read(feature + "\n", GherkinDialects.Default(config.FeatureLanguage));

        Assert.Empty(reading.Errors);
        Assert.Equal(culture, ProjectSteps.CultureOf(config.BindingCultureOf(reading)).Name);
    }

    [Fact]
    public void SpecFlows_file_names_its_cultures_as_reqnrolls_does()
    {
        ReqnrollConfig config = ReqnrollConfig.Parse("""{ "language": { "feature": "de-AT" }, "bindingCulture": { "name": " " } }""", specFlow: true);

        Assert.Equal<(string?, string?)>(("de-AT", null), (config.FeatureLanguage, config.BindingCulture));
        Assert.Equal("de-AT", config.BindingCultureOf(FeatureReading.Read("Funktionalität: F\n", GherkinDialects.Default(config.FeatureLanguage))));
    }
}
