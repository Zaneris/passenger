using System.Xml;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>What a Docker Compose project (.dcproj) says of its run, read from its text as written, since the SDK that would
/// evaluate it is Visual Studio's alone: each property the first a PropertyGroup sets, whatever condition stands on it.</summary>
public sealed record ComposeProjectFile
{
    /// <summary>Its compose files' name before <c>.yml</c> (DockerComposeBaseFilePath), from its folder; docker-compose unless set.</summary>
    public string BaseFilePath { get; init; } = "docker-compose";

    /// <summary>The compose files every command takes after the base and its override (AdditionalComposeFilePaths), from its folder.</summary>
    public IReadOnlyList<string> AdditionalFiles { get; init; } = [];

    /// <summary>The service its run launches (DockerServiceName); empty when it names none.</summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>The address its browser opens (DockerServiceUrl), with the tokens <see cref="ServiceUrls"/> fills; empty for none.</summary>
    public string ServiceUrl { get; init; } = string.Empty;

    /// <summary>Whether its run opens the browser: DockerLaunchAction LaunchBrowser, or with no DockerLaunchAction,
    /// DockerLaunchBrowser true.</summary>
    public bool LaunchesBrowser { get; init; }

    /// <summary>Its Compose project's name (DockerComposeProjectName), which every compose command takes; null where it names
    /// none and Compose names it as it would any other files.</summary>
    public string? ProjectName { get; init; }

    /// <summary>The files it holds, as its ItemGroups name them (None, Content and the like, and Folder), each as written
    /// from its folder; an item MSBuild would expand (a wildcard, a property) or hide (Visible false) is left out.</summary>
    public IReadOnlyList<ComposeItem> Items { get; init; } = [];

    /// <summary>The item types whose items are files a project holds, a folder of it too.</summary>
    private static readonly HashSet<string> FileItems = new(StringComparer.Ordinal) { "None", "Content", "Compile", "EmbeddedResource", "Folder" };

    /// <summary>A .dcproj's text read; null where it is not XML.</summary>
    public static ComposeProjectFile? Read(string text)
    {
        XElement? root;
        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            root = XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
        if (root is null)
        {
            return null;
        }
        string? Property(string name) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim() is { Length: > 0 } value ? value : null;
        return new ComposeProjectFile
        {
            BaseFilePath = Property(MSBuildNames.DockerComposeBaseFilePath) ?? "docker-compose",
            AdditionalFiles = (Property(MSBuildNames.AdditionalComposeFilePaths) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ServiceName = Property(MSBuildNames.DockerServiceName) ?? string.Empty,
            ServiceUrl = Property(MSBuildNames.DockerServiceUrl) ?? string.Empty,
            LaunchesBrowser = Property(MSBuildNames.DockerLaunchAction) is { } action
                ? string.Equals(action, LaunchBrowserAction, StringComparison.OrdinalIgnoreCase)
                : bool.TryParse(Property(MSBuildNames.DockerLaunchBrowser), out bool launch) && launch,
            ProjectName = Property(MSBuildNames.DockerComposeProjectName),
            Items = [.. root.Elements().Where(e => e.Name.LocalName == "ItemGroup").Elements().SelectMany(ItemsOf)],
        };
    }

    /// <summary>The files an item element names, its metadata written as an attribute or an element.</summary>
    private static IEnumerable<ComposeItem> ItemsOf(XElement element)
    {
        string type = element.Name.LocalName;
        if (!FileItems.Contains(type) || element.Attribute("Include")?.Value is not { } include)
        {
            return [];
        }
        string? Metadata(string name) =>
            (element.Attribute(name)?.Value ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value)?.Trim() is { Length: > 0 } value ? value : null;
        if (string.Equals(Metadata("Visible"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        string? link = Metadata("Link") is { } named ? ItemPaths.Unescaped(named) : null;
        string? dependentUpon = Metadata("DependentUpon") is { } parent ? ItemPaths.Unescaped(parent) : null;
        return include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(written => written.IndexOfAny(['*', '?']) < 0 && !written.Contains("$(", StringComparison.Ordinal) && !written.Contains("@(", StringComparison.Ordinal)
                && !written.Contains("%(", StringComparison.Ordinal))
            .Select(written => new ComposeItem(type, ItemPaths.Unescaped(written), link, dependentUpon));
    }

    /// <summary>The launch action that opens the browser, a .dcproj's DockerLaunchAction or a Docker Compose launch profile's
    /// composeLaunchAction.</summary>
    public const string LaunchBrowserAction = "LaunchBrowser";
}

/// <summary>A file a Docker Compose project holds: its item type, its path, link and DependentUpon as written (from the
/// project's folder, the DependentUpon from the file's).</summary>
public sealed record ComposeItem(string Type, string Include, string? Link = null, string? DependentUpon = null);
