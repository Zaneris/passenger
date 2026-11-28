using Microsoft.CodeAnalysis.CSharp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Projects;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>A XAML file read against a project of its C# alone, no assembly referenced: the files are named in a folder
/// that is never made, so nothing is read from the disk.</summary>
internal static class XamlFiles
{
    /// <summary>The project's folder, under the root of the temporary folder's drive.</summary>
    public static readonly string Folder = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "passenger-unit-xaml", "App");

    /// <summary>A file's path in the project.</summary>
    public static string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>The XAML <paramref name="text"/> of a file named <paramref name="name"/>, read in a project of the dialect
    /// whose C# is <paramref name="sources"/> (each a file's name and text).</summary>
    public static XamlFile Read(string name, string text, XamlDialect dialect, params (string Name, string Text)[] sources) =>
        new(PathOf(name), XmlTree.Parse(text), Project(dialect, sources));

    /// <summary>The XAML <paramref name="text"/> of a file named <paramref name="name"/>, read in a project of the dialect
    /// whose index holds the file's own summary, as the project's index holds each of its files.</summary>
    public static XamlFile Indexed(string name, string text, XamlDialect dialect)
    {
        XmlTree tree = XmlTree.Parse(text);
        XamlFileSummary summary = XamlFileSummary.Of(PathOf(name), tree, (_, _) => null);
        return new XamlFile(PathOf(name), tree, Project(dialect, new Dictionary<string, XamlFileSummary> { [summary.Path] = summary }));
    }

    /// <summary>The project of the dialect, assembly App, whose C# is <paramref name="sources"/>.</summary>
    public static XamlProject Project(XamlDialect dialect, params (string Name, string Text)[] sources) =>
        Project(dialect, new Dictionary<string, XamlFileSummary>(), sources);

    private static XamlProject Project(XamlDialect dialect, IReadOnlyDictionary<string, XamlFileSummary> files, params (string Name, string Text)[] sources)
    {
        List<SourceTypes> read = [.. sources.Select(s => SourceTypes.Read(PathOf(s.Name), s.Text, "App", CSharpParseOptions.Default))];
        TypeSystem types = new([], read, "App", implied: dialect.FrameworkDefinitions(read.SelectMany(s => s.Types)));
        ProjectInputs inputs = new(PathOf("App.csproj"), "App", "net10.0", [], [], [.. sources.Select(s => PathOf(s.Name))], [], null, []);
        return new XamlProject(inputs, types, dialect, files, [], new HashSet<string>(), []);
    }

    /// <summary>A text with edits made to it, each placed in the text as given, those at one place in their order.</summary>
    public static string Applied(string text, IEnumerable<TextEdit> edits)
    {
        string made = text;
        foreach (TextEdit edit in edits.Select((e, i) => (Edit: e, Order: i)).OrderByDescending(e => e.Edit.Start).ThenByDescending(e => e.Order).Select(e => e.Edit))
        {
            made = made[..edit.Start] + edit.Text + made[(edit.Start + edit.Length)..];
        }
        return made;
    }
}
