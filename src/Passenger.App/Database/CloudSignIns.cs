using System.Data.Common;
using System.Text.Json;
using Amazon;
using Amazon.RDS.Util;
using Amazon.Redshift;
using Amazon.Redshift.Model;
using Amazon.RedshiftServerless;
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

    /// <summary>A Microsoft Entra ID token of the account signed in, asked at each connection (<see cref="AzureSignIn"/>).</summary>
    AzureEntra,
}

/// <summary>A connection's cloud sign-in: its kind, the AWS profile it signs in with (empty for the environment's or the
/// default one) and the region it asks (empty for the one the server's host names, else the profile's); for Secrets
/// Manager the secret's name or ARN, and for Redshift's IAM the cluster or serverless workgroup (workgroup/NAME), empty for
/// the one its host names; for Entra ID the account (empty for the one signed in) and the tenant its token is of (empty
/// for the account's own).</summary>
public sealed record CloudSignIn(CloudSignInKind Kind, string Profile, string Region)
{
    public string Secret { get; init; } = string.Empty;

    public string Cluster { get; init; } = string.Empty;

    public string Account { get; init; } = string.Empty;

    public string Tenant { get; init; } = string.Empty;
}

/// <summary>A connection's string with its sign-in laid over it, and the token SqlClient takes apart from it (Entra ID's,
/// for SQL Server), null for none.</summary>
public sealed record SignedIn(string Connection, string? AccessToken);

/// <summary>What a cloud sign-in lays over a connection's string as it opens: the password an IAM token is, or the user and
/// password a secret holds, each made or read at each connection and never kept, masked wherever said.</summary>
internal static class CloudSignIns
{
    /// <summary>The kinds each sign-in signs in to: IAM authentication PostgreSQL's, MySQL's and Redshift's (RDS for SQL
    /// Server takes none), a secret every kind RDS runs and Redshift, Entra ID Azure's SQL, PostgreSQL and MySQL.</summary>
    public static bool Signs(CloudSignInKind sign, DatabaseKind kind) => sign switch
    {
        CloudSignInKind.AwsIam => kind is DatabaseKind.Postgres or DatabaseKind.MySql or DatabaseKind.Redshift,
        CloudSignInKind.AzureEntra => kind is DatabaseKind.Postgres or DatabaseKind.MySql or DatabaseKind.SqlServer,
        _ => kind is DatabaseKind.Postgres or DatabaseKind.MySql or DatabaseKind.SqlServer or DatabaseKind.Redshift,
    };

    /// <summary>Why a kind takes no sign-in of a kind, in its own words.</summary>
    public static string WhyNot(CloudSignInKind sign, DatabaseKind kind) =>
        sign == CloudSignInKind.AzureEntra ? Refusals.NoEntraSignIn(DatabaseKinds.Title(kind)) : Refusals.NoCloudSignIn(DatabaseKinds.Title(kind));

    /// <summary>The connection's string with what its sign-in gives laid over it, and SQL Server's Entra ID token apart.</summary>
    public static async Task<SignedIn> SignedInAsync(DatabaseKind kind, string connection, CloudSignIn signIn, CancellationToken cancel)
    {
        if (!Signs(signIn.Kind, kind))
        {
            throw new InvalidOperationException(WhyNot(signIn.Kind, kind));
        }
        if (signIn.Kind == CloudSignInKind.AzureEntra)
        {
            return await EntraAsync(kind, connection, signIn, cancel);
        }
        return new SignedIn(await AwsAsync(kind, connection, signIn, cancel), null);
    }

    /// <summary>An Entra ID token of the sign-in's account for the kind's Azure service: SQL Server's handed to SqlClient
    /// apart, its own user and password taken off; PostgreSQL's and MySQL's the password, the account's name the user where
    /// the connection names none.</summary>
    private static async Task<SignedIn> EntraAsync(DatabaseKind kind, string connection, CloudSignIn signIn, CancellationToken cancel)
    {
        string account = signIn.Account is { Length: > 0 } named ? named : await AzureSignIn.OnlyAccountAsync();
        string token = await AzureSignIn.TokenAsync(account, signIn.Tenant, kind == DatabaseKind.SqlServer ? AzureSignIn.AzureSql : AzureSignIn.OpenSourceDatabases, cancel);
        DbConnectionStringBuilder laid = DatabaseKinds.Builder(kind, connection);
        if (kind == DatabaseKind.SqlServer)
        {
            foreach (string key in (string[])["User ID", "Password", "Integrated Security", "Authentication"])
            {
                laid.Remove(key);
            }
            return new SignedIn(laid.ConnectionString, token);
        }
        if (DatabaseKinds.Value(connection, "Username", "User ID", "UserID", "User", "Uid") is null)
        {
            laid["User ID"] = account;
        }
        laid["Password"] = token;
        return new SignedIn(laid.ConnectionString, null);
    }

    /// <summary>The connection's string with what an AWS sign-in gives laid over it.</summary>
    private static async Task<string> AwsAsync(DatabaseKind kind, string connection, CloudSignIn signIn, CancellationToken cancel)
    {
        (string host, int port) = DatabaseKinds.Endpoint(kind, connection);
        string region = RegionOf(signIn, host) ?? throw new InvalidOperationException(Refusals.NoAwsRegion(host));
        AWSCredentials credentials = await AwsProfiles.CredentialsAsync(signIn.Profile, cancel);
        DbConnectionStringBuilder laid = DatabaseKinds.Builder(kind, connection);
        if (signIn.Kind == CloudSignInKind.AwsIam && kind == DatabaseKind.Redshift)
        {
            (string user, string password) = await RedshiftAsync(credentials, region, host, DatabaseKinds.Value(connection, "Database") ?? "dev", signIn, cancel);
            ContainerSecrets.Mark(password);
            laid["User ID"] = user;
            laid["Password"] = password;
            return laid.ConnectionString;
        }
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

    /// <summary>A Redshift user's credentials for a while, as Redshift gives them for the profile's IAM identity: a cluster's
    /// (GetClusterCredentialsWithIAM), or a serverless workgroup's (GetCredentials), the one the sign-in names, else the one
    /// its host names.</summary>
    private static async Task<(string User, string Password)> RedshiftAsync(AWSCredentials credentials, string region, string host, string database, CloudSignIn signIn,
        CancellationToken cancel)
    {
        const string Workgroup = "workgroup/";
        string named = signIn.Cluster is { Length: > 0 } own ? own
            : DatabaseKinds.IsRedshiftHost(host) ? (host.Contains(".redshift-serverless.", StringComparison.OrdinalIgnoreCase) ? Workgroup : string.Empty) + host.Split('.')[0]
            : throw new InvalidOperationException(Refusals.NoRedshiftCluster(host));
        try
        {
            if (named.StartsWith(Workgroup, StringComparison.Ordinal))
            {
                using AmazonRedshiftServerlessClient serverless = new(credentials, AwsProfiles.Config<AmazonRedshiftServerlessConfig>(region));
                Amazon.RedshiftServerless.Model.GetCredentialsResponse given = await serverless.GetCredentialsAsync(new Amazon.RedshiftServerless.Model.GetCredentialsRequest
                {
                    WorkgroupName = named[Workgroup.Length..], DbName = database,
                }, cancel);
                return (given.DbUser, given.DbPassword);
            }
            using AmazonRedshiftClient cluster = new(credentials, AwsProfiles.Config<AmazonRedshiftConfig>(region));
            GetClusterCredentialsWithIAMResponse issued = await cluster.GetClusterCredentialsWithIAMAsync(new GetClusterCredentialsWithIAMRequest
            {
                ClusterIdentifier = named, DbName = database,
            }, cancel);
            return (issued.DbUser, issued.DbPassword);
        }
        catch (AmazonServiceException refused)
        {
            throw new IOException(Refusals.RedshiftRefusedIam(named, refused.Message), refused);
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
        source.SignIn is { } signIn ? (signIn.Kind == CloudSignInKind.AzureEntra ? null : signIn.Profile)
        : source.Kind == DatabaseKind.DynamoDb && DatabaseKinds.Value(source.ConnectionString, "AccessKey") is null && DatabaseKinds.Value(source.ConnectionString, "ServiceURL", "Endpoint") is null
            ? DatabaseKinds.Value(source.ConnectionString, "Profile") ?? string.Empty
        : null;

    /// <summary>Whether a connection signs in through Entra ID, whose sign-in Sign In makes.</summary>
    public static bool SignsInToEntra(DatabaseSource source) => source.SignIn is { Kind: CloudSignInKind.AzureEntra };

    /// <summary>Whether a kept sign-in reads as one: a kind there is, and a secret named where it reads one.</summary>
    public static bool Usable(CloudSignIn? signIn) =>
        signIn is { Profile: not null, Region: not null, Secret: not null, Cluster: not null, Account: not null, Tenant: not null } && Enum.IsDefined(signIn.Kind)
        && (signIn.Kind != CloudSignInKind.AwsSecret || signIn.Secret.Length > 0);
}
