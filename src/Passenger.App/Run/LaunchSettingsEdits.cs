using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A run configuration's changes written into the launch profile it was made from, in its project's
/// <c>Properties/launchSettings.json</c>, or one of the folder's own added as a new profile (the file made as dotnet new
/// makes it, if there is none), as text: a value replaced where it stands, a property or profile added after the last,
/// indented as they are (on their line, for one written on one), one taken out with its line, so the other profiles and
/// properties, the comments and trailing commas dotnet run's reader takes, stay byte for byte as they were.</summary>
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

    /// <summary>Where a new launchSettings.json says its schema, as dotnet new writes it.</summary>
    private const string Schema = "https://json.schemastore.org/launchsettings.json";

    /// <summary>The name a configuration of the folder's own is saved under as a profile of its project: its own, less the
    /// project's name the configurations generated from its profiles begin with ("Hello: Staging" is Staging).</summary>
    public static string ProfileName(ProjectModel project, RunConfiguration configuration)
    {
        string generated = RunConfiguration.NameOf(project, string.Empty);
        string name = configuration.Name.Trim();
        return name.StartsWith(generated, StringComparison.OrdinalIgnoreCase) ? name[generated.Length..].Trim() : name;
    }

    /// <summary>The profile a configuration of a project run here is saved as, its values as dotnet run and Passenger read them:
    /// its program, arguments, working folder (none for the project's), browser and variables, ASP.NET Core's addresses as
    /// the <c>applicationUrl</c> its browser's address is made from, when it has one. What a profile has no place for (a
    /// framework, the console, building first, the output folder to run in) is left for the folder to keep.</summary>
    public static JsonObject ProfileOf(RunConfiguration configuration)
    {
        bool project = configuration.Program.Length == 0;
        JsonObject profile = new() { ["commandName"] = project ? LaunchProfile.ProjectCommand : LaunchProfile.ExecutableCommand };
        if (!project)
        {
            profile["executablePath"] = configuration.Program;
        }
        if (configuration.Arguments.Length > 0)
        {
            profile["commandLineArgs"] = configuration.Arguments;
        }
        if (configuration.WorkingDirectory.Trim() is { Length: > 0 } folder && folder != "$(ProjectDir)")
        {
            profile["workingDirectory"] = folder;
        }
        string? urls = project && configuration.BrowserUrl.Length > 0 && configuration.Environment.GetValueOrDefault("ASPNETCORE_URLS") is { } given
            && !string.IsNullOrWhiteSpace(given) ? given : null;
        if (configuration.LaunchBrowser)
        {
            profile["launchBrowser"] = true;
        }
        LaunchProfile made = new(string.Empty, profile["commandName"]!.GetValue<string>(), null, null, null, new Dictionary<string, string>(), urls, configuration.LaunchBrowser, null);
        if (configuration.BrowserUrl.Length > 0 && LaunchUrl(made, configuration.BrowserUrl) is { Length: > 0 } url)
        {
            profile["launchUrl"] = url;
        }
        if (urls is not null)
        {
            profile["applicationUrl"] = urls;
        }
        IEnumerable<KeyValuePair<string, string>> variables = configuration.Environment.Where(v => urls is null || v.Key != "ASPNETCORE_URLS");
        if (variables.Any())
        {
            profile["environmentVariables"] = new JsonObject(variables.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)JsonValue.Create(v.Value))));
        }
        return profile;
    }

    /// <summary>Adds a profile to a project's launchSettings.json as a save writes (<see cref="FileText.Save"/>), its text
    /// changed as <see cref="Added"/> changes it; with none there, the file is made, its folder too, as dotnet new makes one
    /// (its schema, then its profiles) with the line breaks and charset its .editorconfig asks. Null once written, else why not.</summary>
    public static string? Add(string path, string profile, JsonObject values)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EditorConfig conventions = EditorConfig.For(path);
            FileEncoding charset = conventions.Charset ?? FileEncoding.Utf8;
            LineSeparator separator = conventions.EndOfLine ?? LineSeparator.LF;
            string made = JsonText.Laid(new JsonObject { ["$schema"] = Schema, ["profiles"] = new JsonObject { [profile] = values.DeepClone() } }) + "\n";
            FileText.Save(path, LineEndings.In(made, separator.Held()), charset.Encoding, charset.Bom, separator == LineSeparator.CR);
            return null;
        }
        FileText file = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
        (string? text, string? problem) = Added(file.Text, profile, values, Path.GetFileName(path));
        if (text is null)
        {
            return problem;
        }
        FileText.Save(path, text, file.Encoding, file.HasBom, file.CrBreaks);
        return null;
    }

    /// <summary>The text with a profile added after its last, laid out as the file lays out its profiles, every other
    /// character as it was (a file with no <c>profiles</c> given them after its last property); null with why when it does
    /// not read as JSON or has a profile of that name, its letter case aside, as dotnet run tells them apart.</summary>
    public static (string? Text, string? Problem) Added(string text, string profile, JsonObject values, string named)
    {
        JsonText.Node root;
        try
        {
            root = new JsonText.Reader(text).Document();
        }
        catch (FormatException unreadable)
        {
            return (null, $"{named} does not read as JSON: {unreadable.Message}");
        }
        if (root.Kind != JsonText.Kind.Object)
        {
            return (null, $"{named} does not hold an object");
        }
        JsonText.Layout layout = new(text, JsonText.Unit(text, root));
        if (root.Member("profiles")?.Value is not { } profiles)
        {
            return (layout.Add(root, "profiles", new JsonObject { [profile] = values.DeepClone() }), null);
        }
        if (profiles.Kind != JsonText.Kind.Object)
        {
            return (null, $"{named}'s profiles are not an object");
        }
        if (profiles.Members.FirstOrDefault(m => string.Equals(m.Name, profile, StringComparison.OrdinalIgnoreCase)) is { } taken)
        {
            return (null, $"{named} has a profile named {taken.Name} already");
        }
        return (layout.Add(profiles, profile, values), null);
    }

    /// <summary>Writes the changes into a profile of a launchSettings.json as a save writes (<see cref="FileText.Save"/>):
    /// its encoding, byte order mark and line breaks as they were. Null once written, else why not.</summary>
    public static string? Write(string path, string profile, IReadOnlyList<Change> changes)
    {
        FileText file = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
        (string? text, string? problem) = Changed(file.Text, profile, changes, Path.GetFileName(path));
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
    public static (string? Text, string? Problem) Changed(string text, string profile, IReadOnlyList<Change> changes, string named)
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
                return (null, $"{named} does not read as JSON: {unreadable.Message}");
            }
            if (root.Member("profiles")?.Value is not { Kind: JsonText.Kind.Object } profiles || profiles.Member(profile)?.Value is not { Kind: JsonText.Kind.Object } found)
            {
                return (null, $"{named} has no profile {profile}");
            }
            JsonText.Layout layout = new(text, JsonText.Unit(text, root));
            text = change.Entry is null ? layout.Set(found, change.Property, change.Value) : layout.SetEntry(found, change.Property, change.Entry, change.Value);
        }
        return (text, null);
    }
}
