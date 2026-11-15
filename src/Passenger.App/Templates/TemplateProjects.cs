using System.Xml;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.App.Templates;

/// <summary>What a template's project file says of what it builds, read as the template carries it, before dotnet new
/// makes anything of it.</summary>
public static class TemplateProjects
{
    /// <summary>The .NET SDK, which builds a library of a project that names no output type.</summary>
    private const string DotnetSdk = "Microsoft.NET.Sdk";

    /// <summary>The output type of a class library.</summary>
    public const string Library = "Library";

    /// <summary>Whether a path a template names is a .NET project file.</summary>
    public static bool IsProject(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>The output type a project file builds: the OutputType it writes, where every one it writes says the same
    /// and one holds under no condition, MSBuild's or the template's; Library where it writes none, imports no file and
    /// names the .NET SDK alone; null where another SDK or a condition decides it, or the text is no project.</summary>
    public static string? OutputType(string project)
    {
        XElement root;
        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(project), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            if (XDocument.Load(reader).Root is not { Name.LocalName: "Project" } read)
            {
                return null;
            }
            root = read;
        }
        catch (XmlException)
        {
            return null;
        }
        List<string> written = [];
        bool holds = false;
        // The template's own conditions are comments around what they keep or drop (<!--#if (Exe)--> … <!--#endif-->).
        int within = 0;
        foreach (XNode node in root.DescendantNodes())
        {
            if (node is XComment comment)
            {
                string said = comment.Value.Trim();
                within += said.StartsWith("#if", StringComparison.Ordinal) ? 1 : said.StartsWith("#endif", StringComparison.Ordinal) ? -1 : 0;
            }
            else if (node is XElement { Name.LocalName: MSBuildNames.OutputType, Parent: { Name.LocalName: "PropertyGroup" } group } element)
            {
                written.Add(element.Value.Trim());
                holds |= within == 0 && group.Parent == root && element.Attribute("Condition") is null && group.Attribute("Condition") is null;
            }
        }
        if (written.Count > 0)
        {
            return holds && written.All(w => string.Equals(w, written[0], StringComparison.OrdinalIgnoreCase)) ? written[0] : null;
        }
        bool importsFile = root.Descendants().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is null);
        return !importsFile && Sdks(root) is [{ } sdk] && string.Equals(sdk, DotnetSdk, StringComparison.OrdinalIgnoreCase) ? Library : null;
    }

    /// <summary>The SDKs a project names, each once and without its version: its Sdk attribute's, its Sdk elements' and
    /// those its imports take.</summary>
    private static List<string> Sdks(XElement root)
    {
        IEnumerable<string?> named = root.Descendants().Select(e => e.Name.LocalName switch
        {
            "Sdk" => e.Attribute("Name")?.Value,
            "Import" => e.Attribute("Sdk")?.Value,
            _ => null,
        });
        return [.. named.Prepend(root.Attribute("Sdk")?.Value).OfType<string>()
            .SelectMany(s => s.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Split('/')[0].Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
