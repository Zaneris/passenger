using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>The folder Add Migration writes a context's migrations into, as dotnet ef's <c>--output-dir</c> takes it: a
/// folder of the project, from its own, in forward slashes. None given, dotnet ef puts a migration beside the context's
/// last one, else in <see cref="Default"/>. The folders offered are those holding the context's migrations as the
/// project's files were last read; the one last given is kept by EfFolderChoices. On any thread.</summary>
public static class EfOutputFolders
{
    /// <summary>Where dotnet ef puts a context's first migration when no folder is given.</summary>
    public const string Default = "Migrations";

    /// <summary>The folders kept as the state file has them, those that name no folder of a project left out.</summary>
    public static Dictionary<string, Dictionary<string, string>> Clamped(Dictionary<string, Dictionary<string, string>>? kept) =>
        (kept ?? []).Where(p => p.Key is { Length: > 0 } && p.Value is not null)
            .Select(p => (p.Key, Contexts: p.Value.Where(c => c.Key is { Length: > 0 } && c.Value is { Length: > 0 } folder && Inside(folder))
                .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal)))
            .Where(p => p.Contexts.Count > 0)
            .ToDictionary(p => p.Key, p => p.Contexts, StringComparer.Ordinal);

    /// <summary>A folder typed or picked for <c>--output-dir</c> as dotnet ef is given it: from the project's folder in forward
    /// slashes, one given whole taken from there; null for none (empty, dotnet ef's own choice), or why it is no folder of
    /// the project.</summary>
    public static (string? Folder, string? Why) Read(string? typed, ProjectModel project)
    {
        string text = typed?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return (null, null);
        }
        string? folder;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text, project.Folder));
            // One given whole may reach the project through a link, as a picker gives it.
            folder = PathNames.Relative(Path.IsPathRooted(text) ? PathNames.Canonical(full) : full, project.Folder);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"\"{text}\" names no folder: {error.Message}");
        }
        return folder is null ? (null, $"{text} is outside {project.Name}: its migrations go in a folder of the project") : (folder, null);
    }

    /// <summary>Whether a kept folder names one inside a project, as <see cref="Read"/> gives one.</summary>
    private static bool Inside(string folder) =>
        folder == "." || (!Path.IsPathRooted(folder) && folder.Split('/').All(part => part is not (".." or "." or "")));

    /// <summary>The folders, from the project's, holding a context's migrations and its model snapshot as the last read of the
    /// project found them: the one its last migration is in first, where dotnet ef puts the next given none.</summary>
    public static IReadOnlyList<string> Holding(EfProjectRead? read, ProjectModel project, string? context)
    {
        if (read is null || context is null)
        {
            return [];
        }
        return [.. read.MigrationsOf(context)
            .OrderByDescending(m => m.Id is not null).ThenByDescending(m => m.Id, StringComparer.Ordinal)
            .Select(m => Path.GetDirectoryName(m.File) is { } folder ? PathNames.Relative(folder, project.Folder) : null)
            .OfType<string>().Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The folder of a context's last migration, beside which dotnet ef puts its next given no folder; null for a
    /// context with none, whose first dotnet ef puts in <see cref="Default"/>.</summary>
    public static string? LastIn(EfProjectRead? read, ProjectModel project, string? context) =>
        context is not null && read?.MigrationsOf(context).Where(m => m.Id is not null).MaxBy(m => m.Id, StringComparer.Ordinal) is { } last
        && Path.GetDirectoryName(last.File) is { } folder ? PathNames.Relative(folder, project.Folder) : null;
}
