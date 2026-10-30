using System.Diagnostics;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>One project evaluated for the code help in its MSBuild files: the open files' text read in place of the disk's
/// (each put in the evaluation's own collection under its path, so the project and whatever imports it read it), a
/// multi-targeted project's first framework evaluated, and what the evaluation knows read out (properties and where each
/// was set, items, targets, imports, the errors and warnings it logged). Answers are kept while the files they read and the
/// folders their wildcards walked hold and the texts are the same, so asking again with nothing typed costs a look at those.</summary>
internal static class Inspection
{
    /// <summary>How many includes an item type or an element lists, and the longest value kept: enough to show, never the
    /// thousands of files a wildcard takes in.</summary>
    private const int Listed = 50;

    private const int LongestValue = 4000;

    private const int Kept = 16;

    private static readonly List<(string Key, ProjectInsight Insight)> Cache = [];

    public static ProjectInsight Run(InspectParams asked, CancellationToken cancel)
    {
        string key = Key(asked);
        lock (Cache)
        {
            int found = Cache.FindIndex(c => c.Key == key);
            if (found >= 0)
            {
                (string Key, ProjectInsight Insight) kept = Cache[found];
                Cache.RemoveAt(found);
                // The folders at once: each is a read of the folder, thousands of them for a large project.
                if (kept.Insight.Inputs.All(Stamps.Holds) && kept.Insight.Folders.AsParallel().All(Stamps.Holds))
                {
                    Cache.Add(kept);
                    return kept.Insight;
                }
            }
        }
        ProjectInsight insight = Evaluate(asked, cancel);
        lock (Cache)
        {
            Cache.Add((key, insight));
            if (Cache.Count > Kept)
            {
                Cache.RemoveAt(0);
            }
        }
        return insight;
    }

    /// <summary>What an answer was asked for: the project, its global properties and the texts read in place of the disk's.</summary>
    private static string Key(InspectParams asked) =>
        string.Join('\n', [asked.Project, .. asked.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"),
            .. asked.Texts.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}:{t.Value.Length}:{string.GetHashCode(t.Value)}")]);

    private static ProjectInsight Evaluate(InspectParams asked, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        Logged logged = new();
        Dictionary<string, string> global = new(asked.Properties, StringComparer.OrdinalIgnoreCase);
        using ProjectCollection collection = new(global, [logged], ToolsetDefinitionLocations.Default);
        EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Isolated);
        List<InsightDiagnostic> problems = [];
        foreach ((string path, string text) in asked.Texts)
        {
            try
            {
                using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
                ProjectRootElement root = ProjectRootElement.Create(reader, collection, preserveFormatting: true);
                root.FullPath = path;
            }
            catch (Exception error) when (error is InvalidProjectFileException or XmlException)
            {
                problems.Add(Of(error, path));
            }
        }
        cancel.ThrowIfCancellationRequested();
        Stamp file = Stamps.File(asked.Project);
        if (problems.Count > 0)
        {
            // An open file whose XML does not read: evaluated from the disk's text, every place in it would be another text's.
            return Failed(asked, file, problems, began);
        }
        // Stamped before they are read: a file come or gone once evaluation has begun changes the stamps too.
        IReadOnlyList<Stamp> walked = Evaluation.Walked(asked.Project);
        List<Project> loaded = [];
        try
        {
            Project project = Evaluation.Load(asked.Project, collection, context, global, null);
            loaded.Add(project);
            string? framework = null;
            if (project.GetPropertyValue("TargetFramework").Length == 0
                && project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [{ } first, ..])
            {
                cancel.ThrowIfCancellationRequested();
                framework = first;
                project = Evaluation.Load(asked.Project, collection, context, global, first);
                loaded.Add(project);
            }
            return Read(project, framework, file, walked, [.. problems, .. logged.Found], Stopwatch.GetElapsedTime(began).TotalSeconds);
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            InsightDiagnostic failed = Of(error, asked.Project);
            List<InsightDiagnostic> all = [.. logged.Found];
            if (failed.Code == "MSB4236")
            {
                // The resolvers' own account of an unknown SDK, logged before MSBuild's error, says the same at the same place.
                all.RemoveAll(d => d.Code is null && d.Line == failed.Line && d.File == failed.File);
            }
            if (!all.Any(d => d.Message == failed.Message && d.Line == failed.Line))
            {
                all.Add(failed);
            }
            return Failed(asked, file, all, began);
        }
        finally
        {
            foreach (Project project in loaded)
            {
                collection.UnloadProject(project);
            }
        }
    }

    private static ProjectInsight Failed(InspectParams asked, Stamp file, List<InsightDiagnostic> diagnostics, long began) => new()
    {
        Project = asked.Project,
        Diagnostics = diagnostics,
        // The files the failure names among them, so a fix to one is evaluated.
        Inputs = [file, .. asked.Texts.Keys.Concat(diagnostics.Select(d => d.File)).Where(p => p.Length > 0 && !PathNames.Same(p, asked.Project)).Select(Stamps.File)
            .Concat(Evaluation.LookedFor(asked.Project, null)).DistinctBy(s => s.Path, StringComparer.Ordinal)],
        Seconds = Stopwatch.GetElapsedTime(began).TotalSeconds,
    };

    /// <summary>An exception's place and words: MSBuild's own for a project that does not load, the reader's for XML that
    /// does not read.</summary>
    private static InsightDiagnostic Of(Exception error, string path) => error switch
    {
        InvalidProjectFileException invalid => new InsightDiagnostic(true, invalid.ErrorCode, invalid.BaseMessage,
            string.IsNullOrEmpty(invalid.ProjectFile) ? path : invalid.ProjectFile, invalid.LineNumber, invalid.ColumnNumber),
        XmlException xml => new InsightDiagnostic(true, "MSB4025", $"The project file could not be loaded. {xml.Message}", path, xml.LineNumber, xml.LinePosition),
        _ => new InsightDiagnostic(true, null, error.Message, path, 0, 0),
    };

    private static ProjectInsight Read(Project project, string? framework, Stamp file, IReadOnlyList<Stamp> walked, List<InsightDiagnostic> diagnostics, double seconds)
    {
        List<string> roots = [.. Evaluation.OutsideRoots(project)];
        bool Installed(string path) => roots.Any(root => PathNames.IsUnder(path, root));
        FileTable files = new();
        List<InsightProperty> properties = [];
        foreach (ProjectProperty property in project.Properties)
        {
            List<InsightPlace> set = [];
            for (ProjectProperty? at = property; at is not null && set.Count < 12; at = at.Predecessor)
            {
                if (at.Xml is { } xml)
                {
                    set.Add(files.Place(xml.Location));
                }
            }
            PropertyOrigin origin = property.IsGlobalProperty ? PropertyOrigin.Global
                : property.IsEnvironmentProperty ? PropertyOrigin.Environment
                : property.IsReservedProperty ? PropertyOrigin.Reserved
                : PropertyOrigin.Set;
            properties.Add(new InsightProperty(property.Name, Clip(property.EvaluatedValue), origin, set));
        }
        List<InsightItemType> types = [];
        List<InsightFramework> frameworks = [];
        Dictionary<ProjectItemElement, List<ProjectItem>> made = [];
        foreach (string type in project.ItemTypes.Order(StringComparer.OrdinalIgnoreCase))
        {
            ICollection<ProjectItem> items = project.GetItems(type);
            List<InsightPlace> defined = [];
            HashSet<ProjectItemElement> elements = [];
            foreach (ProjectItem item in items)
            {
                if (item.Xml is { } element && elements.Add(element) && !Installed(element.Location.File))
                {
                    defined.Add(files.Place(element.Location));
                    made[element] = [];
                }
                if (item.Xml is { } own && made.TryGetValue(own, out List<ProjectItem>? its))
                {
                    its.Add(item);
                }
            }
            types.Add(new InsightItemType(type, items.Count, [.. items.Take(Listed).Select(i => Clip(i.EvaluatedInclude))],
                [.. items.SelectMany(i => i.DirectMetadata.Select(m => m.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(Listed)], defined));
            if (type == "SupportedTargetFramework")
            {
                frameworks.AddRange(items
                    .Select(i => (Identifier: i.EvaluatedInclude.Split(',')[0], Version: Version.TryParse(i.EvaluatedInclude.Split("Version=v").Last(), out Version? v) ? v : new Version(0, 0),
                        Framework: new InsightFramework(i.GetMetadataValue("Alias"), i.GetMetadataValue("DisplayName"))))
                    .Where(f => f.Framework.Alias.Length > 0)
                    .OrderBy(f => f.Identifier switch { ".NETCoreApp" => 0, ".NETStandard" => 1, _ => 2 })
                    .ThenByDescending(f => f.Version)
                    .Select(f => f.Framework)
                    .DistinctBy(f => f.Alias, StringComparer.OrdinalIgnoreCase));
            }
        }
        List<InsightItem> elementItems = [.. made.Where(m => m.Value.Count > 0).Select(m => new InsightItem(m.Key.ItemType, files.Place(m.Key.Location), m.Value.Count,
            [.. m.Value.Take(Listed).Select(i => Clip(i.EvaluatedInclude))],
            m.Value[0].DirectMetadata.Take(Listed).ToDictionary(d => d.Name, d => Clip(d.EvaluatedValue), StringComparer.OrdinalIgnoreCase)))];
        // Every definition of every target, in the order the evaluation read them: the last is the one in force.
        Dictionary<string, List<InsightPlace>> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectRootElement root in new[] { project.Xml }.Concat(project.Imports.Select(i => i.ImportedProject)).Distinct())
        {
            foreach (ProjectTargetElement target in root.Targets)
            {
                (targets.TryGetValue(target.Name, out List<InsightPlace>? places) ? places : targets[target.Name] = []).Add(files.Place(target.Location));
            }
        }
        List<InsightTarget> targetList = [];
        foreach ((string name, List<InsightPlace> places) in targets)
        {
            if (project.Targets.TryGetValue(name, out Microsoft.Build.Execution.ProjectTargetInstance? inForce))
            {
                InsightPlace force = files.Place(inForce.Location);
                places.Remove(force);
                places.Insert(0, force);
            }
            targetList.Add(new InsightTarget(name, places));
        }
        List<InsightImport> imports = [];
        foreach (ResolvedImport import in project.Imports)
        {
            ProjectImportElement importing = import.ImportingElement;
            if (importing.Location.File is { Length: > 0 } from && !Installed(from))
            {
                string? sdk = importing.ImplicitImportLocation != ImplicitImportLocation.None ? importing.Sdk : null;
                imports.Add(new InsightImport(files.Place(importing.Location), import.ImportedProject.FullPath, sdk));
            }
        }
        return new ProjectInsight
        {
            Project = project.FullPath,
            Evaluated = true,
            Framework = framework,
            Diagnostics = [.. diagnostics.Where(d => d.File.Length == 0 || !Installed(d.File))],
            Properties = properties,
            ItemTypes = types,
            Items = elementItems,
            Targets = targetList,
            Imports = imports,
            Frameworks = frameworks,
            Installed = roots,
            Inputs = Evaluation.Inputs(file, [project], roots, Evaluation.LookedFor(project.FullPath, project)),
            Folders = Evaluation.Folders(project.FullPath, walked, Evaluation.Items([project]).Select(i => i.Path)),
            Files = files.Paths,
            Seconds = seconds,
        };
    }

    private static string Clip(string value) => value.Length > LongestValue ? value[..LongestValue] + "…" : value;

    /// <summary>The files places name, each once.</summary>
    private sealed class FileTable
    {
        private readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);

        public List<string> Paths { get; } = [];

        public InsightPlace Place(ElementLocation location)
        {
            string path = location.File ?? string.Empty;
            if (!indexes.TryGetValue(path, out int index))
            {
                index = Paths.Count;
                indexes[path] = index;
                Paths.Add(path);
            }
            return new InsightPlace(index, location.Line, location.Column);
        }
    }

    /// <summary>The errors and warnings the evaluation logs, and the imports it skipped for a file not there (an import
    /// MSBuild's build would fail on, which the load settings let the evaluation go past), as MSBuild's MSB4019.</summary>
    private sealed class Logged : ILogger
    {
        private readonly List<InsightDiagnostic> found = [];

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

        public string? Parameters { get; set; }

        public IReadOnlyList<InsightDiagnostic> Found
        {
            get
            {
                lock (found)
                {
                    return [.. found];
                }
            }
        }

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (_, e) => Add(new InsightDiagnostic(true, e.Code, e.Message ?? string.Empty, e.File ?? string.Empty, e.LineNumber, e.ColumnNumber));
            eventSource.WarningRaised += (_, e) => Add(new InsightDiagnostic(false, e.Code, e.Message ?? string.Empty, e.File ?? string.Empty, e.LineNumber, e.ColumnNumber));
            eventSource.MessageRaised += (_, e) =>
            {
                if (e is ProjectImportedEventArgs { ImportIgnored: true, ImportedProjectFile: { Length: > 0 } missing } skipped && missing.IndexOfAny(['*', '?']) < 0 && !File.Exists(missing))
                {
                    Add(new InsightDiagnostic(true, "MSB4019",
                        $"The imported project \"{missing}\" was not found. Confirm that the expression in the Import declaration \"{skipped.UnexpandedProject}\" is correct, and that the file exists on disk.",
                        skipped.ProjectFile ?? skipped.File ?? string.Empty, skipped.LineNumber, skipped.ColumnNumber));
                }
            };
        }

        public void Shutdown()
        {
        }

        private void Add(InsightDiagnostic diagnostic)
        {
            lock (found)
            {
                if (!found.Contains(diagnostic))
                {
                    found.Add(diagnostic);
                }
            }
        }
    }
}
