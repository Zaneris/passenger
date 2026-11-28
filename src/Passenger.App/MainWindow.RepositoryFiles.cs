using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The Explorer's .editorconfig, .gitignore and .gitattributes: each made in the row's folder from Passenger's one
/// text of it (<see cref="RepositoryFiles"/>) as one command Undo takes back, or, where the folder has one already, that
/// one opened.</summary>
public partial class MainWindow
{
    private void AddRepositoryFileActions()
    {
        foreach ((string id, RepositoryFile file) in (ReadOnlySpan<(string, RepositoryFile)>)[(AppActions.NewEditorConfig, RepositoryFile.EditorConfig),
            (AppActions.NewGitIgnore, RepositoryFile.GitIgnore), (AppActions.NewGitAttributes, RepositoryFile.GitAttributes)])
        {
            string name = RepositoryFiles.NameOf(file);
            Row(id, ActionTitles.NewRepositoryFile(name), row => IdeErrors.Observe(AddRepositoryFileAsync(id, NewGoesIn(row)!, file)),
                row => NewGoesIn(row) is { } into ? ExplorerNode.KeptOut(into) : $"Nothing new goes in {row.Name}",
                row => NewGoesIn(row) is { } into && File.Exists(Path.Combine(into, name)) ? ActionTitles.OpenRepositoryFile(name) : ActionTitles.RepositoryFileInMenu(name));
        }
    }

    /// <summary>A folder's .editorconfig, .gitignore or .gitattributes made and opened, or the one there opened; what it came
    /// to said.</summary>
    public async Task AddRepositoryFileAsync(string id, string folder, RepositoryFile file)
    {
        string name = RepositoryFiles.NameOf(file);
        string path = Path.Combine(folder, name);
        string shown = PathNames.Shown(folder, Folder);
        if (File.Exists(path))
        {
            OpenFile(path);
            status.Text = $"{name} is already in {shown}: opened it";
            return;
        }
        using Working? work = actions!.Work.Begin(id, $"Making {name} in {shown}…", stoppable: false);
        (string? text, string? why) = await RepositoryFiles.TextAsync(file, folder);
        if (text is null)
        {
            status.Text = $"{name} was not made: {why}";
            work?.Tell(false, status.Text);
            return;
        }
        string said;
        using (CommandHistory.Recording recording = Editors.Commands.Record($"New {name}"))
        {
            recording.Made(path);
            bool made = false;
            try
            {
                Following([path], () => made = FileText.SaveNewOnly(path, text) is not null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                recording.Unwritten(path);
                status.Text = NotMade(name, error);
                work?.Tell(false, status.Text);
                return;
            }
            string? unshared = null;
            if (!made)
            {
                recording.Unwritten(path);
            }
            else
            {
                unshared = IntoShared([PathNames.Canonical(path)], recording) ?? KeepFolders([], [PathNames.Canonical(path)], [], recording);
            }
            said = !made ? $"{name} came in {shown} meanwhile: opened it" : unshared ?? $"New {name} in {shown}";
        }
        OpenFile(path);
        Explorer.Reveal(PathNames.Canonical(path));
        status.Text = said;
        work?.Tell(true, said);
    }
}
