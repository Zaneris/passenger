using Passenger.Core;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Metadata;

/// <summary>The resource keys a framework's theme file holds (WinUI's <c>generic.xaml</c> beside its projection): those
/// it defines, and those its own values take as theme or static resources without defining them, which the system gives
/// (<c>SystemAccentColor</c>). Read once per file and the time it was last written, for the whole process.</summary>
public static class ThemeFileKeys
{
    private static readonly FileMemo<IReadOnlySet<string>> Kept = new();

    /// <summary>The keys of the file at a path; none for a file not there or not read.</summary>
    public static IReadOnlySet<string> Of(string path) => Kept.Get(path, Read) ?? new HashSet<string>();

    private static IReadOnlySet<string> Read(string path)
    {
        XmlTree tree = XmlTree.Parse(File.ReadAllText(path));
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (XmlElement element in tree.Elements)
        {
            if (XamlFile.Directive(element, "Key") is { Value.Length: > 0 } key)
            {
                keys.Add(key.Value);
            }
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (attribute.HasValue && MarkupExtension.Parse(tree.Text, attribute.ValueStart, attribute.ValueEnd) is { } extension)
                {
                    Taken(extension, keys);
                }
            }
        }
        return keys;
    }

    /// <summary>The keys a value takes as a resource, its nested extensions' too.</summary>
    private static void Taken(MarkupExtension extension, HashSet<string> keys)
    {
        if (!StackLeft.Enough())
        {
            return;
        }
        if (extension.LocalName is "ThemeResource" or "StaticResource" && extension.Argument(null) is { Nested: null, Value.Length: > 0 } key)
        {
            keys.Add(key.Value);
        }
        foreach (MarkupArgument argument in extension.Arguments)
        {
            if (argument.Nested is { } nested)
            {
                Taken(nested, keys);
            }
        }
    }
}
