using System.Data.Common;
using System.Text.Json;
using Amazon;
using Amazon.RDS.Util;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>How a connection signs in to its server with no password Passenger keeps.</summary>
public enum CloudSignInKind
{
    /// <summary>An RDS or Aurora IAM authentication token, made from the AWS profile's credentials at each connection.</summary>
    AwsIam,

    /// <summary>The user and password a Secrets Manager secret holds, read at each connection.</summary>
    AwsSecret,
}

/// <summary>A connection's cloud sign-in: its kind, the AWS profile it signs in with (empty for the environment's or the
/// default one) and the region it asks (empty for the one the server's host names, else the profile's), and for Secrets
/// Manager the secret's name or ARN.</summary>
public sealed record CloudSignIn(CloudSignInKind Kind, string Profile, string Region)
{
    public string Secret { get; init; } = string.Empty;
}

/// <summary>What a cloud sign-in lays over a connection's string as it opens: the password an IAM token is, or the user and
/// password a secret holds, each made or read at each connection and never kept, masked wherever said.</summary>
internal static class CloudSignIns
{
    /// <summary>The kinds each sign-in signs in to: IAM authentication only PostgreSQL's and MySQL's (RDS for SQL Server
    /// takes none), a secret every kind RDS runs.</summary>
    public static bool Signs(CloudSignInKind sign, DatabaseKind kind) => sign switch
    {
        CloudSignInKind.AwsIam => kind is DatabaseKind.Postgres or DatabaseKind.MySql,
        _ => kind is DatabaseKind.Postgres or DatabaseKind.MySql or DatabaseKind.SqlServer,
    };

    /// <summary>The connection's string with what its sign-in gives laid over it.</summary>
    public static async Task<string> SignedInAsync(DatabaseKind kind, string connection, CloudSignIn signIn, CancellationToken cancel)
    {
        if (!Signs(signIn.Kind, kind))
        {
            throw new InvalidOperationException(Refusals.NoCloudSignIn(DatabaseKinds.Title(kind)));
        }
        (string host, int port) = DatabaseKinds.Endpoint(kind, connection);
        string region = RegionOf(signIn, host) ?? throw new InvalidOperationException(Refusals.NoAwsRegion(host));
        AWSCredentials credentials = await AwsProfiles.CredentialsAsync(signIn.Profile, cancel);
        DbConnectionStringBuilder laid = DatabaseKinds.Builder(kind, connection);
        if (signIn.Kind == CloudSignInKind.AwsIam)
        {
            string user = DatabaseKinds.Value(connection, "Username", "User ID", "UserID", "User", "Uid") ?? throw new InvalidOperationException(Refusals.NoIamUser);
            string token = await RDSAuthTokenGenerator.GenerateAuthTokenAsync(credentials, RegionEndpoint.GetBySystemName(region), host, port, user);
            ContainerSecrets.Mark(token);
            laid["Password"] = token;
            return laid.ConnectionString;
        }
        using AmazonSecretsManagerClient secrets = new(credentials, AwsProfiles.Config<AmazonSecretsManagerConfig>(region));
        GetSecretValueResponse read;
        try
        {
            read = await secrets.GetSecretValueAsync(new GetSecretValueRequest { SecretId = signIn.Secret }, cancel);
        }
        catch (AmazonServiceException refused)
        {
            throw new IOException(Refusals.SecretUnread(signIn.Secret, refused.Message), refused);
        }
        try
        {
            using JsonDocument secret = JsonDocument.Parse(read.SecretString ?? string.Empty);
            string user = JsonMembers.Text(secret.RootElement, "username") ?? throw new FormatException("no username");
            string password = JsonMembers.Text(secret.RootElement, "password") ?? throw new FormatException("no password");
            ContainerSecrets.Mark(password);
            laid["User ID"] = user;
            laid["Password"] = password;
            return laid.ConnectionString;
        }
        catch (Exception unread) when (unread is JsonException or FormatException)
        {
            throw new IOException(Refusals.SecretUnread(signIn.Secret, $"it holds no username and password as RDS's secrets do ({unread.Message})"), unread);
        }
    }

    /// <summary>The region a sign-in asks: its own, else the one an RDS or Redshift host names (….us-east-1.rds.amazonaws.com),
    /// else its profile's; null where none says one.</summary>
    public static string? RegionOf(CloudSignIn signIn, string host)
    {
        if (signIn.Region is { Length: > 0 } own)
        {
            return own;
        }
        string[] labels = host.Split('.');
        int amazon = Array.FindLastIndex(labels, l => l.Equals("amazonaws", StringComparison.OrdinalIgnoreCase));
        return amazon >= 2 ? labels[amazon - 2] : AwsProfiles.RegionOf(signIn.Profile is { Length: > 0 } profile ? profile : "default");
    }

    /// <summary>The AWS profile a connection signs in with: its sign-in's, a DynamoDB connection's own with no key of its
    /// own (empty for the environment's or the default); null for one that signs in to no AWS service.</summary>
    public static string? ProfileOf(DatabaseSource source) =>
        source.SignIn is { } signIn ? signIn.Profile
        : source.Kind == DatabaseKind.DynamoDb && DatabaseKinds.Value(source.ConnectionString, "AccessKey") is null && DatabaseKinds.Value(source.ConnectionString, "ServiceURL", "Endpoint") is null
            ? DatabaseKinds.Value(source.ConnectionString, "Profile") ?? string.Empty
        : null;

    /// <summary>Whether a kept sign-in reads as one: a kind there is, and a secret named where it reads one.</summary>
    public static bool Usable(CloudSignIn? signIn) =>
        signIn is { Profile: not null, Region: not null, Secret: not null } && Enum.IsDefined(signIn.Kind) && (signIn.Kind != CloudSignInKind.AwsSecret || signIn.Secret.Length > 0);
}
