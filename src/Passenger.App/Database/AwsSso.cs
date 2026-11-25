using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SSO;
using Amazon.SSO.Model;
using Amazon.SSOOIDC;
using Amazon.SSOOIDC.Model;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>What an AWS profile signing in through IAM Identity Center names: its portal's start URL and region, its
/// sso-session where it names one, and the account and role whose credentials it takes.</summary>
public sealed record AwsSsoProfile(string StartUrl, string Region, string? Session, string Account, string Role, IReadOnlyList<string> Scopes)
{
    /// <summary>What its sign-in is kept and cached by, as the AWS CLI keys its cache: the sso-session's name, else the start URL.</summary>
    public string Key => Session ?? StartUrl;
}

/// <summary>A profile's sign-in through IAM Identity Center, Passenger's own: OIDC's device sign-in, its page opened in the
/// person's browser for their approval there, its tokens kept in the system's credential store, never in a file (an
/// AWS CLI sign-in's cache, under .aws/sso/cache, read where Passenger holds none); a token refreshed while its refresh
/// token holds, and the profile's role credentials asked of the portal with it.</summary>
public static class AwsSso
{
    /// <summary>The service the credential store keeps sign-ins under.</summary>
    public const string CredentialService = "Passenger AWS IAM Identity Center";

    /// <summary>The OAuth grant of a device's sign-in, and of a refresh.</summary>
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private const string RefreshGrant = "refresh_token";

    /// <summary>A profile's IAM Identity Center parts, null where it signs in otherwise.</summary>
    public static AwsSsoProfile? Of(CredentialProfile profile)
    {
        CredentialProfileOptions options = profile.Options;
        return options is { SsoAccountId.Length: > 0, SsoRoleName.Length: > 0, SsoStartUrl.Length: > 0, SsoRegion.Length: > 0 }
            ? new AwsSsoProfile(options.SsoStartUrl, options.SsoRegion, options.SsoSession is { Length: > 0 } session ? session : null, options.SsoAccountId,
                options.SsoRoleName, options.SsoRegistrationScopes is { Length: > 0 } scopes ? [.. scopes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)] : ["sso:account:access"])
            : null;
    }

    /// <summary>The role credentials of a profile signed in, and when AWS says they expire: an <see cref="AwsSignInNeededException"/>
    /// where it has no sign-in, or one that has run out and can no longer be refreshed.</summary>
    public static async Task<(AWSCredentials Credentials, DateTimeOffset Expires)> CredentialsAsync(string profile, AwsSsoProfile sso, CancellationToken cancel)
    {
        JsonObject token = await TokenAsync(profile, sso, cancel);
        using AmazonSSOClient portal = new(new AnonymousAWSCredentials(), AwsProfiles.Config<AmazonSSOConfig>(sso.Region));
        try
        {
            GetRoleCredentialsResponse given = await portal.GetRoleCredentialsAsync(new GetRoleCredentialsRequest
            {
                AccessToken = (string?)token["accessToken"], AccountId = sso.Account, RoleName = sso.Role,
            }, cancel);
            RoleCredentials role = given.RoleCredentials;
            ContainerSecrets.Mark(role.SecretAccessKey);
            ContainerSecrets.Mark(role.SessionToken);
            return (new SessionAWSCredentials(role.AccessKeyId, role.SecretAccessKey, role.SessionToken),
                role.Expiration is long expires and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expires) : DateTimeOffset.MaxValue);
        }
        catch (UnauthorizedException)
        {
            SystemCredentials.Forget(CredentialService, sso.Key);
            throw new AwsSignInNeededException(profile);
        }
    }

    /// <summary>Signs a profile in through OIDC's device sign-in: Passenger registered with the portal, its page opened
    /// through <paramref name="browse"/> for the person to approve there, <paramref name="told"/> given the code the page
    /// shows, and the tokens kept once they approve; canceled, or refused there, nothing is kept.</summary>
    public static async Task SignInAsync(string profile, Action<Uri> browse, Action<string> told, CancellationToken cancel)
    {
        if (!AwsProfiles.Store().TryGetProfile(profile, out CredentialProfile? found) || Of(found) is not { } sso)
        {
            throw new InvalidOperationException(Refusals.AwsProfileUnusable(profile, "no IAM Identity Center sign-in"));
        }
        using AmazonSSOOIDCClient oidc = new(new AnonymousAWSCredentials(), AwsProfiles.Config<AmazonSSOOIDCConfig>(sso.Region));
        RegisterClientResponse client = await oidc.RegisterClientAsync(new RegisterClientRequest
        {
            ClientName = "Passenger", ClientType = "public", Scopes = [.. sso.Scopes],
        }, cancel);
        ContainerSecrets.Mark(client.ClientSecret);
        StartDeviceAuthorizationResponse device = await oidc.StartDeviceAuthorizationAsync(new StartDeviceAuthorizationRequest
        {
            ClientId = client.ClientId, ClientSecret = client.ClientSecret, StartUrl = sso.StartUrl,
        }, cancel);
        told(device.UserCode);
        browse(new Uri(device.VerificationUriComplete is { Length: > 0 } complete ? complete : device.VerificationUri));
        TimeSpan every = TimeSpan.FromSeconds(device.Interval is int interval and > 0 ? interval : 5);
        Deadline until = Deadline.In(TimeSpan.FromSeconds(device.ExpiresIn is int expires and > 0 ? expires : 600));
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                CreateTokenResponse made = await oidc.CreateTokenAsync(new CreateTokenRequest
                {
                    ClientId = client.ClientId, ClientSecret = client.ClientSecret, GrantType = DeviceGrant, DeviceCode = device.DeviceCode,
                }, cancel);
                Keep(sso, made, client.ClientId, client.ClientSecret, client.ClientSecretExpiresAt ?? 0);
                AwsProfiles.Forget(profile);
                return;
            }
            catch (AuthorizationPendingException)
            {
            }
            catch (SlowDownException)
            {
                every += TimeSpan.FromSeconds(5);
            }
            catch (Exception ended) when (ended is ExpiredTokenException or AccessDeniedException)
            {
                throw new InvalidOperationException(Refusals.AwsSignInNotApproved(profile), ended);
            }
            if (until.Passed)
            {
                throw new InvalidOperationException(Refusals.AwsSignInNotApproved(profile));
            }
            await Task.Delay(every, cancel);
        }
    }

    /// <summary>The access token of a profile's sign-in: Passenger's own kept, else an AWS CLI sign-in's cached, while it
    /// holds; else refreshed by its refresh token, kept anew; else an <see cref="AwsSignInNeededException"/>.</summary>
    private static async Task<JsonObject> TokenAsync(string profile, AwsSsoProfile sso, CancellationToken cancel)
    {
        JsonObject? kept = Read(SystemCredentials.Read(CredentialService, sso.Key)) ?? Read(CliCached(sso));
        if (kept is null || (string?)kept["startUrl"] is { } started && started != sso.StartUrl)
        {
            throw new AwsSignInNeededException(profile);
        }
        if (Due(kept, "expiresAt") is { } expires && expires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1))
        {
            return kept;
        }
        if ((string?)kept["refreshToken"] is not { Length: > 0 } refresh || (string?)kept["clientId"] is not { Length: > 0 } clientId
            || (string?)kept["clientSecret"] is not { Length: > 0 } clientSecret || Due(kept, "registrationExpiresAt") is { } registered && registered <= DateTimeOffset.UtcNow)
        {
            throw new AwsSignInNeededException(profile);
        }
        using AmazonSSOOIDCClient oidc = new(new AnonymousAWSCredentials(), AwsProfiles.Config<AmazonSSOOIDCConfig>(sso.Region));
        try
        {
            CreateTokenResponse made = await oidc.CreateTokenAsync(new CreateTokenRequest
            {
                ClientId = clientId, ClientSecret = clientSecret, GrantType = RefreshGrant, RefreshToken = refresh,
            }, cancel);
            return Keep(sso, made, clientId, clientSecret, Due(kept, "registrationExpiresAt")?.ToUnixTimeSeconds() ?? 0);
        }
        catch (Exception refused) when (refused is InvalidGrantException or ExpiredTokenException or UnauthorizedClientException or InvalidClientException)
        {
            SystemCredentials.Forget(CredentialService, sso.Key);
            throw new AwsSignInNeededException(profile);
        }
    }

    /// <summary>Keeps a token in the credential store, as the AWS CLI's cache writes one, and gives it.</summary>
    private static JsonObject Keep(AwsSsoProfile sso, CreateTokenResponse made, string clientId, string clientSecret, long registrationExpires)
    {
        ContainerSecrets.Mark(made.AccessToken);
        ContainerSecrets.Mark(made.RefreshToken ?? string.Empty);
        JsonObject token = new()
        {
            ["startUrl"] = sso.StartUrl,
            ["region"] = sso.Region,
            ["accessToken"] = made.AccessToken,
            ["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(made.ExpiresIn ?? 3600).ToString("O", CultureInfo.InvariantCulture),
            ["refreshToken"] = made.RefreshToken,
            ["clientId"] = clientId,
            ["clientSecret"] = clientSecret,
            ["registrationExpiresAt"] = registrationExpires > 0 ? DateTimeOffset.FromUnixTimeSeconds(registrationExpires).ToString("O", CultureInfo.InvariantCulture) : null,
        };
        SystemCredentials.Store(CredentialService, sso.Key, token.ToJsonString());
        return token;
    }

    /// <summary>The AWS CLI's cached sign-in for a profile's session or start URL, its file named by the SHA-1 of that key.</summary>
    private static string? CliCached(AwsSsoProfile sso)
    {
        string file = Path.Combine(AwsProfiles.Home, ".aws", "sso", "cache", Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(sso.Key))) + ".json");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonObject? Read(string? text)
    {
        try
        {
            return text is { Length: > 0 } ? JsonNode.Parse(text) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A moment a token's record names, as AWS gives it, null for none or one unread.</summary>
    private static DateTimeOffset? Due(JsonObject token, string name) =>
        DateTimeOffset.TryParse((string?)token[name], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) ? at : null;
}

/// <summary>A profile that signs in through IAM Identity Center has no sign-in that holds: the person signs in again.</summary>
public sealed class AwsSignInNeededException(string profile) : IOException(Refusals.AwsSignInNeeded(profile))
{
    public string Profile { get; } = profile;
}
