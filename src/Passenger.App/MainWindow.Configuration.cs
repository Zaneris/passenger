using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Features;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>A project's configuration as the window follows it: Manage User Secrets on a project (the Solution view's row, Find
/// Action on the file shown's project), which gives it a UserSecretsId where it has none and opens its secrets.json, made when
/// missing; and a settings file, user secrets or launch profile typed in, after which the open C# files' reads of keys nothing
/// gave and an AppHost's parameters are looked at again once typing pauses, as their reads of localized keys are after a .resx
/// or XLIFF file is saved.</summary>
public partial class MainWindow
{
    /// <summary>How long typing in a settings file pauses before what reads its keys is looked at again.</summary>
    private static readonly TimeSpan ConfigurationPause = TimeSpan.FromMilliseconds(600);

    private Debounce? configurationTyped;

    private void AddConfiguration()
    {
        configurationTyped = new Debounce(ConfigurationPause, () =>
        {
            session.CSharp?.ConfigurationChanged();
            return Task.CompletedTask;
        });
        Closed += (_, _) => configurationTyped.Stop();
        actions!.Add(new IdeAction(A.ManageUserSecrets, "Manage User Secrets", ExplorerGroup, c => ManageUserSecrets(SecretsProjectOf(c)!.Path))
        {
            WhyNot = c => SecretsProjectOf(c) is not null ? null
                : RowOf(c) is { } row ? $"{row.Name} is not a project"
                : c.File is null ? Refusals.NoFile : $"{c.File.Name} is in no project",
        });
        Editors.Opened += (_, document) =>
        {
            if (ConfigurationFiles.IsSettings(document.Path) || ConfigurationFiles.SecretsIdOf(document.Path) is not null
                || Path.GetFileName(document.Path).Equals(Run.LaunchProfile.SettingsFile, StringComparison.OrdinalIgnoreCase))
            {
                document.Text.Changed += (_, _) => configurationTyped.Restart();
            }
            else if (Passenger.Xaml.Localization.ResourceFiles.Holds(document.Path))
            {
                document.Saved += (_, _) => session.CSharp?.LocalizationChanged();
            }
        };
    }

    /// <summary>The project Manage User Secrets acts on: the Explorer's project row where its menu or keys ask, else the project
    /// of the file shown.</summary>
    private ProjectModel? SecretsProjectOf(ActionContext context) => RowOf(context) is { } row
        ? row is { Kind: NodeKind.Project, Path: { } path } ? SolutionProject(path) : null
        : context.File is { } file ? ProjectOf(file.Path) : null;

    /// <summary>A project's user secrets opened in an editor tab: its UserSecretsId given first where it has none (a
    /// new GUID, set in its project file) and its secrets.json made where missing, both one command of global undo, in
    /// the user secrets' folder (<see cref="UserSecrets.Root"/>). Null once open, else why not, which the status bar
    /// says too.</summary>
    public string? ManageUserSecrets(string project)
    {
        if (SolutionProject(project) is not { } model)
        {
            return SecretsSaid($"{Path.GetFileName(project)} is not one of the solution's projects");
        }
        string name = Path.GetFileNameWithoutExtension(model.Path);
        (UserSecretsFile? secrets, string? problem) = UserSecrets.Ensure(model.Path, model.Property(Passenger.Host.CarriedProperties.UserSecretsId),
            Editors.Texts().Text);
        if (secrets is null)
        {
            return SecretsSaid(problem);
        }
        List<XamlFileChange> changes = [.. secrets.Given is { } given ? [given] : Array.Empty<XamlFileChange>()];
        if (!File.Exists(secrets.File))
        {
            changes.Add(new XamlFileChange(secrets.File, null, "{\n}\n"));
        }
        if (changes.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(secrets.File)!);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return SecretsSaid($"{name}'s user secrets could not be made: {error.Message}");
            }
            if (Assist.ApplyFileChanges("Manage User Secrets", changes) is { } refused)
            {
                return SecretsSaid($"{name}'s user secrets could not be made: {refused}");
            }
        }
        OpenFile(secrets.File);
        return SecretsSaid(null, secrets.Given is not null ? $"Gave {name} the UserSecretsId {secrets.Id}; its secrets.json is open, kept out of its files"
            : $"{name}'s secrets.json is open, kept out of its files");
    }

    private string? SecretsSaid(string? problem, string? done = null)
    {
        status.Text = problem ?? done ?? string.Empty;
        return problem;
    }
}
