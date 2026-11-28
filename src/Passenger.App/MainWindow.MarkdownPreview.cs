using Passenger.App.Editors;
using Passenger.App.State;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>A Markdown file's preview beside its editor (<see cref="MarkdownPreview"/>), an SVG's picture
/// (<see cref="SvgPreview"/>) and an HTML file's page (<see cref="HtmlPreview"/>), each group's own: View, Markdown, SVG or
/// HTML Preview and the preview toggle on a group's tab strip show or hide it for every file of its kind, a Markdown file's
/// layouts on its tab strip showing its editor alone, both or its preview alone, the choice kept in the store's
/// previews.json, so a file opens as the last one was left; a link to a file in a Markdown preview opens that file here.</summary>
public partial class MainWindow
{
    private void AddMarkdownPreview()
    {
        actions!.Add(new IdeAction(A.MarkdownPreview, "Markdown Preview", "Editor", _ => Editors.ShowsMarkdownPreview = !Editors.ShowsMarkdownPreview)
        {
            WhyNot = WhyNotMarkdown,
            IsChecked = _ => Editors.ShowsMarkdownPreview,
            Menus = [new MenuPlace("View", 4, 1)],
            Words = ["Preview Markdown"],
        });
        AddMarkdownLayout(A.MarkdownEditorOnly, "Editor Only", PaneView.Editor, ["Hide Markdown Preview"]);
        AddMarkdownLayout(A.MarkdownEditorAndPreview, "Editor and Preview", PaneView.Split,
            ["Markdown Preview Beside the Editor", "Markdown Preview Under the Editor", "Turn Markdown Split"]);
        AddMarkdownLayout(A.MarkdownPreviewOnly, "Preview Only", PaneView.Preview, ["Markdown Preview Alone"]);
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
        Editors.MarkdownLayoutChanged += (_, _) =>
        {
            if (keepsWorkspace && state is not null)
            {
                state.Previews.Set(state.Previews.Value with { MarkdownLayout = Editors.MarkdownLayout });
            }
        };
        Editors.Root = () => Folder;
        Editors.LeftOutUnder = LeftOutUnderAsync;
        Editors.FileLinkClicked += (_, path) => OpenLinkedFile(path);
    }

    private static Words? WhyNotMarkdown(ActionContext context) => context.File is null ? Refusals.NoFile
        : context.File.Language != Languages.Language.Markdown ? "The preview is for Markdown files" : null;

    /// <summary>A Markdown file's layout as an action, checked while it is in force: Editor and Preview chosen while it is
    /// turns the split, the preview under the editor or beside it, back to as the group's shape stands it.</summary>
    private void AddMarkdownLayout(string id, string title, PaneView view, IReadOnlyList<string> words) =>
        actions!.Add(new IdeAction(id, title, "Editor", _ =>
        {
            PaneLayout layout = Editors.MarkdownLayout;
            Editors.MarkdownLayout = view == PaneView.Split && layout.View == view
                ? layout with { Stacked = Editors.ActiveGroup.TurnedSplit }
                : layout with { View = view };
        })
        {
            WhyNot = WhyNotMarkdown,
            IsChecked = _ => Editors.MarkdownLayout.View == view,
            Words = words,
        });

    /// <summary>What .gitignore leaves out below a folder, as its repository's git says: none where it is in no repository
    /// or git is not installed, and the folder whole where git could not say, so nothing below it is served.</summary>
    private static async Task<IReadOnlyList<string>> LeftOutUnderAsync(string folder)
    {
        if (await Passenger.Vcs.Git.OpenAsync(folder) is not { Tree: { } git })
        {
            return [];
        }
        try
        {
            return await git.LeftOutUnderAsync(folder);
        }
        catch (InvalidOperationException error)
        {
            IdeErrors.Write($"The HTML preview serves nothing below {folder}, as git could not say what .gitignore leaves out: {error.Message}");
            return [folder];
        }
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

    /// <summary>A file a preview's link names, opened in the editor; one not there said in the status bar, and a share's or
    /// a device's never asked of the disk.</summary>
    private void OpenLinkedFile(string path)
    {
        if (!FoundPaths.Askable(path))
        {
            status.Text = $"{path} is another machine's or a device's, which is never opened";
        }
        else if (File.Exists(path))
        {
            OpenFile(path);
        }
        else
        {
            status.Text = $"{PathNames.Shown(path, Folder)} is not there";
        }
    }
}
