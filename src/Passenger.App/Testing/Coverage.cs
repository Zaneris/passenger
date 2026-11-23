using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>How a line of code fared in a run with coverage: run with every branch it has taken, run with some of its
/// branches never taken, or never run.</summary>
public enum CoverageState
{
    Covered,
    Partial,
    Missed,
}

/// <summary>A line's coverage: the line (from one), how many times it ran, and how many of its branches were taken of how
/// many it has (none for a line without branches).</summary>
public readonly record struct CoveredLine(int Line, long Hits, int Branches, int BranchesTaken)
{
    public CoverageState State => Hits == 0 ? CoverageState.Missed : BranchesTaken < Branches ? CoverageState.Partial : CoverageState.Covered;

    /// <summary>Two methods' counts of one line as one: their runs and their branches added.</summary>
    public CoveredLine And(CoveredLine other) => new(Line, Hits + other.Hits, Branches + other.Branches, BranchesTaken + other.BranchesTaken);
}

/// <summary>What a stretch of code's coverage comes to: its lines, those run, its branches and those taken.</summary>
public readonly record struct CoverageCounts(int Lines, int LinesRun, int Branches, int BranchesTaken)
{
    public CoverageCounts Plus(CoveredLine line) =>
        new(Lines + 1, LinesRun + (line.Hits > 0 ? 1 : 0), Branches + line.Branches, BranchesTaken + line.BranchesTaken);

    /// <summary>The share of lines run, null with no lines.</summary>
    public double? LineRate => Lines == 0 ? null : (double)LinesRun / Lines;

    public double? BranchRate => Branches == 0 ? null : (double)BranchesTaken / Branches;

    /// <summary>A share as a person reads it, a whole percentage but for less than one short of whole: "—" for none.</summary>
    public static string Percent(double? rate) => rate is not { } value ? "—"
        : value is > 0.99 and < 1 ? "99%" : value is > 0 and < 0.01 ? "<1%" : $"{Math.Round(value * 100, MidpointRounding.ToZero).ToString(CultureInfo.InvariantCulture)}%";

    /// <summary>The lines in words: "82% of lines (41 of 50)".</summary>
    public string LinesSaid => $"{Percent(LineRate)} of lines ({Numbers.Said(LinesRun)} of {Numbers.Said(Lines)})";

    public string BranchesSaid => Branches == 0 ? "no branches" : $"{Percent(BranchRate)} of branches ({Numbers.Said(BranchesTaken)} of {Numbers.Said(Branches)})";
}

/// <summary>A method of a type as Coverlet names it: its name, its parameters' types, and its lines.</summary>
public sealed record CoverageMethod(string Name, string Signature, IReadOnlyList<CoveredLine> Lines);

/// <summary>A type of one file as Coverlet names it: its assembly, its name (nested and compiler-made types after a '/'), its
/// file (null where the disk has none of the name the report gives) and the methods and lines it has there.</summary>
public sealed record CoverageClass(string Package, string Name, string? Path, IReadOnlyList<CoverageMethod> Methods, IReadOnlyList<CoveredLine> Lines);

/// <summary>A level of the coverage tree.</summary>
public enum CoverageNodeKind
{
    Project,
    Namespace,
    Type,
    Member,
}

/// <summary>A row of the coverage tree: a project (an assembly), a namespace, a type or a member, with what its lines come
/// to, the file and line (from one) it starts at, and the rows below it in name order.</summary>
public sealed class CoverageNode(CoverageNodeKind kind, string name, string fullName)
{
    public CoverageNodeKind Kind { get; } = kind;

    public string Name { get; } = name;

    /// <summary>Its name with those above it, what a filter matches.</summary>
    public string FullName { get; } = fullName;

    public CoverageCounts Counts { get; internal set; }

    public string? Path { get; internal set; }

    public int Line { get; internal set; }

    public IReadOnlyList<CoverageNode> Children { get; internal set; } = [];
}

/// <summary>A run's coverage: what its runners' reports said, one a test assembly, merged (<see cref="Read"/>), as each file's
/// lines and as a tree of projects, namespaces, types and members, and written out as one Cobertura report. Made off the
/// window's thread; read on any.</summary>
public sealed class CoverageReport
{
    private CoverageReport(IReadOnlyList<CoverageClass> classes, IReadOnlyDictionary<string, IReadOnlyList<CoveredLine>> files, IReadOnlyList<CoverageNode> projects,
        CoverageCounts counts, DateTimeOffset taken)
    {
        Classes = classes;
        Files = files;
        Projects = projects;
        Counts = counts;
        Taken = taken;
    }

    public IReadOnlyList<CoverageClass> Classes { get; }

    /// <summary>Each file's lines in order, by its canonical path.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CoveredLine>> Files { get; }

    public IReadOnlyList<CoverageNode> Projects { get; }

    /// <summary>What every file's lines come to.</summary>
    public CoverageCounts Counts { get; }

    /// <summary>When the run ended.</summary>
    public DateTimeOffset Taken { get; }

    /// <summary>A file's lines, none for a file no report named.</summary>
    public IReadOnlyList<CoveredLine> In(string path) => Files.TryGetValue(path, out IReadOnlyList<CoveredLine>? lines) ? lines : [];

    /// <summary>Coverlet's JSON reports read from their files and merged (<see cref="Of"/>), each source file the disk's,
    /// canonical, where it has one of that name. A worker's: it reads the files.</summary>
    public static CoverageReport Read(IEnumerable<string> reports, DateTimeOffset taken, Action<string>? unread = null)
    {
        Dictionary<string, string?> found = new(StringComparer.Ordinal);
        return Of(reports.Distinct(PathNames.Comparer).Select(r => (Path.GetFileName(r), (Func<byte[]>)(() => File.ReadAllBytes(r)))), taken, named =>
        {
            if (!found.TryGetValue(named, out string? known))
            {
                found[named] = known = Path.IsPathFullyQualified(named) && File.Exists(named) ? PathNames.Canonical(Path.GetFullPath(named)) : null;
            }
            return known;
        }, unread);
    }

    /// <summary>Coverlet's JSON reports merged, one a test assembly: a line's runs added, a branch taken where any report took
    /// it (each known by its method and its place in the method's code, so two test projects over one assembly take its
    /// branches together). A source file is the one <paramref name="fileOf"/> finds for the name a report gives, none where
    /// it finds none; a report that cannot be read is said through <paramref name="unread"/> and left out.</summary>
    public static CoverageReport Of(IEnumerable<(string Name, Func<byte[]> Bytes)> reports, DateTimeOffset taken, Func<string, string?> fileOf,
        Action<string>? unread = null)
    {
        Dictionary<(string Module, string? Path, string Class, string Method), MethodRun> runs = [];
        foreach ((string name, Func<byte[]> bytes) in reports)
        {
            try
            {
                using JsonDocument read = JsonDocument.Parse(bytes());
                Take(read.RootElement, runs, fileOf);
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or JsonException)
            {
                unread?.Invoke($"The coverage report {name} could not be read: {failed.Message}");
            }
        }
        List<CoverageClass> classes = [];
        foreach (IGrouping<(string Module, string? Path, string Class), KeyValuePair<(string Module, string? Path, string Class, string Method), MethodRun>> type in
            runs.GroupBy(r => (r.Key.Module, r.Key.Path, r.Key.Class)))
        {
            List<CoverageMethod> methods = [.. type.Select(m => Method(m.Key.Method, m.Value.Lines()))];
            classes.Add(new CoverageClass(type.Key.Module, type.Key.Class, type.Key.Path, methods, Together(methods.SelectMany(m => m.Lines))));
        }
        Dictionary<string, IReadOnlyList<CoveredLine>> files = new(PathNames.Comparer);
        CoverageCounts counts = default;
        foreach (IGrouping<string, CoverageClass> file in classes.Where(c => c.Path is not null).GroupBy(c => c.Path!, PathNames.Comparer))
        {
            IReadOnlyList<CoveredLine> lines = Together(file.SelectMany(c => c.Lines));
            files[file.Key] = lines;
            foreach (CoveredLine line in lines)
            {
                counts = counts.Plus(line);
            }
        }
        return new CoverageReport(classes, files, CoverageTree.Of(classes, files), counts, taken);
    }

    /// <summary>One report taken in: module, file, class and method, each method's lines' runs and its branches' by their places.</summary>
    private static void Take(JsonElement root, Dictionary<(string, string?, string, string), MethodRun> runs, Func<string, string?> fileOf)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("it holds no modules");
        }
        foreach (JsonProperty module in root.EnumerateObject())
        {
            string name = module.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || module.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? module.Name[..^4] : module.Name;
            foreach (JsonProperty file in Members(module.Value))
            {
                string? path = fileOf(file.Name);
                foreach (JsonProperty type in Members(file.Value))
                {
                    foreach (JsonProperty method in Members(type.Value))
                    {
                        (string, string?, string, string) key = (name, path, type.Name, method.Name);
                        if (!runs.TryGetValue(key, out MethodRun? run))
                        {
                            runs[key] = run = new MethodRun();
                        }
                        run.Add(method.Value);
                    }
                }
            }
        }

        static IEnumerable<JsonProperty> Members(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element.EnumerateObject() : [];
    }

    /// <summary>A method as Coverlet names it ("System.Int32 Ns.Type::Sign(System.Int32)"): its name and its parameters.</summary>
    private static CoverageMethod Method(string named, IReadOnlyList<CoveredLine> lines)
    {
        int colons = named.IndexOf("::", StringComparison.Ordinal);
        string member = colons < 0 ? named : named[(colons + 2)..];
        int open = member.IndexOf('(', StringComparison.Ordinal);
        return open < 0 ? new CoverageMethod(member, string.Empty, lines) : new CoverageMethod(member[..open], member[open..], lines);
    }

    /// <summary>Lines of several methods as one each: a line two of them share (a field's initializer in each constructor,
    /// a lambda on its method's line) runs as both did, with both one's branches.</summary>
    private static IReadOnlyList<CoveredLine> Together(IEnumerable<CoveredLine> lines) =>
        [.. lines.GroupBy(l => l.Line).Select(g => g.Aggregate((a, b) => a.And(b))).OrderBy(l => l.Line)];

    /// <summary>What every report said of one method: each line's runs, and each branch's by its line and its place in the
    /// method's code.</summary>
    private sealed class MethodRun
    {
        private readonly Dictionary<int, long> lines = [];
        private readonly Dictionary<(int Line, int Offset, int End, int Path, int Ordinal), long> branches = [];

        public void Add(JsonElement method)
        {
            foreach (JsonProperty line in JsonMembers.Properties(method, "Lines"))
            {
                if (int.TryParse(line.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
                    && line.Value.ValueKind == JsonValueKind.Number && line.Value.TryGetInt64(out long hits))
                {
                    lines[number] = lines.GetValueOrDefault(number) + hits;
                }
            }
            foreach (JsonElement branch in JsonMembers.Items(method, "Branches"))
            {
                if (JsonMembers.Bool(branch, "IsReachable") == false || JsonMembers.Number(branch, "Line") is not int line)
                {
                    continue;
                }
                (int, int, int, int, int) place = (line, JsonMembers.Number(branch, "Offset") ?? 0, JsonMembers.Number(branch, "EndOffset") ?? 0,
                    JsonMembers.Number(branch, "Path") ?? 0, JsonMembers.Number(branch, "Ordinal") ?? 0);
                branches[place] = branches.GetValueOrDefault(place) + Math.Max(0, JsonMembers.Number(branch, "Hits") ?? 0);
            }
        }

        public IReadOnlyList<CoveredLine> Lines()
        {
            Dictionary<int, (int All, int Taken)> on = [];
            foreach (((int line, _, _, _, _), long hits) in branches)
            {
                (int all, int taken) = on.GetValueOrDefault(line);
                on[line] = (all + 1, taken + (hits > 0 ? 1 : 0));
            }
            return [.. lines.OrderBy(l => l.Key).Select(l => new CoveredLine(l.Key, l.Value, on.GetValueOrDefault(l.Key).All, on.GetValueOrDefault(l.Key).Taken))];
        }
    }

    /// <summary>The report as one Cobertura document, every file named by its full path: what a person exports to read with
    /// another tool.</summary>
    public string ToCobertura()
    {
        XElement packages = new("packages");
        foreach (IGrouping<string, CoverageClass> package in Classes.GroupBy(c => c.Package, StringComparer.Ordinal).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            CoverageCounts its = Tally(package.SelectMany(c => c.Lines));
            XElement classes = new("classes");
            foreach (CoverageClass type in package.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                CoverageCounts typeCounts = Tally(type.Lines);
                classes.Add(new XElement("class", new XAttribute("name", type.Name), new XAttribute("filename", type.Path ?? string.Empty), Rates(typeCounts),
                    new XAttribute("complexity", 0),
                    new XElement("methods", type.Methods.Select(m => new XElement("method", new XAttribute("name", m.Name), new XAttribute("signature", m.Signature),
                        Rates(Tally(m.Lines)), new XAttribute("complexity", 0), new XElement("lines", m.Lines.Select(Line))))),
                    new XElement("lines", type.Lines.Select(Line))));
            }
            packages.Add(new XElement("package", new XAttribute("name", package.Key), Rates(its), new XAttribute("complexity", 0), classes));
        }
        XElement root = new("coverage", Rates(Counts),
            new XAttribute("lines-covered", Counts.LinesRun), new XAttribute("lines-valid", Counts.Lines),
            new XAttribute("branches-covered", Counts.BranchesTaken), new XAttribute("branches-valid", Counts.Branches),
            new XAttribute("complexity", 0), new XAttribute("version", "1.9"), new XAttribute("timestamp", Taken.ToUnixTimeSeconds()),
            new XElement("sources"), packages);
        // Saved as UTF-8, which a writer into a string would call UTF-16 in the declaration it writes.
        StringBuilder text = new("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        using (XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
        {
            root.Save(writer);
        }
        return text.Append('\n').ToString();

        static CoverageCounts Tally(IEnumerable<CoveredLine> lines) => lines.Aggregate(default(CoverageCounts), (counts, line) => counts.Plus(line));

        static XAttribute[] Rates(CoverageCounts counts) =>
        [
            new("line-rate", (counts.LineRate ?? 1).ToString("0.####", CultureInfo.InvariantCulture)),
            new("branch-rate", (counts.BranchRate ?? 1).ToString("0.####", CultureInfo.InvariantCulture)),
        ];

        static XElement Line(CoveredLine line)
        {
            XElement written = new("line", new XAttribute("number", line.Line), new XAttribute("hits", line.Hits), new XAttribute("branch", line.Branches > 0 ? "True" : "False"));
            if (line.Branches > 0)
            {
                written.Add(new XAttribute("condition-coverage", $"{line.BranchesTaken * 100 / line.Branches}% ({line.BranchesTaken}/{line.Branches})"));
            }
            return written;
        }
    }
}
