using System.Text;
using System.Xml.Linq;
using NuGet.Configuration;
using Passenger.Text;
using XmlText = Passenger.Workspace.XmlText;

namespace Passenger.Packages;

/// <summary>A feed as the Sources tab's dialog gives it: its name and URL, whether it is enabled and may use HTTP, and
/// the credentials to keep for it (a password of null or empty keeps the one the file has; no user name takes the
/// credentials out).</summary>
public sealed record FeedEdit(string Name, string Url, bool Enabled, bool AllowInsecure, string? Username, string? Password)
{
    /// <summary>Leaves the password out, so a record printed never shows it.</summary>
    public override string ToString() => $"{Name} ({Url})";
}

/// <summary>Changes one nuget.config: a feed added, edited, removed, enabled or disabled, and its credentials, through
/// <see cref="XmlText"/>, so every other character of the file stays as it was (its declaration or none, comments,
/// quotes, byte order mark, line ends and permissions). A password goes in clear
/// text but on Windows, where NuGet encrypts it for the user.</summary>
public static class NuGetConfigEditor
{
    private static readonly string Sources = NuGetConfigNames.PackageSources;
    private static readonly string Disabled = NuGetConfigNames.DisabledPackageSources;
    private static readonly string Credentials = NuGetConfigNames.Credentials;

    public static void Add(string path, FeedEdit feed) => Change(path, text =>
    {
        text.AddChild(Sources, FeedElement(feed, null));
        Credential(text, feed, null);
        Enable(text, feed.Name, feed.Enabled);
    });

    /// <summary>Changes a feed the file names, under its old name; a new name follows it into the file's other sections.</summary>
    public static void Update(string path, string name, FeedEdit feed) => Change(path, text =>
    {
        XElement element = Child(text, Sources, name) ?? throw new InvalidOperationException($"{Path.GetFileName(path)} names no feed {name}");
        text.Replace(element, FeedElement(feed, element));
        if (!feed.Name.Equals(name, StringComparison.Ordinal))
        {
            if (Child(text, Disabled, name) is { } disabled)
            {
                text.Remove(disabled);
            }
            while (Mapped(text, name, feed.Name) is { } mapped)
            {
                text.SetAttribute(mapped, NuGetConfigNames.Attribute(mapped, NuGetConfigNames.Key)!.Name.LocalName, feed.Name);
            }
        }
        Credential(text, feed, name);
        Enable(text, feed.Name, feed.Enabled);
    });

    /// <summary>Takes a feed out of the file, with what the file says of it elsewhere.</summary>
    public static void Remove(string path, string name) => Change(path, text =>
    {
        while ((Child(text, Sources, name) ?? Child(text, Disabled, name) ?? Held(text, name).FirstOrDefault()) is { } element)
        {
            text.Remove(element);
        }
    });

    /// <summary>Disables a feed in this file, or takes this file's disabling of it away.</summary>
    public static void SetEnabled(string path, string name, bool enabled) => Change(path, text => Enable(text, name, enabled));

    private static void Enable(XmlText text, string name, bool enabled)
    {
        XElement? disabled = Child(text, Disabled, name);
        if (enabled && disabled is not null)
        {
            text.Remove(disabled);
        }
        else if (!enabled && disabled is null)
        {
            text.AddChild(Disabled, Pair(name, "true"));
        }
    }

    private static XElement? Child(XmlText text, string section, string name) => NuGetConfigNames.Add(text.Section(section), name);

    /// <summary>The mapping's element for a feed, not yet under its new name.</summary>
    private static XElement? Mapped(XmlText text, string name, string renamed) => text.Section(NuGetConfigNames.Mapping)?.Elements()
        .FirstOrDefault(e => NuGetConfigNames.Is(e, NuGetConfigNames.MappedSource) && NuGetConfigNames.KeyOf(e) is { } key
            && NuGetConfigNames.SameKey(key, name) && key != renamed);

    private static IEnumerable<XElement> Held(XmlText text, string name) => NuGetConfigNames.CredentialsOf(text.Section(Credentials), name);

    /// <summary>Writes a feed's credentials, as the dialog left them, into the one element of packageSourceCredentials holding
    /// them, named as the feed is; any other element holding them is taken out.</summary>
    private static void Credential(XmlText text, FeedEdit feed, string? oldName)
    {
        IEnumerable<XElement> Holding() => Held(text, feed.Name).Concat(oldName is null ? [] : Held(text, oldName)).Distinct();
        while (Holding().Skip(1).LastOrDefault() is { } extra)
        {
            text.Remove(extra);
        }
        XElement? held = Holding().FirstOrDefault();
        if (string.IsNullOrEmpty(feed.Username))
        {
            if (held is not null)
            {
                text.Remove(held);
            }
            return;
        }
        (string Key, string Value)? password = null;
        if (feed.Password is { Length: > 0 } given)
        {
            PackageSourceCredential secret = PackageSourceCredential.FromUserInput(feed.Name, feed.Username, given,
                storePasswordInClearText: !OperatingSystem.IsWindows(), validAuthenticationTypesText: null);
            password = (secret.IsPasswordClearText ? NuGetConfigNames.ClearTextPassword : NuGetConfigNames.Password, secret.PasswordText);
        }
        else if (NuGetConfigNames.Adds(held).FirstOrDefault(a => NuGetConfigNames.IsPassword(NuGetConfigNames.KeyOf(a))) is { } kept)
        {
            password = (NuGetConfigNames.PasswordKey(NuGetConfigNames.KeyOf(kept)!), NuGetConfigNames.ValueOf(kept) ?? string.Empty);
        }
        string indent = text.ChildIndent(Credentials);
        string name = NuGetConfigNames.CredentialsElement(feed.Name);
        List<string> values = [Pair(NuGetConfigNames.Username, feed.Username)];
        if (password is { } secretValue)
        {
            values.Add(Pair(secretValue.Key, secretValue.Value));
        }
        foreach (XElement other in NuGetConfigNames.Adds(held).Where(a => !NuGetConfigNames.IsUsername(NuGetConfigNames.KeyOf(a)) && !NuGetConfigNames.IsPassword(NuGetConfigNames.KeyOf(a))))
        {
            values.Add(Pair(NuGetConfigNames.KeyOf(other) ?? string.Empty, NuGetConfigNames.ValueOf(other) ?? string.Empty));
        }
        string element = $"<{name}>{string.Concat(values.Select(v => text.NewLine + indent + text.Unit + v))}{text.NewLine}{indent}</{name}>";
        if (held is not null)
        {
            text.Replace(held, element);
        }
        else
        {
            text.AddChild(Credentials, element);
        }
    }

    /// <summary>An add element of a key and its value.</summary>
    private static string Pair(string key, string value) => $"<add key=\"{XmlEntities.Encode(key, '"')}\" value=\"{XmlEntities.Encode(value, '"')}\" />";

    /// <summary>A feed's add element: its key and value, what else the element had, and its insecure switch.</summary>
    private static string FeedElement(FeedEdit feed, XElement? was)
    {
        StringBuilder element = new($"<add key=\"{XmlEntities.Encode(feed.Name, '"')}\" value=\"{XmlEntities.Encode(feed.Url, '"')}\"");
        foreach (XAttribute other in was?.Attributes().Where(a => !NuGetConfigNames.Named(a, NuGetConfigNames.Key, NuGetConfigNames.Value, NuGetConfigNames.AllowInsecureConnections)) ?? [])
        {
            element.Append($" {other.Name.LocalName}=\"{XmlEntities.Encode(other.Value, '"')}\"");
        }
        if (feed.AllowInsecure)
        {
            element.Append($" {NuGetConfigNames.AllowInsecureConnections}=\"true\"");
        }
        return element.Append(" />").ToString();
    }

    /// <summary>Reads the file, makes the changes on its text, and writes it back as it came (<see cref="FileText.SaveOver"/>).
    /// A file not there yet, or empty, is made with NuGet's empty configuration, one not there as its .editorconfig asks
    /// (<see cref="FileText.SaveNew"/>).</summary>
    private static void Change(string path, Action<XmlText> change)
    {
        FileText? file = File.Exists(path) ? FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file") : null;
        XmlText text = new(file is { Text.Length: > 0 } read ? read.Text : "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n</configuration>\n");
        change(text);
        _ = file is null ? FileText.SaveNew(path, text.Text) : FileText.SaveOver(path, file, text.Text);
    }
}
