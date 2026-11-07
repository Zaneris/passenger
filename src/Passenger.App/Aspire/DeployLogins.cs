using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>Whether this machine holds the login a deploy step needs, read from each tool's own files, never by running it
/// (the Azure CLI sends its telemetry after every command, and writes its logs and caches as it runs), nothing but an
/// account's name or a context's taken from them: the Azure CLI's account in its profile (azureProfile.json, which lists
/// the accounts az login made and holds no token), Helm's Kubernetes context in kubectl's configuration (its
/// current-context), each where the tool's own variable puts it, as the AppHost's own steps would find them.</summary>
public static partial class DeployLogins
{
    /// <summary>The Azure CLI's folder, in place of .azure in the home folder.</summary>
    public const string AzureConfigFolder = "AZURE_CONFIG_DIR";

    /// <summary>kubectl's configuration files, in place of .kube/config in the home folder, separated as PATH is.</summary>
    public const string KubeConfig = "KUBECONFIG";

    /// <summary>What a step of these needs is missing here, as words after its name ("needs the Azure CLI's login, …"), the
    /// engine's problem given where it does not answer; null when each login it needs is here.</summary>
    public static string? Missing(DeployNeeds needs, string? engineProblem)
    {
        string? why = needs.HasFlag(DeployNeeds.AzureLogin) ? AzureWhyNot() : null;
        why ??= needs.HasFlag(DeployNeeds.KubernetesContext) ? KubernetesWhyNot() : null;
        return why ?? (needs.HasFlag(DeployNeeds.ContainerEngine) && engineProblem is not null ? $"needs a container engine that answers: {engineProblem}" : null);
    }

    /// <summary>Steps missing the same said once: "deploy and deploy-api need the Azure CLI's login, …".</summary>
    public static string Said(IReadOnlyList<string> steps, string missing) =>
        steps.Count > 1 && missing.StartsWith("needs ", StringComparison.Ordinal) ? $"{Plural.Listed(steps)} need {missing["needs ".Length..]}" : $"{Plural.Listed(steps)} {missing}";

    /// <summary>Why the Azure CLI's login is not here: no az on PATH, or a profile that lists no account; null when it is.</summary>
    public static string? AzureWhyNot()
    {
        if (Programs.OnPath(OperatingSystem.IsWindows() ? "az.cmd" : "az") is null)
        {
            return "needs the Azure CLI's login, and no az is on PATH: install the Azure CLI, then run az login in the Terminal";
        }
        string folder = Environment.GetEnvironmentVariable(AzureConfigFolder) is { Length: > 0 } named ? Path.GetFullPath(named)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azure");
        string profile = Path.Combine(folder, "azureProfile.json");
        string? text = Read(profile);
        return text is not null && AzureAccountIn(text) is not null ? null
            : $"needs the Azure CLI's login, and az has none here ({PathNames.Shown(profile, null)} lists no account): run az login in the Terminal";
    }

    /// <summary>The account an Azure CLI profile's text holds, its default subscription's (else its first's) user; null
    /// where it lists none or does not read as one.</summary>
    public static string? AzureAccountIn(string profile)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(profile.TrimStart('﻿'));
            IReadOnlyList<JsonElement> subscriptions = JsonMembers.Items(read.RootElement, "subscriptions");
            if (subscriptions.Count == 0)
            {
                return null;
            }
            JsonElement chosen = subscriptions.FirstOrDefault(s => JsonMembers.Bool(s, "isDefault") == true, subscriptions[0]);
            return JsonMembers.Text(JsonMembers.Object(chosen, "user"), "name") ?? JsonMembers.Text(chosen, "name") ?? "an account";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Why Helm's Kubernetes context is not here: no helm on PATH, or kubectl's configuration sets no current
    /// context; null when it is.</summary>
    public static string? KubernetesWhyNot()
    {
        if (Programs.OnPath(OperatingSystem.IsWindows() ? "helm.exe" : "helm") is null)
        {
            return "needs Helm, and no helm is on PATH: install Helm, then deploy again";
        }
        IReadOnlyList<string> files = KubeConfigFiles(Environment.GetEnvironmentVariable(KubeConfig), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return files.Any(file => Read(file) is { } text && CurrentContextIn(text) is not null) ? null
            : $"needs a Kubernetes context, and {Plural.Listed([.. files.Select(file => PathNames.Shown(file, null))])} sets none: choose one with kubectl config use-context in the Terminal";
    }

    /// <summary>kubectl's configuration files: those its variable names, in order, else the one in the home folder.</summary>
    public static IReadOnlyList<string> KubeConfigFiles(string? named, string home) =>
        named is { Length: > 0 } && named.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } files
            ? files
            : [Path.Combine(home, ".kube", "config")];

    /// <summary>The context a kubectl configuration's text makes current, its top-level current-context, in YAML or in the
    /// JSON kubectl reads as YAML; null where it sets none.</summary>
    public static string? CurrentContextIn(string configuration)
    {
        if (configuration.TrimStart().StartsWith('{'))
        {
            return CurrentContextInJson(configuration);
        }
        foreach (string line in configuration.Split('\n'))
        {
            if (CurrentContextLine().Match(line) is { Success: true } found)
            {
                string value = found.Groups[1].Value;
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                value = (comment >= 0 ? value[..comment] : value).Trim().Trim('"', '\'');
                return value.Length > 0 ? value : null;
            }
        }
        return null;
    }

    private static string? CurrentContextInJson(string configuration)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(configuration);
            return JsonMembers.Text(read.RootElement, "current-context") is { Length: > 0 } named ? named : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A top-level current-context of a YAML mapping, its key as YAML may write it, bare or quoted.</summary>
    [GeneratedRegex("""^(?:current-context|"current-context"|'current-context')\s*:(.*)$""")]
    private static partial Regex CurrentContextLine();

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
