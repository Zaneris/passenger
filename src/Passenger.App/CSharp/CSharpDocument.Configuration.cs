using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using static Microsoft.CodeAnalysis.CSharpExtensions;

namespace Passenger.App.CSharp;

public sealed partial class CSharpDocument
{
    private const string ConfigurationDiagnosticSource = "Passenger.Configuration";

    /// <summary>The names of configuration's API a file holds for its reads to be asked for: a cheap look at its text, so a
    /// file that reads none asks Passenger's extension nothing. A read's receiver declared in the file is configuration by
    /// one of these.</summary>
    private static readonly string[] ConfigurationWords = ["Configuration", "GetSection", "GetRequiredSection"];

    /// <summary>The calls a read of configuration is made with, by name, as the extension looks for them.</summary>
    private static readonly HashSet<string> ConfigurationCalls = new(StringComparer.Ordinal)
    {
        "GetSection", "GetRequiredSection", "GetValue", "GetConnectionString", "Configure", "Bind", "BindConfiguration", "Get",
    };

    /// <summary>Whether the file may read a key nothing gives: its text names configuration's API, or a type it declares
    /// takes members from another file (a partial type's other parts, a base type) and reads as the API is read.</summary>
    internal bool MayReadConfiguration => Holds(ConfigurationWords) || ReadsThroughItsType();

    /// <summary>Whether a type the file declares can reach configuration by a member declared elsewhere, which its text then
    /// never names: one partial or deriving from another, holding a string indexer or a call of the API's names.</summary>
    private bool ReadsThroughItsType()
    {
        if (document.Syntax?.Current is not { } parsed)
        {
            return false;
        }
        IEnumerable<TypeDeclarationSyntax> types = parsed.Root
            .DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
            .OfType<TypeDeclarationSyntax>();
        return types.Any(type => (type.Modifiers.Any(SyntaxKind.PartialKeyword) || type.BaseList is not null) && type.Members.Any(ReadsAsConfiguration));

        static bool ReadsAsConfiguration(MemberDeclarationSyntax member) => member is not BaseTypeDeclarationSyntax && member.DescendantNodes().Any(node => node switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: var name } } => ConfigurationCalls.Contains(name.Identifier.ValueText),
            InvocationExpressionSyntax { Expression: SimpleNameSyntax name } => ConfigurationCalls.Contains(name.Identifier.ValueText),
            ElementAccessExpressionSyntax access => access.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax literal }]
                && literal.IsKind(SyntaxKind.StringLiteralExpression),
            _ => false,
        });
    }

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
