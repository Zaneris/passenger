using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Projects;

/// <summary>Keeps one project's XAML context current: its assemblies read once, each C# file's types and each XAML file's
/// summary read again only when that file changes, and a new <see cref="XamlProject"/> made from them on demand. The
/// first load reads every file; after that a change costs its own file and a type system over what is kept. Safe on any
/// thread: every call takes the builder's lock, and the projects it hands out are immutable.</summary>
public sealed class XamlProjectBuilder
{
    private readonly object gate = new();
    private readonly string? dotnetRoot;

    /// <summary>What MSBuild's evaluation says of where a project's restore and build put things and what its C# is parsed
    /// with, by its path; any thread asks it.</summary>
    private readonly Func<string, ProjectEvaluation?>? evaluations;

    /// <summary>What the C# of a project with no evaluation to say so is parsed with.</summary>
    private readonly CSharpParseOptions unevaluated;

    private readonly Action<Exception>? failed;
    private readonly Dictionary<string, SourceTypes> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XamlFileSummary> files = new(StringComparer.Ordinal);
    private ProjectInputs inputs;
    private List<(ProjectInputs Inputs, List<string> Files)> referenced = [];
    private IReadOnlyList<AssemblyTypes> assemblies = [];
    private IReadOnlySet<string> themeKeys = new HashSet<string>();
    private IReadOnlyList<string> assets = [];
    private XamlProject? built;

    /// <summary>The XAML classes the last type system was made with (<see cref="XamlClasses"/>), so a XAML change that
    /// declares none anew keeps it.</summary>
    private string classes = string.Empty;

    private XamlProjectBuilder(ProjectInputs inputs, XamlDialect dialect, string? dotnetRoot, CSharpParseOptions unevaluated, Action<Exception>? failed,
        Func<string, ProjectEvaluation?>? evaluations)
    {
        this.inputs = inputs;
        Dialect = dialect;
        this.dotnetRoot = dotnetRoot;
        this.unevaluated = unevaluated;
        this.failed = failed;
        this.evaluations = evaluations;
    }

    public XamlDialect Dialect { get; }

    public string ProjectPath => inputs.ProjectPath;

    public string Folder => inputs.Folder;

    /// <summary>A builder for a project whose XAML Passenger reads (an Avalonia or WPF project), or null for another;
    /// <paramref name="failed"/> hears of a file a load could not read, which it leaves out, <paramref name="evaluations"/>
    /// answers what MSBuild's evaluation says of a project (where its restore and build put things, what its C# is parsed
    /// with), from any thread, and a project it says nothing of has its C# parsed with <paramref name="unevaluated"/>.</summary>
    public static XamlProjectBuilder? For(string projectPath, string? dotnetRoot, CSharpParseOptions unevaluated, Action<Exception>? failed = null,
        Func<string, ProjectEvaluation?>? evaluations = null)
    {
        ProjectInputs inputs = ProjectInputs.Read(projectPath, dotnetRoot, evaluations?.Invoke(projectPath));
        return DialectOf(inputs) is { } dialect ? new XamlProjectBuilder(inputs, dialect, dotnetRoot, unevaluated, failed, evaluations) : null;
    }

    /// <summary>The dialect a project's XAML is in, by the framework it references: Avalonia's for a project that references
    /// its package, WPF's for one whose restore references WPF's shared framework (<c>UseWPF</c>, which the project file
    /// says before a restore); null for any other (MAUI, WinUI), whose XAML Passenger does not read.</summary>
    public static XamlDialect? DialectOf(ProjectInputs inputs)
    {
        if (inputs.Uses("Avalonia"))
        {
            return AvaloniaDialect.Instance;
        }
        bool wpf = inputs.FrameworkReferences.Any(f => f is "Microsoft.WindowsDesktop.App.WPF" or "Microsoft.WindowsDesktop.App")
            || string.Equals(inputs.PropertyValue("UseWPF"), "true", StringComparison.OrdinalIgnoreCase);
        return wpf ? WpfDialect.Instance : null;
    }

    /// <summary>Whether a file is one of the project's XAML files.</summary>
    public bool Owns(string path) =>
        Dialect.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && PathNames.Within(path, Folder) && !InBuildOutput(path);

    /// <summary>Reads everything: the assemblies, every C# and XAML file (an open file's text from <paramref name="open"/>
    /// rather than disk), the assets.</summary>
    public XamlProject Load(Func<string, string?>? open = null)
    {
        lock (gate)
        {
            inputs = ProjectInputs.Read(inputs.ProjectPath, dotnetRoot, evaluations?.Invoke(inputs.ProjectPath));
            assemblies = [.. inputs.References.AsParallel().AsOrdered().Select(AssemblyTypes.Get).OfType<AssemblyTypes>()];
            themeKeys = ThemeKeys(assemblies);
            referenced = [];
            CollectReferences(inputs, new HashSet<string>(StringComparer.Ordinal) { inputs.ProjectPath });
            sources.Clear();
            List<(string Path, ProjectInputs Project)> cs = [.. inputs.SourceFiles.Select(f => (f, inputs))];
            cs.AddRange(referenced.SelectMany(r => r.Inputs.SourceFiles.Select(f => (f, r.Inputs))));
            foreach (SourceTypes read in cs.AsParallel().Select(f => Reading(() => SourceTypes.Read(f.Path, open?.Invoke(f.Path) ?? ReadText(f.Path), f.Project.AssemblyName,
                ParsingOf(f.Project)))).OfType<SourceTypes>())
            {
                sources[read.Path] = read;
            }
            files.Clear();
            assets = [.. Dialect.AssetItems.SelectMany(inputs.Items).Distinct(StringComparer.Ordinal)];
            List<string> xaml = [.. XamlFilesUnder(inputs.Folder)];
            XamlProject shell = Make();
            foreach (string path in xaml)
            {
                if (Reading(() => XamlFileSummary.Of(path, XmlTree.Parse(open?.Invoke(path) ?? ReadText(path)), shell.ResolveSource)) is { } summary)
                {
                    files[path] = summary;
                }
            }
            return built = Make();
        }
    }

    /// <summary>One file read as the project loads; null for one the reader trips on, said to <see cref="failed"/>, so the
    /// project's other files keep their help.</summary>
    private T? Reading<T>(Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failed?.Invoke(error);
            return null;
        }
    }

    /// <summary>The project as last made, made now if nothing has been.</summary>
    public XamlProject Current
    {
        get
        {
            lock (gate)
            {
                return built ??= Make();
            }
        }
    }

    /// <summary>A C# file's text changed (typed, or written on disk): its types are read again; a file deleted goes. Null
    /// for a file neither the project nor one it references compiles.</summary>
    public XamlProject? SourceChanged(string path, string? text)
    {
        lock (gate)
        {
            ProjectInputs? project = Compiling(path);
            if (project is null)
            {
                return null;
            }
            if (text is null && !File.Exists(path))
            {
                sources.Remove(path);
            }
            else
            {
                sources[path] = SourceTypes.Read(path, text ?? ReadText(path), project.AssemblyName, ParsingOf(project));
            }
            return built = Make();
        }
    }

    /// <summary>A XAML file's text changed: its summary is made again; a file deleted goes.</summary>
    public XamlProject XamlChanged(string path, string? text, XmlTree? tree = null)
    {
        lock (gate)
        {
            if (text is null && tree is null && !File.Exists(path))
            {
                files.Remove(path);
            }
            else
            {
                XamlProject current = built ?? Make();
                files[path] = XamlFileSummary.Of(path, tree ?? XmlTree.Parse(text ?? ReadText(path)), current.ResolveSource);
            }
            return built = Remake();
        }
    }

    /// <summary>An open XAML file's text as it is typed: its summary taken when it says something new (a resource, an
    /// include, a name, a class), so the project's other files see it; null when it says nothing new.</summary>
    public XamlProject? XamlTyped(string path, XmlTree tree)
    {
        lock (gate)
        {
            XamlProject current = built ?? Make();
            XamlFileSummary summary = XamlFileSummary.Of(path, tree, current.ResolveSource);
            if (files.TryGetValue(path, out XamlFileSummary? known) && known.Signature == summary.Signature)
            {
                return null;
            }
            files[path] = summary;
            return built = Remake();
        }
    }

    /// <summary>Which project compiles a C# file, of this one and those it references; null for none.</summary>
    private ProjectInputs? Compiling(string path) =>
        new[] { inputs }.Concat(referenced.Select(r => r.Inputs)).FirstOrDefault(project => project.Compiles(path));

    /// <summary>What a project's C# files are parsed with: what its evaluation says, else what a project with none takes.</summary>
    private CSharpParseOptions ParsingOf(ProjectInputs project) => project.Parsing ?? unevaluated;

    private XamlProject Make()
    {
        TypeSystem types = new(assemblies, [.. sources.Values], inputs.AssemblyName, inputs.ImplicitUsings);
        if (XamlClasses(types) is { Count: > 0 } declared)
        {
            types = new TypeSystem(assemblies, [.. sources.Values, .. declared], inputs.AssemblyName, inputs.ImplicitUsings);
        }
        classes = ClassesSaid();
        return With(types);
    }

    /// <summary>The project after a XAML file changed: its types kept unless the files' classes changed.</summary>
    private XamlProject Remake() => built is null || ClassesSaid() != classes ? Make() : With(built.Types);

    private XamlProject With(TypeSystem types) =>
        new(inputs, types, Dialect, new Dictionary<string, XamlFileSummary>(files, StringComparer.Ordinal), assets, themeKeys,
            [.. referenced.Select(r => (r.Inputs.AssemblyName, r.Inputs.Folder))]);

    /// <summary>What the XAML files say of their classes, to tell when the type system must be made again.</summary>
    private string ClassesSaid() =>
        string.Join('|', files.Values.Where(f => f.Class is not null).Select(f => $"{f.Path}:{f.Class}:{f.Root}").Order(StringComparer.Ordinal));

    /// <summary>The class each XAML file's <c>x:Class</c> names that no C# declares, where the dialect's compiler makes it
    /// (a XAML-only WPF control, MarkupCompile's class alone): a class of its root element's type, declared at its x:Class.</summary>
    private List<SourceTypes> XamlClasses(TypeSystem types)
    {
        List<SourceTypes> declared = [];
        if (!Dialect.MakesClasses)
        {
            return declared;
        }
        foreach (XamlFileSummary file in files.Values)
        {
            if (file.Class is not { Length: > 0 } name || file.ClassPlace is not { } place || types.Find(name) is not null
                || !name.Split('.').All(part => part.Length > 0 && part.All(c => char.IsLetterOrDigit(c) || c == '_')))
            {
                continue;
            }
            int dot = name.LastIndexOf('.');
            TypeRef? root = file.Root is { } element
                ? types.ClrNamespacesOf(element.Uri).Select(n => types.Find(n.Length > 0 ? $"{n}.{element.Name}" : element.Name)).OfType<ClrType>()
                    .Select(t => TypeRef.Named(t.FullName)).FirstOrDefault()
                : null;
            ClrType type = new(dot < 0 ? string.Empty : name[..dot], name[(dot + 1)..], ClrTypeKind.Class, new TypeOrigin(inputs.AssemblyName, null, place),
                _ => new ClrTypeDetails(root, [], [], [], IsAbstract: false, IsStatic: false, IsPublic: true, HasDefaultConstructor: true, []));
            declared.Add(SourceTypes.Declaring(file.Path, [type]));
        }
        return declared;
    }

    private void CollectReferences(ProjectInputs from, HashSet<string> seen)
    {
        foreach (string project in from.ProjectReferences)
        {
            if (!seen.Add(project) || !File.Exists(project))
            {
                continue;
            }
            ProjectInputs read = ProjectInputs.Read(project, dotnetRoot, evaluations?.Invoke(project));
            referenced.Add((read, [.. XamlFilesUnder(read.Folder)]));
            CollectReferences(read, seen);
        }
    }

    /// <summary>The keys a theme's compiled XAML holds: its string constants that read as resource keys.</summary>
    private static IReadOnlySet<string> ThemeKeys(IReadOnlyList<AssemblyTypes> assemblies)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AssemblyTypes assembly in assemblies)
        {
            if (!assembly.Name.Contains(".Themes.", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string value in assembly.Strings)
            {
                if (value.Length >= 3 && char.IsUpper(value[0]) && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_'))
                {
                    keys.Add(value);
                }
            }
        }
        return keys;
    }

    /// <summary>Whether a project's own folders hold a file of a dialect's XAML, by its name, read from the disk alone: one
    /// that holds none has no XAML to read. With the number of files looked at to find out.</summary>
    public static (bool Holds, int Walked) HoldsXaml(string projectPath)
    {
        int walked = 0;
        foreach (string file in ProjectInputs.OwnFolders(Path.GetDirectoryName(projectPath)!).SelectMany(f => ProjectInputs.FilesIn(f, "*")))
        {
            walked++;
            if (AvaloniaDialect.Instance.Extensions.Concat(WpfDialect.Instance.Extensions).Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                return (true, walked);
            }
        }
        return (false, walked);
    }

    /// <summary>The project's own XAML files: under its folder, but for bin, obj, hidden folders and another project's.</summary>
    private IEnumerable<string> XamlFilesUnder(string folder) =>
        ProjectInputs.OwnFolders(folder).SelectMany(f => ProjectInputs.FilesIn(f, "*")).Where(f => Dialect.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

    private bool InBuildOutput(string path)
    {
        string relative = Path.GetRelativePath(inputs.Folder, path).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
