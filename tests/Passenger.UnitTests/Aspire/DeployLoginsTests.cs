using Passenger.App.Aspire;

namespace Passenger.UnitTests.Aspire;

/// <summary>The logins a deploy step needs read from each tool's own files: the Azure CLI's account from its profile, and
/// kubectl's current context from its configuration, the files its variable names first.</summary>
public sealed class DeployLoginsTests
{
    [Fact]
    public void A_deploys_logins_are_read_from_the_Azure_CLIs_profile_and_kubectls_configuration_in_either_form() => Cases.All(
        ("an azure profile gives its default subscriptions user and none without a subscription", An_azure_profile_gives_its_default_subscriptions_user_and_none_without_a_subscription),
        ("a kubectl configuration gives its top level current context and none where it is empty", A_kubectl_configuration_gives_its_top_level_current_context_and_none_where_it_is_empty),
        ("a kubectl configuration in JSON gives its top level current context", A_kubectl_configuration_in_JSON_gives_its_top_level_current_context),
        ("kubectls files are those its variable names in order else the home folders", Kubectls_files_are_those_its_variable_names_in_order_else_the_home_folders));

    private void An_azure_profile_gives_its_default_subscriptions_user_and_none_without_a_subscription()
    {
        const string Profile = "﻿{\"installationId\": \"x\", \"subscriptions\": [" +
            "{\"id\": \"1\", \"name\": \"Other\", \"isDefault\": false, \"user\": {\"name\": \"first@example.com\", \"type\": \"user\"}}," +
            "{\"id\": \"2\", \"name\": \"Chosen\", \"isDefault\": true, \"user\": {\"name\": \"chosen@example.com\", \"type\": \"user\"}}]}";
        Assert.Equal("chosen@example.com", DeployLogins.AzureAccountIn(Profile));
        Assert.Null(DeployLogins.AzureAccountIn("{\"installationId\": \"x\", \"subscriptions\": []}"));
        Assert.Null(DeployLogins.AzureAccountIn("not json"));
    }

    private void A_kubectl_configuration_gives_its_top_level_current_context_and_none_where_it_is_empty()
    {
        Assert.Equal("kind-dev", DeployLogins.CurrentContextIn("apiVersion: v1\nclusters: []\ncurrent-context: \"kind-dev\" # chosen\nkind: Config\n"));
        Assert.Equal("prod", DeployLogins.CurrentContextIn("current-context: prod\r\n"));
        Assert.Null(DeployLogins.CurrentContextIn("apiVersion: v1\ncurrent-context: \"\"\n"));
        Assert.Null(DeployLogins.CurrentContextIn("contexts:\n- context:\n    current-context: nested\n"));
    }

    /// <summary>kubectl reads JSON as YAML, so a configuration of either says the same; a deploy needing the context is
    /// refused where this finds none.</summary>
    private void A_kubectl_configuration_in_JSON_gives_its_top_level_current_context()
    {
        Assert.Equal("demo", DeployLogins.CurrentContextIn("""{"apiVersion":"v1","kind":"Config","clusters":[],"current-context":"demo"}"""));
        Assert.Equal("demo", DeployLogins.CurrentContextIn("\n{\n  \"kind\": \"Config\",\n  \"current-context\": \"demo\",\n  \"users\": []\n}\n"));
        Assert.Null(DeployLogins.CurrentContextIn("""{"kind":"Config","current-context":""}"""));
        Assert.Null(DeployLogins.CurrentContextIn("""{"kind":"Config","contexts":[{"context":{"current-context":"nested"}}]}"""));
        Assert.Null(DeployLogins.CurrentContextIn("""{"kind":"Config","""));
    }

    private void Kubectls_files_are_those_its_variable_names_in_order_else_the_home_folders()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");
        Assert.Equal([Path.Combine(home, ".kube", "config")], DeployLogins.KubeConfigFiles(null, home));
        Assert.Equal(["/a/config", "/b/config"], DeployLogins.KubeConfigFiles($"/a/config{Path.PathSeparator}{Path.PathSeparator}/b/config", home));
    }
}
