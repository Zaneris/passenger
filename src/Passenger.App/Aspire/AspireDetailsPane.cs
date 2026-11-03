using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>A resource's Details tab in the Aspire window: the resource (its type, state and health, what it waits for
/// while it waits, its source, when it started and stopped), its health reports, its addresses (links to the browser),
/// what it waits for and references and what waits for and references it (links to those resources, <paramref name="choose"/>),
/// as the AppHost's model relates them, its environment and properties (a secret's value masked by <see cref="ContainerSecrets"/>'
/// rule, one the AppHost marks sensitive whatever its name, and an argument it marks so whatever is before it) and its
/// volumes. Read from the resources as the window last had them.</summary>
public sealed class AspireDetailsPane(Action<Uri> browse, Action<string> open, Action<string> choose) : DetailsTables
{
    /// <summary>The resource shown, or null.</summary>
    public AspireResource? Resource { get; private set; }

    /// <summary>Whether the resource shown was said left unstarted.</summary>
    private bool shownUnstarted;

    /// <summary>What the other resources said of the one shown (<see cref="RelatedBy"/>) and the states of those it waits for, as shown.</summary>
    private string shownRelated = string.Empty;

    /// <summary>Shows a resource's details, or says to choose one; one its run left unstarted (<paramref name="leftUnstarted"/>,
    /// as its configuration says Don't start) is Not started, never a failure. The resource shown already, which an AppHost's
    /// update that did not change it gives again, is left as it is, its tables never made again to find nothing changed.</summary>
    public void Show(AspireResource? resource, bool leftUnstarted = false, IReadOnlyList<AspireResource>? all = null)
    {
        UiThread.Check();
        IReadOnlyList<AspireResource> others = all ?? [];
        string related = resource is null ? string.Empty : Related(resource, others);
        if (resource is not null && ReferenceEquals(resource, Resource) && leftUnstarted == shownUnstarted && related == shownRelated)
        {
            return;
        }
        Resource = resource;
        shownUnstarted = leftUnstarted;
        shownRelated = related;
        if (resource is null)
        {
            Say("Choose a resource to see its state, health, addresses and environment");
            return;
        }
        // Shown again as each of the AppHost's updates comes: tables that show what they showed stay, a press on a link kept.
        Show(() => Fill(resource, leftUnstarted, others));
    }

    private void Fill(AspireResource resource, bool leftUnstarted, IReadOnlyList<AspireResource> others)
    {
        JsonElement data = resource.Data;
        IReadOnlyList<(string Name, string Type)> own = Relations(resource);
        List<(string, Control)> about = [("Type", Words(JsonMembers.Text(data, "resourceType") ?? JsonMembers.Text(data, "type") ?? "Unknown"))];
        about.Add(("State", leftUnstarted ? State(AspireAppRun.NotStarted, null) : State(resource.State ?? "Unknown", Health(resource))));
        if (leftUnstarted)
        {
            about.Add(("Why", Words(AspireAppRun.NotStartedWhy, brush: TokenKeys.TextMuted)));
        }
        else if (resource.State == Waiting && WaitingFor(own, others) is { Length: > 0 } waiting)
        {
            about.Add(("Why", Words($"Waiting for {waiting}", brush: TokenKeys.TextMuted)));
        }
        if (resource.Source is { } source)
        {
            about.Add(("Source", Path.IsPathFullyQualified(source)
                ? Link(Path.GetFileName(source), source, () => open(source), "aspireSource")
                : Words(source, code: true)));
        }
        foreach ((string name, string field) in (ReadOnlySpan<(string, string)>)[("Started", "startTimestamp"), ("Stopped", "stopTimestamp")])
        {
            if (JsonMembers.Text(data, field) is { } said && DateTimeOffset.TryParse(said, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at))
            {
                about.Add((name, Words(ContainerDetailsPane.When(at))));
            }
        }
        Section("Resource", about);
        if (JsonMembers.Properties(data, "healthReports") is { Count: > 0 } reports)
        {
            Section("Health", [.. reports.Select(report => (report.Name, (Control)Report(report.Value)))]);
        }
        List<(string, Control)> addresses = [];
        foreach (JsonElement url in JsonMembers.Items(data, "urls"))
        {
            if (JsonMembers.Text(url, "url") is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out Uri? target))
            {
                continue;
            }
            string name = JsonMembers.Text(url, "displayName") ?? JsonMembers.Text(url, "name") ?? target.Scheme;
            bool inactive = JsonMembers.Bool(url, "isInactive") is true;
            addresses.Add((name, target.Scheme is "http" or "https" && !inactive
                ? Link(target.AbsoluteUri, target.AbsoluteUri, () => browse(target), "aspireDetailsUrl")
                : Words(target.AbsoluteUri, brush: inactive ? TokenKeys.TextMuted : null)));
        }
        if (addresses.Count > 0)
        {
            Section("URLs", addresses);
        }
        List<(string, Control)> relationships = [.. own.Select(r => (Own(r.Type), (Control)ResourceLink(r.Name, others)))];
        relationships.AddRange(RelatedBy(resource, others).Select(r => (Theirs(r.Type), (Control)ResourceLink(r.Name, others))));
        if (relationships.Count > 0)
        {
            Section("Relationships", relationships);
        }
        if (JsonMembers.Properties(data, "environment") is { Count: > 0 } environment)
        {
            Section("Environment", [.. environment.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => (e.Name, (Control)Value(e.Name, e.Value)))]);
        }
        if (JsonMembers.Properties(data, "properties") is { Count: > 0 } properties)
        {
            Dictionary<string, string> titles = JsonMembers.Properties(data, "propertyMetadata")
                .Where(m => JsonMembers.Text(m.Value, "displayName") is { Length: > 0 })
                .ToDictionary(m => m.Name, m => JsonMembers.Text(m.Value, "displayName")!, StringComparer.Ordinal);
            Section("Properties", [.. properties.Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => (titles.GetValueOrDefault(p.Name) ?? p.Name, (Control)Value(p.Name, p.Value)))]);
        }
        if (JsonMembers.Items(data, "volumes") is { Count: > 0 } volumes)
        {
            Section("Volumes", [.. volumes.Select(v => (JsonMembers.Text(v, "target") ?? "?", (Control)Words(
                $"{JsonMembers.Text(v, "mountType")} {JsonMembers.Text(v, "source")}{(JsonMembers.Bool(v, "isReadOnly") is true ? " (read-only)" : string.Empty)}".Trim())))]);
        }
    }

    /// <summary>Aspire's state of a resource held until what it waits for is ready.</summary>
    private const string Waiting = "Waiting";

    /// <summary>The AppHost's kinds of relationship a resource has to another: it waits for it, references it, or is its child.</summary>
    private const string WaitFor = "WaitFor";
    private const string Reference = "Reference";
    private const string ParentOf = "Parent";

    /// <summary>What a resource says of those it relates to, by their names and the kind of each relationship.</summary>
    private static IReadOnlyList<(string Name, string Type)> Relations(AspireResource resource) =>
        [.. JsonMembers.Items(resource.Data, "relationships").Select(r => (JsonMembers.Text(r, "resourceName") ?? "?", JsonMembers.Text(r, "type") ?? string.Empty))];

    /// <summary>What the other resources say of this one: each that relates to it, by the name it shows, with the kind.</summary>
    private static IEnumerable<(string Name, string Type)> RelatedBy(AspireResource resource, IReadOnlyList<AspireResource> others) =>
        others.Where(other => !ReferenceEquals(other, resource) && other.Name != resource.Name)
            .SelectMany(other => Relations(other).Where(r => resource.IsNamed(r.Name)).Select(r => (other.DisplayName, r.Type)))
            .Distinct();

    /// <summary>Of those a resource waits for, the ones not yet running, each with its state; all of them where none says.</summary>
    private static string WaitingFor(IReadOnlyList<(string Name, string Type)> own, IReadOnlyList<AspireResource> others)
    {
        List<(string Name, AspireResource? Resource)> waited = [.. own.Where(r => r.Type == WaitFor).Select(r => (r.Name, others.FirstOrDefault(o => o.IsNamed(r.Name))))];
        List<string> unready = [.. waited.Where(w => w.Resource is not { State: "Running", HealthStatus: null or "Healthy" })
            .Select(w => w.Resource?.State is { } state ? $"{w.Name} ({state.ToLowerInvariant()})" : w.Name)];
        return Plural.Listed(unready.Count > 0 ? unready : [.. waited.Select(w => w.Name)]);
    }

    /// <summary>A key of what the details read beside the resource itself, so the others' change that alters it shows.</summary>
    private static string Related(AspireResource resource, IReadOnlyList<AspireResource> others) =>
        string.Join("\n", RelatedBy(resource, others).Select(r => $"{r.Name} {r.Type}")
            .Concat(Relations(resource).Select(r => $"{r.Name} {others.FirstOrDefault(o => o.IsNamed(r.Name))?.State} {others.FirstOrDefault(o => o.IsNamed(r.Name))?.HealthStatus}")));

    /// <summary>A relationship's words as the resource it is of says it.</summary>
    private static string Own(string type) => type switch
    {
        WaitFor => "Waits for",
        Reference => "References",
        ParentOf => "Parent",
        _ => type,
    };

    /// <summary>A relationship's words as the resource it names says it.</summary>
    private static string Theirs(string type) => type switch
    {
        WaitFor => "Waited for by",
        Reference => "Referenced by",
        ParentOf => "Child",
        _ => $"{type} of",
    };

    /// <summary>A link to a resource the AppHost runs, which chooses its row; words alone for a name no resource has.</summary>
    private Control ResourceLink(string name, IReadOnlyList<AspireResource> others) =>
        others.Any(o => o.IsNamed(name)) ? Link(name, $"Show {name}", () => choose(name), "aspireRelated") : Words(name);

    /// <summary>A resource's health as the lists show it (healthy, unhealthy, degraded), or null for one with no health
    /// report, as a container with no health check shows none (Aspire calls such a resource healthy once it runs).</summary>
    internal static string? Health(AspireResource resource) =>
        JsonMembers.Properties(resource.Data, "healthReports").Count > 0 ? resource.HealthStatus?.ToLowerInvariant() : null;

    /// <summary>A health report: its status, what it says, and an exception it carries.</summary>
    private static SelectableTextBlock Report(JsonElement report)
    {
        string status = JsonMembers.Text(report, "status")?.ToLowerInvariant() ?? "no status";
        string words = string.Join(": ", ((string?[])[status, JsonMembers.Text(report, "description"), JsonMembers.Text(report, "exceptionMessage")]).Where(w => w is { Length: > 0 }));
        return Words(words, brush: status == "unhealthy" ? TokenKeys.Error : null);
    }

    /// <summary>A variable's or property's value, a secret's masked whatever the AppHost sent (a list's items as a program's
    /// arguments, the word after a flag naming a secret among them); none said so.</summary>
    private static SelectableTextBlock Value(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Words("No value", brush: TokenKeys.TextMuted),
        JsonValueKind.String => Words(ContainerSecrets.Shown(name, value.GetString() ?? string.Empty), code: true),
        JsonValueKind.Array => Words(ContainerSecrets.Shown(name, string.Join(", ", ContainerSecrets.Arguments([.. value.EnumerateArray().Select(Plain)]))), code: true),
        _ => Words(ContainerSecrets.Shown(name, value.GetRawText()), code: true),
    };

    private static string Plain(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
