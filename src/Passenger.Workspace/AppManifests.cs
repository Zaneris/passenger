using System.Xml;
using System.Xml.Linq;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>An Apple app's Info.plist read and changed as text through <see cref="XmlText"/>: the keys of its dictionary
/// at the top, the string MinimumOSVersion holds set anew, and a scene manifest added at the dictionary's end, indented as
/// its keys are; every other character stays as it was. Text that reads as no property list is passed over.</summary>
public static class AppManifests
{
    /// <summary>The least version of the platform the app runs on.</summary>
    public const string MinimumOSVersion = "MinimumOSVersion";

    /// <summary>The scene manifest, the dictionary that has an app take UIKit's scenes.</summary>
    public const string SceneManifest = "UIApplicationSceneManifest";

    /// <summary>Whether an app opens more than one scene: a key of the scene manifest.</summary>
    private const string MultipleScenes = "UIApplicationSupportsMultipleScenes";

    /// <summary>The keys of the dictionary at the top, each with its value's text where it is a string (null for any
    /// other kind); null for text that reads as no property list.</summary>
    public static IReadOnlyDictionary<string, string?>? Keys(string text)
    {
        if (Read(text) is not { } xml || Top(xml) is not { } dict)
        {
            return null;
        }
        Dictionary<string, string?> keys = new(StringComparer.Ordinal);
        foreach ((XElement key, XElement? value) in Pairs(dict))
        {
            keys.TryAdd(key.Value, value is { Name.LocalName: "string" } ? value.Value : null);
        }
        return keys;
    }

    /// <summary>The text with the string MinimumOSVersion holds set to <paramref name="version"/>; null where it holds no
    /// string or the text reads as no property list.</summary>
    public static string? WithMinimumOSVersion(string text, string version)
    {
        if (Read(text) is not { } xml || Top(xml) is not { } dict
            || Pairs(dict).FirstOrDefault(p => p.Key.Value == MinimumOSVersion).Value is not { Name.LocalName: "string" } value)
        {
            return null;
        }
        (int start, int length) = xml.InnerOf(value) ?? (-1, 0);
        if (start < 0)
        {
            xml.Replace(value, $"<string>{XmlEntities.Encode(version)}</string>");
        }
        else
        {
            xml.Apply([(start, length, XmlEntities.Encode(version))]);
        }
        return xml.Text;
    }

    /// <summary>The text with a scene manifest of one scene at the end of the dictionary at the top, its keys' indent kept;
    /// null where it has one or the text reads as no property list.</summary>
    public static string? WithSceneManifest(string text)
    {
        if (Read(text) is not { } xml || Top(xml) is not { } dict || Pairs(dict).Any(p => p.Key.Value == SceneManifest))
        {
            return null;
        }
        string indent = xml.IndentIn(dict);
        string step = xml.StepIn(dict);
        xml.Append(dict, $"<key>{SceneManifest}</key>");
        string nl = xml.NewLine;
        xml.Append(Top(xml)!, $"<dict>{nl}{indent}{step}<key>{MultipleScenes}</key>{nl}{indent}{step}<false/>{nl}{indent}</dict>");
        return xml.Text;
    }

    /// <summary>The dictionary at the top of a property list.</summary>
    private static XElement? Top(XmlText xml) =>
        xml.Root.Name.LocalName == "plist" ? xml.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "dict") : null;

    /// <summary>A dictionary's keys, each with the element after it, its value.</summary>
    private static IEnumerable<(XElement Key, XElement? Value)> Pairs(XElement dict)
    {
        foreach (XElement key in dict.Elements().Where(e => e.Name.LocalName == "key"))
        {
            yield return (key, key.ElementsAfterSelf().FirstOrDefault());
        }
    }

    private static XmlText? Read(string text)
    {
        try
        {
            return new XmlText(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
