using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>The person's Microsoft Entra ID sign-in to Azure, Passenger's own through MSAL, as Azure.Identity's interactive
/// sign-in makes it with its default client for developers' tools (the one every tenant already trusts): the browser
/// opened at Entra's page through the caller (the window's Browse) for the person to sign in there, a device's code
/// where that cannot finish; each account's refresh token kept in the system's credential store, never in a file, and the
/// tokens Azure Resource Manager and Azure's databases take asked silently with it, per tenant. Off the UI thread but the
/// page's opening, which the caller places.</summary>
public static class AzureSignIn
{
    /// <summary>A stand-in for Entra's sign-in and Azure Resource Manager, a test's.</summary>
    [IdentifierHome]
    public const string EndpointVariable = "PASSENGER_AZURE_ENDPOINT";

    /// <summary>The service the credential store keeps the sign-ins under.</summary>
    public const string CredentialService = "Passenger Microsoft Entra ID";

    /// <summary>What Azure Resource Manager, Azure SQL and Azure Database for PostgreSQL and MySQL take tokens for.</summary>
    public const string ResourceManager = "https://management.azure.com/.default";

    public const string AzureSql = "https://database.windows.net/.default";

    public const string OpenSourceDatabases = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Azure.Identity's default client for developers' tools.</summary>
    private const string Client = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>The hosts a stand-in answers for.</summary>
    private static readonly string[] Hosts = ["login.microsoftonline.com", "management.azure.com"];

    /// <summary>The most characters a credential store's entry holds here (Windows' takes 2,560 bytes of UTF-16).</summary>
    private const int Piece = 1200;

    private static readonly Lazy<IPublicClientApplication> App = new(Made);

    private static readonly Lock gate = new();
    private static bool read;

    /// <summary>The address <see cref="EndpointVariable"/> names, or null for Entra's and Azure's own.</summary>
    public static Uri? StandIn => EnvironmentValues.Address(EndpointVariable, "Entra's sign-in and Azure Resource Manager are asked at instead");

    /// <summary>The client Azure Resource Manager is asked through, the stand-in's where one is named.</summary>
    internal static HttpClient Http { get; } = OutboundHttp.Client(TimeSpan.FromSeconds(60), 16 * 1024 * 1024, to: Routed);

    /// <summary>The accounts signed in, by their names (ann@contoso.com).</summary>
    public static async Task<IReadOnlyList<string>> AccountsAsync() =>
        [.. (await App.Value.GetAccountsAsync()).Select(a => a.Username).Where(n => n is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The one account signed in: refused where none is, or several are and the connection chose none.</summary>
    public static async Task<string> OnlyAccountAsync()
    {
        IReadOnlyList<string> accounts = await AccountsAsync();
        return accounts.Count switch
        {
            0 => throw new AzureSignInNeededException(null),
            1 => accounts[0],
            _ => throw new IOException(Refusals.AzureAccountUnchosen),
        };
    }

    /// <summary>Signs an account in: Entra's page opened through <paramref name="browse"/> for the person there, where its
    /// answer comes back to this machine; else a device's code, said through <paramref name="told"/>, its page opened too.
    /// The account's name once signed in.</summary>
    public static async Task<string> SignInAsync(Action<Uri> browse, Action<string> told, CancellationToken cancel)
    {
        try
        {
            return await InteractiveAsync(browse, told, cancel);
        }
        catch (MsalException refused) when (refused is not MsalClientException { ErrorCode: MsalError.AuthenticationCanceledError })
        {
            throw new IOException(Refusals.AzureRefused("the sign-in", refused.Message), refused);
        }
        catch (HttpRequestException unreached)
        {
            throw new IOException(Refusals.AzureRefused("the sign-in", unreached.Message), unreached);
        }
    }

    private static async Task<string> InteractiveAsync(Action<Uri> browse, Action<string> told, CancellationToken cancel)
    {
        try
        {
            AuthenticationResult signed = await App.Value.AcquireTokenInteractive([ResourceManager])
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    OpenBrowserAsync = page =>
                    {
                        browse(page);
                        return Task.CompletedTask;
                    },
                    HtmlMessageSuccess = "Signed in: Passenger has the sign-in. You can close this page.",
                })
                .ExecuteAsync(cancel);
            return signed.Account.Username;
        }
        catch (MsalClientException unfinished) when (unfinished.ErrorCode is not (MsalError.AuthenticationCanceledError or MsalError.AccessDenied))
        {
            AuthenticationResult coded = await App.Value.AcquireTokenWithDeviceCode([ResourceManager], device =>
            {
                told(device.UserCode);
                browse(new Uri(device.VerificationUrl));
                return Task.CompletedTask;
            }).ExecuteAsync(cancel);
            return coded.Account.Username;
        }
    }

    /// <summary>A token for a scope, asked silently for the account named in a tenant (empty for the account's own): an
    /// <see cref="AzureSignInNeededException"/> where no sign-in of it holds.</summary>
    public static async Task<string> TokenAsync(string account, string? tenant, string scope, CancellationToken cancel)
    {
        IAccount chosen = (await App.Value.GetAccountsAsync()).FirstOrDefault(a => string.Equals(a.Username, account, StringComparison.OrdinalIgnoreCase))
            ?? throw new AzureSignInNeededException(account);
        try
        {
            AcquireTokenSilentParameterBuilder asked = App.Value.AcquireTokenSilent([scope], chosen);
            AuthenticationResult given = await (tenant is { Length: > 0 } ? asked.WithTenantId(tenant) : asked).ExecuteAsync(cancel);
            ContainerSecrets.Mark(given.AccessToken);
            return given.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            throw new AzureSignInNeededException(chosen.Username);
        }
        catch (MsalException refused)
        {
            throw new IOException(Refusals.AzureRefused($"a token for {account}", refused.Message), refused);
        }
        catch (HttpRequestException unreached)
        {
            throw new IOException(Refusals.AzureRefused($"a token for {account}", unreached.Message), unreached);
        }
    }

    private static IPublicClientApplication Made()
    {
        PublicClientApplicationBuilder building = PublicClientApplicationBuilder.Create(Client)
            .WithAuthority("https://login.microsoftonline.com/organizations")
            .WithRedirectUri("http://localhost")
            .WithClientName("Passenger")
            .WithClientVersion(Product.Version)
            .WithHttpClientFactory(new Clients(OutboundHttp.Client(TimeSpan.FromSeconds(60), 4 * 1024 * 1024, to: Routed)))
            // Its errors only, which name no person (MSAL's own PII logging off).
            .WithLogging((_, message, _) => IdeLog.Write(IdeLog.Ide, $"Microsoft Entra ID sign-in: {ContainerSecrets.InSaid(message)}"), LogLevel.Error, enablePiiLogging: false,
                enableDefaultPlatformLogging: false);
        IPublicClientApplication app = building.Build();
        app.UserTokenCache.SetBeforeAccess(Load);
        app.UserTokenCache.SetAfterAccess(Keep);
        return app;
    }

    /// <summary>Reads the kept sign-ins into MSAL's cache once, as the first token is asked.</summary>
    private static void Load(TokenCacheNotificationArgs args)
    {
        lock (gate)
        {
            if (read)
            {
                return;
            }
            read = true;
            if (Read() is { } kept)
            {
                args.TokenCache.DeserializeMsalV3(Encoding.UTF8.GetBytes(kept));
            }
        }
    }

    /// <summary>Keeps the sign-ins as they change, their accounts and refresh tokens alone (access tokens last an hour, and are
    /// asked again), in pieces the credential store holds.</summary>
    private static void Keep(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged)
        {
            return;
        }
        JsonObject cache = JsonNode.Parse(args.TokenCache.SerializeMsalV3()) as JsonObject ?? [];
        cache.Remove("AccessToken");
        string kept = cache.ToJsonString();
        lock (gate)
        {
            int pieces = (kept.Length + Piece - 1) / Piece;
            for (int at = 0; at < pieces; at++)
            {
                SystemCredentials.Store(CredentialService, $"cache.{at}", kept.Substring(at * Piece, Math.Min(Piece, kept.Length - at * Piece)));
            }
            int had = int.TryParse(SystemCredentials.Read(CredentialService, "cache"), out int count) ? count : 0;
            for (int at = pieces; at < had; at++)
            {
                SystemCredentials.Forget(CredentialService, $"cache.{at}");
            }
            SystemCredentials.Store(CredentialService, "cache", pieces.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string? Read()
    {
        if (!int.TryParse(SystemCredentials.Read(CredentialService, "cache"), out int pieces) || pieces <= 0)
        {
            return null;
        }
        StringBuilder kept = new();
        for (int at = 0; at < pieces; at++)
        {
            if (SystemCredentials.Read(CredentialService, $"cache.{at}") is not { } piece)
            {
                return null;
            }
            kept.Append(piece);
        }
        return kept.ToString();
    }

    /// <summary>A request to Entra or Azure Resource Manager sent to the stand-in, at the same path.</summary>
    private static Uri Routed(Uri asked) =>
        StandIn is { } standIn && Hosts.Contains(asked.Host, StringComparer.OrdinalIgnoreCase) ? new Uri(standIn, asked.PathAndQuery) : asked;

    /// <summary>MSAL's requests through Passenger's own client.</summary>
    private sealed class Clients(HttpClient client) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => client;
    }
}

/// <summary>An Entra ID account with no sign-in that holds, or none signed in: the person signs in.</summary>
public sealed class AzureSignInNeededException(string? account) : IOException(Refusals.AzureSignInNeeded(account))
{
    public string? Account { get; } = account;
}
