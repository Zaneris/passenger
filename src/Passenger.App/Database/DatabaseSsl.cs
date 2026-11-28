using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace Passenger.App.Database;

/// <summary>How a connection holds its server to SSL: as its driver does, not at all, required but not verified, or
/// verified against a certificate (its authority's, or for SQL Server the server's own), with the host name too.</summary>
public enum SslChoice
{
    Driver,
    Off,
    Required,
    VerifyAuthority,
    VerifyHost,
}

/// <summary>A connection's SSL: how its server is held to it, the file it is verified by, and the client certificate and
/// key it presents (PostgreSQL's and MySQL's; SQL Server takes none).</summary>
public sealed record SslSettings(SslChoice Choice, string? Authority = null, string? Certificate = null, string? Key = null);

/// <summary>A connection's SSL as each driver's own connection-string keys write it: PostgreSQL's SSL Mode, Root
/// Certificate, SSL Certificate, SSL Key and SSL Password; MySQL's SslMode, SslCa, SslCert, SslKey and CertificatePassword
/// (a password Passenger takes itself, as the driver reads only an unencrypted key's file); SQL Server's Encrypt,
/// TrustServerCertificate and ServerCertificate (a file pinning the server's certificate); SQLite has none. A key's
/// password is a secret part, kept as a connection's password is.</summary>
public static class DatabaseSsl
{
    /// <summary>The choices' words, in <see cref="SslChoice"/>'s order.</summary>
    public static readonly string[] Titles = ["As the driver does", "Off", "Required, not verified", "Verified: its certificate", "Verified: its certificate and host name"];

    /// <summary>Whether a kind of database takes a client certificate.</summary>
    public static bool TakesClientCertificate(DatabaseKind kind) => kind is DatabaseKind.Postgres or DatabaseKind.MySql;

    /// <summary>Whether the connection dialog offers the kind's SSL: PostgreSQL's, SQL Server's and MySQL's, whose drivers
    /// take it from these settings (ClickHouse's https and Snowflake's own are their connection strings').</summary>
    public static bool Offered(DatabaseKind kind) => DatabaseKinds.SpeaksPostgres(kind) || kind is DatabaseKind.SqlServer or DatabaseKind.MySql;

    /// <summary>What a connection string says of its SSL.</summary>
    public static SslSettings Of(DatabaseKind kind, string connection)
    {
        switch (kind)
        {
            case DatabaseKind.Postgres or DatabaseKind.Redshift:
                NpgsqlConnectionStringBuilder pg = new(connection);
                SslChoice pgChoice = !Has(connection, "sslmode") ? SslChoice.Driver : pg.SslMode switch
                {
                    SslMode.Disable => SslChoice.Off,
                    SslMode.VerifyCA => SslChoice.VerifyAuthority,
                    SslMode.VerifyFull => SslChoice.VerifyHost,
                    SslMode.Require => SslChoice.Required,
                    _ => SslChoice.Driver,
                };
                return new SslSettings(pgChoice, Named(pg.RootCertificate), Named(pg.SslCertificate), Named(pg.SslKey));
            case DatabaseKind.MySql:
                MySqlConnectionStringBuilder my = new(connection);
                SslChoice myChoice = !Has(connection, "sslmode") ? SslChoice.Driver : my.SslMode switch
                {
                    MySqlSslMode.None => SslChoice.Off,
                    MySqlSslMode.Required => SslChoice.Required,
                    MySqlSslMode.VerifyCA => SslChoice.VerifyAuthority,
                    MySqlSslMode.VerifyFull => SslChoice.VerifyHost,
                    _ => SslChoice.Driver,
                };
                return new SslSettings(myChoice, Named(my.SslCa), Named(my.SslCert), Named(my.SslKey));
            case DatabaseKind.SqlServer:
                SqlConnectionStringBuilder sql = new(connection);
                SslChoice sqlChoice = !Has(connection, "encrypt") ? SslChoice.Driver
                    : sql.Encrypt == SqlConnectionEncryptOption.Optional ? SslChoice.Off
                    : sql.TrustServerCertificate ? SslChoice.Required
                    : sql.Encrypt == SqlConnectionEncryptOption.Strict ? SslChoice.VerifyHost : SslChoice.VerifyAuthority;
                return new SslSettings(sqlChoice, Named(sql.ServerCertificate));
            default:
                return new SslSettings(SslChoice.Driver);
        }
    }

    /// <summary>A connection string holding <paramref name="ssl"/> in its driver's keys, its key's password given where it
    /// is (else left as the string holds it); the driver's own choice leaves the mode and its file out.</summary>
    public static string With(DatabaseKind kind, string connection, SslSettings ssl, string? keyPassword = null)
    {
        string? authority = Named(ssl.Authority);
        string? certificate = ssl.Choice == SslChoice.Off ? null : Named(ssl.Certificate);
        string? key = certificate is null ? null : Named(ssl.Key);
        switch (kind)
        {
            case DatabaseKind.Postgres or DatabaseKind.Redshift:
                NpgsqlConnectionStringBuilder pg = new(connection);
                pg.Remove("SSL Mode");
                pg.Remove("Root Certificate");
                pg.Remove("SSL Certificate");
                pg.Remove("SSL Key");
                if (ssl.Choice != SslChoice.Driver)
                {
                    pg.SslMode = ssl.Choice switch
                    {
                        SslChoice.Off => SslMode.Disable,
                        SslChoice.Required => SslMode.Require,
                        SslChoice.VerifyAuthority => SslMode.VerifyCA,
                        _ => SslMode.VerifyFull,
                    };
                    pg.RootCertificate = authority;
                }
                pg.SslCertificate = certificate;
                pg.SslKey = key;
                if (certificate is null)
                {
                    pg.Remove("SSL Password");
                }
                else if (keyPassword is { Length: > 0 })
                {
                    pg.SslPassword = keyPassword;
                }
                return pg.ConnectionString;
            case DatabaseKind.MySql:
                MySqlConnectionStringBuilder my = new(connection);
                my.Remove("SslMode");
                my.Remove("SslCa");
                my.Remove("SslCert");
                my.Remove("SslKey");
                if (ssl.Choice != SslChoice.Driver)
                {
                    my.SslMode = ssl.Choice switch
                    {
                        SslChoice.Off => MySqlSslMode.None,
                        SslChoice.Required => MySqlSslMode.Required,
                        SslChoice.VerifyAuthority => MySqlSslMode.VerifyCA,
                        _ => MySqlSslMode.VerifyFull,
                    };
                    if (authority is not null)
                    {
                        my.SslCa = authority;
                    }
                }
                if (certificate is not null)
                {
                    my.SslCert = certificate;
                    my.SslKey = key ?? string.Empty;
                }
                if (certificate is null)
                {
                    my.Remove("CertificatePassword");
                }
                else if (keyPassword is { Length: > 0 })
                {
                    my.CertificatePassword = keyPassword;
                }
                return my.ConnectionString;
            case DatabaseKind.SqlServer:
                SqlConnectionStringBuilder sql = new(connection);
                sql.Remove("Encrypt");
                sql.Remove("TrustServerCertificate");
                sql.Remove("ServerCertificate");
                if (ssl.Choice != SslChoice.Driver)
                {
                    sql.Encrypt = ssl.Choice switch
                    {
                        SslChoice.Off => SqlConnectionEncryptOption.Optional,
                        SslChoice.VerifyHost => SqlConnectionEncryptOption.Strict,
                        _ => SqlConnectionEncryptOption.Mandatory,
                    };
                    sql.TrustServerCertificate = ssl.Choice == SslChoice.Required;
                    if (authority is not null && ssl.Choice is SslChoice.VerifyAuthority or SslChoice.VerifyHost)
                    {
                        sql.ServerCertificate = authority;
                    }
                }
                return sql.ConnectionString;
            default:
                return connection;
        }
    }

    /// <summary>A MySQL connection's client certificate with its encrypted key, which Passenger reads itself, as the driver
    /// reads only an unencrypted key's file.</summary>
    public static X509Certificate2 ClientCertificate(string certificate, string key, string password)
    {
        X509Certificate2 read = X509Certificate2.CreateFromEncryptedPemFile(certificate, password, key);
        if (!OperatingSystem.IsWindows())
        {
            return read;
        }
        // Windows' TLS takes a client certificate's key only from a store or a PKCS #12 file, not one held in memory.
        using (read)
        {
            return X509CertificateLoader.LoadPkcs12(read.Export(X509ContentType.Pkcs12), null);
        }
    }

    /// <summary>Whether a server's certificate is signed by an authority a PEM file holds (its chain built to it alone) and,
    /// given a host, names it: for a MySQL connection through a tunnel, whose driver would check the tunnel's own address.</summary>
    public static bool Verified(X509Certificate? certificate, string authorities, string? host)
    {
        if (certificate is null)
        {
            return false;
        }
        using X509Certificate2 server = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        X509Certificate2Collection roots = [];
        try
        {
            roots.ImportFromPemFile(authorities);
        }
        catch (Exception unread) when (unread is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        bool trusted = chain.Build(server);
        foreach (X509Certificate2 root in roots)
        {
            root.Dispose();
        }
        return trusted && (host is null || server.MatchesHostname(host));
    }

    /// <summary>Whether a connection string sets a key, written with any case, spaces, hyphens or underscores.</summary>
    private static bool Has(string connection, string key)
    {
        System.Data.Common.DbConnectionStringBuilder written = new() { ConnectionString = connection };
        return written.Keys.Cast<string>().Any(k => string.Concat(k.Where(c => c is not (' ' or '_' or '-'))).Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Named(string? file) => string.IsNullOrWhiteSpace(file) ? null : file;
}
