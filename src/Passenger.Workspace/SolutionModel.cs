using System.Text.RegularExpressions;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.Host;

namespace Passenger.Workspace;

/// <summary>A solution as its file says it is: its folders, their files and its projects from the .slnx or .sln, read
/// directly. What each project holds is MSBuild's evaluation of it, in the project host (<see cref="ProjectModels"/>),
/// which <see cref="WithProjects"/> puts in place of the projects read here.</summary>
public sealed record SolutionModel(string Path, string Name, IReadOnlyList<SolutionEntry> Entries)
{
    /// <summary>The solution folder's GUID in a classic .sln, which marks an entry as a folder.</summary>
    private const string SolutionFolderType = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    public IEnumerable<ProjectModel> Projects => Entries.SelectMany(Flatten);

    /// <summary>The same solution with each project as <paramref name="map"/> gives it (evaluated), its folders kept.</summary>
    public SolutionModel WithProjects(Func<ProjectModel, ProjectModel> map) => this with { Entries = Mapped(Entries, map) };

    private static List<SolutionEntry> Mapped(IEnumerable<SolutionEntry> entries, Func<ProjectModel, ProjectModel> map) =>
        [.. entries.Select(entry => entry switch
        {
            ProjectModel project => map(project),
            SolutionFolder folder => folder with { Children = Mapped(folder.Children, map) },
            _ => entry,
        })];

    /// <summary>Reads a solution, its own path and its projects' named as the file system names them
    /// (<see cref="PathNames.Canonical"/>), whatever letter case or links the solution's lines use.</summary>
    public static SolutionModel? Load(string path)
    {
        path = PathNames.Canonical(path);
        try
        {
            return System.IO.Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? LoadSlnx(path) : LoadSln(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The solution at a folder's top: the one named for the folder if there are several, and of a .sln and a
    /// .slnx alike the .slnx, which <c>dotnet sln migrate</c> writes beside the .sln it leaves; none in a folder that is
    /// gone (deleted under the window) or cannot be read. The window asks <see cref="WorkspaceModel"/>, never this.</summary>
    public static string? Find(string folder)
    {
        List<string> found;
        try
        {
            found = [.. Directory.EnumerateFiles(folder).Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant() is ".sln" or ".slnx")];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        string name = System.IO.Path.GetFileName(folder);
        return found
            .OrderBy(f => System.IO.Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static SolutionModel LoadSlnx(string path)
    {
        string root = System.IO.Path.GetDirectoryName(path)!;
        XElement solution = SharedRead.Xml(path).Root!;
        List<SolutionEntry> entries = [];
        foreach (XElement element in solution.Elements())
        {
            AddSlnx(element, root, entries);
        }
        return new SolutionModel(path, System.IO.Path.GetFileNameWithoutExtension(path), Placed(entries, "/"));
    }

    private static void AddSlnx(XElement element, string root, List<SolutionEntry> into)
    {
        if (element.Name.LocalName == "Project" && element.Attribute("Path")?.Value is { } relative)
        {
            into.Add(ProjectModel.Unevaluated(System.IO.Path.Combine(root, relative.Replace('\\', '/'))));
        }
        else if (element.Name.LocalName == "File" && element.Attribute("Path")?.Value is { } file)
        {
            into.Add(SolutionItem.At(System.IO.Path.Combine(root, file.Replace('\\', '/'))));
        }
        else if (element.Name.LocalName == "Folder" && element.Attribute("Name")?.Value is { } name
            && name.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts)
        {
            // A folder named "/src/tools/" is "tools" inside "src": slnx writes the whole path on each.
            List<SolutionEntry> level = into;
            foreach (string part in parts)
            {
                SolutionFolder? folder = level.OfType<SolutionFolder>().FirstOrDefault(f => f.Name == part);
                if (folder is null)
                {
                    folder = new SolutionFolder(part, string.Empty, []);
                    level.Add(folder);
                }
                level = folder.Children;
            }
            foreach (XElement child in element.Elements())
            {
                AddSlnx(child, root, level);
            }
        }
    }

    /// <summary>The folders given their paths in the solution ("/src/tools/"), known only once the whole file is read.</summary>
    private static List<SolutionEntry> Placed(List<SolutionEntry> entries, string parent) =>
        [.. entries.Select(entry => entry is SolutionFolder folder
            ? folder with { Path = $"{parent}{folder.Name}/", Children = Placed(folder.Children, $"{parent}{folder.Name}/") }
            : entry)];

    private static SolutionModel LoadSln(string path)
    {
        string root = System.IO.Path.GetDirectoryName(path)!;
        Regex project = new(@"^Project\(""\{(?<type>[^}]+)\}""\)\s*=\s*""(?<name>[^""]*)"",\s*""(?<path>[^""]*)"",\s*""\{(?<id>[^}]+)\}""");
        Regex nesting = new("""^\s*\{(?<child>[^}]+)\}\s*=\s*\{(?<parent>[^}]+)\}""");
        Regex item = new(@"^\s*[^=]*?\s*=\s*(?<path>.+?)\s*$");
        Dictionary<string, SolutionEntry> byId = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> parents = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        bool inNesting = false;
        SolutionFolder? inItems = null;
        SolutionFolder? declaring = null;
        foreach (string line in SharedRead.Lines(path))
        {
            if (project.Match(line) is { Success: true } m)
            {
                string id = m.Groups["id"].Value;
                declaring = m.Groups["type"].Value.Equals(SolutionFolderType, StringComparison.OrdinalIgnoreCase)
                    ? new SolutionFolder(m.Groups["name"].Value, string.Empty, [])
                    : null;
                byId[id] = declaring ?? (SolutionEntry)ProjectModel.Unevaluated(System.IO.Path.Combine(root, m.Groups["path"].Value.Replace('\\', '/')));
                order.Add(id);
            }
            else if (declaring is not null && line.Contains("ProjectSection(SolutionItems)", StringComparison.Ordinal))
            {
                inItems = declaring;
            }
            else if (inItems is not null && line.Contains("EndProjectSection", StringComparison.Ordinal))
            {
                inItems = null;
            }
            else if (inItems is not null && item.Match(line) is { Success: true } file)
            {
                // Visual Studio's Solution Items: "README.md = README.md", the path relative to the solution on each side.
                inItems.Children.Add(SolutionItem.At(System.IO.Path.Combine(root, file.Groups["path"].Value.Replace('\\', '/'))));
            }
            else if (line.StartsWith("EndProject", StringComparison.Ordinal) && !line.StartsWith("EndProjectSection", StringComparison.Ordinal))
            {
                declaring = null;
            }
            else if (line.Contains("GlobalSection(NestedProjects)", StringComparison.Ordinal))
            {
                inNesting = true;
            }
            else if (inNesting && line.Contains("EndGlobalSection", StringComparison.Ordinal))
            {
                inNesting = false;
            }
            else if (inNesting && nesting.Match(line) is { Success: true } n)
            {
                parents[n.Groups["child"].Value] = n.Groups["parent"].Value;
            }
        }
        List<SolutionEntry> top = [];
        foreach (string id in order)
        {
            if (parents.TryGetValue(id, out string? parent) && byId.GetValueOrDefault(parent) is SolutionFolder folder)
            {
                folder.Children.Add(byId[id]);
            }
            else
            {
                top.Add(byId[id]);
            }
        }
        return new SolutionModel(path, System.IO.Path.GetFileNameWithoutExtension(path), Placed(top, "/"));
    }

    private static IEnumerable<ProjectModel> Flatten(SolutionEntry entry) => entry switch
    {
        ProjectModel project => [project],
        SolutionFolder folder => folder.Children.SelectMany(Flatten),
        _ => [],
    };
}

public abstract record SolutionEntry(string Name);

/// <summary>A folder of the solution's own, which is in no folder on disk: its path in the solution ("/src/tools/", as a
/// .slnx names it), which is how <see cref="SolutionEdits"/> finds it, and the projects, folders and files it holds.</summary>
public sealed record SolutionFolder(string Name, string Path, List<SolutionEntry> Children) : SolutionEntry(Name);

/// <summary>A file a solution folder holds (Visual Studio's Solution Items), by its canonical path.</summary>
public sealed record SolutionItem(string Name, string Path) : SolutionEntry(Name)
{
    public static SolutionItem At(string path)
    {
        string full = PathNames.Canonical(path);
        return new SolutionItem(System.IO.Path.GetFileName(full), full);
    }
}

/// <summary>One project as MSBuild evaluates it (<see cref="Evaluated"/>, from the project host or the folder's cache of
/// its evaluations): its output type, frameworks, packages, project references and files. Until it has been evaluated
/// it is its path and name alone, and the Solution view shows the files in its folder.</summary>
public sealed record ProjectModel(
    string Path,
    string Name,
    string? OutputType,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<(string Name, string? Version)> Packages,
    IReadOnlyList<string> ProjectReferences,
    bool Exists,
    bool DeclaredTest = false) : SolutionEntry(Name)
{
    /// <summary>VSTest's test SDK and adapters, and what makes a Microsoft.Testing.Platform application: xUnit v3's packages,
    /// TUnit's, and the platform's own build package.</summary>
    private static readonly HashSet<string> TestPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk", "xunit.runner.visualstudio", "NUnit3TestAdapter", "MSTest.TestAdapter", "MSTest",
        "xunit.v3", "xunit.v3.mtp-v1", "xunit.v3.mtp-v2", "xunit.v3.mtp-off", "xunit.v3.core", "xunit.v3.core.mtp-v1", "xunit.v3.core.mtp-v2",
        "xunit.v3.core.mtp-off", "TUnit", "TUnit.Engine", "Microsoft.Testing.Platform.MSBuild",
    };

    /// <summary>MSBuild's evaluation it was made from; null until there is one.</summary>
    public EvaluatedProject? Evaluated { get; init; }

    /// <summary>Whether what it holds is MSBuild's: evaluated, without a problem.</summary>
    public bool IsEvaluated => Evaluated is { Problem: null };

    /// <summary>Why MSBuild could not evaluate it, when it could not.</summary>
    public string? Problem => Evaluated?.Problem;

    public string Folder => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>A property its evaluation carries (<see cref="CarriedProperties"/>) as MSBuild evaluated it, wherever it was
    /// set; null when it has no value, or before the project is evaluated.</summary>
    public string? Property(string name) => Evaluated?.Properties?.GetValueOrDefault(name)?.Value;

    /// <summary>A carried property as evaluated when the project's own files set it, not the SDK's or a package's default.</summary>
    public string? OwnProperty(string name) => Evaluated?.Properties?.GetValueOrDefault(name) is { Own: true } set ? set.Value : null;

    /// <summary>Whether it holds tests VSTest or Microsoft.Testing.Platform runs: it says so (IsTestProject, MSTest.Sdk), or
    /// references a test SDK, an adapter or a Testing Platform test framework.</summary>
    public bool IsTestProject => DeclaredTest || Packages.Any(p => TestPackages.Contains(p.Name));

    /// <summary>Whether it builds something that runs: an executable, not a library or a test.</summary>
    public bool IsRunnable => (string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase)) && !IsTestProject;

    /// <summary>Whether Aspire's evaluated SDK identifies this project as an AppHost.</summary>
    public bool IsAspireHost => Evaluated?.IsAspireHost == true;

    /// <summary>Whether the project host evaluates and builds it with EnableWindowsTargeting (off Windows, for a framework
    /// the SDK marks Windows-only that the project does not enable itself), which Passenger's own builds of it give too.</summary>
    public bool WindowsTargeting => Evaluated?.Dependencies.Any(d => d.WindowsTargeting) == true;

    /// <summary>Off Windows, its target frameworks whose program runs only on Windows, each with the Windows desktop UI it
    /// references (WPF, Windows Forms, or both: the WindowsDesktop shared framework, which only Windows has); none on
    /// Windows, where it runs.</summary>
    public IReadOnlyList<(string Framework, string Desktop)> WindowsOnly
    {
        get
        {
            if (OperatingSystem.IsWindows() || Evaluated is null)
            {
                return [];
            }
            List<(string, string)> found = [];
            foreach (FrameworkDependencies framework in Evaluated.Dependencies)
            {
                if ((framework.Framework ?? TargetFrameworks.FirstOrDefault()) is { } name && Desktop(framework.FrameworkReferences) is { } desktop)
                {
                    found.Add((name, desktop));
                }
            }
            return found;
        }
    }

    /// <summary>Why its program cannot run here, as Run, Debug and Profile say it ("WPF runs only on Windows"), for the
    /// framework given or its first; null when it can.</summary>
    public string? RunsOnlyOnWindows(string? framework = null)
    {
        string? running = framework is { Length: > 0 } ? framework : TargetFrameworks.FirstOrDefault();
        foreach ((string name, string desktop) in WindowsOnly)
        {
            if (running is null || string.Equals(name, running, StringComparison.OrdinalIgnoreCase))
            {
                return $"{desktop} {(desktop.Contains(" and ", StringComparison.Ordinal) ? "run" : "runs")} only on Windows";
            }
        }
        return null;
    }

    /// <summary>The Windows desktop UI a framework's references name: WindowsDesktop's WPF or Windows Forms profile, or
    /// the whole of it (both, as UseWPF with UseWindowsForms references it); null for none.</summary>
    private static string? Desktop(IReadOnlyList<string> references)
    {
        bool whole = references.Contains("Microsoft.WindowsDesktop.App", StringComparer.OrdinalIgnoreCase);
        bool wpf = whole || references.Contains("Microsoft.WindowsDesktop.App.WPF", StringComparer.OrdinalIgnoreCase);
        bool forms = whole || references.Contains("Microsoft.WindowsDesktop.App.WindowsForms", StringComparer.OrdinalIgnoreCase);
        return (wpf, forms) switch
        {
            (true, true) => "WPF and Windows Forms",
            (true, false) => "WPF",
            (false, true) => "Windows Forms",
            _ => null,
        };
    }

    /// <summary>The project a file or folder belongs to: of the projects whose folders hold it, the one nearest it, as a
    /// nested project's files are its own; null when none does. Paths are canonical, as the projects' are.</summary>
    public static ProjectModel? Owning(IEnumerable<ProjectModel> projects, string path) =>
        projects.Where(p => PathNames.Within(path, p.Folder)).MaxBy(p => p.Folder.Length);

    /// <summary>The files of the project that depend on another, as its evaluated DependentUpon says (relative to the
    /// file's own folder, as MSBuild has it), each with that file's path; none before the project is evaluated. Made on
    /// each call from every item, so a folder's nesting asks once for all its files.</summary>
    public Dictionary<string, string> DependentUpon()
    {
        Dictionary<string, string> found = new(PathNames.Comparer);
        foreach (EvaluatedItem item in Evaluated?.Items ?? [])
        {
            if (item.DependentUpon is { } parent && !found.ContainsKey(item.Path))
            {
                found[item.Path] = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, parent.Replace('\\', '/')));
            }
        }
        return found;
    }

    /// <summary>A project not yet evaluated: its path as the file system names it, and its name.</summary>
    public static ProjectModel Unevaluated(string path)
    {
        path = PathNames.Canonical(path);
        return new ProjectModel(path, System.IO.Path.GetFileNameWithoutExtension(path), null, [], [], [], File.Exists(path));
    }

    /// <summary>A project as MSBuild evaluated it: its name as the Solution view shows it (with its assembly's, when that
    /// differs), every framework's packages (but those the SDK adds of itself) and project references.</summary>
    public static ProjectModel From(string path, EvaluatedProject evaluated)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (evaluated.AssemblyName is { Length: > 0 } assembly && assembly != name)
        {
            name = $"{name} ({assembly})";
        }
        List<(string, string?)> packages = [.. evaluated.Dependencies
            .SelectMany(d => d.Packages)
            .Where(p => !p.Implicit)
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Name, p.Version))];
        List<string> references = [.. evaluated.Dependencies
            .SelectMany(d => d.Projects)
            .Select(PathNames.Canonical)
            .Distinct(StringComparer.Ordinal)];
        return new ProjectModel(path, name, evaluated.OutputType, evaluated.TargetFrameworks, packages, references,
            evaluated.Problem is null || File.Exists(path), evaluated.IsTestProject)
        { Evaluated = evaluated };
    }
}

/// <summary>A solution's or project's file read sharing writes, so the read never stands in the way of a program
/// writing it (Windows refuses a write while a reader shares none).</summary>
internal static class SharedRead
{
    public static XDocument Xml(string path)
    {
        using FileStream stream = Open(path);
        return XDocument.Load(stream);
    }

    public static IEnumerable<string> Lines(string path)
    {
        using StreamReader reader = new(Open(path));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
