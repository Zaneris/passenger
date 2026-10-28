using System.Collections.Concurrent;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>The file-based programs' projects the app gave (<see cref="HostMethods.Program"/>), by the program's C# file:
/// each evaluated from its XML where the SDK places it, in memory, as MSBuild evaluates a project file from disk.</summary>
internal static class VirtualProjects
{
    private static readonly ConcurrentDictionary<string, ProgramParams> Given = new(PathNames.Comparer);

    public static bool Take(ProgramParams program)
    {
        Given[program.Path] = program;
        return true;
    }

    /// <summary>A project, from its file, or a program's from what the app gave of it, with the projects of the programs it
    /// references (<c>#:ref</c>) in the same collection, where a build looking for them by their place finds them.</summary>
    public static Project Load(string path, ProjectOptions options)
    {
        if (!ProgramFiles.IsProgram(path))
        {
            return Project.FromFile(path, options);
        }
        ProjectCollection collection = options.ProjectCollection ?? ProjectCollection.GlobalProjectCollection;
        // MSBuild's switch for projects in memory: a reference to one, or a task building its own, finds it rather than the disk.
        options.GlobalProperties = new Dictionary<string, string>(options.GlobalProperties ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            ["_BuildNonexistentProjectsByDefault"] = "true",
        };
        ProjectRootElement root = Root(path, collection);
        HashSet<string> placed = new(PathNames.Comparer) { path };
        Queue<ProjectRootElement> next = new([root]);
        while (next.TryDequeue(out ProjectRootElement? referencing))
        {
            foreach (ProjectItemElement reference in referencing.Items.Where(i => i.ItemType == "ProjectReference"))
            {
                if (reference.Metadata.FirstOrDefault(m => m.Name == ProgramFiles.RefMetadata)?.Value is { Length: > 0 } program
                    && placed.Add(program) && Given.ContainsKey(program))
                {
                    next.Enqueue(Root(program, collection));
                }
            }
        }
        return Project.FromProjectRootElement(root, options);
    }

    /// <summary>A given program's project, made in a collection at its place.</summary>
    private static ProjectRootElement Root(string path, ProjectCollection collection)
    {
        if (!Given.TryGetValue(path, out ProgramParams? given))
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)}: the host was given no project for this program");
        }
        if (given.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        if (given is not { Content: { } content, Project: { } at })
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)}: the .NET SDK made no project for this program");
        }
        using XmlReader reader = XmlReader.Create(new StringReader(content));
        ProjectRootElement root = ProjectRootElement.Create(reader, collection, preserveFormatting: false);
        root.FullPath = at;
        return root;
    }
}
