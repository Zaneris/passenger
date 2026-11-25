using System.Net.Http.Headers;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>An Azure database server an account reaches: its name, its kind, the host it is reached at, and the subscription
/// and tenant it lies in.</summary>
public sealed record AzureServer(string Name, DatabaseKind Kind, string Host, string Subscription, string Tenant)
{
    /// <summary>As the connection dialog lists it.</summary>
    public override string ToString() => $"{Name} ({DatabaseKinds.Title(Kind)}, {Subscription})";
}

/// <summary>The Azure database servers a signed-in account reaches, read from Azure Resource Manager: the account's
/// subscriptions, then in each (with a token of its tenant) its Azure SQL servers and managed instances and its Azure
/// Database for PostgreSQL and MySQL flexible servers, each list read to its last page.</summary>
public static class AzureServers
{
    /// <summary>Each kind of server, as Resource Manager names it and the API version read.</summary>
    private static readonly (string Provider, string Version, DatabaseKind Kind)[] Kinds =
    [
        ("Microsoft.Sql/servers", "2023-08-01", DatabaseKind.SqlServer),
        ("Microsoft.Sql/managedInstances", "2023-08-01", DatabaseKind.SqlServer),
        ("Microsoft.DBforPostgreSQL/flexibleServers", "2024-08-01", DatabaseKind.Postgres),
        ("Microsoft.DBforMySQL/flexibleServers", "2023-12-30", DatabaseKind.MySql),
    ];

    private static readonly Uri Manager = new("https://management.azure.com/");

    /// <summary>The servers the account reaches (empty for the one signed in), by subscription and name.</summary>
    public static async Task<IReadOnlyList<AzureServer>> ListAsync(string account, CancellationToken cancel)
    {
        account = account.Length > 0 ? account : await AzureSignIn.OnlyAccountAsync();
        string home = await AzureSignIn.TokenAsync(account, null, AzureSignIn.ResourceManager, cancel);
        List<AzureServer> found = [];
        foreach (JsonElement subscription in await PagesAsync(new Uri(Manager, "subscriptions?api-version=2022-12-01"), home, cancel))
        {
            string id = JsonMembers.Text(subscription, "subscriptionId") ?? string.Empty;
            string tenant = JsonMembers.Text(subscription, "tenantId") ?? string.Empty;
            string named = JsonMembers.Text(subscription, "displayName") ?? id;
            string token = await AzureSignIn.TokenAsync(account, tenant, AzureSignIn.ResourceManager, cancel);
            foreach ((string provider, string version, DatabaseKind kind) in Kinds)
            {
                foreach (JsonElement server in await PagesAsync(new Uri(Manager, $"subscriptions/{Uri.EscapeDataString(id)}/providers/{provider}?api-version={version}"), token, cancel))
                {
                    if (JsonMembers.Text(JsonMembers.Object(server, "properties"), "fullyQualifiedDomainName") is { Length: > 0 } host)
                    {
                        found.Add(new AzureServer(JsonMembers.Text(server, "name") ?? host, kind, host, named, tenant));
                    }
                }
            }
        }
        return [.. found.OrderBy(s => s.Subscription, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A list's items, each page's value, following its nextLink.</summary>
    private static async Task<List<JsonElement>> PagesAsync(Uri first, string token, CancellationToken cancel)
    {
        List<JsonElement> items = [];
        for (Uri? page = first; page is not null;)
        {
            string asked = page.AbsolutePath;
            using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, page);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using HttpResponseMessage answer = await AzureSignIn.Http.SendAsync(request, cancel);
                string text = await answer.Content.ReadAsStringAsync(cancel);
                if (!answer.IsSuccessStatusCode)
                {
                    throw new IOException(Refusals.AzureRefused(page.AbsolutePath, JsonMessage(text) ?? $"{(int)answer.StatusCode} {answer.ReasonPhrase}"));
                }
                using JsonDocument read = JsonDocument.Parse(text);
                items.AddRange(JsonMembers.Items(read.RootElement, "value").Select(i => i.Clone()));
                page = JsonMembers.Text(read.RootElement, "nextLink") is { Length: > 0 } next ? new Uri(next) : null;
            }
            catch (Exception unread) when (unread is HttpRequestException or JsonException or UriFormatException)
            {
                throw new IOException(Refusals.AzureRefused(asked, unread.Message), unread);
            }
        }
        return items;
    }

    /// <summary>The message an error's JSON gives, null where it gives none.</summary>
    private static string? JsonMessage(string text)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(text);
            return JsonMembers.Text(JsonMembers.Object(read.RootElement, "error"), "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
