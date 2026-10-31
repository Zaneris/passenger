using System.Text.Json;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.Host;

namespace Passenger.Workspace;

/// <summary>A file-based program's project as the .NET SDK makes it (<c>dotnet run-api</c>, run in the program's folder, so
/// the SDK its global.json picks makes it), for the project host, with the heads it was made from (the program's and each
/// C# file it includes, whose directives are its project's too) and the programs it references.</summary>
public static class ProgramProjects
{
    /// <summary>The program's project, or why the SDK made none: its errors for the directives it does not read, each at
    /// its line ("app.cs(3): Unrecognized directive 'packag'.").</summary>
    public static async Task<(ProgramParams Program, IReadOnlyList<Stamp> Heads, IReadOnlyList<string> Referenced)> ReadAsync(string path, CancellationToken cancel)
    {
        Stamp head = Stamps.Head(path);
        string folder = Path.GetDirectoryName(path)!;
        string asked = JsonSerializer.Serialize(new Dictionary<string, string> { ["$type"] = "GetProject", ["EntryPointFileFullPath"] = path });
        // Its answer is its line of JSON, taken as it comes: the SDK's first run in a new home writes its welcome before it.
        ChildAnswer answer = await ChildProcesses.AnswerAsync(ChildProcesses.DotnetCommand(["run-api"], folder), l => l.TrimStart().StartsWith('{'), asked,
            cancel: cancel).ConfigureAwait(false);
        string name = Path.GetFileName(path);
        if (answer.Answer?.Trim() is not { } line)
        {
            return (new ProgramParams(path, null, null, $"{name}: the .NET SDK made no project for it: {answer.Said}"), [head], []);
        }
        try
        {
            using JsonDocument read = JsonDocument.Parse(line);
            JsonElement root = read.RootElement;
            if (JsonMembers.Text(root, "$type") == "Error")
            {
                return (new ProgramParams(path, null, null, $"{name}: {JsonMembers.Text(root, "Message") ?? "the .NET SDK made no project for it"}"), [head], []);
            }
            List<string> problems = [];
            foreach (JsonElement diagnostic in JsonMembers.Items(root, "Diagnostics"))
            {
                problems.Add($"{At(diagnostic, name)}: {JsonMembers.Text(diagnostic, "Message")}");
            }
            string? content = JsonMembers.Text(root, "Content");
            string? project = JsonMembers.Text(root, "ProjectPath");
            (List<string> included, List<string> referenced) = Named(content);
            IReadOnlyList<Stamp> heads = [head, .. included.Where(f => !PathNames.Same(f, path)).Select(Stamps.Head)];
            return (new ProgramParams(path, project, content, problems.Count > 0 ? string.Join(" ", problems) : null), heads, referenced);
        }
        catch (JsonException error)
        {
            return (new ProgramParams(path, null, null, $"{name}: the .NET SDK's answer does not read: {error.Message}"), [head], []);
        }
    }

    /// <summary>Where a diagnostic of the SDK's is, as a build names a place: its file and line from 1.</summary>
    private static string At(JsonElement diagnostic, string name)
    {
        if (JsonMembers.Object(diagnostic, "Location") is not { } location)
        {
            return name;
        }
        string file = JsonMembers.Text(location, "Path") is { } path ? Path.GetFileName(path) : name;
        return JsonMembers.Number(JsonMembers.Object(JsonMembers.Object(location, "Span"), "Start"), "Line") is { } line ? $"{file}({line + 1})" : file;
    }

    /// <summary>The C# files the project compiles that a directive named by its path (no wildcard nor property in it), and
    /// the programs it references (<c>#:ref</c>), by their C# files.</summary>
    private static (List<string> Included, List<string> Referenced) Named(string? content)
    {
        if (content is null)
        {
            return ([], []);
        }
        try
        {
            XDocument project = XDocument.Parse(content);
            List<string> included = [.. project.Descendants("Compile")
                .Select(c => (string?)c.Attribute("Include"))
                .OfType<string>()
                .Where(i => i.IndexOfAny(['*', '?', '$', ';']) < 0 && Path.IsPathFullyQualified(i) && ProgramFiles.IsProgram(i))];
            List<string> referenced = [.. project.Descendants("ProjectReference")
                .Select(r => (string?)r.Attribute(ProgramFiles.RefMetadata))
                .OfType<string>()
                .Where(r => Path.IsPathFullyQualified(r) && ProgramFiles.IsProgram(r))];
            return (included, referenced);
        }
        catch (System.Xml.XmlException)
        {
            return ([], []);
        }
    }
}
