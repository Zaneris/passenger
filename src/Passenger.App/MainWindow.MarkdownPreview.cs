using Passenger.App.Editors;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>A Markdown file's preview beside its editor (<see cref="MarkdownPreview"/>), an SVG's picture
/// (<see cref="SvgPreview"/>) and an HTML file's page (<see cref="HtmlPreview"/>), each group's own: View, Markdown, SVG or
/// HTML Preview and the preview toggle on a group's tab strip show or hide it for every file of its kind, the choice kept
/// in the store's previews.json, so a file opens as the last one was left; a link to a file in a Markdown preview opens
/// that file here.</summary>
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
        actions.Add(new IdeAction(A.SvgPreview, "SVG Preview", "Editor", _ => ToggleSvgPreview())
        {
            WhyNot = context => context.File is null ? Refusals.NoFile : !EditorArea.IsSvg(context.File) ? "The picture is for SVG files" : null,
            IsChecked = _ => Editors.ShowsSvgPreview,
            Menus = [new MenuPlace("View", 4, 2)],
            Words = ["Preview SVG", "SVG Picture"],
        });
        actions.Add(new IdeAction(A.HtmlPreview, "HTML Preview", "Editor", _ => ToggleHtmlPreview())
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : context.File.Language != Languages.Language.Html ? "The preview is for HTML files" : null,
            IsChecked = _ => Editors.ShowsHtmlPreview,
            Menus = [new MenuPlace("View", 4, 3)],
            Words = ["Preview HTML", "Web Page Preview"],
        });
        Editors.Root = () => Folder;
        Editors.FileLinkClicked += (_, path) => OpenLinkedFile(path);
    }

    private void ToggleHtmlPreview()
    {
        Editors.ShowsHtmlPreview = !Editors.ShowsHtmlPreview;
        if (keepsWorkspace && state is not null)
        {
            state.Previews.Set(state.Previews.Value with { Html = Editors.ShowsHtmlPreview });
        }
    }

    private void ToggleSvgPreview()
    {
        Editors.ShowsSvgPreview = !Editors.ShowsSvgPreview;
        if (keepsWorkspace && state is not null)
        {
            state.Previews.Set(state.Previews.Value with { Svg = Editors.ShowsSvgPreview });
        }
    }

    private void ToggleMarkdownPreview()
    {
        Editors.ShowsMarkdownPreview = !Editors.ShowsMarkdownPreview;
        if (keepsWorkspace && state is not null)
        {
            state.Previews.Set(state.Previews.Value with { Markdown = Editors.ShowsMarkdownPreview });
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
