using System.Text.Json;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

public sealed partial class CSharpDocument
{
    private const string ConfigurationDiagnosticSource = "Passenger.Configuration";

    /// <summary>The names of configuration's API a file must hold for its reads to be asked for: a cheap look at its text,
    /// so a file that reads none asks Passenger's extension nothing. A read's receiver is configuration by one of these.</summary>
    private static readonly string[] ConfigurationWords = ["Configuration", "GetSection", "GetRequiredSection"];

    /// <summary>Whether the file's text names configuration's API, so it may read a key nothing gives.</summary>
    internal bool MayReadConfiguration => Holds(ConfigurationWords);

    /// <summary>A settings file, user secrets or launch profile changed: pulled again when the file was warned of a key or may
    /// read one, or is an AppHost's, whose parameters' values are read there.</summary>
    internal void ConfigurationChanged()
    {
        if (diagnostics.Placed.Any(d => d.Source == ConfigurationDiagnosticSource) || MayReadConfiguration || AspireRole == Aspire.AspireRole.AppHost)
        {
            Pull(TimeSpan.Zero);
        }
    }

    /// <summary>The keys the file's warnings say nothing gives, on a server's line of the text as it stands (any line for
    /// null): each with where it is read, the key, and whether its name is a secret's, which its fix keeps in user secrets.</summary>
    internal IReadOnlyList<(TextRange Range, string Key, bool Secret)> ConfigurationMissingOn(int? line)
    {
        List<(TextRange, string, bool)> missing = [];
        foreach ((Diagnostic diagnostic, TextRange range) in diagnostics.PlacedRanges)
        {
            if (diagnostic.Source == ConfigurationDiagnosticSource
                && (line is not { } on || diagnostic.Range.Start.Line <= on && on <= diagnostic.Range.End.Line)
                && diagnostic.Data is { ValueKind: JsonValueKind.Object } data
                && data.TryGetProperty("key", out JsonElement key) && key.GetString() is { } named)
            {
                missing.Add((range, named, data.TryGetProperty("secret", out JsonElement secret) && secret.GetString() == "true"));
            }
        }
        return missing;
    }

    /// <summary>Whether a configuration key is a secret's by Passenger's one rule (<see cref="Containers.ContainerSecrets.Secret(string)"/>),
    /// its sections read as the words of one name (Payments:ApiKey as Payments_ApiKey).</summary>
    internal static bool SecretKey(string key) => Containers.ContainerSecrets.Secret(key.Replace(':', '_'));

    /// <summary>The file's reads of keys nothing the project runs with gives (<see cref="ConfigurationFiles.Given"/>),
    /// each a warning on its key: a value read (an indexer, GetValue, GetConnectionString, a section's Value) with no
    /// default and its result never checked for null, or a section GetRequiredSection wants; but those
    /// <paramref name="beside"/> warns of and an Aspire service's connection strings. The reads are asked of the
    /// extension by the project; the files read off the UI thread.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> ConfigurationFindingsAsync(IReadOnlyList<Diagnostic> beside, CancellationToken cancel)
    {
        if (service.Session is not { } roslyn || service.ConfigurationOf?.Invoke(document.Path) is not { } project || !MayReadConfiguration)
        {
            return [];
        }
        ConnectionReadsFound found;
        try
        {
            found = await roslyn.ExtensionAsync<ConnectionReadsFound>(Handlers.ConnectionReads, new ConnectionReadsRequest([project.Project], Keys: true), cancel);
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
        bool aspire = AspireRole == Aspire.AspireRole.Service;
        ConfigurationKey[] reads = [.. (found.Keys ?? [])
            .Where(k => !k.Guarded && k.Kind is ConfigurationKinds.Read or ConfigurationKinds.Section && k.Key.Length > 0 && k.At is not null && k.End is not null
                && k.Path is { } path && PathNames.Same(path, document.Path)
                && !(aspire && k.Key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
                && !beside.Any(d => d.Range.Start.Line == k.At.Line && d.Range.Start.Character <= k.End!.Character && k.At.Character <= d.Range.End.Character))
            // A required section's own read is the value's where both read the same string.
            .GroupBy(k => (k.At!.Line, k.At.Character)).Select(g => g.FirstOrDefault(k => k.Kind == ConfigurationKinds.Read) ?? g.First())];
        if (reads.Length == 0)
        {
            return [];
        }
        Func<string, string?> text = service.OpenTexts().Text;
        (IReadOnlyList<GivenKey> given, IReadOnlyList<string> environments) = await BackgroundWork.Run(() =>
            (ConfigurationFiles.Given(project, text), ConfigurationFiles.Environments(project, text)));
        string settings = string.Join(" or ", environments.Select(e => $"appsettings.{e}.json").Prepend("appsettings.json"));
        return [.. reads.Where(r => !ConfigurationFiles.Gives(given, r.Key, r.Kind == ConfigurationKinds.Section)).Select(r => new Diagnostic
        {
            Range = new LspRange(new LspPosition(r.At!.Line, r.At.Character), new LspPosition(r.End!.Line, r.End.Character)),
            Severity = DiagnosticSeverity.Warning,
            Source = ConfigurationDiagnosticSource,
            Message = $"Nothing {project.Name} runs with gives the configuration key '{r.Key}': not {settings}, a launch profile, its user secrets, "
                + "a compose file or an AppHost.",
            Data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["key"] = r.Key, ["secret"] = SecretKey(r.Key) ? "true" : "false" }),
        })];
    }
}
