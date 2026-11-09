using System.Xml;
using System.Xml.Linq;
using NuGet.Configuration;

namespace Passenger.Packages;

/// <summary>nuget.config's names, and the one way Passenger matches them, reading the file or editing it, as NuGet does: a
/// section by its name as written; an element's name (add, clear) and an attribute's in any letter case; a feed's key, and
/// the keys a feed's credentials hold, in any letter case; and a feed's credentials as the element its name encodes to, in
/// any letter case, the first of them, since NuGet keeps one such element of a file and edits it whatever its case.</summary>
internal static class NuGetConfigNames
{
    public static readonly string PackageSources = ConfigurationConstants.PackageSources;
    public static readonly string DisabledPackageSources = ConfigurationConstants.DisabledPackageSources;
    public static readonly string Credentials = ConfigurationConstants.CredentialsSectionName;
    public static readonly string Mapping = ConfigurationConstants.PackageSourceMapping;
    public static readonly string Config = ConfigurationConstants.Config;
    public static readonly string Key = ConfigurationConstants.KeyAttribute;
    public static readonly string Value = ConfigurationConstants.ValueAttribute;
    public static readonly string AllowInsecureConnections = ConfigurationConstants.AllowInsecureConnections;
    public static readonly string Username = ConfigurationConstants.UsernameToken;
    public static readonly string Password = ConfigurationConstants.PasswordToken;
    public static readonly string ClearTextPassword = ConfigurationConstants.ClearTextPasswordToken;
    public static readonly string GlobalPackagesFolder = ConfigurationConstants.GlobalPackagesFolder;
    public static readonly string RepositoryPath = ConfigurationConstants.RepositoryPath;

    /// <summary>A mapping's element for a feed, under <see cref="Mapping"/>.</summary>
    public const string MappedSource = "packageSource";

    private const string AddElement = "add";
    private const string ClearElement = "clear";

    /// <summary>A section of the file, by its name as written, whatever namespace the file gives its elements.</summary>
    public static XElement? Section(XElement root, string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>Whether an element is one of a name (add, clear, packageSource) in any letter case.</summary>
    public static bool Is(XElement element, string name) => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A section's add elements, in their order.</summary>
    public static IEnumerable<XElement> Adds(XElement? section) => section?.Elements().Where(e => Is(e, AddElement)) ?? [];

    /// <summary>Whether a section holds a clear element.</summary>
    public static bool Clears(XElement? section) => section?.Elements().Any(e => Is(e, ClearElement)) == true;

    /// <summary>An element's attribute of a name in any letter case.</summary>
    public static XAttribute? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an attribute's name is one of those given, in any letter case.</summary>
    public static bool Named(XAttribute attribute, params ReadOnlySpan<string> names)
    {
        foreach (string name in names)
        {
            if (attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static string? KeyOf(XElement element) => Attribute(element, Key)?.Value;

    public static string? ValueOf(XElement element) => Attribute(element, Value)?.Value;

    /// <summary>Whether two keys (feeds' names, a setting's, a credential's) are one.</summary>
    public static bool SameKey(string? key, string? other) => string.Equals(key, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>A section's add element for a key, the first.</summary>
    public static XElement? Add(XElement? section, string key) => Adds(section).FirstOrDefault(e => SameKey(KeyOf(e), key));

    /// <summary>The elements of a credentials section holding a feed's credentials, in their order.</summary>
    public static IEnumerable<XElement> CredentialsOf(XElement? section, string feed) =>
        section?.Elements().Where(e => SameKey(XmlConvert.DecodeName(e.Name.LocalName), feed)) ?? [];

    /// <summary>The name of the element holding a feed's credentials.</summary>
    public static string CredentialsElement(string feed) => XmlConvert.EncodeLocalName(feed);

    public static bool IsUsername(string? key) => SameKey(key, Username);

    /// <summary>Whether a credential's key holds its password, encrypted or in clear text.</summary>
    public static bool IsPassword(string? key) => SameKey(key, Password) || SameKey(key, ClearTextPassword);

    /// <summary>A password key as NuGet writes it: ClearTextPassword for one in clear text, else Password.</summary>
    public static string PasswordKey(string key) => SameKey(key, ClearTextPassword) ? ClearTextPassword : Password;

    /// <summary>Whether a switch's value is on, as NuGet reads one (<c>true</c> in any letter case).</summary>
    public static bool IsOn(string? value) => bool.TryParse(value, out bool on) && on;
}
