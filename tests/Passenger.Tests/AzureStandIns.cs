using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace Passenger.Tests;

/// <summary>Microsoft Entra ID and Azure Resource Manager as Passenger's Entra sign-in asks them, on a loopback port (Passenger's
/// PASSENGER_AZURE_ENDPOINT): each tenant's token endpoint, an authorization code or a refresh token exchanged for an
/// account's tokens, each named for its account and the service asked (entra.ann.ossrdbms); and Resource Manager's one
/// subscription and the servers a test gives it. The person's sign-in on Entra's page is played by <see cref="ApproveAsync"/>.</summary>
public sealed class StandInEntra : IDisposable
{
    /// <summary>The accounts' home tenant, and the subscription's.</summary>
    public const string Tenant = "7f3c2a10-5d4e-4b6a-9c8d-1e2f3a4b5c6d";

    public const string Subscription = "0b1c2d3e-4f50-4617-8293-a4b5c6d7e8f9";

    /// <summary>Azure.Identity's client for developers' tools, which Passenger signs in as.</summary>
    private const string Client = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private readonly StandInHttp http;

    public StandInEntra() => http = new StandInHttp(HeardAsync, keepAlive: true);

    /// <summary>Each request's method and target, and what failed answering it, for a test's words where a sign-in did not end.</summary>
    public ConcurrentQueue<string> Heard { get; } = new();

    public string Root => http.Root;

    /// <summary>The servers Resource Manager lists, each with its provider (Microsoft.DBforPostgreSQL/flexibleServers), name and host.</summary>
    public ConcurrentQueue<(string Provider, string Name, string Host)> Servers { get; } = new();

    /// <summary>Each token given, with its account, the tenant it was asked of and the service it is for.</summary>
    public ConcurrentQueue<(string Account, string Tenant, string Service)> Issued { get; } = new();

    /// <summary>The token an account (ann, of ann@contoso.com) is given for a service: ossrdbms (PostgreSQL's and MySQL's),
    /// sql or arm.</summary>
    public static string TokenOf(string account, string service) => $"entra.{account}.{service}";

    public static string AccountOf(string name) => $"{name}@contoso.com";

    public void Dispose() => http.Dispose();

    /// <summary>The person's sign-in on Entra's page that <paramref name="page"/> opened, as <paramref name="name"/>: the
    /// browser sent back to the address the page names with a code of that account, posted as a form where the page asks.</summary>
    public static async Task ApproveAsync(Uri page, string name)
    {
        System.Collections.Specialized.NameValueCollection asked = HttpUtility.ParseQueryString(page.Query);
        string back = asked["redirect_uri"] ?? throw new InvalidOperationException($"Entra's page names no address to come back to: {page}");
        Dictionary<string, string> answer = new() { ["code"] = $"code-{name}", ["state"] = asked["state"] ?? string.Empty };
        using HttpClient browser = new() { Timeout = TimeSpan.FromSeconds(30) };
        using HttpResponseMessage answered = asked["response_mode"] == "form_post"
            ? await browser.PostAsync(new Uri(back), new FormUrlEncodedContent(answer))
            : await browser.GetAsync(new Uri($"{back}?{string.Join('&', answer.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"))}"));
        answered.EnsureSuccessStatusCode();
    }

    private async Task HeardAsync(StandInHttp.Exchange asked)
    {
        Heard.Enqueue(asked.Line);
        try
        {
            await AnswerAsync(asked);
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            Heard.Enqueue($"  failed: {failed}");
            throw;
        }
    }

    private async Task AnswerAsync(StandInHttp.Exchange asked)
    {
        string path = asked.Path.Split('?')[0];
        string[] parts = path.Trim('/').Split('/');
        if (asked.Method == "POST" && parts is [var tenant, "oauth2", "v2.0", "token"])
        {
            await TokenAsync(asked, tenant is "organizations" or "common" ? Tenant : tenant);
            return;
        }
        if (path.EndsWith("/discovery/instance", StringComparison.Ordinal))
        {
            await asked.ReplyAsync(200, "application/json", new JsonObject
            {
                ["tenant_discovery_endpoint"] = $"https://login.microsoftonline.com/{Tenant}/v2.0/.well-known/openid-configuration",
                ["api-version"] = "1.1",
                ["metadata"] = new JsonArray(new JsonObject
                {
                    ["preferred_network"] = "login.microsoftonline.com", ["preferred_cache"] = "login.windows.net",
                    ["aliases"] = new JsonArray("login.microsoftonline.com", "login.windows.net", "login.microsoft.com", "sts.windows.net"),
                }),
            }.ToJsonString());
            return;
        }
        if (asked.Header("Authorization") is not { } bearer || !bearer.StartsWith("Bearer entra.", StringComparison.Ordinal) || !bearer.EndsWith(".arm", StringComparison.Ordinal))
        {
            await asked.ReplyAsync(401, "application/json", new JsonObject { ["error"] = new JsonObject { ["code"] = "InvalidAuthenticationToken", ["message"] = "No token of Resource Manager" } }.ToJsonString());
            return;
        }
        if (parts is ["subscriptions"])
        {
            await asked.ReplyAsync(200, "application/json", new JsonObject
            {
                ["value"] = new JsonArray(new JsonObject { ["subscriptionId"] = Subscription, ["tenantId"] = Tenant, ["displayName"] = "Shop" }),
            }.ToJsonString());
            return;
        }
        if (parts is ["subscriptions", Subscription, "providers", var space, var type])
        {
            JsonArray value = [.. Servers.Where(s => s.Provider == $"{space}/{type}")
                .Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["properties"] = new JsonObject { ["fullyQualifiedDomainName"] = s.Host } })];
            await asked.ReplyAsync(200, "application/json", new JsonObject { ["value"] = value }.ToJsonString());
            return;
        }
        await asked.ReplyAsync(404, "application/json", new JsonObject { ["error"] = new JsonObject { ["code"] = "NotFound", ["message"] = $"No {path}" } }.ToJsonString());
    }

    /// <summary>An authorization code's or a refresh token's exchange for an account's tokens in a tenant.</summary>
    private async Task TokenAsync(StandInHttp.Exchange asked, string tenant)
    {
        Dictionary<string, string> form = Encoding.UTF8.GetString(asked.Body).Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')), StringComparer.Ordinal);
        string? name = form.GetValueOrDefault("grant_type") switch
        {
            "authorization_code" when form.GetValueOrDefault("code") is { } code && code.StartsWith("code-", StringComparison.Ordinal) => code["code-".Length..],
            "refresh_token" when form.GetValueOrDefault("refresh_token") is { } kept && kept.StartsWith("refresh-", StringComparison.Ordinal) => kept["refresh-".Length..],
            _ => null,
        };
        if (name is null)
        {
            await asked.ReplyAsync(400, "application/json", new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "AADSTS70000: no such grant" }.ToJsonString());
            return;
        }
        string[] scopes = [.. (form.GetValueOrDefault("scope") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s is not ("openid" or "profile" or "offline_access"))];
        string service = scopes.Any(s => s.Contains("ossrdbms", StringComparison.Ordinal)) ? "ossrdbms"
            : scopes.Any(s => s.Contains("database.windows.net", StringComparison.Ordinal)) ? "sql" : "arm";
        Issued.Enqueue((AccountOf(name), tenant, service));
        string id = $"00000000-0000-4000-8000-{Convert.ToHexString(Encoding.UTF8.GetBytes(name.PadRight(6, '0')[..6])).ToLowerInvariant()}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JsonObject claims = new()
        {
            ["aud"] = Client, ["iss"] = $"https://login.microsoftonline.com/{tenant}/v2.0", ["iat"] = now, ["nbf"] = now, ["exp"] = now + 3600,
            ["name"] = name, ["oid"] = id, ["preferred_username"] = AccountOf(name), ["sub"] = id, ["tid"] = tenant, ["ver"] = "2.0",
        };
        await asked.ReplyAsync(200, "application/json", new JsonObject
        {
            ["token_type"] = "Bearer", ["scope"] = string.Join(' ', scopes), ["expires_in"] = 3600, ["ext_expires_in"] = 3600,
            ["access_token"] = TokenOf(name, service), ["refresh_token"] = $"refresh-{name}",
            ["id_token"] = $"{Encoded(new JsonObject { ["alg"] = "none", ["typ"] = "JWT" })}.{Encoded(claims)}.",
            ["client_info"] = Encoded(new JsonObject { ["uid"] = id, ["utid"] = Tenant }),
        }.ToJsonString());
    }

    private static string Encoded(JsonObject json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
