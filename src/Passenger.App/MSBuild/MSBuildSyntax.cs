using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.App.MSBuild;

/// <summary>What an element of an MSBuild file is, by where it stands: the Project at the top, a structural element
/// (PropertyGroup, Target, Import), a property in a PropertyGroup, an item in an ItemGroup, a piece of an item's
/// metadata, or a task in a Target.</summary>
public enum MSBuildRole
{
    Unknown,
    Project,
    Structure,
    Property,
    Item,
    Metadata,
    Task,
}

/// <summary>A <c>$(Property)</c>, <c>@(Item)</c> or <c>%(Metadata)</c> in a value: its sigil, the name (a metadata's
/// after its item type, <c>%(Compile.Link)</c>) and where the name is written.</summary>
public readonly record struct MSBuildReference(char Sigil, string Name, TextRange NameRange, string? ItemType);

/// <summary>Reading an MSBuild file's markup (Passenger.Xaml's tolerant <see cref="XmlTree"/>, which reads one mid-edit):
/// each element's role, the references in values, and the attributes whose values name targets, paths or packages.</summary>
public static class MSBuildSyntax
{
    /// <summary>Items whose Include names a package, whose Version or VersionOverride is one of its versions.</summary>
    public static readonly HashSet<string> PackageItems = new(StringComparer.OrdinalIgnoreCase) { "PackageReference", "PackageVersion", "GlobalPackageReference", "PackageDownload" };

    public static MSBuildRole RoleOf(XmlElement element)
    {
        if (element.Parent is not { } parent)
        {
            return element.LocalName == "Project" ? MSBuildRole.Project : MSBuildRole.Unknown;
        }
        switch (parent.LocalName)
        {
            case "PropertyGroup":
                return MSBuildRole.Property;
            case "ItemGroup" or "ItemDefinitionGroup":
                return MSBuildRole.Item;
            case "Target":
                return element.LocalName is "PropertyGroup" or "ItemGroup" or "OnError" ? MSBuildRole.Structure : MSBuildRole.Task;
            case "ProjectExtensions" or "ParameterGroup":
                return MSBuildRole.Unknown;
        }
        return RoleOf(parent) switch
        {
            MSBuildRole.Item => MSBuildRole.Metadata,
            MSBuildRole.Task => element.LocalName == "Output" ? MSBuildRole.Structure : MSBuildRole.Unknown,
            MSBuildRole.Property or MSBuildRole.Metadata or MSBuildRole.Unknown => MSBuildRole.Unknown,
            _ => MSBuildRole.Structure,
        };
    }

    /// <summary>Whether a character may be in a property's, item type's or metadata's name.</summary>
    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    /// <summary>The reference whose name holds an offset of a value (the text between <paramref name="start"/> and
    /// <paramref name="end"/>), the caret just after its name included; null outside any.</summary>
    public static MSBuildReference? ReferenceAt(string text, int start, int end, int offset)
    {
        if (offset < start || offset > end)
        {
            return null;
        }
        for (int at = Math.Min(offset, end) - 1; at > start; at--)
        {
            char c = text[at];
            if (c == '(' && text[at - 1] is '$' or '@' or '%')
            {
                int nameStart = at + 1;
                int nameEnd = nameStart;
                while (nameEnd < end && (IsNameChar(text[nameEnd]) || (text[at - 1] == '%' && text[nameEnd] == '.')))
                {
                    nameEnd++;
                }
                if (offset > nameEnd)
                {
                    return null;
                }
                string name = text[nameStart..nameEnd];
                string? type = null;
                if (text[at - 1] == '%' && name.IndexOf('.') is var dot and >= 0)
                {
                    type = name[..dot];
                    nameStart += dot + 1;
                    name = name[(dot + 1)..];
                }
                return new MSBuildReference(text[at - 1], name, TextRange.FromBounds(nameStart, nameEnd), type);
            }
            if (!IsNameChar(c) && !(c == '.' && at > 0))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The attributes whose values list targets by name, by the element they are on.</summary>
    public static bool NamesTargets(XmlElement element, XmlAttribute attribute) => (element.LocalName, attribute.Name) switch
    {
        ("Target", "DependsOnTargets" or "BeforeTargets" or "AfterTargets" or "Name") => true,
        ("Project", "DefaultTargets" or "InitialTargets") => true,
        ("CallTarget", "Targets") => true,
        ("OnError", "ExecuteTargets") => true,
        ("MSBuild", "Targets") => true,
        _ => false,
    };

    /// <summary>The entry of a semicolon-separated list (targets, frameworks) that holds an offset: its text and range.</summary>
    public static (string Text, TextRange Range) EntryAt(string text, int start, int end, int offset)
    {
        int from = offset;
        while (from > start && text[from - 1] != ';')
        {
            from--;
        }
        int to = offset;
        while (to < end && text[to] != ';')
        {
            to++;
        }
        while (from < to && char.IsWhiteSpace(text[from]))
        {
            from++;
        }
        while (to > from && char.IsWhiteSpace(text[to - 1]))
        {
            to--;
        }
        return (text[from..to], TextRange.FromBounds(from, Math.Max(from, to)));
    }

    /// <summary>An attribute's value as written, or a metadata element's text, of an element: <c>Version="1.0"</c> or
    /// <c>&lt;Version&gt;1.0&lt;/Version&gt;</c>.</summary>
    public static string? ValueOf(XmlElement element, string name, string text)
    {
        if (element.Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { HasValue: true } attribute)
        {
            return attribute.Value;
        }
        return element.Children.FirstOrDefault(c => c.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } child ? TextOf(child, text) : null;
    }

    /// <summary>An element's text content, trimmed.</summary>
    public static string TextOf(XmlElement element, string text)
    {
        TextRange content = element.Content;
        return content.Length == 0 ? string.Empty : XmlEntities.DecodeText(text.Substring(content.Start, content.Length)).Trim();
    }

    /// <summary>Whether a value has MSBuild expressions in it, which only the evaluation can read.</summary>
    public static bool IsExpression(string value) => value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal)
        || value.Contains("%(", StringComparison.Ordinal);
}
