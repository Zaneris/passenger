using System.Text.Json;
using Coverlet.Core;
using Coverlet.Core.Abstractions;
using Coverlet.Core.Helpers;
using Coverlet.Core.Instrumentation;
using Coverlet.Core.Symbols;

namespace Passenger.CoverageMap;

/// <summary>Coverlet's instrumenter over a test run's copy: each module beside the test assembly that it can measure (its
/// symbols and sources there, the test assembly itself and what generated code and code its author left out of coverage hold
/// left out) has its counting written in, in place, and is never put back. The map written beside says, for each module
/// instrumented, its name, its file, the file its counts go to as the run ends (as its tracker names it), its source files,
/// and for each count, by its place in the tracker's array, the source file and the lines it stands for.</summary>
internal static class Instrumenting
{
    /// <summary>Attributes whose code no person runs a test to cover.</summary>
    private static readonly string[] LeftOut = ["GeneratedCodeAttribute", "CompilerGeneratedAttribute", "ExcludeFromCodeCoverageAttribute"];

    public static int Run(string[] args)
    {
        if (args is not [{ Length: > 0 } test, { Length: > 0 } map, .. string[] named] || !File.Exists(test))
        {
            Console.Error.WriteLine("Passenger.CoverageMap: name a built test assembly, the map to write, and any modules alone to instrument");
            return 2;
        }
        Said said = new();
        FileSystem files = new();
        SourceRootTranslator roots = new(sourceMappingFile: null, said, files);
        InstrumentationHelper helper = new(new ProcessExitHandler(), new RetryHelper(), files, said, roots);
        CoverageParameters parameters = new()
        {
            IncludeFilters = named.Length == 0 ? null : [.. named.Select(n => $"[{n}]*")],
            ExcludeAttributes = LeftOut,
            DisableManagedInstrumentationRestore = true,
        };
        CoveragePrepareResult prepared = new Coverage(Path.GetFullPath(test), parameters, said, helper, files, roots, new CecilSymbolHelper()).PrepareModules();
        using (FileStream written = File.Create(map))
        using (Utf8JsonWriter json = new(written))
        {
            json.WriteStartObject();
            json.WriteStartArray("modules");
            foreach (InstrumenterResult result in prepared.Results)
            {
                Write(json, result);
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return said.Failed ? 1 : 0;
    }

    /// <summary>A module's map: each count's file (by its place among the module's documents) and lines, a branch's the line
    /// it branches on, a line's those it spans less those a count nested inside it stands for, as Coverlet's report reads them.</summary>
    private static void Write(Utf8JsonWriter json, InstrumenterResult result)
    {
        json.WriteStartObject();
        json.WriteString("module", result.Module);
        json.WriteString("path", result.ModulePath);
        json.WriteString("hits", result.HitsFilePath);
        List<Document> documents = [.. result.Documents.Values];
        json.WriteStartArray("documents");
        foreach (Document document in documents)
        {
            json.WriteStringValue(document.Path);
        }
        json.WriteEndArray();
        Dictionary<int, List<HitCandidate>> byDocument = [];
        foreach (HitCandidate candidate in result.HitCandidates)
        {
            if (!candidate.isBranch)
            {
                if (!byDocument.TryGetValue(candidate.docIndex, out List<HitCandidate>? its))
                {
                    byDocument[candidate.docIndex] = its = [];
                }
                its.Add(candidate);
            }
        }
        json.WriteStartArray("places");
        foreach (HitCandidate candidate in result.HitCandidates)
        {
            json.WriteStartArray();
            json.WriteNumberValue(candidate.docIndex);
            if (candidate.isBranch)
            {
                json.WriteNumberValue(candidate.start);
            }
            else
            {
                HashSet<int> nested = [];
                if (candidate.end != candidate.start)
                {
                    foreach (HitCandidate inner in byDocument[candidate.docIndex])
                    {
                        if (inner != candidate && inner.start > candidate.start && inner.end < candidate.end)
                        {
                            for (int line = inner.start; line <= (inner.end == 0 ? inner.start : inner.end); line++)
                            {
                                nested.Add(line);
                            }
                        }
                    }
                }
                for (int line = candidate.start; line <= candidate.end; line++)
                {
                    if (!nested.Contains(line))
                    {
                        json.WriteNumberValue(line);
                    }
                }
            }
            json.WriteEndArray();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>What Coverlet says: its warnings and errors on standard error, an error failing the run.</summary>
    private sealed class Said : ILogger
    {
        public bool Failed { get; private set; }

        public void LogVerbose(string message)
        {
        }

        public void LogInformation(string message, bool important = false)
        {
        }

        public void LogWarning(string message) => Console.Error.WriteLine(message);

        public void LogError(string message)
        {
            Failed = true;
            Console.Error.WriteLine(message);
        }

        public void LogError(Exception exception) => LogError(exception.ToString());
    }
}
