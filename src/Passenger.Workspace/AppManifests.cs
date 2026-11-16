using System.Xml;
using System.Xml.Linq;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>An Apple app's Info.plist (and a workload's Versions.plist) read and changed as text through <see cref="XmlText"/>:
/// the keys of its dictionary at the top and the strings of one within it, a key's string set anew, and a scene manifest
/// added at the dictionary's end, indented as its keys are; every other character stays as it was. Text that reads as no
/// property list is passed over.</summary>
public static class AppManifests
{
    /// <summary>The least version of the platform an iOS app runs on.</summary>
    public const string MinimumOSVersion = "MinimumOSVersion";

    /// <summary>The least version of macOS a Mac Catalyst app runs on.</summary>
    public const string MinimumSystemVersion = "LSMinimumSystemVersion";

    /// <summary>The scene configuration MAUI's app delegate asks iOS for, by its name.</summary>
    public const string MauiSceneConfiguration = "__MAUI_DEFAULT_SCENE_CONFIGURATION__";

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

    /// <summary>The strings of a dictionary the one at the top holds under <paramref name="key"/> (a workload's
    /// MacCatalystVersionMap); none where it holds no such dictionary or the text reads as no property list.</summary>
    public static IReadOnlyDictionary<string, string> Strings(string text, string key)
    {
        Dictionary<string, string> strings = new(StringComparer.Ordinal);
        if (Read(text) is { } xml && Top(xml) is { } dict && Pairs(dict).FirstOrDefault(p => p.Key.Value == key).Value is { Name.LocalName: "dict" } inner)
        {
            foreach ((XElement name, XElement? value) in Pairs(inner))
            {
                if (value is { Name.LocalName: "string" })
                {
                    strings.TryAdd(name.Value, value.Value);
                }
            }
        }
        return strings;
    }

    /// <summary>The text with the string MinimumOSVersion holds set to <paramref name="version"/>; null where it holds no
    /// string or the text reads as no property list.</summary>
    public static string? WithMinimumOSVersion(string text, string version) => WithString(text, MinimumOSVersion, version);

    /// <summary>The text with the string <paramref name="key"/> holds set to <paramref name="value"/>; null where it holds
    /// no string or the text reads as no property list.</summary>
    public static string? WithString(string text, string key, string value)
    {
        if (Read(text) is not { } xml || Top(xml) is not { } dict
            || Pairs(dict).FirstOrDefault(p => p.Key.Value == key).Value is not { Name.LocalName: "string" } held)
        {
            return null;
        }
        (int start, int length) = xml.InnerOf(held) ?? (-1, 0);
        if (start < 0)
        {
            xml.Replace(held, $"<string>{XmlEntities.Encode(value)}</string>");
        }
        else
        {
            xml.Apply([(start, length, XmlEntities.Encode(value))]);
        }
        return xml.Text;
    }

    /// <summary>The text with a scene manifest of one scene at the end of the dictionary at the top, its keys' indent kept,
    /// and with <paramref name="configuration"/> (its name and scene delegate class) the app's one scene configuration; null
    /// where it has one or the text reads as no property list.</summary>
    public static string? WithSceneManifest(string text, (string Name, string Delegate)? configuration = null)
    {
        if (Read(text) is not { } xml || Top(xml) is not { } dict || Pairs(dict).Any(p => p.Key.Value == SceneManifest))
        {
            return null;
        }
        string indent = xml.IndentIn(dict);
        string step = xml.StepIn(dict);
        xml.Append(dict, $"<key>{SceneManifest}</key>");
        string nl = xml.NewLine;
        string In(int steps) => nl + indent + string.Concat(Enumerable.Repeat(step, steps));
        string configured = configuration is not { } named ? string.Empty
            : $"{In(1)}<key>{SceneConfigurations}</key>{In(1)}<dict>{In(2)}<key>{ApplicationRole}</key>{In(2)}<array>{In(3)}<dict>"
                + $"{In(4)}<key>{ConfigurationName}</key>{In(4)}<string>{XmlEntities.Encode(named.Name)}</string>"
                + $"{In(4)}<key>{DelegateClassName}</key>{In(4)}<string>{XmlEntities.Encode(named.Delegate)}</string>"
                + $"{In(3)}</dict>{In(2)}</array>{In(1)}</dict>";
        xml.Append(Top(xml)!, $"<dict>{In(1)}<key>{MultipleScenes}</key>{In(1)}<false/>{configured}{In(0)}</dict>");
        return xml.Text;
    }

    // A scene manifest's configurations: those of an app's window, each named, with its scene delegate's class.
    private const string SceneConfigurations = "UISceneConfigurations";
    private const string ApplicationRole = "UIWindowSceneSessionRoleApplication";
    private const string ConfigurationName = "UISceneConfigurationName";
    private const string DelegateClassName = "UISceneDelegateClassName";

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
