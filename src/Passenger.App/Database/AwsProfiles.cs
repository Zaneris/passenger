using System.Collections.Concurrent;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>The person's AWS credentials as the Database window signs in with them, read from their own files (~/.aws's
/// config and credentials, or where AWS_CONFIG_FILE and AWS_SHARED_CREDENTIALS_FILE put them) and never kept by Passenger:
/// a profile's keys, the program its credential_process names (run through <see cref="ChildProcesses"/>, as every program
/// Passenger starts is), the role it assumes, or its IAM Identity Center sign-in (<see cref="AwsSso"/>); with no profile
/// named, the environment's keys, else AWS_PROFILE's or the default profile. Never an instance's or a container's role,
/// whose address no developer's machine answers. What a credential_process or a role gives is held in this process until
/// it is due to expire. Off the UI thread.</summary>
public static class AwsProfiles
{
    /// <summary>A stand-in for every AWS service Passenger asks for credentials or secrets (STS, IAM Identity Center, Secrets
    /// Manager, Redshift), a test's.</summary>
    [IdentifierHome]
    public const string EndpointVariable = "PASSENGER_AWS_ENDPOINT";

    /// <summary>The folder read in place of the person's home for .aws's files, a test run's.</summary>
    [IdentifierHome]
    public const string HomeVariable = "PASSENGER_AWS_HOME";

    /// <summary>The most a credential_process is given to answer.</summary>
    private static readonly TimeSpan ProcessLimit = TimeSpan.FromMinutes(1);

    /// <summary>Credentials given for a while, by profile, with when AWS said they expire.</summary>
    private static readonly ConcurrentDictionary<string, (AWSCredentials Credentials, DateTimeOffset Expires)> Held = new(StringComparer.Ordinal);

    /// <summary>The address <see cref="EndpointVariable"/> names, or null for AWS's own.</summary>
    public static Uri? StandIn => EnvironmentValues.Address(EndpointVariable, "every AWS service Passenger asks for credentials and secrets is asked at instead");

    /// <summary>The folder .aws is read in: the person's home, or <see cref="HomeVariable"/>'s.</summary>
    public static string Home => Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home
        ? Path.GetFullPath(home) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The profiles the person's files name, in order.</summary>
    public static IReadOnlyList<string> Names() => [.. Store().ListProfiles().Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The profile's own region, null for none or no such profile.</summary>
    public static string? RegionOf(string profile) => Store().TryGetProfile(profile, out CredentialProfile? found) ? found.Region?.SystemName : null;

    /// <summary>Whether the profile signs in through IAM Identity Center, which <see cref="AwsSso.SignInAsync"/> signs in to.</summary>
    public static bool SignsInThroughIdentityCenter(string profile) => Store().TryGetProfile(profile, out CredentialProfile? found) && AwsSso.Of(found) is not null;

    /// <summary>The profile's credentials, or those the environment gives for none (see the class).</summary>
    public static async Task<AWSCredentials> CredentialsAsync(string? profile, CancellationToken cancel)
    {
        if (profile is not { Length: > 0 })
        {
            if (EnvironmentKeys() is { } keys)
            {
                return keys;
            }
            profile = Environment.GetEnvironmentVariable("AWS_PROFILE") is { Length: > 0 } named ? named : "default";
        }
        return await OfAsync(profile, [], cancel);
    }

    /// <summary>A client's configuration for a region: the stand-in's address where <see cref="EndpointVariable"/> names one,
    /// else AWS's own, retried once and bounded as the Database window's requests are.</summary>
    public static T Config<T>(string region) where T : ClientConfig, new()
    {
        T config = new() { Timeout = TimeSpan.FromSeconds(30), MaxErrorRetry = 1 };
        if (StandIn is { } standIn)
        {
            config.ServiceURL = standIn.AbsoluteUri;
            config.AuthenticationRegion = region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        return config;
    }

    /// <summary>Forgets what is held for a profile, as its sign-in changed.</summary>
    public static void Forget(string profile) => Held.TryRemove(profile, out _);

    private static async Task<AWSCredentials> OfAsync(string name, IReadOnlyList<string> through, CancellationToken cancel)
    {
        if (through.Contains(name, StringComparer.Ordinal) || through.Count > 8)
        {
            throw new InvalidOperationException(Refusals.AwsProfileUnusable(name, "a role whose source profiles lead back to it").English);
        }
        if (Held.TryGetValue(name, out (AWSCredentials Credentials, DateTimeOffset Expires) held) && held.Expires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5))
        {
            return held.Credentials;
        }
        if (!Store().TryGetProfile(name, out CredentialProfile? profile))
        {
            throw new InvalidOperationException(Refusals.NoAwsProfile(name).English);
        }
        CredentialProfileOptions options = profile.Options;
        if (options.RoleArn is { Length: > 0 } role)
        {
            return await AssumedAsync(profile, role, through, cancel);
        }
        if (options.CredentialProcess is { Length: > 0 } process)
        {
            return await ProcessAsync(name, process, cancel);
        }
        if (AwsSso.Of(profile) is { } sso)
        {
            (AWSCredentials credentials, DateTimeOffset expires) = await AwsSso.CredentialsAsync(name, sso, cancel);
            Held[name] = (credentials, expires);
            return credentials;
        }
        if (options is { AccessKey.Length: > 0, SecretKey.Length: > 0 })
        {
            return options.Token is { Length: > 0 } token ? new SessionAWSCredentials(options.AccessKey, options.SecretKey, token) : new BasicAWSCredentials(options.AccessKey, options.SecretKey);
        }
        throw new InvalidOperationException((Refusals.AwsProfileUnusable(name, options.WebIdentityTokenFile is { Length: > 0 } ? "a web identity's token file"
            : options.LoginSession is { Length: > 0 } ? "aws login's console session" : "no credentials")).English);
    }

    /// <summary>The role a profile names, assumed through STS with its source profile's credentials, or the environment's.</summary>
    private static async Task<AWSCredentials> AssumedAsync(CredentialProfile profile, string role, IReadOnlyList<string> through, CancellationToken cancel)
    {
        CredentialProfileOptions options = profile.Options;
        if (options.MfaSerial is { Length: > 0 })
        {
            throw new InvalidOperationException(Refusals.AwsProfileUnusable(profile.Name, "a role that asks for an MFA code").English);
        }
        AWSCredentials source = options.SourceProfile is { Length: > 0 } from ? await OfAsync(from, [.. through, profile.Name], cancel)
            : options.CredentialSource == "Environment" && EnvironmentKeys() is { } keys ? keys
            : throw new InvalidOperationException(Refusals.AwsProfileUnusable(profile.Name, $"a role whose credentials come from {options.CredentialSource ?? "nowhere"}").English);
        using AmazonSecurityTokenServiceClient sts = new(source, Config<AmazonSecurityTokenServiceConfig>(profile.Region?.SystemName ?? "us-east-1"));
        AssumeRoleResponse assumed = await sts.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = role,
            RoleSessionName = options.RoleSessionName is { Length: > 0 } session ? session : "passenger",
            ExternalId = options.ExternalID is { Length: > 0 } external ? external : null,
        }, cancel);
        Credentials given = assumed.Credentials;
        ContainerSecrets.Mark(given.SecretAccessKey);
        ContainerSecrets.Mark(given.SessionToken);
        SessionAWSCredentials credentials = new(given.AccessKeyId, given.SecretAccessKey, given.SessionToken);
        Held[profile.Name] = (credentials, given.Expiration is { } expires ? new DateTimeOffset(expires, TimeSpan.Zero) : DateTimeOffset.MaxValue);
        return credentials;
    }

    /// <summary>What a profile's credential_process answers, its program found as the Terminal finds it and run through
    /// <see cref="ChildProcesses"/> in the person's home: its JSON's keys, held until its Expiration where it gives one.</summary>
    private static async Task<AWSCredentials> ProcessAsync(string profile, string line, CancellationToken cancel)
    {
        IReadOnlyList<string> words = CommandLine.Split(line);
        if (words.Count == 0 || words[0].Length == 0)
        {
            throw new InvalidOperationException(Refusals.AwsProfileUnusable(profile, "a credential_process that names no program").English);
        }
        string program = Path.IsPathRooted(words[0]) ? words[0] : Programs.Command(words[0])
            ?? throw new InvalidOperationException(Refusals.AwsProcessFailed(profile, $"{words[0]} is not on PATH").English);
        ChildResult ran = await ChildProcesses.RunAsync(ChildProcesses.Command(program, words.Skip(1), Home), limit: ProcessLimit, cancel: cancel);
        if (!ran.Succeeded)
        {
            throw new InvalidOperationException(Refusals.AwsProcessFailed(profile, ContainerSecrets.InSaid(ran.Error.Trim() is { Length: > 0 } said ? said : $"it ended with {ran.ExitCode}")).English);
        }
        try
        {
            using JsonDocument answer = JsonDocument.Parse(ran.Output);
            string access = JsonMembers.Text(answer.RootElement, "AccessKeyId") ?? throw new FormatException("no AccessKeyId");
            string secret = JsonMembers.Text(answer.RootElement, "SecretAccessKey") ?? throw new FormatException("no SecretAccessKey");
            string? token = JsonMembers.Text(answer.RootElement, "SessionToken");
            ContainerSecrets.Mark(secret);
            ContainerSecrets.Mark(token ?? string.Empty);
            AWSCredentials credentials = token is { Length: > 0 } ? new SessionAWSCredentials(access, secret, token) : new BasicAWSCredentials(access, secret);
            Held[profile] = (credentials, DateTimeOffset.TryParse(JsonMembers.Text(answer.RootElement, "Expiration"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset expires) ? expires : DateTimeOffset.MaxValue);
            return credentials;
        }
        catch (Exception unread) when (unread is JsonException or FormatException)
        {
            throw new InvalidOperationException(Refusals.AwsProcessFailed(profile, $"its answer is not the JSON AWS's credential_process gives ({unread.Message})").English, unread);
        }
    }

    /// <summary>The keys AWS's own variables give, null where they give none.</summary>
    private static AWSCredentials? EnvironmentKeys() =>
        (Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"), Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"), Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN")) switch
        {
            ({ Length: > 0 } access, { Length: > 0 } secret, { Length: > 0 } token) => new SessionAWSCredentials(access, secret, token),
            ({ Length: > 0 } access, { Length: > 0 } secret, _) => new BasicAWSCredentials(access, secret),
            _ => null,
        };

    /// <summary>The person's profiles, read now: the credentials file AWS_SHARED_CREDENTIALS_FILE names, else .aws's in
    /// <see cref="Home"/>, the config file beside it unless AWS_CONFIG_FILE names one.</summary>
    internal static CredentialProfileStoreChain Store() =>
        new(Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") is { Length: > 0 } named ? named : Path.Combine(Home, ".aws", "credentials"));
}
