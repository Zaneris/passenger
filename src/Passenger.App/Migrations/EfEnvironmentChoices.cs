using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>An environment the Migrations window offers dotnet ef's runs of a startup project, its name what the field shows,
/// with what names it where that is not the person's choice (the run configuration, the default).</summary>
public sealed record EfEnvironment(string Name, string? Note = null)
{
    public override string ToString() => Name;
}

/// <summary>The environment the Migrations window runs dotnet ef in for each startup project, chosen in the window: none
/// chosen, the run configuration's. Kept with the folder (<see cref="WorkspaceState.MigrationEnvironments"/>) while the
/// window keeps state. A UI-thread object.</summary>
public sealed class EfEnvironmentChoices
{
    private readonly Dictionary<string, string> chosen = new(PathNames.Comparer);
    private StateDocument<WorkspaceState>? kept;

    /// <summary>A choice changed, or the folder's kept choices were taken.</summary>
    public event EventHandler? Changed;

    /// <summary>The environment chosen for a startup project; null where none is.</summary>
    public string? Of(ProjectModel startup) => chosen.GetValueOrDefault(startup.Path);

    /// <summary>Chooses a startup project's environment, kept with the folder; null goes back to none chosen.</summary>
    public void Choose(ProjectModel startup, string? environment)
    {
        UiThread.Check();
        if (Of(startup) == environment)
        {
            return;
        }
        if (environment is null)
        {
            chosen.Remove(startup.Path);
        }
        else
        {
            chosen[startup.Path] = environment;
        }
        if (kept is { } document)
        {
            document.Set(document.Value with { MigrationEnvironments = Kept });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The choices made, by startup project, as the folder keeps them.</summary>
    public Dictionary<string, string> Kept => new(chosen, StringComparer.Ordinal);

    /// <summary>The folder's documents from now on, the choices it kept taken first.</summary>
    public void Keep(StateDocument<WorkspaceState> document)
    {
        UiThread.Check();
        kept = document;
        foreach ((string startup, string environment) in document.Value.MigrationEnvironments)
        {
            chosen[startup] = environment;
        }
        if (document.Value.MigrationEnvironments.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Why a name typed for an environment is none, as appsettings.{Environment}.json takes it: letters, digits and
    /// '.', '-' or '_' after the first; null for a name.</summary>
    public static string? WhyNot(string name) =>
        name.Length > 0 && char.IsLetterOrDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_') ? null
        : $"\"{name}\" names no environment: an environment's name is letters and digits, with '.', '-' or '_' after the first";

    /// <summary>The environments a startup project names: those its appsettings.{Environment}.json files are of, and those
    /// its launch profiles run it in (Development for a profile that names none, or where it has none), each once, in
    /// order. Read from the disk, on any thread.</summary>
    public static IReadOnlyList<string> Named(ProjectModel startup)
    {
        List<string> found = [.. ConfigurationFiles.Environments(Run.LaunchProfile.SettingsPath(startup), Text)];
        try
        {
            found.AddRange(Directory.EnumerateFiles(startup.Folder, AppSettingsFiles.Of("*")).Select(AppSettingsFiles.EnvironmentOf).OfType<string>()
                .Where(e => WhyNot(e) is null));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed names none of its settings' environments.
        }
        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? Text(string path)
    {
        try
        {
            return FileText.Load(path)?.Text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
