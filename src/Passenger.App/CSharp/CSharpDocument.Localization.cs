using System.Text.Json;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Xaml.Localization;

namespace Passenger.App.CSharp;

/// <summary>The localized strings a file reads (<see cref="CSharpService.LocalizedAsync"/>): each key where it is read, the
/// resources they are of, and each resource's files, null for one none are found of.</summary>
internal sealed record Localized(IReadOnlyList<LocalizedKey> Keys, IReadOnlyList<LocalizedResource> Resources, IReadOnlyList<ResourceSet?> Sets)
{
    /// <summary>For a resource none are found of, the files beside the Razor component reading it that are named for its file
    /// rather than its type (ServiceQueue-Edit.fr.resx for ServiceQueue_Edit), which the localizer never reads; else null.</summary>
    public IReadOnlyList<ResourceSet?> Unnamed { get; init; } = [];

    /// <summary>Those files of a Razor file's, for each resource none are found of whose type's name is not the file's.</summary>
    public static IReadOnlyList<ResourceSet?> UnnamedOf(string path, IReadOnlyList<LocalizedResource> resources, IReadOnlyList<ResourceSet?> sets,
        Func<string, TextSnapshot?> open)
    {
        bool razor = RazorFiles.Is(path);
        string stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        return [.. resources.Select((r, i) => razor && sets[i] is null && r.Kind == LocalizedKinds.Localizer
            && !Path.GetFileName(stem).Equals(r.Name.Split('.')[^1], StringComparison.OrdinalIgnoreCase)
            && ResourceFiles.Of(stem, open) is { Files.Count: > 0 } near ? near : null)];
    }

    /// <summary>A resource's files: those beside the code that name it (a Designer file's .resx, a .cs file's of its type's name)
    /// first, else those MSBuild names as ResourceManager looks it up, under its project's folder; null where it has none. Its
    /// paths are canonical, as the server may spell them otherwise.</summary>
    public static ResourceSet? SetOf(LocalizedResource resource, Func<string, TextSnapshot?> open)
    {
        string folder = PathNames.Canonical(Path.GetDirectoryName(resource.Project)!);
        IEnumerable<string> beside = resource.Beside.Select(b => Path.Combine(PathNames.Canonical(Path.GetDirectoryName(b)!), Path.GetFileName(b)));
        return ResourceFiles.Find(beside, folder, resource.RootNamespace, resource.Name, open);
    }
}

public sealed partial class CSharpDocument
{
    private const string LocalizationDiagnosticSource = "Passenger.Localization";

    /// <summary>Whether the file's last pull found it reads localized strings.</summary>
    private bool readsLocalized;

    /// <summary>Whether the file has been pulled since it was opened, so what it reads is known.</summary>
    private bool localizationPulled;

    /// <summary>Whether the file may read localized strings, so an agent's call pulls it before it reads Passenger's fixes: its
    /// last pull found some, or none has landed yet, or its text names a localizer.</summary>
    internal bool MayLocalize => readsLocalized || !localizationPulled || Holds(["Localizer"]);

    /// <summary>A .resx or XLIFF file changed: pulled again when the file was warned of a key a language lacks or reads one.</summary>
    internal void LocalizationChanged()
    {
        if (readsLocalized || diagnostics.Placed.Any(d => d.Source == LocalizationDiagnosticSource))
        {
            Pull(TimeSpan.Zero);
        }
    }

    /// <summary>The keys the file's warnings say a language lacks, on a server's line of the text as it stands (any line for
    /// null): each with where it is read, the key, the file to give it in, its culture, the resource's base path, and whether
    /// the file has the key untranslated (an XLIFF unit still new).</summary>
    internal IReadOnlyList<(TextRange Range, string Key, string File, string Culture, string Base, bool Untranslated)> LocalizationMissingOn(int? line)
    {
        List<(TextRange, string, string, string, string, bool)> missing = [];
        foreach ((Diagnostic diagnostic, TextRange range) in diagnostics.PlacedRanges)
        {
            if (diagnostic.Source == LocalizationDiagnosticSource
                && (line is not { } on || diagnostic.Range.Start.Line <= on && on <= diagnostic.Range.End.Line)
                && diagnostic.Data is { ValueKind: JsonValueKind.Object } data
                && Field(data, "key") is { } key && Field(data, "file") is { } file && Field(data, "culture") is { } culture && Field(data, "base") is { } basePath)
            {
                missing.Add((range, key, file, culture, basePath, Field(data, "untranslated") == "true"));
            }
        }
        return missing;

        static string? Field(JsonElement data, string name) => data.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
    }

    /// <summary>The file's reads of localized strings a language lacks, each a warning where its key is written, one a language:
    /// a key of a localizer (<c>IStringLocalizer&lt;T&gt;</c>, <c>IHtmlLocalizer&lt;T&gt;</c>) or a generated resource class that a
    /// culture of its files has no entry of, or an XLIFF unit of still new. The reads are Passenger's extension's, by the
    /// compiler's binding (a Razor file's through the C# Razor makes of it); the files are read off the UI thread. A component
    /// whose files are named for its file rather than its type, which its localizer never reads, is warned of once.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> LocalizationFindingsAsync(CancellationToken cancel)
    {
        if (Lsp is null || await service.LocalizedAsync(document.Path, cancel) is not { } localized)
        {
            return [];
        }
        readsLocalized = localized.Keys.Count > 0;
        localizationPulled = true;
        List<Diagnostic> found = [];
        HashSet<int> unread = [];
        foreach (LocalizedKey read in localized.Keys)
        {
            // Files named for the component's file, not its type: said once, at its first key.
            if (localized.Unnamed.ElementAtOrDefault(read.Resource) is { } near && unread.Add(read.Resource))
            {
                LocalizedResource resource = localized.Resources[read.Resource];
                found.Add(new Diagnostic
                {
                    Range = new LspRange(new LspPosition(read.At.Line, read.At.Character), new LspPosition(read.End.Line, read.End.Character)),
                    Severity = DiagnosticSeverity.Warning,
                    Source = LocalizationDiagnosticSource,
                    Message = $"No .resx gives {resource.Type}'s strings, so each shows as written: {Plural.Listed([.. near.Files.Select(f => f.Name)])} "
                        + $"{(near.Files.Count == 1 ? "is" : "are")} named for {near.Name}, not {resource.Name.Split('.')[^1]}",
                });
            }
            if (read.Key.Length == 0 || localized.Sets[read.Resource] is not { } set)
            {
                continue;
            }
            foreach (ResourceGap gap in set.Lacking(read.Key))
            {
                found.Add(new Diagnostic
                {
                    Range = new LspRange(new LspPosition(read.At.Line, read.At.Character), new LspPosition(read.End.Line, read.End.Character)),
                    Severity = DiagnosticSeverity.Warning,
                    Source = LocalizationDiagnosticSource,
                    Message = gap.Message,
                    Data = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        ["key"] = gap.Key,
                        ["file"] = gap.File.Path,
                        ["culture"] = gap.Culture,
                        ["base"] = gap.BasePath,
                        ["untranslated"] = gap.Untranslated ? "true" : "false",
                    }),
                });
            }
        }
        return found;
    }

    /// <summary>Whether the file's text names any of some words: looked through a piece at a time, never copied whole.</summary>
    private bool Holds(string[] words)
    {
        TextSnapshot text = document.Text.Snapshot;
        bool found = false;
        string carry = string.Empty;
        text.VisitChunks(0, text.Length, chunk =>
        {
            if (found)
            {
                return;
            }
            ReadOnlySpan<char> piece = chunk.Span;
            // A word split between two pieces is found where the end of one meets the start of the next.
            string seam = carry + new string(piece[..Math.Min(piece.Length, 24)]);
            foreach (string word in words)
            {
                found |= piece.Contains(word, StringComparison.Ordinal) || seam.Contains(word, StringComparison.Ordinal);
            }
            carry = piece.Length >= 24 ? new string(piece[^24..]) : (carry + new string(piece))[^Math.Min(24, carry.Length + piece.Length)..];
        });
        return found;
    }
}
