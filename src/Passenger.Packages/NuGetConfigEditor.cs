using System.Text;
using System.Xml;
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
    private const string Sources = "packageSources";
    private const string Disabled = "disabledPackageSources";
    private const string Credentials = "packageSourceCredentials";

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
            while (text.Section("packageSourceMapping")?.Elements("packageSource").FirstOrDefault(e => (string?)e.Attribute("key") == name) is { } mapped)
            {
                text.SetAttribute(mapped, "key", feed.Name);
            }
        }
        Credential(text, feed, name);
        Enable(text, feed.Name, feed.Enabled);
    });

    /// <summary>Takes a feed out of the file, with what the file says of it elsewhere.</summary>
    public static void Remove(string path, string name) => Change(path, text =>
    {
        while ((Child(text, Sources, name) ?? Child(text, Disabled, name) ?? Held(text, name)) is { } element)
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
            text.AddChild(Disabled, $"<add key=\"{Escape(name)}\" value=\"true\" />");
        }
    }

    /// <summary>The add element a section has for a name, as NuGet matches keys (ignoring case).</summary>
    private static XElement? Child(XmlText text, string section, string name) =>
        text.Section(section)?.Elements("add").FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), name, StringComparison.OrdinalIgnoreCase));

    private static XElement? Held(XmlText text, string name) => text.Section(Credentials)?.Element(XmlConvert.EncodeLocalName(name));

    /// <summary>Writes a feed's credentials, as the dialog left them, under its name in packageSourceCredentials.</summary>
    private static void Credential(XmlText text, FeedEdit feed, string? oldName)
    {
        XElement? held = Held(text, feed.Name) ?? (oldName is null ? null : Held(text, oldName));
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
            password = (secret.IsPasswordClearText ? "ClearTextPassword" : "Password", secret.PasswordText);
        }
        else if (held?.Elements("add").FirstOrDefault(a => (string?)a.Attribute("key") is "Password" or "ClearTextPassword") is { } kept)
        {
            password = ((string)kept.Attribute("key")!, (string?)kept.Attribute("value") ?? string.Empty);
        }
        string indent = text.ChildIndent(Credentials);
        string name = XmlConvert.EncodeLocalName(feed.Name);
        List<string> values = [$"<add key=\"Username\" value=\"{Escape(feed.Username)}\" />"];
        if (password is { } secretValue)
        {
            values.Add($"<add key=\"{secretValue.Key}\" value=\"{Escape(secretValue.Value)}\" />");
        }
        foreach (XElement other in held?.Elements("add").Where(a => (string?)a.Attribute("key") is not ("Username" or "Password" or "ClearTextPassword")) ?? [])
        {
            values.Add($"<add key=\"{Escape((string?)other.Attribute("key") ?? string.Empty)}\" value=\"{Escape((string?)other.Attribute("value") ?? string.Empty)}\" />");
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

    /// <summary>A feed's add element: its key and value, what else the element had, and its insecure switch.</summary>
    private static string FeedElement(FeedEdit feed, XElement? was)
    {
        StringBuilder element = new($"<add key=\"{Escape(feed.Name)}\" value=\"{Escape(feed.Url)}\"");
        foreach (XAttribute other in was?.Attributes().Where(a => a.Name.LocalName is not ("key" or "value" or "allowInsecureConnections")) ?? [])
        {
            element.Append($" {other.Name.LocalName}=\"{Escape(other.Value)}\"");
        }
        if (feed.AllowInsecure)
        {
            element.Append(" allowInsecureConnections=\"true\"");
        }
        return element.Append(" />").ToString();
    }

    private static string Escape(string value) => XmlText.Escape(value);

    /// <summary>Reads the file, makes the changes on its text, and writes it back as it came through
    /// <see cref="FileText.Save"/>. A file not there yet, or empty, is made with NuGet's empty configuration.</summary>
    private static void Change(string path, Action<XmlText> change)
    {
        FileText? file = File.Exists(path) ? FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file") : null;
        XmlText text = new(file is { Text.Length: > 0 } read ? read.Text : "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n</configuration>\n");
        change(text);
        FileText.Save(path, text.Text, file?.Encoding ?? new UTF8Encoding(false), file?.HasBom ?? false, file?.CrBreaks ?? false);
    }
}
