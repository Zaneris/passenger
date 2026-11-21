using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>The output folder Add Migration was last given for each context (<see cref="EfOutputFolders"/>), by project and
/// context, kept with the folder (<see cref="WorkspaceState.MigrationFolders"/>) while the window keeps state: the one its
/// question fills in, and an agent's Add Migration takes given none. A UI-thread object.</summary>
public sealed class EfFolderChoices
{
    private readonly Dictionary<string, Dictionary<string, string>> chosen = new(PathNames.Comparer);
    private StateDocument<WorkspaceState>? kept;

    /// <summary>The folder last given for a project's context, from the project's; null where none is.</summary>
    public string? Of(ProjectModel project, string context) =>
        chosen.TryGetValue(project.Path, out Dictionary<string, string>? contexts) ? contexts.GetValueOrDefault(EfTargets.ContextName(context)) : null;

    /// <summary>Keeps the folder given for a project's context with the folder; null goes back to none given.</summary>
    public void Choose(ProjectModel project, string context, string? folder)
    {
        UiThread.Check();
        string name = EfTargets.ContextName(context);
        if (Of(project, name) == folder)
        {
            return;
        }
        if (!chosen.TryGetValue(project.Path, out Dictionary<string, string>? contexts))
        {
            contexts = chosen[project.Path] = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (folder is not null)
        {
            contexts[name] = folder;
        }
        else
        {
            contexts.Remove(name);
        }
        if (contexts.Count == 0)
        {
            chosen.Remove(project.Path);
        }
        if (kept is { } document)
        {
            document.Set(document.Value with { MigrationFolders = Kept });
        }
    }

    /// <summary>The folders given, by project then context, as the folder keeps them.</summary>
    public Dictionary<string, Dictionary<string, string>> Kept =>
        chosen.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The folder's documents from now on, the folders it kept taken first.</summary>
    public void Keep(StateDocument<WorkspaceState> document)
    {
        UiThread.Check();
        kept = document;
        foreach ((string project, Dictionary<string, string> contexts) in document.Value.MigrationFolders)
        {
            chosen[project] = new Dictionary<string, string>(contexts, StringComparer.Ordinal);
        }
    }
}
