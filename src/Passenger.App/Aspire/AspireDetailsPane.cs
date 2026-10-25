using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;

namespace Passenger.App.Aspire;

/// <summary>A resource's Details tab in the Aspire window: the resource (its type, state and health, its source, when it
/// started and stopped), its health reports, its addresses (links to the browser), what it relates to, its environment
/// and properties (a secret's value masked by <see cref="ContainerSecrets"/>' rule, one the AppHost marks sensitive
/// whatever its name) and its volumes. Read from the resource as the window last had it.</summary>
public sealed class AspireDetailsPane(Action<Uri> browse, Action<string> open) : DetailsTables
{
    /// <summary>The resource shown, or null.</summary>
    public AspireResource? Resource { get; private set; }

    /// <summary>Whether the resource shown was said left unstarted.</summary>
    private bool shownUnstarted;

    /// <summary>Shows a resource's details, or says to choose one; one its run left unstarted (<paramref name="leftUnstarted"/>,
    /// as its configuration says Don't start) is Not started, never a failure. The resource shown already, which an AppHost's
    /// update that did not change it gives again, is left as it is, its tables never made again to find nothing changed.</summary>
    public void Show(AspireResource? resource, bool leftUnstarted = false)
    {
        UiThread.Check();
        if (resource is not null && ReferenceEquals(resource, Resource) && leftUnstarted == shownUnstarted)
        {
            return;
        }
        Resource = resource;
        shownUnstarted = leftUnstarted;
        if (resource is null)
        {
            Say("Choose a resource to see its state, health, addresses and environment");
            return;
        }
        // Shown again as each of the AppHost's updates comes: tables that show what they showed stay, a press on a link kept.
        Show(() => Fill(resource, leftUnstarted));
    }

    private void Fill(AspireResource resource, bool leftUnstarted)
    {
        JsonElement data = resource.Data;
        List<(string, Control)> about = [("Type", Words(Text(data, "resourceType") ?? Text(data, "type") ?? "Unknown"))];
        about.Add(("State", leftUnstarted ? State(AspireAppRun.NotStarted, null) : State(resource.State ?? "Unknown", Health(resource))));
        if (leftUnstarted)
        {
            about.Add(("Why", Words(AspireAppRun.NotStartedWhy, brush: TokenKeys.TextMuted)));
        }
        if (resource.Source is { } source)
        {
            about.Add(("Source", Path.IsPathFullyQualified(source)
                ? Link(Path.GetFileName(source), source, () => open(source), "aspireSource")
                : Words(source, code: true)));
        }
        foreach ((string name, string field) in (ReadOnlySpan<(string, string)>)[("Started", "startTimestamp"), ("Stopped", "stopTimestamp")])
        {
            if (Text(data, field) is { } said && DateTimeOffset.TryParse(said, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at))
            {
                about.Add((name, Words(ContainerDetailsPane.When(at))));
            }
        }
        Section("Resource", about);
        if (Members(data, "healthReports") is { Count: > 0 } reports)
        {
            Section("Health", [.. reports.Select(report => (report.Name, (Control)Report(report.Value)))]);
        }
        List<(string, Control)> addresses = [];
        foreach (JsonElement url in Items(data, "urls"))
        {
            if (Text(url, "url") is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out Uri? target))
            {
                continue;
            }
            string name = Text(url, "displayName") ?? Text(url, "name") ?? target.Scheme;
            bool inactive = url.TryGetProperty("isInactive", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            addresses.Add((name, target.Scheme is "http" or "https" && !inactive
                ? Link(target.AbsoluteUri, target.AbsoluteUri, () => browse(target), "aspireDetailsUrl")
                : Words(target.AbsoluteUri, brush: inactive ? TokenKeys.TextMuted : null)));
        }
        if (addresses.Count > 0)
        {
            Section("URLs", addresses);
        }
        if (Items(data, "relationships") is { Count: > 0 } relations)
        {
            Section("Relationships", [.. relations.Select(r => (Text(r, "resourceName") ?? "?", (Control)Words(Text(r, "type") ?? string.Empty)))]);
        }
        if (Members(data, "environment") is { Count: > 0 } environment)
        {
            Section("Environment", [.. environment.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => (e.Name, (Control)Value(e.Name, e.Value)))]);
        }
        if (Members(data, "properties") is { Count: > 0 } properties)
        {
            Dictionary<string, string> titles = Members(data, "propertyMetadata")
                .Where(m => m.Value.ValueKind == JsonValueKind.Object && Text(m.Value, "displayName") is { Length: > 0 })
                .ToDictionary(m => m.Name, m => Text(m.Value, "displayName")!, StringComparer.Ordinal);
            Section("Properties", [.. properties.Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => (titles.GetValueOrDefault(p.Name) ?? p.Name, (Control)Value(p.Name, p.Value)))]);
        }
        if (Items(data, "volumes") is { Count: > 0 } volumes)
        {
            Section("Volumes", [.. volumes.Select(v => (Text(v, "target") ?? "?", (Control)Words(
                $"{Text(v, "mountType")} {Text(v, "source")}{(v.TryGetProperty("isReadOnly", out JsonElement read) && read.ValueKind == JsonValueKind.True ? " (read-only)" : string.Empty)}".Trim())))]);
        }
    }

    /// <summary>A resource's health as the lists show it (healthy, unhealthy, degraded), or null for one with no health
    /// report, as a container with no health check shows none (Aspire calls such a resource healthy once it runs).</summary>
    internal static string? Health(AspireResource resource) =>
        Members(resource.Data, "healthReports").Count > 0 ? resource.HealthStatus?.ToLowerInvariant() : null;

    /// <summary>A health report: its status, what it says, and an exception it carries.</summary>
    private static SelectableTextBlock Report(JsonElement report)
    {
        string status = Text(report, "status")?.ToLowerInvariant() ?? "no status";
        string words = string.Join(": ", ((string?[])[status, Text(report, "description"), Text(report, "exceptionMessage")]).Where(w => w is { Length: > 0 }));
        return Words(words, brush: status == "unhealthy" ? TokenKeys.Error : null);
    }

    /// <summary>A variable's or property's value, a secret's masked whatever the AppHost sent; none said so.</summary>
    private static SelectableTextBlock Value(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Words("No value", brush: TokenKeys.TextMuted),
        JsonValueKind.String => Words(ContainerSecrets.Shown(name, value.GetString() ?? string.Empty), code: true),
        JsonValueKind.Array => Words(ContainerSecrets.Shown(name, string.Join(", ", value.EnumerateArray().Select(Plain))), code: true),
        _ => Words(ContainerSecrets.Shown(name, value.GetRawText()), code: true),
    };

    private static string Plain(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static IReadOnlyList<JsonProperty> Members(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.Object
            ? [.. field.EnumerateObject()] : [];

    private static IReadOnlyList<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.Array
            ? [.. field.EnumerateArray()] : [];
}
