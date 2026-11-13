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
        };
    }

    /// <summary>The launch action that opens the browser, a .dcproj's DockerLaunchAction or a Docker Compose launch profile's
    /// composeLaunchAction.</summary>
    public const string LaunchBrowserAction = "LaunchBrowser";
}
