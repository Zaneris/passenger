using System.Diagnostics;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>A run of tests with their coverage measured by Coverlet, which Passenger carries under coverage/ and gives the
/// run, never the person's projects: VSTest's runs take its data collector through their settings
/// (<see cref="Into"/>), a Testing Platform application runs under its console (<see cref="Wrapped"/>), each writing Coverlet's
/// JSON, whose branches a merge tells apart. It holds the folder the run's reports are written under, the reports the runners
/// wrote, one a test assembly, and the tests whose coverage was not measured, with why. Its runners tell it from their own
/// threads.</summary>
public sealed class CoverageRun(string folder)
{
    /// <summary>The name Coverlet's data collector goes by in VSTest's settings.</summary>
    public const string CollectorName = "XPlat Code Coverage";

    /// <summary>Coverlet's collector inside a test host, which VSTest loads by this name from its file and which flushes what
    /// it counted before the host ends.</summary>
    private const string InProcessCollector = "Coverlet.Collector.DataCollection.CoverletInProcDataCollector";

    /// <summary>Attributes whose code no person runs a test to cover: what a compiler or a tool generated, and what its
    /// author left out of coverage.</summary>
    private static readonly string[] LeftOut = ["GeneratedCodeAttribute", "CompilerGeneratedAttribute", "ExcludeFromCodeCoverageAttribute"];

    private readonly List<string> reports = [];
    private readonly List<string> unmeasured = [];

    /// <summary>The folder Coverlet's collector is carried in, which VSTest takes as an adapter's.</summary>
    public static string Collector => CarriedFiles.Folder("coverage", "collector");

    /// <summary>Coverlet's console, which a Testing Platform application runs under.</summary>
    public static string Console => Path.Combine(CarriedFiles.Folder("coverage", "console"), "coverlet.console.dll");

    /// <summary>Where the run's reports are written, the run's own.</summary>
    public string Folder { get; } = folder;

    /// <summary>A run's own folder for its reports, made now under Passenger's own temporary folder. A worker's.</summary>
    public static CoverageRun Begin() =>
        new(Directory.CreateDirectory(Path.Combine(UserTemporary.Own(UserTemporary.Of(State.StateStore.DefaultRoot, "coverage")), Guid.NewGuid().ToString("N"))).FullName);

    /// <summary>The reports the runners wrote, as they told them.</summary>
    public IReadOnlyList<string> Reports
    {
        get
        {
            lock (reports)
            {
                return [.. reports];
            }
        }
    }

    /// <summary>What the run's coverage leaves out, each with why: tests run where Coverlet does not measure.</summary>
    public IReadOnlyList<string> Unmeasured
    {
        get
        {
            lock (reports)
            {
                return [.. unmeasured];
            }
        }
    }

    /// <summary>A runner wrote a report of what its tests ran.</summary>
    public void Wrote(string report)
    {
        lock (reports)
        {
            reports.Add(report);
        }
    }

    /// <summary>Tests whose coverage was not measured, and why, said once however many times it is told.</summary>
    public void Missed(string why)
    {
        lock (reports)
        {
            if (!unmeasured.Contains(why))
            {
                unmeasured.Add(why);
            }
        }
    }

    /// <summary>The run's reports read and merged, and its folder let go of. A worker's.</summary>
    public CoverageReport Read(Action<string>? unread)
    {
        CoverageReport report = CoverageReport.Read(Reports.Where(File.Exists), DateTimeOffset.Now, unread);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            // Passenger's own temporary folder: what a test host still holds stays until the system clears it.
        }
        return report;
    }

    /// <summary>VSTest's settings given Coverlet's collector, carried: found among the adapters, measuring in the test host and
    /// writing its JSON to the run's folder, and left out of what generated code and code its author left out of coverage
    /// holds. A collector the project's own settings already configure keeps its configuration, JSON its format.</summary>
    public void Into(XElement root)
    {
        XElement configuration = Child(root, "RunConfiguration");
        string adapters = (string?)configuration.Element("TestAdaptersPaths") is { Length: > 0 } given ? $"{given};{Collector}" : Collector;
        configuration.SetElementValue("TestAdaptersPaths", adapters);
        configuration.SetElementValue("ResultsDirectory", Folder);
        XElement collectors = Child(Child(root, "DataCollectionRunSettings"), "DataCollectors");
        XElement? collector = collectors.Elements("DataCollector").FirstOrDefault(c => string.Equals((string?)c.Attribute("friendlyName"), CollectorName, StringComparison.OrdinalIgnoreCase));
        if (collector is null)
        {
            collectors.Add(collector = new XElement("DataCollector", new XAttribute("friendlyName", CollectorName)));
            Child(collector, "Configuration").SetElementValue("ExcludeByAttribute", string.Join(',', LeftOut));
        }
        collector.SetAttributeValue("enabled", "True");
        Child(collector, "Configuration").SetElementValue("Format", "json");
        XElement inProcess = Child(Child(root, "InProcDataCollectionRunSettings"), "InProcDataCollectors");
        if (!inProcess.Elements("InProcDataCollector").Any(c => string.Equals((string?)c.Attribute("friendlyName"), CollectorName, StringComparison.OrdinalIgnoreCase)))
        {
            // VSTest takes Coverlet's collector by its file's name and its type's, whatever its version.
            inProcess.Add(new XElement("InProcDataCollector", new XAttribute("friendlyName", CollectorName), new XAttribute("enabled", "True"),
                new XAttribute("assemblyQualifiedName", $"{InProcessCollector}, coverlet.collector, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"),
                new XAttribute("codebase", Path.Combine(Collector, "coverlet.collector.dll"))));
        }

        static XElement Child(XElement parent, string name)
        {
            if (parent.Element(name) is not { } child)
            {
                parent.Add(child = new XElement(name));
            }
            return child;
        }
    }

    /// <summary>A Testing Platform application's start under Coverlet's console, on Passenger's own runtime: it measures the
    /// assemblies beside the application that it can (those with symbols, of the person's code), starts the application as
    /// <paramref name="host"/> would with these arguments, and once it ends writes its report, given back as
    /// <paramref name="report"/>, and puts the assemblies back as they were.</summary>
    public ProcessStartInfo Wrapped(string assembly, string framework, string host, IReadOnlyList<string> arguments, string folder,
        IReadOnlyDictionary<string, string>? environment, out string report)
    {
        report = Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(assembly)}.{framework}.{Guid.NewGuid():N}.json");
        List<string> words = [assembly, "--target", host, "--targetargs", WindowsCommandLine.Of(arguments), "--format", "json", "--output", report];
        foreach (string attribute in LeftOut)
        {
            words.AddRange(["--exclude-by-attribute", attribute]);
        }
        return ChildProcesses.WithVariables(ChildProcesses.HelperCommand(Console, words, folder), environment);
    }
}
