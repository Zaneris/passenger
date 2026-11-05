using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Windows.Diagnostics;
using Passenger.Core;

namespace Passenger.Inspector.Wpf;

/// <summary>Where in its XAML WPF made an object (<see cref="VisualDiagnostics.GetXamlSourceInfo"/>, from a Debug build's BAML
/// line information): its line and column, and its BAML's pack URI, turned into the file on disk. The assembly's own symbols
/// name each XAML file a class of it was made from (MarkupCompile's #line), and so the project's folder its BAML names are
/// relative to; the launched project's folder stands in for the app's own assembly when its symbols do not say.</summary>
internal static class Sources
{
    /// <summary>Each assembly's XAML files by the lower-case path its BAML is named by, and its project's folder, once read.</summary>
    private static readonly Dictionary<string, (Dictionary<string, string> Files, string? Folder)> Read = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>The launched project's folder, from Passenger, or null.</summary>
    public static string? Project { get; set; }

    /// <summary>Where WPF recorded an object was made in XAML, or null.</summary>
    public static SourcePlace? Place(object made) =>
        Info(made) is { SourceUri: { } uri, LineNumber: > 0 } info && FileOf(uri) is { } file ? new SourcePlace(file, info.LineNumber, info.LinePosition) : null;

    /// <summary>Where an object of a root's collection came from, as far as WPF recorded: the XAML file given, another, or none.</summary>
    public static XamlOrigin Origin(object made, string file) =>
        Info(made) is null ? XamlOrigin.Unrecorded : Place(made) is { } place && PathNames.Same(place.File, file) ? XamlOrigin.ThisFile : XamlOrigin.AnotherFile;

    private static XamlSourceInfo? Info(object made)
    {
        try
        {
            return VisualDiagnostics.GetXamlSourceInfo(made);
        }
        catch (Exception error) when (error is MissingMethodException or TypeLoadException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The file a BAML's URI names: a file URI's path, or a pack URI's component in its assembly's project.</summary>
    public static string? FileOf(Uri uri)
    {
        if (uri.IsAbsoluteUri && uri.IsFile)
        {
            return uri.LocalPath;
        }
        string path = Uri.UnescapeDataString(uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString).Replace('\\', '/');
        int component = path.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        string assembly;
        string relative;
        if (component >= 0)
        {
            // "/App;v1.0.0.0;component/x.xaml" names its assembly first, its version and key after it.
            assembly = path[..component].TrimStart('/').Split(';')[0];
            relative = path[(component + ";component/".Length)..];
        }
        else
        {
            assembly = Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty;
            relative = path.TrimStart('/');
        }
        return assembly.Length == 0 || relative.Length == 0 ? null : Find(assembly, relative);
    }

    private static string? Find(string assembly, string relative)
    {
        (Dictionary<string, string> files, string? folder) = Of(assembly);
        string key = relative.ToLowerInvariant();
        if (files.TryGetValue(key, out string? known))
        {
            return known;
        }
        if (folder is not null && Cased(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar))) is { } found)
        {
            return found;
        }
        return null;
    }

    /// <summary>What an assembly's symbols and BAML say of its XAML: each file a document names, by the BAML name it is
    /// compiled to, and the project's folder they share.</summary>
    private static (Dictionary<string, string> Files, string? Folder) Of(string name)
    {
        lock (Gate)
        {
            if (Read.TryGetValue(name, out (Dictionary<string, string>, string?) known))
            {
                return known;
            }
            Dictionary<string, string> files = [];
            string? folder = null;
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (assembly is not null)
            {
                List<string> baml = Baml(assembly);
                foreach (string document in XamlDocuments(assembly))
                {
                    string slashed = document.Replace('\\', '/');
                    foreach (string compiled in baml)
                    {
                        string xaml = compiled[..^".baml".Length] + ".xaml";
                        if (slashed.EndsWith("/" + xaml, StringComparison.OrdinalIgnoreCase))
                        {
                            files[xaml] = document;
                            folder ??= document[..^xaml.Length].TrimEnd('/', '\\');
                        }
                    }
                }
                if (folder is null && assembly == Assembly.GetEntryAssembly() && Project is { Length: > 0 } project)
                {
                    folder = project;
                }
            }
            Read[name] = (files, folder);
            return (files, folder);
        }
    }

    /// <summary>The names of an assembly's compiled XAML, as WPF's resources hold them ("views/mainwindow.baml").</summary>
    private static List<string> Baml(Assembly assembly)
    {
        List<string> names = [];
        try
        {
            using Stream? stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.g.resources");
            if (stream is null)
            {
                return names;
            }
            using ResourceReader reader = new(stream);
            foreach (DictionaryEntry entry in reader)
            {
                if (entry.Key is string key && key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(key.ToLowerInvariant());
                }
            }
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // No resources it can read: its symbols' names alone, or the project's folder, find its XAML.
        }
        return names;
    }

    /// <summary>The XAML files an assembly's portable symbols (beside it or embedded) name.</summary>
    private static List<string> XamlDocuments(Assembly assembly)
    {
        List<string> documents = [];
        if (assembly.IsDynamic || assembly.Location is not { Length: > 0 } location || !File.Exists(location))
        {
            return documents;
        }
        try
        {
            using FileStream stream = File.OpenRead(location);
            using PEReader pe = new(stream);
            if (!pe.TryOpenAssociatedPortablePdb(location, path => File.Exists(path) ? File.OpenRead(path) : null, out MetadataReaderProvider? provider, out _) || provider is null)
            {
                return documents;
            }
            using (provider)
            {
                MetadataReader pdb = provider.GetMetadataReader();
                foreach (DocumentHandle handle in pdb.Documents)
                {
                    string name = pdb.GetString(pdb.GetDocument(handle).Name);
                    if (name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        documents.Add(name);
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Symbols it cannot read: the project's folder alone finds the app's own XAML.
        }
        return documents;
    }

    /// <summary>A file's path as the disk names it (a BAML's name is lower case), or null when it is not there.</summary>
    private static string? Cased(string path)
    {
        try
        {
            return File.Exists(path) ? PathNames.Canonical(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }
}
