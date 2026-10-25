using Passenger.Core;
using Passenger.Workspace;
using Passenger.Xaml.Features;

namespace Passenger.App.Workspace;

/// <summary>A project's user secrets as a command that keeps a value in them finds them: its id, its secrets.json there or
/// not, and for an id just made the change that sets it in the project file, for the command to make with its own.</summary>
public sealed record UserSecretsFile(string Id, string File, XamlFileChange? Given);

/// <summary>Where a project's user secrets are kept on this machine: .NET's own folder (APPDATA's Microsoft/UserSecrets
/// on Windows, ~/.microsoft/usersecrets elsewhere), or <see cref="Variable"/>'s in its place, read for that alone; and a
/// project's id, found (<see cref="Of"/>) and made (<see cref="Ensure"/>) one way. A container run mounts a project's; an
/// AppHost's parameters are read there.</summary>
public static class UserSecrets
{
    /// <summary>The folder kept in place of .NET's own when set.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_USER_SECRETS";

    public static string Root => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } given ? given
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");

    /// <summary>The folder of an id's secrets, there or not; null for an id that names no one folder (empty, a separator in it,
    /// a dot or two alone).</summary>
    public static string? FolderOf(string? id) =>
        id is not { Length: > 0 } || id is "." or ".." || id.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) >= 0
            ? null : Path.Combine(Root, id);

    /// <summary>An id's secrets.json, there or not; null for an id that names no one folder.</summary>
    public static string? FileOf(string? id) => FolderOf(id) is { } folder ? Path.Combine(folder, "secrets.json") : null;

    /// <summary>A project's UserSecretsId: as MSBuild evaluated it, else as its file's text sets it (an id written since the
    /// evaluation, or one it has yet to take); null for none.</summary>
    public static string? Of(string project, string? evaluated, Func<string, string?> text) =>
        evaluated is { Length: > 0 } ? evaluated
        : text(project) is { } written && ProjectEdits.PropertyIn(written, "UserSecretsId") is { Length: > 0 } set ? set : null;

    /// <summary>A project's user secrets for a command to keep a value in: its id (<see cref="Of"/>), else a new GUID, as
    /// <c>dotnet user-secrets init</c> gives one, with the change that sets it in the project file; or why it can have none.</summary>
    public static (UserSecretsFile? Secrets, string? Problem) Ensure(string project, string? evaluated, Func<string, string?> text)
    {
        string name = Path.GetFileNameWithoutExtension(project);
        string? id = Of(project, evaluated, text);
        XamlFileChange? given = null;
        if (id is null)
        {
            string made = Guid.NewGuid().ToString();
            if (text(project) is not { } now)
            {
                return (null, $"{name} has no UserSecretsId, and {Path.GetFileName(project)} does not read to be given one");
            }
            if (ProjectEdits.WithProperty(now, project, "UserSecretsId", made) is { Text: null, Problem: var problem })
            {
                return (null, $"{name} has no UserSecretsId, and could not be given one: {problem}");
            }
            id = made;
            given = new XamlFileChange(project, then => ProjectEdits.WithProperty(then, project, "UserSecretsId", made) is { Text: { } changed } && changed != then
                ? [Passenger.Text.TextEdit.Between(then, changed)] : []);
        }
        return FileOf(id) is { } file ? (new UserSecretsFile(id, PathNames.Canonical(file), given), null)
            : (null, $"{name}'s UserSecretsId, {id}, names no folder for its secrets");
    }
}
