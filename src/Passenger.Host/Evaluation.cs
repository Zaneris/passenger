using System.Collections.Concurrent;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>MSBuild's own evaluation of projects (conditions, imports, wildcards, Directory.Build.props and central
/// package management all as a build sees them), many at once on one shared context. Every project's outer evaluation
/// comes first, in the order asked, so each shows as soon as it can; a multi-targeted project's frameworks, which cost it
/// as much again each and change little of what it shows, follow once every outer one is in.</summary>
internal static class Evaluation
{
    /// <summary>A missing or broken import is no failure, an unknown SDK is.</summary>
    internal const ProjectLoadSettings Settings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreEmptyImports
        | ProjectLoadSettings.IgnoreInvalidImports | ProjectLoadSettings.RejectCircularImports
        | ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition | ProjectLoadSettings.FailOnUnresolvedSdk;

    /// <summary>The item types that are a project's files, beside the ones a project's own targets add (AvailableItemName,
    /// the item types offered as a file's build action).</summary>
    private static readonly string[] FileItemTypes =
        ["Compile", "None", "Content", "EmbeddedResource", "AdditionalFiles", "Page", "Resource", "ApplicationDefinition", "Folder"];

    /// <summary>The most folders stamped for one project, so a wildcard over a folder of thousands is not walked at length.</summary>
    private const int FolderLimit = 20_000;

    /// <summary>Evaluates the projects asked, handing each over as it is done; cancelled, it starts no other project.</summary>
    public static void Run(EvaluateParams asked, Action<EvaluatedProject> evaluated, CancellationToken cancel)
    {
        using ProjectCollection collection = new(asked.Properties.ToDictionary(p => p.Key, p => p.Value), loggers: null, ToolsetDefinitionLocations.Default);
        EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancel };
        ConcurrentDictionary<string, (EvaluatedProject Outer, IReadOnlyList<string> Frameworks)> targeted = new(PathNames.Comparer);
        // Load-balanced, so each worker takes the next project in the order asked: the active file's first.
        Parallel.ForEach(Partitioner.Create(asked.Projects.ToArray(), loadBalance: true), parallel, path =>
        {
            (EvaluatedProject outer, IReadOnlyList<string> frameworks) = Outer(path, collection, context, asked.Properties);
            evaluated(outer);
            if (!outer.Complete)
            {
                targeted[path] = (outer, frameworks);
            }
        });
        List<(EvaluatedProject Outer, IReadOnlyList<string> Frameworks)> inOrder =
            [.. asked.Projects.Distinct(PathNames.Comparer).Where(targeted.ContainsKey).Select(p => targeted[p])];
        Parallel.ForEach(Partitioner.Create(inOrder.ToArray(), loadBalance: true), parallel,
            t => evaluated(Frameworks(t.Outer, t.Frameworks, collection, context, asked.Properties)));
    }

    /// <summary>A project's outer evaluation: all of it for a project with one framework (or none), and for a
    /// multi-targeted one what it shows until its frameworks are in.</summary>
    private static (EvaluatedProject Project, IReadOnlyList<string> Frameworks) Outer(string path, ProjectCollection collection, EvaluationContext context, IReadOnlyDictionary<string, string> properties)
    {
        // Stamped before they are read: whatever changes once evaluation has begun changes the stamps too.
        Stamp file = Stamps.File(path);
        IReadOnlyList<Stamp> folders = Walked(Path.GetDirectoryName(path)!);
        if (file.Ticks == 0)
        {
            return (new EvaluatedProject { Path = path, Problem = $"{path} does not exist", Inputs = [file] }, []);
        }
        Project? outer = null;
        try
        {
            outer = Load(path, collection, context, properties, null);
            string single = outer.GetPropertyValue("TargetFramework");
            List<string> frameworks = single.Length > 0
                ? [single]
                : [.. outer.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
            bool multi = single.Length == 0 && frameworks.Count > 0;
            return (Read(path, [outer], frameworks, [null], file, folders) with { Complete = !multi }, frameworks);
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return (new EvaluatedProject { Path = path, Problem = error.Message, Inputs = Failed(path, file, error), Folders = folders }, []);
        }
        finally
        {
            if (outer is not null)
            {
                collection.UnloadProject(outer);
            }
        }
    }

    /// <summary>A multi-targeted project complete: each framework's evaluation, with what its outer one depended on.</summary>
    private static EvaluatedProject Frameworks(EvaluatedProject outer, IReadOnlyList<string> frameworks, ProjectCollection collection, EvaluationContext context, IReadOnlyDictionary<string, string> properties)
    {
        List<Project> loaded = [];
        try
        {
            foreach (string framework in frameworks)
            {
                loaded.Add(Load(outer.Path, collection, context, properties, framework));
            }
            EvaluatedProject inner = Read(outer.Path, loaded, frameworks, [.. frameworks], outer.Inputs[0], []);
            return inner with
            {
                Inputs = [.. outer.Inputs.Concat(inner.Inputs).DistinctBy(s => s.Path, PathNames.Comparer)],
                Folders = [.. outer.Folders.Concat(inner.Folders).DistinctBy(s => s.Path, PathNames.Comparer)],
            };
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return outer with { Complete = true, Problem = error.Message };
        }
        finally
        {
            foreach (Project project in loaded)
            {
                collection.UnloadProject(project);
            }
        }
    }

    /// <summary>One project read from its evaluations (its outer one, or each framework's), with its inputs: the project
    /// file stamped as it was read, the imports outside the SDK and package folders, and the folders its files come
    /// from.</summary>
    private static EvaluatedProject Read(string path, IReadOnlyList<Project> each, IReadOnlyList<string> frameworks, IReadOnlyList<string?> named, Stamp file, IReadOnlyList<Stamp> folders)
    {
        Project first = each[0];
        HashSet<string> excluded = OutsideRoots(first);
        List<EvaluatedItem> items = Items(each);
        return new EvaluatedProject
        {
            Path = path,
            AssemblyName = NonEmpty(first.GetPropertyValue("AssemblyName")),
            OutputType = NonEmpty(first.GetPropertyValue("OutputType")),
            TargetFrameworks = frameworks,
            IsTestProject = each.Any(p => p.GetPropertyValue("IsTestProject").Equals("true", StringComparison.OrdinalIgnoreCase)),
            IsAspireHost = each.Any(p => p.GetPropertyValue("IsAspireHost").Equals("true", StringComparison.OrdinalIgnoreCase)),
            Dependencies = [.. each.Select((p, i) => Dependencies(p, named[i]))],
            Items = items,
            Properties = Carried(first, excluded),
            Inputs = Inputs(file, each, excluded, LookedFor(path, first)),
            Folders = Folders(path, folders, items.Select(i => i.Path)),
        };
    }

    /// <summary>The files evaluations of a project read, stamped, each once: the project file as it was read, the imports
    /// outside the SDK and package folders (<see cref="OutsideRoots"/>), and <paramref name="also"/> (what else the caller
    /// read, and what it looks for above the project, <see cref="LookedFor"/>).</summary>
    internal static IReadOnlyList<Stamp> Inputs(Stamp file, IEnumerable<Project> evaluations, IReadOnlyCollection<string> outside, IEnumerable<Stamp> also) =>
        [file, .. evaluations
            .SelectMany(p => p.Imports.Select(i => i.ImportedProject.FullPath))
            .Distinct(PathNames.Comparer)
            .Where(i => !outside.Any(root => i.StartsWith(root, PathNames.Comparison)))
            .Select(Stamps.File)
            .Concat(also)
            .DistinctBy(s => s.Path, PathNames.Comparer)];

    /// <summary>The folders a project's files come from, stamped: those under its own (<see cref="Walked"/>, stamped before
    /// it was evaluated), and the folder of each file it takes from elsewhere (../Shared/*.cs), not all under it: a file at
    /// the repository's top (a README packed with every project) would have the whole repository walked.</summary>
    internal static IReadOnlyList<Stamp> Folders(string path, IReadOnlyList<Stamp> walked, IEnumerable<string> files)
    {
        string own = Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar;
        return [.. walked, .. files
            .Where(f => !f.StartsWith(own, PathNames.Comparison))
            .Select(f => Path.GetDirectoryName(f)!)
            .Distinct(PathNames.Comparer)
            .Where(d => !walked.Any(f => PathNames.Same(f.Path, d)) && Directory.Exists(d))
            .Select(Stamps.Folder)];
    }

    /// <summary>One evaluation of a project, of one framework when <paramref name="framework"/> is given.</summary>
    internal static Project Load(string path, ProjectCollection collection, EvaluationContext context, IReadOnlyDictionary<string, string> properties, string? framework)
    {
        Dictionary<string, string> global = new(properties, StringComparer.OrdinalIgnoreCase);
        if (framework is not null)
        {
            global["TargetFramework"] = framework;
        }
        return WindowsTargeting.Load(given => Project.FromFile(path, new ProjectOptions
        {
            GlobalProperties = given,
            ProjectCollection = collection,
            EvaluationContext = context,
            LoadSettings = Settings,
        }), global, collection);
    }

    private static string? NonEmpty(string value) => value.Length > 0 ? value : null;

    /// <summary>The carried properties (<see cref="CarriedProperties.Names"/>) that have a value, each with whether the
    /// element that last set it lies in the project's own files, outside the SDK and the package folders: the SDK turns
    /// EnableSdkContainerSupport on for every executable, which asks for nothing.</summary>
    private static Dictionary<string, EvaluatedProperty> Carried(Project project, HashSet<string> excluded)
    {
        Dictionary<string, EvaluatedProperty> carried = new(StringComparer.Ordinal);
        foreach (string name in CarriedProperties.Names)
        {
            if (project.GetProperty(name) is { EvaluatedValue.Length: > 0 } property)
            {
                string? file = property.Xml?.ContainingProject?.FullPath;
                bool own = file is { Length: > 0 } && !excluded.Any(root => file.StartsWith(root, PathNames.Comparison));
                carried[name] = new EvaluatedProperty(property.EvaluatedValue, own);
            }
        }
        return carried;
    }

    /// <summary>A framework's packages, each with the version restore takes (its own, its VersionOverride, else under
    /// central package management its PackageVersion's), and the projects it references.</summary>
    private static FrameworkDependencies Dependencies(Project project, string? framework)
    {
        Dictionary<string, string> central = new(StringComparer.OrdinalIgnoreCase);
        if (project.GetPropertyValue("ManagePackageVersionsCentrally").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ProjectItem version in project.GetItems("PackageVersion"))
            {
                central[version.EvaluatedInclude] = version.GetMetadataValue("Version");
            }
        }
        List<PackageDependency> packages = [.. project.GetItems("PackageReference")
            .Select(p => new PackageDependency(
                p.EvaluatedInclude,
                NonEmpty(p.GetMetadataValue("VersionOverride")) ?? NonEmpty(p.GetMetadataValue("Version")) ?? central.GetValueOrDefault(p.EvaluatedInclude),
                p.GetMetadataValue("IsImplicitlyDefined").Equals("true", StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        List<string> projects = [.. project.GetItems("ProjectReference").Select(r => r.GetMetadataValue("FullPath")).Distinct(PathNames.Comparer)];
        return new FrameworkDependencies(framework, packages, projects)
        {
            FrameworkReferences = [.. project.GetItems("FrameworkReference").Select(r => r.EvaluatedInclude).Distinct(StringComparer.OrdinalIgnoreCase)],
            WindowsTargeting = WindowsTargeting.Given(project),
        };
    }

    /// <summary>The project's files, every evaluation's, each once: of the file item types and those the project names as
    /// such, but those it hides (Visible false).</summary>
    internal static List<EvaluatedItem> Items(IReadOnlyList<Project> evaluations)
    {
        List<EvaluatedItem> items = [];
        HashSet<(string, string)> seen = [];
        foreach (Project project in evaluations)
        {
            IEnumerable<string> types = FileItemTypes.Concat(project.GetItems("AvailableItemName").Select(i => i.EvaluatedInclude)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string type in types)
            {
                foreach (ProjectItem item in project.GetItems(type))
                {
                    if (item.GetMetadataValue("Visible").Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string full = item.GetMetadataValue("FullPath");
                    if (full.Length == 0 || !seen.Add((type, full)))
                    {
                        continue;
                    }
                    items.Add(new EvaluatedItem(type, full, NonEmpty(item.GetMetadataValue("Link")), NonEmpty(item.GetMetadataValue("DependentUpon"))));
                }
            }
        }
        return items;
    }

    /// <summary>Where imports never change but with their versions, which the imports kept say: the .NET install (the
    /// SDK, its props and targets) and the NuGet package folders.</summary>
    internal static HashSet<string> OutsideRoots(Project project)
    {
        HashSet<string> roots = new(PathNames.Comparer);
        foreach (string property in new[] { "NetCoreRoot", "NetCoreTargetingPackRoot", "MSBuildBinPath", "MSBuildExtensionsPath", "NuGetPackageRoot" })
        {
            Add(project.GetPropertyValue(property));
        }
        foreach (string folder in project.GetPropertyValue("NuGetPackageFolders").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Add(folder);
        }
        return roots;

        void Add(string folder)
        {
            if (folder.Length > 0 && Path.IsPathFullyQualified(folder))
            {
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar);
            }
        }
    }

    /// <summary>The files MSBuild looks for from a project's folder up, by the property that names each and its default name;
    /// global.json, which no property names, gives the SDKs from the package folders their versions (msbuild-sdks).</summary>
    private static readonly (string? Property, string Name)[] Above =
    [
        ("_DirectoryBuildPropsFile", "Directory.Build.props"),
        ("_DirectoryBuildTargetsFile", "Directory.Build.targets"),
        ("_DirectoryPackagesPropsFile", "Directory.Packages.props"),
        (null, "global.json"),
    ];

    /// <summary>What would change a project's evaluation by coming or going, stamped (most as not there): each
    /// Directory.Build.props, .targets, Directory.Packages.props and global.json looked for from the project's folder up,
    /// as far as the first it finds, and the props and targets a restore writes for the project to import. Without an
    /// evaluation (one that failed), by their default names and place.</summary>
    internal static IEnumerable<Stamp> LookedFor(string path, Project? evaluated)
    {
        string folder = Path.GetDirectoryName(path)!;
        foreach ((string? property, string name) in Above)
        {
            string named = property is not null && evaluated?.GetPropertyValue(property) is { Length: > 0 } given ? given : name;
            for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
            {
                Stamp stamp = Stamps.File(Path.Combine(at, named));
                yield return stamp;
                if (stamp.Length >= 0)
                {
                    break;
                }
            }
        }
        string restored = evaluated?.GetPropertyValue("MSBuildProjectExtensionsPath") is { Length: > 0 } written && Path.IsPathFullyQualified(written)
            ? written
            : Path.Combine(folder, "obj");
        string file = Path.GetFileName(path);
        yield return Stamps.File(Path.Combine(restored, file + ".nuget.g.props"));
        yield return Stamps.File(Path.Combine(restored, file + ".nuget.g.targets"));
    }

    /// <summary>What an evaluation that failed read, so a fix to it has the project evaluated again: the project file, the
    /// file MSBuild names as broken, and what it looks for above the project.</summary>
    internal static IReadOnlyList<Stamp> Failed(string path, Stamp file, Exception error)
    {
        List<Stamp> read = [file];
        if (error is InvalidProjectFileException { ProjectFile: { Length: > 0 } broken } && !PathNames.Same(broken, path))
        {
            read.Add(Stamps.File(broken));
        }
        return [.. read.Concat(LookedFor(path, null)).DistinctBy(s => s.Path, PathNames.Comparer)];
    }

    /// <summary>Every folder under a project's that a wildcard would enter, stamped: each once whatever links reach it, and
    /// none a link goes up to (<see cref="PathNames.FoldersUnder"/>), the nearest first up to the limit, several read at once.</summary>
    internal static IReadOnlyList<Stamp> Walked(string folder) =>
        [.. PathNames.FoldersUnder(folder, sub => Stamps.Counts(Path.GetFileName(sub), folder: true)).Take(FolderLimit).AsParallel().AsOrdered().Select(Stamps.Folder)];
}
