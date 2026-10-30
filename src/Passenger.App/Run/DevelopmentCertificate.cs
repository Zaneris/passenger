using System.Diagnostics;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>The .NET command line <c>dotnet dev-certs</c> runs through: the machine's
/// (<see cref="DevelopmentCertificate.Machine"/>), or a test's stand-in.</summary>
public delegate ProcessStartInfo DotnetWords(IReadOnlyList<string> words, string folder);

/// <summary>ASP.NET Core's development certificate for a run that serves HTTPS (a container run's services, an Aspire
/// AppHost and the services it starts): how it stands on this machine (<c>dotnet dev-certs https --check --trust</c>), made
/// and trusted when the person asks (<c>--trust</c>, which the system may ask about too), and, for containers, exported
/// once into the state as PEM with its key unencrypted (<c>-ep … --format Pem --no-password</c>) in a folder only the person
/// enters, so no password is on any command line; each through ChildProcesses, bounded, saying why when it does not come.</summary>
public static partial class DevelopmentCertificate
{
    /// <summary>The certificate exported for containers: its folder, which a container mounts read-only, and its certificate
    /// and key there, which Kestrel reads as PEM.</summary>
    public sealed record Exported(string Folder, string Certificate, string Key);

    /// <summary>The exported certificate's name in its folder, its key's the same with <c>.key</c>.</summary>
    private const string CertificateFile = "aspnetapp.pem";

    /// <summary>What an earlier Passenger exported there: a .pfx and the password beside it.</summary>
    private static readonly string[] Earlier = ["aspnetapp.pfx", "password"];

    /// <summary>What tells a .NET program that serves HTTPS to serve HTTP for this run, there being no certificate to give it
    /// (the person chose so, nobody was there to ask, or the export failed), given its environment: its https addresses as
    /// http, and its HTTPS ports among its HTTP ones, so it listens where it would and Kestrel finds no HTTPS to want a
    /// certificate for. Only what changes.</summary>
    public static Dictionary<string, string> OverHttp(IReadOnlyDictionary<string, string> environment)
    {
        Dictionary<string, string> changed = new(StringComparer.Ordinal);
        if (environment.GetValueOrDefault(AspNetCoreVariables.Urls) is { } urls && AspNetCoreVariables.HttpsIn(urls))
        {
            changed[AspNetCoreVariables.Urls] = AspNetCoreVariables.AsHttp(urls);
        }
        if (environment.GetValueOrDefault(AspNetCoreVariables.HttpsPorts) is { Length: > 0 } https)
        {
            string http = environment.GetValueOrDefault(AspNetCoreVariables.HttpPorts) ?? string.Empty;
            changed[AspNetCoreVariables.HttpPorts] = string.Join(';', (http + ";" + https).Split([';', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());
            changed[AspNetCoreVariables.HttpsPorts] = string.Empty;
        }
        return changed;
    }

    /// <summary>How this machine's development certificate stands.</summary>
    public enum Standing
    {
        Trusted,
        NotTrusted,
        Missing,
        Unknown,
    }

    /// <summary>The machine's own dotnet.</summary>
    public static readonly DotnetWords Machine = (words, folder) => ChildProcesses.DotnetCommand(words, folder);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>How long the export or the trust may take: one waiting on the system's certificate store (a locked keychain
    /// asking for its password) is said rather than the run waiting for good.</summary>
    public static readonly TimeSpan ExportLimit = TimeSpan.FromSeconds(60);

    /// <summary>How long the check may take, which asks nothing of the person.</summary>
    public static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(30);

    /// <summary>Whether a certificate is there and trusted, as dev-certs answers (6: none valid, 7: not trusted), what it said,
    /// and the thumbprint of the certificate it found, when it named one. Stopped, it throws <see cref="OperationCanceledException"/>.</summary>
    public static async Task<(Standing Standing, string Said, string? Thumbprint)> CheckAsync(DotnetWords dotnet, string folder, CancellationToken cancel)
    {
        try
        {
            ChildResult answered = await ChildProcesses.RunAsync(dotnet(["dev-certs", "https", "--check", "--trust"], folder), limit: CheckLimit, cancel: cancel,
                answers: [6, 7]);
            string? thumbprint = answered.ExitCode is 0 or 7 && Thumbprint().Match(answered.Output) is { Success: true } found ? found.Value.ToUpperInvariant() : null;
            return (answered.ExitCode switch { 0 => Standing.Trusted, 6 => Standing.Missing, 7 => Standing.NotTrusted, _ => Standing.Unknown }, Said(answered), thumbprint);
        }
        catch (TimeoutException)
        {
            return (Standing.Unknown, $"dotnet dev-certs https --check did not answer within {CheckLimit.TotalSeconds:0} s", null);
        }
        catch (Exception failed) when (failed is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (Standing.Unknown, failed.Message, null);
        }
    }

    /// <summary>A certificate's SHA-1 thumbprint, as dev-certs names the one it found ("A valid certificate was found: …").</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\b[0-9A-Fa-f]{40}\b")]
    private static partial System.Text.RegularExpressions.Regex Thumbprint();

    /// <summary>Makes the certificate if there is none and trusts it, as <c>dotnet dev-certs https --trust</c> does (the
    /// system may ask the person too), the copy exported before let go: null once trusted, else why not.</summary>
    public static async Task<string?> TrustAsync(DotnetWords dotnet, string folder, string state, CancellationToken cancel)
    {
        try
        {
            ChildResult trusted = await ChildProcesses.RunAsync(dotnet(["dev-certs", "https", "--trust"], folder), limit: ExportLimit, cancel: cancel);
            Forget(state);
            return trusted.Succeeded ? null : $"dotnet dev-certs https --trust ended with exit code {trusted.ExitCode}: {Said(trusted)}";
        }
        catch (TimeoutException)
        {
            return $"dotnet dev-certs https --trust did not end within {ExportLimit.TotalSeconds:0} s (the system's certificate store may be asking for a password)";
        }
        catch (Exception failed) when (failed is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return failed.Message;
        }
    }

    /// <summary>The certificate exported into the state (once for the certificate dev-certs has, <paramref name="thumbprint"/>
    /// as its check named it: again once that is another, or after <see cref="TrustAsync"/>), or why it is not. Stopped, it
    /// throws <see cref="OperationCanceledException"/>.</summary>
    public static async Task<(Exported? Certificate, string? Problem)> ExportAsync(DotnetWords dotnet, string state, string? thumbprint, CancellationToken cancel)
    {
        string folder = Path.Combine(state, "https");
        string file = Path.Combine(folder, CertificateFile);
        // dev-certs writes a PEM export's key beside it, named for it.
        string key = Path.ChangeExtension(file, ".key");
        // The exported certificate's thumbprint: a copy of one since replaced would be refused by browsers trusting the new one.
        string exportedOf = Path.Combine(folder, "thumbprint");
        await Gate.WaitAsync(cancel);
        try
        {
            OwnFiles.Folder(folder);
            // An earlier Passenger's .pfx and its password, which the folder mounted would give every container.
            Delete(state, Earlier);
            if (File.Exists(file) && File.Exists(key)
                && (thumbprint is null || File.Exists(exportedOf) && string.Equals((await File.ReadAllTextAsync(exportedOf, cancel)).Trim(), thumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                return (new Exported(folder, file, key), null);
            }
            Forget(state);
            // PEM with its key unencrypted, in a folder only the person enters: dev-certs takes a password only on its command line.
            ChildResult exported = await ChildProcesses.RunAsync(dotnet(["dev-certs", "https", "-ep", file, "--format", "Pem", "--no-password"], folder),
                limit: ExportLimit, cancel: cancel);
            if (!exported.Succeeded || !File.Exists(file) || !File.Exists(key))
            {
                return (null, $"dotnet dev-certs https -ep ended with exit code {exported.ExitCode}: {Said(exported)}");
            }
            // Whatever modes dev-certs gave them, in the folder no one else enters.
            OwnFiles.Keep(file);
            OwnFiles.Keep(key);
            if (thumbprint is not null)
            {
                OwnFiles.Write(exportedOf, thumbprint);
            }
            return (new Exported(folder, file, key), null);
        }
        catch (TimeoutException)
        {
            return (null, $"dotnet dev-certs https -ep did not end within {ExportLimit.TotalSeconds:0} s (the system's certificate store may be asking for a password)");
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FileNotFoundException
            or InvalidOperationException)
        {
            return (null, failed.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Lets go of the copy exported before, which a certificate made since no longer matches.</summary>
    private static void Forget(string state) =>
        Delete(state, [CertificateFile, Path.ChangeExtension(CertificateFile, ".key"), "thumbprint", .. Earlier]);

    /// <summary>Deletes these files of the state's certificate folder, where there.</summary>
    private static void Delete(string state, IEnumerable<string> files)
    {
        try
        {
            foreach (string kept in files)
            {
                if (Path.Combine(state, "https", kept) is var file && File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            // An export kept stands until the next one replaces it.
        }
    }

    private static string Said(ChildResult result) =>
        string.Join(' ', ((string[])[result.Error.Trim(), result.Output.Trim()]).Where(s => s.Length > 0)) is { Length: > 0 } said ? said : "it said nothing";
}
