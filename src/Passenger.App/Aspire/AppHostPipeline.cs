using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>A step of an AppHost's pipeline as the AppHost declares it: its name, its words, the steps it runs after, its
/// tags and the resource it belongs to.</summary>
public sealed record AppHostStep(string Name, string? Description, IReadOnlyList<string> DependsOn, IReadOnlyList<string> Tags, string? Resource);

/// <summary>What a step needs of this machine, as the steps it runs say: the Azure CLI's login (Aspire's Azure environments
/// take its credential when they deploy), a container engine that answers (an image built or pushed, Docker Compose's
/// up), or a Kubernetes context for Helm.</summary>
[Flags]
public enum DeployNeeds
{
    None = 0,
    AzureLogin = 1,
    ContainerEngine = 2,
    KubernetesContext = 4,
}

/// <summary>An AppHost's pipeline as Aspire's CLI reads it: listed by the AppHost itself, run as <c>aspire do --list-steps</c>
/// runs it (<see cref="ListArguments"/>), which an AppHost of Aspire 13.6 or later answers over the CLI's backchannel
/// without running any step, and one before it refuses as an operation it does not know; its deploy steps those the
/// <c>deploy</c> step runs after, as Aspire's own words for it say every deploy step is, and what each needs from the steps
/// it runs. Pure.</summary>
public static class AppHostPipeline
{
    /// <summary>The step that writes an AppHost's files (Docker Compose's, Bicep, Helm's charts).</summary>
    public const string Publish = "publish";

    /// <summary>The step every deploy step is required by.</summary>
    public const string Deploy = "deploy";

    /// <summary>What an AppHost's backchannel says it can do, listing its steps among them.</summary>
    public const string ListsSteps = "pipeline-steps.v2";

    /// <summary>The backchannel's requests, as the AppHost's target names them.</summary>
    public const string CapabilitiesRequest = "GetCapabilitiesAsync";

    public const string StepsRequest = "GetPipelineStepsAsync";

    public const string StopRequest = "RequestStopAsync";

    /// <summary>The first Aspire whose AppHost lists its steps.</summary>
    public static readonly Version FirstListing = new(13, 6);

    /// <summary>Aspire's own step for an Azure environment's login, and for the container engine's.</summary>
    private const string AzureLoginStep = "validate-azure-login";

    private const string EngineStep = "check-container-runtime";

    /// <summary>Aspire's tags of steps that run on the container engine (images built and pushed, Docker Compose's up),
    /// and of Helm's deploy.</summary>
    private static readonly string[] EngineTags = ["build-compute", "push-container-image", "docker-compose-up"];

    private const string HelmTag = "helm-deploy";

    /// <summary>Aspire's own steps and tags that run within every deploy (its prerequisites, image builds and pushes, the
    /// summaries printed after), never offered alone.</summary>
    private static readonly HashSet<string> Within =
    [
        "deploy-prereq", "build", "build-prereq", "push", "push-prereq", Publish, "publish-prereq", "process-parameters", "diagnostics",
        "validate-compute-environments", "before-start", EngineStep, "destroy", "destroy-prereq",
    ];

    private static readonly string[] WithinTags = ["build-compute", "push-container-image", "print-summary"];

    /// <summary>What an AppHost is run with to list its steps, as Aspire's CLI runs it: an operation that runs none.</summary>
    public static IReadOnlyList<string> ListArguments { get; } = ["--operation", "inspect", "--list-steps", "true"];

    /// <summary>What an AppHost is run with to run a step and those it runs after, its files written to a folder.</summary>
    public static IReadOnlyList<string> RunArguments(string step, string output) => ["--operation", "publish", "--step", step, "--output-path", output];

    /// <summary>Why an AppHost of an Aspire version is not asked for its steps: one not known, or one before the first
    /// that lists them; null when it is.</summary>
    public static string? WhyNotListed(string? hostingVersion)
    {
        if (hostingVersion is null || !Version.TryParse(hostingVersion.Split('-', '+')[0], out Version? version))
        {
            return "its Aspire version is not known, so its deploy steps are not read";
        }
        return version < FirstListing
            ? $"its Aspire, {hostingVersion}, lists no deploy steps: Aspire {FirstListing.Major}.{FirstListing.Minor} is the first that does"
            : null;
    }

    /// <summary>The steps an AppHost's answer lists, in its order (each after those it runs after); none where it lists none.</summary>
    public static IReadOnlyList<AppHostStep> Read(JsonElement answer) =>
        [.. JsonMembers.Items(answer, "Steps", anyCase: true)
            .Select(step => (Name: JsonMembers.Text(step, "Name", anyCase: true), Step: step))
            .Where(each => each.Name is { Length: > 0 })
            .Select(each => new AppHostStep(each.Name!, JsonMembers.Text(each.Step, "Description", anyCase: true),
                JsonMembers.Texts(each.Step, "DependsOn", anyCase: true), JsonMembers.Texts(each.Step, "Tags", anyCase: true),
                JsonMembers.Text(each.Step, "ResourceName", anyCase: true)))];

    /// <summary>The deploy steps offered: <c>deploy</c>, then each step it runs after that the AppHost's environments
    /// declare and no other of them runs after (an environment's last, which runs the rest of it), in the AppHost's order;
    /// none where it has nothing to deploy.</summary>
    public static IReadOnlyList<AppHostStep> DeploySteps(IReadOnlyList<AppHostStep> steps)
    {
        if (steps.FirstOrDefault(s => s.Name == Deploy) is not { } deploy)
        {
            return [];
        }
        HashSet<string> after = [.. deploy.DependsOn];
        AppHostStep[] own = [.. steps.Where(s => after.Contains(s.Name) && !Within.Contains(s.Name) && !s.Tags.Any(tag => WithinTags.Contains(tag)))];
        HashSet<string> runBefore = [.. own.SelectMany(s => Closure(s.Name, steps).Where(before => before.Name != s.Name).Select(before => before.Name))];
        AppHostStep[] last = [.. own.Where(s => !runBefore.Contains(s.Name))];
        return last.Length == 0 ? [] : [deploy, .. last];
    }

    /// <summary>A step and every step it runs after.</summary>
    public static IReadOnlyList<AppHostStep> Closure(string step, IReadOnlyList<AppHostStep> steps)
    {
        Dictionary<string, AppHostStep> named = steps.DistinctBy(s => s.Name).ToDictionary(s => s.Name, StringComparer.Ordinal);
        List<AppHostStep> found = [];
        HashSet<string> seen = [];
        Stack<string> next = new([step]);
        while (next.TryPop(out string? name))
        {
            if (seen.Add(name) && named.TryGetValue(name, out AppHostStep? each))
            {
                found.Add(each);
                foreach (string before in each.DependsOn)
                {
                    next.Push(before);
                }
            }
        }
        return found;
    }

    /// <summary>What a step needs of this machine, from the steps it runs: Azure's login step, an engine's step or one of
    /// its tags, Helm's deploy (but under an Azure environment, whose AKS cluster's credentials it fetches itself).</summary>
    public static DeployNeeds Needs(string step, IReadOnlyList<AppHostStep> steps)
    {
        IReadOnlyList<AppHostStep> runs = Closure(step, steps);
        DeployNeeds needs = DeployNeeds.None;
        if (runs.Any(s => s.Name == AzureLoginStep))
        {
            needs |= DeployNeeds.AzureLogin;
        }
        if (runs.Any(s => s.Name == EngineStep || s.Tags.Any(tag => EngineTags.Contains(tag))))
        {
            needs |= DeployNeeds.ContainerEngine;
        }
        if (!needs.HasFlag(DeployNeeds.AzureLogin) && runs.Any(s => s.Tags.Contains(HelmTag)))
        {
            needs |= DeployNeeds.KubernetesContext;
        }
        return needs;
    }
}
