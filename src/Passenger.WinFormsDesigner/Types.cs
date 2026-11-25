using System.Reflection;

namespace Passenger.WinFormsDesigner;

/// <summary>Types by the names designer code writes them, read as the C# compiler reads them there: a keyword, a full name, a
/// name in the class's namespace or one around it, or one the usings bring, among the assemblies the app runs with (the
/// framework's, the project's and those it references), a nested type's after its holder's.</summary>
internal sealed class Types
{
    private static readonly Dictionary<string, Type> Keywords = new(StringComparer.Ordinal)
    {
        ["object"] = typeof(object), ["string"] = typeof(string), ["bool"] = typeof(bool), ["char"] = typeof(char), ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte), ["short"] = typeof(short), ["ushort"] = typeof(ushort), ["int"] = typeof(int), ["uint"] = typeof(uint),
        ["long"] = typeof(long), ["ulong"] = typeof(ulong), ["float"] = typeof(float), ["double"] = typeof(double), ["decimal"] = typeof(decimal),
    };

    private readonly IReadOnlyList<string> scopes;
    private readonly Dictionary<string, Type?> found = new(StringComparer.Ordinal);
    private readonly Assembly? project;

    /// <param name="space">The class's namespace, whose own and outer namespaces' names it reads first.</param>
    /// <param name="usings">The namespaces the file's usings and the project's global ones bring.</param>
    public Types(string? space, IReadOnlyList<string> usings, Assembly? project)
    {
        this.project = project;
        List<string> around = [];
        for (string? at = space; at is { Length: > 0 }; at = at.LastIndexOf('.') is var dot and > 0 ? at[..dot] : null)
        {
            around.Add(at);
        }
        scopes = [.. around, .. usings, string.Empty];
    }

    /// <summary>A type by its written name; null where none of that name is found.</summary>
    public Type? Find(string written)
    {
        if (Keywords.TryGetValue(written, out Type? keyword))
        {
            return keyword;
        }
        if (found.TryGetValue(written, out Type? known))
        {
            return known;
        }
        Type? resolved = null;
        foreach (string scope in scopes)
        {
            if ((resolved = Full(scope.Length == 0 ? written : $"{scope}.{written}")) is not null)
            {
                break;
            }
        }
        found[written] = resolved;
        return resolved;
    }

    /// <summary>A type by a full name, its dots past a type's name read as nesting.</summary>
    private Type? Full(string name)
    {
        string runtime = name;
        while (true)
        {
            foreach (Assembly assembly in Assemblies())
            {
                if (assembly.GetType(runtime, throwOnError: false) is { } type)
                {
                    return type;
                }
            }
            int dot = runtime.LastIndexOf('.');
            if (dot < 0)
            {
                return null;
            }
            runtime = $"{runtime[..dot]}+{runtime[(dot + 1)..]}";
        }
    }

    /// <summary>The assemblies of the types designer code names most, loaded whether or not anything has used them yet.</summary>
    private static readonly Assembly[] Framework = [.. new[] { typeof(System.Windows.Forms.Form), typeof(System.Drawing.Point), typeof(System.Drawing.Color),
        typeof(System.Drawing.Font), typeof(System.ComponentModel.Component), typeof(System.ComponentModel.TypeConverter) }.Select(t => t.Assembly).Distinct()];

    /// <summary>The assemblies a name is looked for in: the project's, Windows Forms', every one loaded, then those the project
    /// references.</summary>
    private IEnumerable<Assembly> Assemblies()
    {
        if (project is not null)
        {
            yield return project;
        }
        foreach (Assembly framework in Framework)
        {
            yield return framework;
        }
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (loaded != project && !loaded.IsDynamic)
            {
                yield return loaded;
            }
        }
        foreach (AssemblyName referenced in project?.GetReferencedAssemblies() ?? [])
        {
            Assembly? taken = null;
            try
            {
                taken = Assembly.Load(referenced);
            }
            catch (Exception missing) when (missing is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Console.Error.WriteLine($"{referenced.Name} did not load: {missing.Message}");
            }
            if (taken is not null)
            {
                yield return taken;
            }
        }
    }
}
