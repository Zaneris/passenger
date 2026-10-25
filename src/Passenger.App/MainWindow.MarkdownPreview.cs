using Passenger.App.Editors;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>A Markdown file's preview beside its editor (<see cref="MarkdownPreview"/>), each group's own: View, Markdown
/// Preview and the preview toggle on a group's tab strip show or hide it for every Markdown file, the choice kept in the store's
/// previews.json, so a file opens as the last one was left; a link to a file in it opens that file here.</summary>
public partial class MainWindow
{
    private void AddMarkdownPreview()
    {
        actions!.Add(new IdeAction(A.MarkdownPreview, "Markdown Preview", "Editor", _ => ToggleMarkdownPreview())
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : context.File.Language != Languages.Language.Markdown ? "The preview is for Markdown files" : null,
            IsChecked = _ => Editors.ShowsMarkdownPreview,
            Menus = [new MenuPlace("View", 4, 1)],
            Words = ["Preview Markdown", "Editor and Preview"],
        });
        Editors.Root = () => Folder;
        Editors.FileLinkClicked += (_, path) => OpenLinkedFile(path);
    }

    private void ToggleMarkdownPreview()
    {
        Editors.ShowsMarkdownPreview = !Editors.ShowsMarkdownPreview;
        if (keepsWorkspace)
        {
            state?.Previews.Set(new State.PreviewsState { Markdown = Editors.ShowsMarkdownPreview });
        }
    }

    /// <summary>A file a preview's link names, opened in the editor; one not there said in the status bar.</summary>
    private void OpenLinkedFile(string path)
    {
        if (File.Exists(path))
        {
            OpenFile(path);
        }
        else
        {
            status.Text = $"{PathNames.Shown(path, Folder)} is not there";
        }
    }
}
