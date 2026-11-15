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
}
