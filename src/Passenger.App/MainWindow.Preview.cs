using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The XAML previewer beside the editor: View, XAML Preview shows each editor group's XAML file of an Avalonia
/// or WPF project with its design view to its right, drawn from the project's last build by its framework's previewer
/// and following the text as typing pauses. Where the previewer places the file's elements (WPF's), a click on one
/// selects its start tag in the editor and the element at the caret is outlined. A file's preview runs while the file
/// is open and the preview on; it ends as either goes.</summary>
public partial class MainWindow
{
    /// <summary>How long typing pauses before the previewer is sent the text.</summary>
    private static readonly TimeSpan PreviewSettle = TimeSpan.FromMilliseconds(400);

    private readonly Dictionary<OpenDocument, ShownPreview> previews = [];
    private bool previewing;

    /// <summary>The preview of a file while it is shown.</summary>
    public XamlPreview? PreviewOf(OpenDocument document) => previews.TryGetValue(document, out ShownPreview shown) ? shown.Preview : null;

    /// <summary>The preview pane of a file while it is shown.</summary>
    public XamlPreviewPane? PreviewPaneOf(OpenDocument document) => previews.TryGetValue(document, out ShownPreview shown) ? shown.Pane : null;

    private void AddPreview()
    {
        actions!.Add(new IdeAction(A.XamlPreview, "XAML Preview", "Editor", _ => TogglePreview())
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : session.Xaml.For(context.File) is null ? "The previewer is for the XAML of an Avalonia or WPF project" : null,
            IsChecked = _ => previewing,
            Menus = [new MenuPlace("View", 4, 0)],
        });
        Editors.HasXamlPreview = document => session.Xaml.For(document) is not null;
        Editors.PreviewToggleClicked += (_, group) => Request(group.Active?.Language == Languages.Language.Markdown ? A.MarkdownPreview : A.XamlPreview);
        Editors.ActiveChanged += (_, _) => FollowPreviews();
        Editors.Closed += (_, document) => EndPreview(document);
        Editors.ForEachEditor(editor => editor.SelectionChanged += (_, _) => OutlineAtCaret(editor));
        Closed += (_, _) =>
        {
            foreach (OpenDocument document in previews.Keys.ToList())
            {
                EndPreview(document);
            }
        };
    }

    /// <summary>The previews follow a session's XAML from now: the last session's end with it, and its own are made as its
    /// files are read, and again from each build it finishes.</summary>
    private void BindPreviews(ProjectSession next)
    {
        foreach (OpenDocument document in previews.Keys.ToList())
        {
            EndPreview(document);
        }
        next.Xaml.ProjectChanged += (_, _) =>
        {
            if (session == next)
            {
                FollowPreviews();
                // A preview that found no previewer may have read the project before its restore landed.
                foreach (XamlPreview preview in previews.Values.Select(p => p.Preview).Where(p => !p.Running))
                {
                    IdeErrors.Observe(preview.RestartAsync());
                }
            }
        };
        buildRun.BuildFinished += (_, outcome) =>
        {
            if (session == next)
            {
                PreviewsBuilt(outcome);
            }
        };
        FollowPreviews();
    }

    private void TogglePreview()
    {
        previewing = !previewing;
        Editors.ShowsXamlPreview = previewing;
        if (!previewing)
        {
            foreach (OpenDocument document in previews.Keys.ToList())
            {
                EndPreview(document);
            }
        }
        FollowPreviews();
    }

    /// <summary>Each editor group shows the preview of its XAML file while previews are on, and none otherwise; a Markdown
    /// file's preview beside its editor is its group's own.</summary>
    private void FollowPreviews()
    {
        foreach (EditorGroup group in Editors.Groups)
        {
            group.FollowPreviewToggle();
            if (previewing && group.Active is { } shown && session.Xaml.For(shown) is { } document)
            {
                group.Side = PaneFor(shown, document);
            }
            else if (group.Side is XamlPreviewPane)
            {
                group.Side = null;
            }
        }
    }

    private XamlPreviewPane PaneFor(OpenDocument shown, XamlDocument document)
    {
        if (previews.TryGetValue(shown, out ShownPreview kept))
        {
            return kept.Pane;
        }
        XamlPreview preview = new(document);
        XamlPreviewPane pane = new(preview);
        pane.ElementChosen += (_, element) => ChooseInEditor(pane, element);
        preview.Changed += (_, _) => OutlineAtCaret(pane, preview);
        Debounce typing = new(PreviewSettle, () =>
        {
            preview.Update();
            return Task.CompletedTask;
        });
        EventHandler<DocumentChangedEventArgs> changed = (_, _) => typing.Restart();
        shown.Text.Changed += changed;
        previews[shown] = new(preview, pane, typing, changed);
        IdeErrors.Observe(preview.StartAsync());
        return pane;
    }

    /// <summary>The element at the caret of an editor beside a preview, outlined in its frame.</summary>
    private void OutlineAtCaret(CodeEditor editor)
    {
        if (previews.Count > 0 && Editors.Groups.FirstOrDefault(g => g.Editor == editor) is { Side: XamlPreviewPane pane, Active: { } shown }
            && previews.TryGetValue(shown, out ShownPreview kept) && kept.Pane == pane)
        {
            pane.ShowOutline(kept.Preview.ElementAround(editor.Selections.Primary.Caret));
        }
    }

    /// <summary>A preview's frame or elements changed: the element at its editor's caret outlined again.</summary>
    private void OutlineAtCaret(XamlPreviewPane pane, XamlPreview preview)
    {
        foreach (EditorGroup group in Editors.Groups.Where(g => g.Side == pane))
        {
            pane.ShowOutline(preview.ElementAround(group.Editor.Selections.Primary.Caret));
        }
    }

    /// <summary>An element clicked in a preview: its start tag selected in the editor beside it, the caret at its start.</summary>
    private void ChooseInEditor(XamlPreviewPane pane, PreviewElement element)
    {
        if (Editors.Groups.FirstOrDefault(g => g.Side == pane) is not { } group)
        {
            return;
        }
        group.Editor.Selections = SelectionSet.Single(new Selection(element.Tag.End, element.Tag.Start));
        group.Editor.Focus();
    }

    /// <summary>A build that succeeded is what each preview draws from then on.</summary>
    private void PreviewsBuilt(BuildOutcome outcome)
    {
        if (outcome.Status != BuildStatus.Succeeded)
        {
            return;
        }
        foreach (XamlPreview preview in previews.Values.Select(p => p.Preview))
        {
            IdeErrors.Observe(preview.RestartAsync());
        }
    }

    private void EndPreview(OpenDocument document)
    {
        if (!previews.Remove(document, out ShownPreview shown))
        {
            return;
        }
        document.Text.Changed -= shown.Changed;
        shown.Typing.Stop();
        shown.Preview.Dispose();
        foreach (EditorGroup group in Editors.Groups.Where(g => g.Side == shown.Pane))
        {
            group.Side = null;
        }
    }

    /// <summary>A file's preview while it is shown: its pane, the pause in typing that sends the previewer the text, and
    /// the handler of the text's changes that restarts it.</summary>
    private readonly record struct ShownPreview(XamlPreview Preview, XamlPreviewPane Pane, Debounce Typing, EventHandler<DocumentChangedEventArgs> Changed);
}
