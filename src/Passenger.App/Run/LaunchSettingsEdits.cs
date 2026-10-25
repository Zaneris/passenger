using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A run configuration's changes written into the launch profile it was made from, in its project's
/// <c>Properties/launchSettings.json</c>, as text: a value replaced where it stands, a property added after the profile's
/// last, indented as they are (on their line, for a profile written on one), one taken out with its line, so the other
/// profiles and properties, the comments and trailing commas dotnet run's reader takes, stay byte for byte as they were.</summary>
public static class LaunchSettingsEdits
{
    /// <summary>One value of a profile: a property (<c>commandLineArgs</c>) or, given <see cref="Entry"/>, one entry of an
    /// object property (a variable of <c>environmentVariables</c>); a null value takes it out.</summary>
    public sealed record Change(string Property, string? Entry, JsonNode? Value);

    /// <summary>What of a generated configuration's changes from its source (<paramref name="edited"/> over
    /// <paramref name="source"/>) its launch profile can hold, as changes to the profile. What the file has no place for (a
    /// target framework, the console, building first, a program for a profile that runs its project, a variable the
    /// profile's application URL or HTTPS port gives) is left out, to stay kept with the folder.</summary>
    public static IReadOnlyList<Change> For(ProjectModel project, LaunchProfile profile, RunConfiguration source, RunConfiguration edited)
    {
        List<Change> changes = [];
        bool container = profile.RunsInContainer;
        if (!container && edited.Arguments != source.Arguments)
        {
            changes.Add(new Change("commandLineArgs", null, edited.Arguments.Length > 0 ? JsonValue.Create(edited.Arguments) : null));
        }
        if (!container && edited.Program != source.Program && edited.Program.Length > 0 && profile.Is(LaunchProfile.ExecutableCommand))
        {
            changes.Add(new Change("executablePath", null, JsonValue.Create(edited.Program)));
        }
        if (!container && edited.WorkingDirectory != source.WorkingDirectory && edited.WorkingDirectory.Trim().Length > 0)
        {
            changes.Add(new Change("workingDirectory", null, JsonValue.Create(edited.WorkingDirectory)));
        }
        if (edited.LaunchBrowser != source.LaunchBrowser)
        {
            changes.Add(new Change("launchBrowser", null, JsonValue.Create(edited.LaunchBrowser)));
        }
        if (edited.BrowserUrl != source.BrowserUrl && LaunchUrl(profile, edited.BrowserUrl) is { } url)
        {
            changes.Add(new Change("launchUrl", null, url.Length > 0 ? JsonValue.Create(url) : null));
        }
        foreach ((string name, string value) in edited.Environment)
        {
            if (!source.Environment.TryGetValue(name, out string? was) || was != value)
            {
                changes.Add(new Change("environmentVariables", name, JsonValue.Create(value)));
            }
        }
        foreach (string name in source.Environment.Keys.Where(n => !edited.Environment.ContainsKey(n) && profile.EnvironmentVariables.ContainsKey(n)))
        {
            changes.Add(new Change("environmentVariables", name, null));
        }
        if (container && edited.RunArguments != source.RunArguments && RunWords(project, profile, edited.RunArguments) is { } words)
        {
            string key = profile.RunArgumentsKey ?? LaunchProfile.RunArgumentsKeys[profile.Is(LaunchProfile.DockerCommand) ? 0 : 1];
            changes.Add(new Change(key, null, words.Length > 0 ? JsonValue.Create(words) : null));
        }
        return changes;
    }

    /// <summary>The <c>launchUrl</c> that makes a browser address: relative to the profile's first application URL (a
    /// container's published address) where it begins so, empty for that address itself, else as written when it is a
    /// whole address; null when the profile cannot make it.</summary>
    private static string? LaunchUrl(LaunchProfile profile, string url)
    {
        string? root = profile.RunsInContainer ? Containers.ContainerConfigurations.ServiceUrl
            : profile.ApplicationUrl?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (root is not null && (url == root || url == root.TrimEnd('/') + "/"))
        {
            return string.Empty;
        }
        if (root is not null && url.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            return url[(root.TrimEnd('/').Length + 1)..];
        }
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? whole) && whole.Scheme is "http" or "https"
            || (profile.RunsInContainer && url.Contains('{', StringComparison.Ordinal)) ? url : null;
    }

    /// <summary>A container profile's own <c>docker run</c> words: what follows the words its ports and env files make,
    /// which the configuration's begin with; null when they no longer begin so.</summary>
    private static string? RunWords(ProjectModel project, LaunchProfile profile, string edited)
    {
        string made = Containers.ContainerConfigurations.From(project, profile with { RunArguments = null }).RunArguments;
        return made.Length == 0 ? edited.Trim()
            : edited == made ? string.Empty
            : edited.StartsWith(made + " ", StringComparison.Ordinal) ? edited[(made.Length + 1)..].Trim()
            : null;
    }

    /// <summary>Writes the changes into a profile of a launchSettings.json as a save writes (<see cref="FileText.Save"/>):
    /// its encoding, byte order mark and line breaks as they were. Null once written, else why not.</summary>
    public static string? Write(string path, string profile, IReadOnlyList<Change> changes)
    {
        FileText file = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
        (string? text, string? problem) = Changed(file.Text, profile, changes);
        if (text is null)
        {
            return problem;
        }
        if (text != file.Text)
        {
            FileText.Save(path, text, file.Encoding, file.HasBom, file.CrBreaks);
        }
        return null;
    }

    /// <summary>The text with the profile's values changed, every other character as it was; null with why when it does not
    /// read as JSON or has no such profile.</summary>
    public static (string? Text, string? Problem) Changed(string text, string profile, IReadOnlyList<Change> changes)
    {
        foreach (Change change in changes)
        {
            JsonText.Node root;
            try
            {
                root = new JsonText.Reader(text).Document();
            }
            catch (FormatException unreadable)
            {
                return (null, $"launchSettings.json does not read as JSON: {unreadable.Message}");
            }
            if (root.Member("profiles")?.Value is not { Kind: JsonText.Kind.Object } profiles || profiles.Member(profile)?.Value is not { Kind: JsonText.Kind.Object } found)
            {
                return (null, $"launchSettings.json has no profile {profile}");
            }
            JsonText.Layout layout = new(text, JsonText.Unit(text, root));
            text = change.Entry is null ? layout.Set(found, change.Property, change.Value) : layout.SetEntry(found, change.Property, change.Entry, change.Value);
        }
        return (text, null);
    }
}
