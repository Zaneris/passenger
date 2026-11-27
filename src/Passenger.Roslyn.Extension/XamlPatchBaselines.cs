using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Passenger.Core;

namespace Passenger.Roslyn.Extension;

/// <summary>What MAUI's XAML generator writes one program's Hot Reload patches from: of each XAML file, the text its pages
/// show, its parsed tree and the ids the program registered its elements under. MAUI keeps one such record process-wide
/// (XamlHotReloadState), moved by every run of its generator, the editor's live checks' too, so a program's updates keep
/// their own, put in place for their runs and taken out after; begun from the ids the program's build registered, as its
/// PDB's generated code says them, and moved on only as an update is committed.</summary>
internal sealed partial class XamlPatchBaselines
{
    /// <summary>The generator's record of each file the program's updates have run on, by MAUI's own key.</summary>
    private Dictionary<object, Entry> ours = [];

    /// <summary>The records as the update emitted and not yet committed or discarded left them.</summary>
    private Dictionary<object, Entry>? pending;

    /// <summary>The update the records were last run for is the program's: they are what its pages show from now.</summary>
    public void Commit()
    {
        if (pending is { } taken)
        {
            ours = taken;
            pending = null;
        }
    }

    /// <summary>The update was not taken: the records stay as the program holds them.</summary>
    public void Discard() => pending = null;

    /// <summary>Runs the generator for the program's update: its records in place of the process's for <paramref name="primed"/>
    /// (a run over the texts the app holds, after which a file met for the first time is given the ids its build registered)
    /// and <paramref name="generated"/> (the run the update is made of), the process's put back after.</summary>
    public async Task<T> RunAsync<T>(IReadOnlyList<Project> projects, Func<Task> primed, Func<Task<T>> generated)
    {
        List<Kept> kept = [.. projects.Select(Kept.Of).OfType<Kept>().DistinctBy(k => k.Cache)];
        List<Dictionary<object, object?>> outside = [.. kept.Select(k => k.TakeAll())];
        try
        {
            foreach (Kept generator in kept)
            {
                generator.Put(ours);
            }
            await primed().ConfigureAwait(false);
            foreach (Kept generator in kept)
            {
                foreach ((object key, Entry seeded) in generator.Snapshot().Where(e => !ours.ContainsKey(e.Key)))
                {
                    ours[key] = Built(seeded, key, projects);
                }
                generator.Put(ours);
            }
            T made = await generated().ConfigureAwait(false);
            pending = new Dictionary<object, Entry>(ours);
            foreach (Kept generator in kept)
            {
                foreach ((object key, Entry entry) in generator.Snapshot())
                {
                    pending[key] = entry;
                }
            }
            return made;
        }
        finally
        {
            for (int i = 0; i < kept.Count; i++)
            {
                kept[i].PutBack(outside[i]);
            }
        }
    }

    /// <summary>A file's first record, seeded from the text its build made, with the ids that build registered its elements
    /// under, read by line and column from its generated code in the build's PDB; as seeded where that cannot be read.</summary>
    private static Entry Built(Entry seeded, object key, IReadOnlyList<Project> projects)
    {
        if (key is not ITuple { Length: 3 } named || named[0] is not string assembly || named[1] is not string framework || named[2] is not string file
            || seeded.Ids is not { } ids
            || projects.FirstOrDefault(p => p.AssemblyName == assembly && FrameworkOf(p) == framework) is not { } project
            || BuiltIds(project, file) is not { Ids.Count: > 0 } built)
        {
            return seeded;
        }
        Dictionary<object, string> placed = [];
        foreach (DictionaryEntry node in ids)
        {
            if (node.Value is string id && id.Length > 0 && node.Key is IXmlLineInfo at && at.HasLineInfo()
                && built.Ids.TryGetValue((at.LineNumber, at.LinePosition), out string? registered))
            {
                placed[node.Key] = registered;
            }
        }
        int next = Math.Max(seeded.NextId, built.Next);
        IDictionary remapped = (IDictionary)Activator.CreateInstance(ids.GetType())!;
        foreach (DictionaryEntry node in ids)
        {
            // An element the build registered under no id it says is given one no element of its has.
            remapped[node.Key] = node.Value is string { Length: 0 } root ? root : placed.GetValueOrDefault(node.Key) ?? (next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return seeded with { Ids = remapped, NextId = next };
    }

    /// <summary>A project's target framework, as MSBuild gives the generator it.</summary>
    private static string? FrameworkOf(Project project) =>
        project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.targetFramework", out string? framework) ? framework : null;

    /// <summary>The ids a project's build registered the elements of a XAML file under, by their line and column in the file
    /// as built, and the first id past every one it registered (a template's too): its generated InitializeComponent,
    /// embedded in the build's PDB, places each element it makes (RegisterSourceInfo) and registers it
    /// (XamlComponentRegistry.Register). Null where the PDB or that code is not there.</summary>
    private static (Dictionary<(int Line, int Column), string> Ids, int Next)? BuiltIds(Project project, string file)
    {
        if (project.CompilationOutputInfo.AssemblyPath is not { } assembly
            || project.AnalyzerOptions.AdditionalFiles.FirstOrDefault(a => PathNames.Same(a.Path, file)) is not { } text
            || !project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(text).TryGetValue("build_metadata.additionalfiles.RelativePath", out string? relative))
        {
            return null;
        }
        string hint = GeneratedMarkup.HintOf(relative, project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(text), "xsg");
        if (PortablePdbs.EmbeddedSources(Path.ChangeExtension(assembly, ".pdb"), name => Path.GetFileName(name) == hint) is not [(_, string code), ..])
        {
            return null;
        }
        Dictionary<string, (int, int)> places = [];
        foreach (Match placed in Placed().Matches(code))
        {
            places[placed.Groups["name"].Value] = (int.Parse(placed.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(placed.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        Dictionary<(int, int), string> ids = [];
        foreach (Match registered in Registered().Matches(code))
        {
            if (places.TryGetValue(registered.Groups["name"].Value, out (int, int) at))
            {
                ids[at] = registered.Groups["id"].Value;
            }
        }
        int next = Numbered().Matches(code).Select(m => int.TryParse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture, out int id) ? id + 1 : 0)
            .DefaultIfEmpty(0).Max();
        return (ids, next);
    }

    [GeneratedRegex("""RegisterSourceInfo\((?<name>[A-Za-z_][A-Za-z0-9_]*)!, new global::System\.Uri\(@"[^"]*", global::System\.UriKind\.Relative\), (?<line>\d+), (?<column>\d+)\)""")]
    private static partial Regex Placed();

    [GeneratedRegex("""XamlComponentRegistry\.Register\(this, "(?<id>[^"]*)", (?<name>[A-Za-z_][A-Za-z0-9_]*)\)""")]
    private static partial Regex Registered();

    [GeneratedRegex("""XamlComponentRegistry\.Register[A-Za-z]*\(this, "(?<id>\d+)""")]
    private static partial Regex Numbered();

    /// <summary>A file's record as MAUI's generator keeps it: its text, parsed tree and element ids, the next id it gives, its
    /// version and the fields its first text declared, each dictionary a copy of its own.</summary>
    private sealed record Entry(string? Text, object? Root, IDictionary? Ids, int NextId, int Version, IDictionary? Fields);

    /// <summary>MAUI's generator's record in the server, of the generator a project runs: its cache of each file by (assembly,
    /// target framework, path), the lock it takes, and its entries' type.</summary>
    private sealed class Kept(IDictionary cache, object gate, Type entry)
    {
        public IDictionary Cache => cache;

        public static Kept? Of(Project project)
        {
            foreach (Microsoft.CodeAnalysis.Diagnostics.AnalyzerReference reference in project.AnalyzerReferences)
            {
                foreach (ISourceGenerator generator in reference.GetGenerators(LanguageNames.CSharp))
                {
                    if (generator.GetGeneratorType() is { FullName: "Microsoft.Maui.Controls.SourceGen.XamlGenerator" } type
                        && type.Assembly.GetType("Microsoft.Maui.Controls.SourceGen.XamlHotReloadState") is { } state
                        && state.GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is IDictionary cache
                        && state.GetField("_lock", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is { } gate
                        && state.GetNestedType("CacheEntry", BindingFlags.NonPublic | BindingFlags.Public) is { } entry)
                    {
                        return new Kept(cache, gate, entry);
                    }
                }
            }
            return null;
        }

        /// <summary>Every record taken out, each as the process had it.</summary>
        public Dictionary<object, object?> TakeAll()
        {
            lock (gate)
            {
                Dictionary<object, object?> taken = [];
                foreach (DictionaryEntry record in cache)
                {
                    taken[record.Key] = record.Value;
                }
                cache.Clear();
                return taken;
            }
        }

        /// <summary>The records put back as the process had them, the program's taken out.</summary>
        public void PutBack(Dictionary<object, object?> taken)
        {
            lock (gate)
            {
                cache.Clear();
                foreach ((object key, object? value) in taken)
                {
                    cache[key] = value;
                }
            }
        }

        /// <summary>The program's records put in place, each a new entry the generator may change.</summary>
        public void Put(Dictionary<object, Entry> records)
        {
            lock (gate)
            {
                cache.Clear();
                foreach ((object key, Entry record) in records)
                {
                    object made = Activator.CreateInstance(entry, nonPublic: true)!;
                    Set(made, "XamlText", record.Text);
                    Set(made, "ParsedRoot", record.Root);
                    Set(made, "NodeIds", Copy(record.Ids));
                    Set(made, "NextNodeId", record.NextId);
                    Set(made, "Version", record.Version);
                    Set(made, "GeneratedFields", Copy(record.Fields));
                    cache[key] = made;
                }
            }
        }

        /// <summary>Each record as it stands, copied.</summary>
        public Dictionary<object, Entry> Snapshot()
        {
            lock (gate)
            {
                Dictionary<object, Entry> records = [];
                foreach (DictionaryEntry record in cache)
                {
                    if (record.Value is { } kept)
                    {
                        records[record.Key] = new Entry(Get(kept, "XamlText") as string, Get(kept, "ParsedRoot"), Copy(Get(kept, "NodeIds") as IDictionary),
                            Get(kept, "NextNodeId") is int next ? next : 0, Get(kept, "Version") is int version ? version : 0, Copy(Get(kept, "GeneratedFields") as IDictionary));
                    }
                }
                return records;
            }
        }

        private object? Get(object record, string name) => entry.GetProperty(name)?.GetValue(record);

        private void Set(object record, string name, object? value) => entry.GetProperty(name)?.SetValue(record, value);

        private static IDictionary? Copy(IDictionary? dictionary)
        {
            if (dictionary is null)
            {
                return null;
            }
            IDictionary copy = (IDictionary)Activator.CreateInstance(dictionary.GetType())!;
            foreach (DictionaryEntry item in dictionary)
            {
                copy[item.Key] = item.Value;
            }
            return copy;
        }
    }
}
