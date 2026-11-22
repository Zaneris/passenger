using System.Text.Json;
using Passenger.App.Aspire;

namespace Passenger.UnitTests.Aspire;

/// <summary>An AppHost's pipeline as its backchannel lists it: read from its answer, its deploy steps deploy and each
/// environment's last, what each needs from the steps it runs, and only an Aspire that lists its steps asked.</summary>
public sealed class AppHostPipelineTests
{
    /// <summary>A Docker Compose environment's and an Azure Container Apps environment's steps, as Aspire 13.6 declares them.</summary>
    private static readonly AppHostStep[] Steps =
    [
        new("deploy-prereq", null, [], [], null),
        new("build-prereq", null, [], [], null),
        new("check-container-runtime", null, [], [], null),
        new("build-api", null, ["build-prereq"], ["build-compute"], "api"),
        new("prepare-compose", null, ["build-api"], [], "compose"),
        new("docker-compose-up-compose", null, ["prepare-compose"], ["docker-compose-up"], "compose"),
        new("validate-azure-login", null, ["deploy-prereq"], [], "azure"),
        new("create-provisioning-context", null, ["validate-azure-login"], [], "azure"),
        new("provision-azure-bicep-resources", null, ["create-provisioning-context"], ["provision-infra"], "azure"),
        new("deploy-web", null, ["provision-azure-bicep-resources", "build-api"], ["deploy-compute"], "web"),
        new("print-web-summary", null, ["deploy-web"], ["print-summary"], "web"),
        new("helm-deploy-k8s", null, ["deploy-prereq"], ["helm-deploy"], "k8s"),
        new(AppHostPipeline.Deploy, null, ["deploy-prereq", "build-api", "docker-compose-up-compose", "validate-azure-login", "create-provisioning-context",
            "provision-azure-bicep-resources", "deploy-web", "print-web-summary", "helm-deploy-k8s"], [], null),
    ];

    [Fact]
    public void An_AppHosts_deploy_steps_are_read_from_its_backchannel_with_what_each_needs_and_only_an_Aspire_that_lists_them_asked() => Cases.All(
        ("steps are read from the backchannels answer as it names them", Steps_are_read_from_the_backchannels_answer_as_it_names_them),
        ("deploy steps are deploy and each environments last step in the apphosts order", Deploy_steps_are_deploy_and_each_environments_last_step_in_the_apphosts_order),
        ("an apphost whose deploy runs only aspires own steps offers none", An_apphost_whose_deploy_runs_only_aspires_own_steps_offers_none),
        ("a step needs what the steps it runs need", A_step_needs_what_the_steps_it_runs_need),
        ("only an aspire that lists its steps is asked for them", () => Cases.Each<(string?, bool)>(
        [
            ("13.6.1", true),
            ("13.7.0-preview.1.25500.1", true),
            ("13.5.4", false),
            (null, false),
        ], row => Only_an_aspire_that_lists_its_steps_is_asked_for_them(row.Item1, row.Item2))),
        ("a step is run by name with its files written to the folder given", A_step_is_run_by_name_with_its_files_written_to_the_folder_given));

    private void Deploy_steps_are_deploy_and_each_environments_last_step_in_the_apphosts_order()
    {
        Assert.Equal([AppHostPipeline.Deploy, "docker-compose-up-compose", "deploy-web", "helm-deploy-k8s"], AppHostPipeline.DeploySteps(Steps).Select(s => s.Name));
    }

    private void An_apphost_whose_deploy_runs_only_aspires_own_steps_offers_none()
    {
        AppHostStep[] nothing = [new("deploy-prereq", null, [], [], null), new(AppHostPipeline.Deploy, null, ["deploy-prereq"], [], null)];
        Assert.Empty(AppHostPipeline.DeploySteps(nothing));
        Assert.Empty(AppHostPipeline.DeploySteps([new("publish", null, [], [], null)]));
    }

    private void A_step_needs_what_the_steps_it_runs_need()
    {
        Assert.Equal(DeployNeeds.ContainerEngine, AppHostPipeline.Needs("docker-compose-up-compose", Steps));
        Assert.Equal(DeployNeeds.AzureLogin | DeployNeeds.ContainerEngine, AppHostPipeline.Needs("deploy-web", Steps));
        Assert.Equal(DeployNeeds.KubernetesContext, AppHostPipeline.Needs("helm-deploy-k8s", Steps));
        Assert.Equal(DeployNeeds.AzureLogin | DeployNeeds.ContainerEngine, AppHostPipeline.Needs(AppHostPipeline.Deploy, Steps));
        Assert.Equal(DeployNeeds.None, AppHostPipeline.Needs("deploy-prereq", Steps));
    }

    private void Steps_are_read_from_the_backchannels_answer_as_it_names_them()
    {
        using JsonDocument answer = JsonDocument.Parse("""
            {"Steps":[{"Name":"deploy","Description":"Aggregation step for all deploy operations.","DependsOn":["deploy-prereq"],"Tags":[],"ResourceName":null},
                      {"Name":"docker-compose-up-env","Description":null,"DependsOn":["prepare-env"],"Tags":["docker-compose-up"],"ResourceName":"env"},
                      {"Description":"no name"}]}
            """);
        IReadOnlyList<AppHostStep> steps = AppHostPipeline.Read(answer.RootElement);
        Assert.Equal(["deploy", "docker-compose-up-env"], steps.Select(s => s.Name));
        Assert.Equal("Aggregation step for all deploy operations.", steps[0].Description);
        Assert.Equal(["prepare-env"], steps[1].DependsOn);
        Assert.Equal(["docker-compose-up"], steps[1].Tags);
        Assert.Equal("env", steps[1].Resource);
    }

    private void Only_an_aspire_that_lists_its_steps_is_asked_for_them(string? version, bool asked)
    {
        Assert.Equal(asked, AppHostPipeline.WhyNotListed(version) is null);
    }

    private void A_step_is_run_by_name_with_its_files_written_to_the_folder_given()
    {
        Assert.Equal(["--operation", "publish", "--step", "deploy", "--output-path", "out"], AppHostPipeline.RunArguments("deploy", "out"));
        Assert.Equal(["--operation", "inspect", "--list-steps", "true"], AppHostPipeline.ListArguments);
    }
}
