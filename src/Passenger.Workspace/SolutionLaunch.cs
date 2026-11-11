using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>What a multi-project launch profile does with one project, by Visual Studio's own words: start it under the
/// debugger, start it without, or leave it.</summary>
public enum ProjectLaunchAction
{
    Start,
    StartWithoutDebugging,
    None,
}

/// <summary>One project of a multi-project launch profile: its project file (canonical), what is done with it (null for an
/// action Passenger does not know, kept as written), and the launch profile of its own it starts in (its
/// <c>DebugTarget</c>; null or empty for its default). <see cref="Read"/> is its entry as the file held it, so what Passenger
/// does not model is written back as it was.</summary>
public sealed record SolutionLaunchProject(string Project, ProjectLaunchAction? Action, string? DebugTarget)
{
    public JsonObject? Read { get; init; }

    /// <summary>The <c>Action</c> as written, kept for one Passenger does not know.</summary>
    public string? ActionWritten { get; init; }
}

/// <summary>A multi-project launch profile of a solution, as Visual Studio keeps it beside the solution: its name, its
/// projects in the order they start, and whether it is the team's (<c>.slnLaunch</c>) or the person's own
/// (<c>.slnLaunch.user</c>). <see cref="Read"/> is it as its file held it, and <see cref="Index"/> its place there (-1 for
/// one made here).</summary>
public sealed record SolutionLaunchProfile(string Name, IReadOnlyList<SolutionLaunchProject> Projects, bool Shared)
{
    public JsonObject? Read { get; init; }

    public int Index { get; init; } = -1;
}

/// <summary>Visual Studio's multi-project launch profiles: a JSON array of profiles in <c>Name.slnLaunch</c> beside the
/// solution (<c>Name.sln</c> or <c>Name.slnx</c>) for the team, and in <c>Name.slnLaunch.user</c> for the person, each a
/// <c>Name</c> and its <c>Projects</c>, each project a <c>Path</c> relative to the solution's folder (written with '\'),
/// an <c>Action</c> and, where it starts in a launch profile of its own, a <c>DebugTarget</c>. Read as Visual Studio's
/// reader takes JSON (comments and trailing commas passed over), and written back as text where each profile stands
/// (<see cref="JsonText"/>): a profile unchanged keeps its every character, and what Passenger does not model keeps its
/// place in the one changed.</summary>
public static class SolutionLaunch
{
    public const string Extension = ".slnLaunch";

    public const string UserExtension = ".slnLaunch.user";

    /// <summary>The team's file of a solution's profiles: its name with <c>.slnLaunch</c> for <c>.sln</c> or <c>.slnx</c>.</summary>
    public static string SharedFile(string solution) => Path.ChangeExtension(solution, Extension);

    /// <summary>The person's own file of a solution's profiles, beside the team's.</summary>
    public static string UserFile(string solution) => SharedFile(solution) + ".user";

    /// <summary>Whether a path is a solution's file of profiles, the team's or the person's.</summary>
    public static bool IsFile(ReadOnlySpan<char> path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(UserExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The profiles of a solution, the team's file's then the person's, each with the problem of a file that does not
    /// read; none for files not there. On any thread.</summary>
    public static (IReadOnlyList<SolutionLaunchProfile> Profiles, IReadOnlyList<string> Problems) Read(string solution)
    {
        List<SolutionLaunchProfile> profiles = [];
        List<string> problems = [];
        string folder = Path.GetDirectoryName(solution)!;
        foreach ((string file, bool shared) in (ReadOnlySpan<(string, bool)>)[(SharedFile(solution), true), (UserFile(solution), false)])
        {
            string text;
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }
                text = FileText.Load(file)?.Text ?? throw new IOException("it is not a text file");
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)} could not be read: {unread.Message}");
                continue;
            }
            (IReadOnlyList<SolutionLaunchProfile> read, string? problem) = Parse(text, folder, shared);
            profiles.AddRange(read);
            if (problem is not null)
            {
                problems.Add($"{Path.GetFileName(file)} {problem}");
            }
        }
        return (profiles, problems);
    }

    /// <summary>The profiles a file's text holds, its projects' paths read from the solution's <paramref name="folder"/>; with
    /// why where it does not read as an array of profiles. A profile without a name, or an item that is no object, is
    /// passed over; a project's path is its <c>Path</c>, else the <c>Name</c> early files wrote in its place.</summary>
    public static (IReadOnlyList<SolutionLaunchProfile> Profiles, string? Problem) Parse(string text, string folder, bool shared)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text.TrimStart('﻿'), Keys, LenientJson.Document);
        }
        catch (JsonException unreadable)
        {
            return ([], $"does not read as JSON: {unreadable.Message}");
        }
        if (root is not JsonArray items)
        {
            return ([], "does not hold a list of profiles");
        }
        List<SolutionLaunchProfile> profiles = [];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject profile || TextOf(profile, NameKey) is not { Length: > 0 } name)
            {
                continue;
            }
            List<SolutionLaunchProject> projects = [];
            foreach (JsonObject project in (profile[ProjectsKey] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((TextOf(project, PathKey) ?? TextOf(project, NameKey)) is not { Length: > 0 } written)
                {
                    continue;
                }
                string? action = TextOf(project, ActionKey);
                projects.Add(new SolutionLaunchProject(SolutionFilter.Named(written, folder), ActionOf(action), TextOf(project, TargetKey))
                {
                    Read = project,
                    ActionWritten = action,
                });
            }
            profiles.Add(new SolutionLaunchProfile(name, projects, shared) { Read = profile, Index = i });
        }
        return (profiles, null);
    }

    /// <summary>An action as written, by Visual Studio's names, their letter case aside; null for one Passenger does not know.</summary>
    public static ProjectLaunchAction? ActionOf(string? written) =>
        Enum.TryParse(written, ignoreCase: true, out ProjectLaunchAction action) && Enum.IsDefined(action) && !int.TryParse(written, out _) ? action : null;

    /// <summary>One file's text (<paramref name="text"/>, null for none yet) holding these profiles in this order, paths from
    /// the solution's <paramref name="folder"/>: one read from it (its <see cref="SolutionLaunchProfile.Index"/>) unchanged kept,
    /// one changed written in its place, one gone taken out, one new put last, an item that is no profile kept, laid out as
    /// Visual Studio lays it out; written anew for those read in another order. Null with why for a text that is no list of
    /// profiles, left for the person to mend.</summary>
    public static (string? Text, string? Problem) Written(string? text, IReadOnlyList<SolutionLaunchProfile> profiles, string folder)
    {
        List<JsonObject> values = [.. profiles.Select(p => ValueOf(p, folder))];
        string eol = text is not null && text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (text is null || text.Trim('\uFEFF').Trim().Length == 0)
        {
            return (JsonText.Laid(new JsonArray([.. values]), Unit, eol), null);
        }
        JsonText.Node root;
        try
        {
            root = new JsonText.Reader(text).Document();
        }
        catch (FormatException unreadable)
        {
            return (null, $"does not read as JSON: {unreadable.Message}");
        }
        if (root.Kind != JsonText.Kind.Array)
        {
            return (null, "does not hold a list of profiles");
        }
        Dictionary<int, JsonObject> byIndex = [];
        List<int> read = [];
        bool[] taken = new bool[profiles.Count];
        for (int p = 0; p < profiles.Count; p++)
        {
            if (profiles[p] is { Read: not null, Index: >= 0 } was && was.Index < root.Items.Count && byIndex.TryAdd(was.Index, values[p]))
            {
                read.Add(was.Index);
                taken[p] = true;
            }
        }
        // A new profile goes after the last read: one before a profile read, or those read in another order, is another order.
        bool inOrder = read.SequenceEqual(read.Order()) && taken.SkipWhile(t => t).All(t => !t);
        if (!inOrder)
        {
            return (JsonText.Laid(new JsonArray([.. values]), JsonText.ItemUnit(text, root), eol), null);
        }
        string written = text;
        string unit = JsonText.ItemUnit(written, root);
        // From the last item to the first, each read again where it stands now: those before it are where they were.
        for (int i = root.Items.Count - 1; i >= 0; i--)
        {
            JsonText.Node now = new JsonText.Reader(written).Document();
            JsonText.Layout layout = new(written, unit);
            JsonText.Node item = now.Items[i];
            string itemText = written[item.Start..item.End];
            if (!byIndex.TryGetValue(i, out JsonObject? value))
            {
                if (IsProfile(itemText))
                {
                    written = layout.Remove(now, i);
                }
            }
            else if (!Holds(itemText, value))
            {
                written = layout.RewriteLaid(item, value);
            }
        }
        for (int p = read.Count; p < profiles.Count; p++)
        {
            // Those read are the first, in order: the rest are new here.
            JsonText.Node now = new JsonText.Reader(written).Document();
            written = new JsonText.Layout(written, unit).AppendLaid(now, values[p]);
        }
        return (written, null);
    }

    /// <summary>One level of indentation of a file written anew, as Visual Studio writes it.</summary>
    private const string Unit = "  ";

    /// <summary>Whether a profile's text as it stands holds what it is to hold now.</summary>
    private static bool Holds(string itemText, JsonObject value) => ItemOf(itemText) is { } read && JsonNode.DeepEquals(read, value);

    /// <summary>Whether an item is one the file's reader takes for a profile (<see cref="Parse"/>): an object with a name.</summary>
    private static bool IsProfile(string itemText) => ItemOf(itemText) is JsonObject item && TextOf(item, NameKey) is { Length: > 0 };

    private static JsonNode? ItemOf(string itemText)
    {
        try
        {
            return JsonNode.Parse(itemText, Keys, LenientJson.Document);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A profile as its file is to hold it: what it was read as, with its name and projects as they are now; a
    /// project unchanged keeps its entry, a changed one its members Passenger does not model.</summary>
    private static JsonObject ValueOf(SolutionLaunchProfile profile, string folder)
    {
        JsonObject value = profile.Read is { } read ? (JsonObject)read.DeepClone() : [];
        Set(value, NameKey, profile.Name);
        JsonArray projects = [];
        foreach (SolutionLaunchProject project in profile.Projects)
        {
            projects.Add(ProjectValue(project, folder));
        }
        Set(value, ProjectsKey, projects);
        return value;
    }

    /// <summary>A project's entry: as read where nothing of it changed, else its path, action and launch profile set over
    /// what it was read as.</summary>
    private static JsonObject ProjectValue(SolutionLaunchProject project, string folder)
    {
        JsonObject value = project.Read is { } read ? (JsonObject)read.DeepClone() : [];
        // Early files wrote a project's path as its Name.
        string? pathKey = Key(value, PathKey) ?? Key(value, NameKey);
        string? writtenPath = pathKey is null ? null : TextOf(value, pathKey);
        if (writtenPath is null || !PathNames.Same(SolutionFilter.Named(writtenPath, folder), project.Project))
        {
            Set(value, pathKey ?? PathKey, Path.GetRelativePath(folder, project.Project).Replace('/', '\\'));
        }
        if (project.Action is { } action ? ActionOf(TextOf(value, ActionKey)) != action : TextOf(value, ActionKey) != project.ActionWritten)
        {
            Set(value, ActionKey, project.Action?.ToString() ?? project.ActionWritten);
        }
        if (!string.Equals(TextOf(value, TargetKey) ?? string.Empty, project.DebugTarget ?? string.Empty, StringComparison.Ordinal))
        {
            Set(value, TargetKey, project.DebugTarget is { Length: > 0 } target ? target : null);
        }
        return value;
    }

    private const string NameKey = "Name";
    private const string ProjectsKey = "Projects";
    private const string PathKey = "Path";
    private const string ActionKey = "Action";
    private const string TargetKey = "DebugTarget";

    /// <summary>Members named as Visual Studio's reader takes them, their letter case aside.</summary>
    private static readonly JsonNodeOptions Keys = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The member's own spelling of a name, or null where the object has none.</summary>
    private static string? Key(JsonObject value, string name) =>
        value.Select(member => member.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static string? TextOf(JsonObject value, string name) =>
        Key(value, name) is { } key && value[key] is JsonValue text && text.TryGetValue(out string? read) ? read : null;

    /// <summary>A member set under its own spelling where it has one, put after the last where not; null takes it out.</summary>
    private static void Set(JsonObject value, string name, JsonNode? now)
    {
        string? key = Key(value, name);
        if (now is null)
        {
            if (key is not null)
            {
                value.Remove(key);
            }
            return;
        }
        value[key ?? name] = now;
    }

    private static void Set(JsonObject value, string name, string? now) => Set(value, name, now is null ? null : JsonValue.Create(now));
}
