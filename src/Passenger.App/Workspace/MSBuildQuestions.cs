using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Workspace;

/// <summary>A question of one project for MSBuild: the targets it runs, the properties, items and target results it
/// answers once they ran, and the properties it is given over the solution's.</summary>
public sealed record MSBuildQuestion(IReadOnlyList<string> Targets)
{
    public IReadOnlyList<string> Properties { get; init; } = [];

    public IReadOnlyList<string> Items { get; init; } = [];

    /// <summary>The targets whose results it answers, which MSBuild runs too.</summary>
    public IReadOnlyList<string> TargetResults { get; init; } = [];

    public IReadOnlyDictionary<string, string> Given { get; init; } = new Dictionary<string, string>();

    /// <summary>MSBuild's switches asking for the answer.</summary>
    public IReadOnlyList<string> Asking =>
        [.. Properties.Select(p => $"-getProperty:{p}"), .. Items.Select(i => $"-getItem:{i}"), .. TargetResults.Select(t => $"-getTargetResult:{t}")];

    /// <summary>The property MSBuild answers alone, its value bare, as it does one asked with nothing else; null where it
    /// answers in JSON.</summary>
    public string? Alone => Properties is [{ } alone] && Items.Count == 0 && TargetResults.Count == 0 ? alone : null;
}

/// <summary>MSBuild's questions of a project, each asked of the .NET CLI as the IDE's own question (no telemetry), in the
/// project's folder: its targets run with the globals given but their target framework, the question's own properties over
/// them and the framework where one is given; its answer read once, by <see cref="MSBuildAnswer"/>.</summary>
public static class MSBuildQuestions
{
    /// <summary>What MSBuild answers the question of the project, for <paramref name="framework"/> or the project as a
    /// whole.</summary>
    public static async Task<MSBuildAnswer> AskAsync(string project, string? framework, IReadOnlyDictionary<string, string> globals, MSBuildQuestion question,
        CancellationToken cancel)
    {
        ChildResult said = await ChildProcesses.RunAsync(ChildProcesses.DotnetQuestion(Arguments(project, framework, globals, question), Path.GetDirectoryName(project)!),
            cancel: cancel);
        return new MSBuildAnswer(said, question.Alone);
    }

    /// <summary>The .NET CLI's arguments that ask the question, which a target run for its work rather than its answer (an
    /// installer's) is given too.</summary>
    public static IReadOnlyList<string> Arguments(string project, string? framework, IReadOnlyDictionary<string, string> globals, MSBuildQuestion question)
    {
        Dictionary<string, string> given = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in globals.Where(g => !string.Equals(g.Key, MSBuildNames.TargetFramework, StringComparison.OrdinalIgnoreCase)).Concat(question.Given))
        {
            given[name] = value;
        }
        if (framework is not null)
        {
            given[MSBuildNames.TargetFramework] = framework;
        }
        return ["msbuild", project, "-nologo", .. question.Targets.Count > 0 ? [$"-target:{string.Join(';', question.Targets)}"] : Array.Empty<string>(),
            .. question.Asking, .. given.Select(g => $"-property:{g.Key}={g.Value}")];
    }
}

/// <summary>What MSBuild answered a question: how it ended and what it wrote, and the answer read from its output past any
/// line beside it there (a task's own words): the JSON of what was asked (<see cref="JsonMembers.ObjectIn"/>), or a
/// property asked alone, whose value comes bare as the output's last line.</summary>
public sealed class MSBuildAnswer
{
    // The members MSBuild's JSON answer holds what was asked in.
    private const string PropertiesMember = "Properties";
    private const string ItemsMember = "Items";
    private const string TargetResultsMember = "TargetResults";

    private readonly JsonElement? json;

    private readonly string? alone;

    private readonly string? bare;

    /// <summary>The answer of a run asking <paramref name="alone"/> alone where it names one, else asking in JSON.</summary>
    public MSBuildAnswer(ChildResult said, string? alone = null)
    {
        Said = said;
        this.alone = alone;
        if (alone is null)
        {
            json = JsonMembers.ObjectIn(said.Output);
        }
        else
        {
            bare = said.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        }
    }

    public ChildResult Said { get; }

    public bool Succeeded => Said.Succeeded;

    /// <summary>Whether MSBuild wrote an answer to read.</summary>
    public bool Answered => json is not null || bare is not null;

    /// <summary>A property's value as MSBuild answered it; null where it answered none of that name.</summary>
    public string? Property(string name) =>
        alone is null ? JsonMembers.Text(JsonMembers.Object(json, PropertiesMember), name)
        : string.Equals(name, alone, StringComparison.OrdinalIgnoreCase) ? bare : null;

    /// <summary>The properties answered with a value, by name in any letter case.</summary>
    public IReadOnlyDictionary<string, string> Properties
    {
        get
        {
            IEnumerable<KeyValuePair<string, string?>> answered = alone is null
                ? JsonMembers.Properties(json, PropertiesMember).Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => KeyValuePair.Create(p.Name, p.Value.GetString()))
                : [KeyValuePair.Create(alone, bare)];
            return answered.Where(p => p.Value is { Length: > 0 }).ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>An item type's items as MSBuild answered them, each with its metadata; none where it answered none.</summary>
    public IReadOnlyList<JsonElement> Items(string type) => JsonMembers.Items(JsonMembers.Object(json, ItemsMember), type);

    /// <summary>A target's result as MSBuild answered it (its Result and Items); null where it answered none.</summary>
    public JsonElement? TargetResult(string target) => JsonMembers.Object(JsonMembers.Object(json, TargetResultsMember), target);
}
