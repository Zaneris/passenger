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

/// <summary>A connection's SSL as each driver's own connection-string keys write it: PostgreSQL's SSL Mode and Root
/// Certificate, MySQL's SslMode and SslCa, SQL Server's Encrypt, TrustServerCertificate and ServerCertificate (a file
/// pinning the server's certificate); SQLite has none.</summary>
public static class DatabaseSsl
{
    /// <summary>The choices' words, in <see cref="SslChoice"/>'s order.</summary>
    public static readonly string[] Titles = ["As the driver does", "Off", "Required, not verified", "Verified: its certificate", "Verified: its certificate and host name"];

    /// <summary>The choice a connection string makes, and the certificate file it verifies by (null for none).</summary>
    public static (SslChoice Choice, string? Certificate) Of(DatabaseKind kind, string connection)
    {
        switch (kind)
        {
            case DatabaseKind.Postgres:
                NpgsqlConnectionStringBuilder pg = new(connection);
                SslChoice pgChoice = !Has(connection, "sslmode") ? SslChoice.Driver : pg.SslMode switch
                {
                    SslMode.Disable => SslChoice.Off,
                    SslMode.VerifyCA => SslChoice.VerifyAuthority,
                    SslMode.VerifyFull => SslChoice.VerifyHost,
                    SslMode.Require => SslChoice.Required,
                    _ => SslChoice.Driver,
                };
                return (pgChoice, Named(pg.RootCertificate));
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
                return (myChoice, Named(my.SslCa));
            case DatabaseKind.SqlServer:
                SqlConnectionStringBuilder sql = new(connection);
                SslChoice sqlChoice = !Has(connection, "encrypt") ? SslChoice.Driver
                    : sql.Encrypt == SqlConnectionEncryptOption.Optional ? SslChoice.Off
                    : sql.TrustServerCertificate ? SslChoice.Required
                    : sql.Encrypt == SqlConnectionEncryptOption.Strict ? SslChoice.VerifyHost : SslChoice.VerifyAuthority;
                return (sqlChoice, Named(sql.ServerCertificate));
            default:
                return (SslChoice.Driver, null);
        }
    }

    /// <summary>A connection string holding <paramref name="choice"/> and its certificate file in its driver's keys; the
    /// driver's own choice, both keys left out.</summary>
    public static string With(DatabaseKind kind, string connection, SslChoice choice, string? certificate)
    {
        string? file = string.IsNullOrWhiteSpace(certificate) ? null : certificate.Trim();
        switch (kind)
        {
            case DatabaseKind.Postgres:
                NpgsqlConnectionStringBuilder pg = new(connection);
                pg.Remove("SSL Mode");
                pg.Remove("Root Certificate");
                if (choice != SslChoice.Driver)
                {
                    pg.SslMode = choice switch
                    {
                        SslChoice.Off => SslMode.Disable,
                        SslChoice.Required => SslMode.Require,
                        SslChoice.VerifyAuthority => SslMode.VerifyCA,
                        _ => SslMode.VerifyFull,
                    };
                    pg.RootCertificate = file;
                }
                return pg.ConnectionString;
            case DatabaseKind.MySql:
                MySqlConnectionStringBuilder my = new(connection);
                my.Remove("SslMode");
                my.Remove("SslCa");
                if (choice != SslChoice.Driver)
                {
                    my.SslMode = choice switch
                    {
                        SslChoice.Off => MySqlSslMode.None,
                        SslChoice.Required => MySqlSslMode.Required,
                        SslChoice.VerifyAuthority => MySqlSslMode.VerifyCA,
                        _ => MySqlSslMode.VerifyFull,
                    };
                    my.SslCa = file ?? string.Empty;
                }
                return my.ConnectionString;
            case DatabaseKind.SqlServer:
                SqlConnectionStringBuilder sql = new(connection);
                sql.Remove("Encrypt");
                sql.Remove("TrustServerCertificate");
                sql.Remove("ServerCertificate");
                if (choice != SslChoice.Driver)
                {
                    sql.Encrypt = choice switch
                    {
                        SslChoice.Off => SqlConnectionEncryptOption.Optional,
                        SslChoice.VerifyHost => SqlConnectionEncryptOption.Strict,
                        _ => SqlConnectionEncryptOption.Mandatory,
                    };
                    sql.TrustServerCertificate = choice == SslChoice.Required;
                    if (file is not null && choice is SslChoice.VerifyAuthority or SslChoice.VerifyHost)
                    {
                        sql.ServerCertificate = file;
                    }
                }
                return sql.ConnectionString;
            default:
                return connection;
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
