using System.Xml;
using System.Xml.Linq;

namespace Passenger.Workspace;

/// <summary>A Windows app package's identity, as its manifest (<c>AppxManifest.xml</c>) declares it: the name, publisher,
/// version and processor it is registered by, its applications' ids, and the packages it depends on.</summary>
public sealed record WindowsPackageIdentity(string Name, string Publisher, string Version, string? Processor, IReadOnlyList<string> Applications,
    IReadOnlyList<WindowsPackageDependency> Dependencies);

/// <summary>A package an app package depends on (Windows App SDK's runtime), by its name, least version and publisher.</summary>
public sealed record WindowsPackageDependency(string Name, string MinVersion, string Publisher);

/// <summary>What a packaged app's build says its package's layout is (its <c>.build.appxrecipe</c>, written beside its
/// output by the Windows SDK's MSIX tooling): the folder the layout is made in, its manifest, and each file it holds, from
/// where the build has it to where the package has it.</summary>
public sealed record WindowsPackageLayout(string Folder, string Manifest, IReadOnlyList<(string From, string To)> Files);

/// <summary>A packaged Windows app's build read as the Windows SDK's tooling writes it: its layout's recipe and its
/// manifest's identity, read from their XML alone. Text that reads as neither is passed over.</summary>
public static class WindowsPackageLayouts
{
    /// <summary>The recipe a build writes beside its output, by the program's name (<c>App1.build.appxrecipe</c>).</summary>
    public static string RecipeOf(string output, string targetName) => Path.Combine(output, targetName + ".build.appxrecipe");

    /// <summary>A recipe's layout: its LayoutDir, its manifest's file, and each packaged file's from and to (the to as a path
    /// below the layout, '\' or '/' kept as written); null for text that reads as no recipe.</summary>
    public static WindowsPackageLayout? Recipe(string text)
    {
        XDocument? read = Read(text);
        string? folder = Elements(read, "LayoutDir").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        XElement? manifest = Elements(read, "AppXManifest").FirstOrDefault();
        if (folder is null || manifest?.Attribute("Include")?.Value is not { Length: > 0 } manifestFile)
        {
            return null;
        }
        List<(string, string)> files = [(manifestFile, PackagePath(manifest) ?? "AppxManifest.xml")];
        foreach (XElement file in Elements(read, "AppxPackagedFile"))
        {
            if (file.Attribute("Include")?.Value is { Length: > 0 } from && PackagePath(file) is { } to)
            {
                files.Add((from, to));
            }
        }
        return new WindowsPackageLayout(folder, manifestFile, files);
    }

    /// <summary>A manifest's identity; null for text that reads as no package manifest.</summary>
    public static WindowsPackageIdentity? Identity(string text)
    {
        XDocument? read = Read(text);
        XElement? identity = Elements(read, "Identity").FirstOrDefault(e => e.Parent?.Name.LocalName == "Package");
        if (identity?.Attribute("Name")?.Value is not { Length: > 0 } name || identity.Attribute("Publisher")?.Value is not { Length: > 0 } publisher)
        {
            return null;
        }
        List<string> applications = [.. Elements(read, "Application").Where(e => e.Parent?.Name.LocalName == "Applications")
            .Select(e => e.Attribute("Id")?.Value).OfType<string>().Where(id => id.Length > 0)];
        List<WindowsPackageDependency> dependencies = [.. Elements(read, "PackageDependency")
            .Where(e => e.Attribute("Name")?.Value is { Length: > 0 })
            .Select(e => new WindowsPackageDependency(e.Attribute("Name")!.Value, e.Attribute("MinVersion")?.Value ?? string.Empty, e.Attribute("Publisher")?.Value ?? string.Empty))];
        return new WindowsPackageIdentity(name, publisher, identity.Attribute("Version")?.Value ?? "0.0.0.0", identity.Attribute("ProcessorArchitecture")?.Value,
            applications, dependencies);
    }

    private static string? PackagePath(XElement file) => file.Elements().FirstOrDefault(e => e.Name.LocalName == "PackagePath")?.Value.Trim() is { Length: > 0 } path ? path : null;

    private static IEnumerable<XElement> Elements(XDocument? document, string name) => document?.Descendants().Where(e => e.Name.LocalName == name) ?? [];

    private static XDocument? Read(string text)
    {
        try
        {
            return XDocument.Parse(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
