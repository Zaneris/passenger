using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>The modules Passenger's instrumenting wrote counting into (src/Passenger.CoverageMap), as its map says: for each,
/// by the file its tracker names, its source files and, for each count by its place in the tracker's array, the file and
/// the lines it stands for; with what a test ran read from the places it counted (<see cref="Ran"/>). Read on any thread.</summary>
public sealed class InstrumentedCode
{
    private readonly Dictionary<string, Module> modules;

    private InstrumentedCode(Dictionary<string, Module> modules) => this.modules = modules;

    /// <summary>None instrumented.</summary>
    public static InstrumentedCode None { get; } = new(new Dictionary<string, Module>(StringComparer.Ordinal));

    /// <summary>The modules instrumented, by name.</summary>
    public IEnumerable<string> Names => modules.Values.Select(m => m.Name);

    /// <summary>Each source file's lines a count stands for, by canonical path: the lines a test could run.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Coverable()
    {
        Dictionary<string, HashSet<int>> lines = new(PathNames.Comparer);
        foreach (Module module in modules.Values)
        {
            foreach ((int document, int[] places) in module.Places)
            {
                if (!lines.TryGetValue(module.Documents[document], out HashSet<int>? its))
                {
                    lines[module.Documents[document]] = its = [];
                }
                its.UnionWith(places);
            }
        }
        return lines.ToDictionary(l => l.Key, l => (IReadOnlyList<int>)[.. l.Value.Order()], PathNames.Comparer);
    }

    /// <summary>These modules' maps over this one's, each module by name in place of what was known of it.</summary>
    public InstrumentedCode With(InstrumentedCode newer)
    {
        Dictionary<string, Module> all = new(StringComparer.Ordinal);
        HashSet<string> renewed = [.. newer.modules.Values.Select(m => m.Name)];
        foreach ((string hits, Module module) in modules)
        {
            if (!renewed.Contains(module.Name))
            {
                all[hits] = module;
            }
        }
        foreach ((string hits, Module module) in newer.modules)
        {
            all[hits] = module;
        }
        return new InstrumentedCode(all);
    }

    /// <summary>The lines a test ran, by canonical source file, from the places each tracker counted (by the file it names),
    /// those of a module no map names passed over.</summary>
    public Dictionary<string, HashSet<int>> Ran(IReadOnlyDictionary<string, int[]> counted)
    {
        Dictionary<string, HashSet<int>> ran = new(PathNames.Comparer);
        foreach ((string hits, int[] places) in counted)
        {
            if (!modules.TryGetValue(hits, out Module? module))
            {
                continue;
            }
            foreach (int place in places)
            {
                if (place >= 0 && place < module.Places.Length)
                {
                    (int document, int[] lines) = module.Places[place];
                    if (!ran.TryGetValue(module.Documents[document], out HashSet<int>? its))
                    {
                        ran[module.Documents[document]] = its = [];
                    }
                    its.UnionWith(lines);
                }
            }
        }
        return ran;
    }

    /// <summary>The instrumenting's map read from its JSON, each source file the disk's canonical path where it has one of
    /// that name; null for a map that does not read as one.</summary>
    public static InstrumentedCode? Of(ReadOnlySpan<byte> json, Func<string, string> canonical)
    {
        try
        {
            Utf8JsonReader reader = new(json);
            using JsonDocument read = JsonDocument.ParseValue(ref reader);
            Dictionary<string, Module> modules = new(StringComparer.Ordinal);
            foreach (JsonElement module in JsonMembers.Items(read.RootElement, "modules"))
            {
                if (JsonMembers.Text(module, "module") is not { } name || JsonMembers.Text(module, "hits") is not { } hits)
                {
                    continue;
                }
                string[] documents = [.. JsonMembers.Items(module, "documents").Select(d => d.ValueKind == JsonValueKind.String ? canonical(d.GetString()!) : string.Empty)];
                List<(int, int[])> places = [];
                foreach (JsonElement place in JsonMembers.Items(module, "places"))
                {
                    int[] numbers = place.ValueKind == JsonValueKind.Array ? [.. place.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetInt32())] : [];
                    places.Add(numbers is [var document, .. var lines] && document >= 0 && document < documents.Length ? (document, lines) : (0, []));
                }
                modules[hits] = new Module(name, documents, [.. places]);
            }
            return new InstrumentedCode(modules);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A module instrumented: its name, its source files, and for each count its file (by its place among them) and lines.</summary>
    private sealed record Module(string Name, string[] Documents, (int Document, int[] Lines)[] Places);
}
