using Passenger.App.Aspire;

namespace Passenger.UnitTests.Aspire;

/// <summary>The logins a deploy step needs read from each tool's own files: the Azure CLI's account from its profile, and
/// kubectl's current context from its configuration, the files its variable names first.</summary>
public sealed class DeployLoginsTests
{
    [Fact]
    public void An_azure_profile_gives_its_default_subscriptions_user_and_none_without_a_subscription()
    {
        const string Profile = "﻿{\"installationId\": \"x\", \"subscriptions\": [" +
            "{\"id\": \"1\", \"name\": \"Other\", \"isDefault\": false, \"user\": {\"name\": \"first@example.com\", \"type\": \"user\"}}," +
            "{\"id\": \"2\", \"name\": \"Chosen\", \"isDefault\": true, \"user\": {\"name\": \"chosen@example.com\", \"type\": \"user\"}}]}";
        Assert.Equal("chosen@example.com", DeployLogins.AzureAccountIn(Profile));
        Assert.Null(DeployLogins.AzureAccountIn("{\"installationId\": \"x\", \"subscriptions\": []}"));
        Assert.Null(DeployLogins.AzureAccountIn("not json"));
    }

    [Fact]
    public void A_kubectl_configuration_gives_its_top_level_current_context_and_none_where_it_is_empty()
    {
        Assert.Equal("kind-dev", DeployLogins.CurrentContextIn("apiVersion: v1\nclusters: []\ncurrent-context: \"kind-dev\" # chosen\nkind: Config\n"));
        Assert.Equal("prod", DeployLogins.CurrentContextIn("current-context: prod\r\n"));
        Assert.Null(DeployLogins.CurrentContextIn("apiVersion: v1\ncurrent-context: \"\"\n"));
        Assert.Null(DeployLogins.CurrentContextIn("contexts:\n- context:\n    current-context: nested\n"));
    }

    [Fact]
    public void Kubectls_files_are_those_its_variable_names_in_order_else_the_home_folders()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");
        Assert.Equal([Path.Combine(home, ".kube", "config")], DeployLogins.KubeConfigFiles(null, home));
        Assert.Equal(["/a/config", "/b/config"], DeployLogins.KubeConfigFiles($"/a/config{Path.PathSeparator}{Path.PathSeparator}/b/config", home));
    }
}
