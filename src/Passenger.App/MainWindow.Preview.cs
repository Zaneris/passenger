using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The XAML previewer beside the editor: View, XAML Preview shows each editor group's XAML file of an Avalonia,
/// WPF or .NET MAUI project with its design view to its right, drawn from the project's last build by its framework's previewer
/// and following the text as typing pauses. Where the previewer places the file's elements, a click on one
/// selects its start tag in the editor and the element at the caret is outlined. A file's preview is kept while the file
/// is open and the preview on, and ends as either goes; its previewer runs while the preview is shown, or was the last
/// hidden. On or off is kept in the store's previews.json.</summary>
public partial class MainWindow
{
    /// <summary>How long typing pauses before the previewer is sent the text.</summary>
    private static readonly TimeSpan PreviewSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>How many previews not shown keep their previewers, the last shown first, so a look at the code-behind and
    /// back finds the preview live; each other one's previewer ends, to start again as it is shown.</summary>
    private const int KeptHidden = 1;

    private readonly Dictionary<OpenDocument, ShownPreview> previews = [];
    private bool previewing;

    /// <summary>What the last MAUI page's preview was chosen to be drawn as, which each MAUI preview opened since starts as.</summary>
    private MauiLook mauiLook = MauiLook.AsBuilt;

    /// <summary>Counts the times the previews shown were followed, which says the preview shown last.</summary>
    private long previewsFollowed;

    /// <summary>The preview of a file while it is kept: shown, or hidden since.</summary>
    public XamlPreview? PreviewOf(OpenDocument document) => previews.TryGetValue(document, out ShownPreview? shown) ? shown.Preview : null;

    /// <summary>The preview pane of a file while it is kept.</summary>
    public XamlPreviewPane? PreviewPaneOf(OpenDocument document) => previews.TryGetValue(document, out ShownPreview? shown) ? shown.Pane : null;

    private void AddPreview()
    {
        actions!.Add(new IdeAction(A.XamlPreview, "XAML Preview", "Editor", _ => TogglePreview())
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : session.Xaml.For(context.File) is null ? "The previewer is for the XAML of an Avalonia, WPF or .NET MAUI project" : null,
            IsChecked = _ => previewing,
            Menus = [new MenuPlace("View", 4, 0)],
        });
        actions.Add(new IdeAction(A.BuildForPreview, XamlPreviewPane.BuildForPreviewTitle, "Editor", context => IdeErrors.Observe(BuildForPreviewAsync(context.File!)))
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : session.Xaml.For(context.File) is null || ProjectModel.Owning(session.Projects, context.File.Path) is null ? "The previewer is for the XAML of an Avalonia, WPF or .NET MAUI project"
                : null,
            Words = ["preview", "build", "framework"],
        });
        Editors.HasXamlPreview = document => session.Xaml.For(document) is not null;
        Editors.PreviewToggleClicked += (_, group) => Request(group.PreviewAction ?? A.XamlPreview);
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
                // A preview with no previewer may have read the project before its restore landed, or seen its previewer end.
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
        buildRun.LettingGo = () => Task.WhenAll(previews.Values.Select(shown => shown.Preview.LetGoForBuildAsync()));
        FollowPreviews();
    }

    private void TogglePreview()
    {
        ShowPreviews(!previewing);
        if (keepsWorkspace && state is not null)
        {
            state.Previews.Set(state.Previews.Value with { Xaml = previewing });
        }
    }

    /// <summary>Previews on or off: each group's XAML file shown beside its preview, or none.</summary>
    private void ShowPreviews(bool on)
    {
        previewing = on;
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
        FollowPreviewers();
    }

    /// <summary>The previewers run for the previews shown and the last <see cref="KeptHidden"/> hidden; every other one rests.</summary>
    private void FollowPreviewers()
    {
        long now = ++previewsFollowed;
        foreach (ShownPreview shown in previews.Values.Where(p => Shows(p.Pane)))
        {
            shown.Seen = now;
            IdeErrors.Observe(shown.Preview.WakeAsync());
        }
        foreach (ShownPreview hidden in previews.Values.Where(p => p.Seen != now).OrderByDescending(p => p.Seen).Skip(KeptHidden))
        {
            hidden.Preview.Rest();
        }
    }

    /// <summary>Whether an editor group shows a preview's pane beside its file.</summary>
    private bool Shows(XamlPreviewPane pane) => Editors.Groups.Any(g => g.Side == pane);

    /// <summary>The programs on this machine that run a library project's code, in the folder's order: a desktop app's, not
    /// one of a device or the browser; none for a project that runs itself.</summary>
    private IReadOnlyList<ProjectModel> ProgramsOf(ProjectModel library) =>
        library.IsRunnable ? [] : [.. library.ProgramsRunning(session.Projects).Where(p => Run.Devices.DeviceTargets.PlatformsOf(p.TargetFrameworks).Count == 0 && !p.RunsInBrowser)];

    /// <summary>The programs running a library project's code (<see cref="ProgramsOf"/>), each by the assembly its build makes.</summary>
    private IReadOnlyList<string> ProgramsRunning(string project) =>
        session.Projects.FirstOrDefault(p => Passenger.Core.PathNames.Same(p.Path, project)) is { } library
            ? [.. ProgramsOf(library).Select(p => p.Property(Passenger.Host.CarriedProperties.TargetPath)).OfType<string>()]
            : [];

    private XamlPreviewPane PaneFor(OpenDocument shown, XamlDocument document)
    {
        if (previews.TryGetValue(shown, out ShownPreview? kept))
        {
            return kept.Pane;
        }
        XamlPreview preview = new(document) { ProgramsRunning = ProgramsRunning, Look = mauiLook };
        XamlPreviewPane pane = new(preview);
        pane.LookChosen += (_, look) => mauiLook = look;
        pane.ElementChosen += (_, element) => ChooseInEditor(pane, element);
        pane.ActionRequested += (_, action) => Request(action);
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
            && previews.TryGetValue(shown, out ShownPreview? kept) && kept.Pane == pane)
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

    /// <summary>Build for XAML Preview: a XAML file's project built for what its preview draws alone, a multi-targeted one's
    /// framework chosen, so no other framework's build holds the preview up, a MAUI project of platforms alone for none
    /// (<see cref="MauiPreviewHost.PlainBuild"/>); each preview then draws the build it made.</summary>
    private async Task BuildForPreviewAsync(OpenDocument document)
    {
        if (ProjectModel.Owning(session.Projects, document.Path) is not { } owning)
        {
            return;
        }
        // A MAUI project of platforms alone is built for none, which its preview draws and no platform's SDK is needed for.
        if (session.Xaml.For(document)?.Loading.Builder is { IsCompletedSuccessfully: true } loaded && loaded.Result?.Dialect is Passenger.Xaml.Dialects.MauiDialect
            && MauiPreviewHost.PlainBuild(owning) is { } plain)
        {
            if (work.Begin(A.BuildForPreview, $"Building {owning.Name} for no platform, which its preview draws…") is not { } plainWork)
            {
                Say(ActionWork.Busy(work.Of(A.BuildForPreview)!));
                return;
            }
            using (plainWork)
            {
                BuildOutcome built = await buildRun.BuildForAsync(new BuildStep(owning.Path, null) { Properties = plain }, plainWork);
                plainWork.Tell(built.Succeeded, built.Message);
            }
            return;
        }
        // A library's views are drawn in a program that runs them, whose build builds the library too.
        ProjectModel project = owning.IsRunnable ? owning : ProgramsOf(owning).FirstOrDefault() ?? owning;
        string? framework = session.Frameworks.Of(project);
        if (work.Begin(A.BuildForPreview, framework is null ? $"Building {project.Name} for its preview…" : $"Building {project.Name} for {framework}, which its preview draws…")
            is not { } working)
        {
            Say(ActionWork.Busy(work.Of(A.BuildForPreview)!));
            return;
        }
        using (working)
        {
            BuildOutcome built = await buildRun.BuildForAsync(new BuildStep(project.Path, framework), working);
            working.Tell(built.Succeeded, built.Message);
        }
    }

    /// <summary>A build that succeeded is what each preview draws from then on: a shown preview whose build it changed
    /// starts its previewer again, and a hidden one's ends, to start as it is shown; after any other end, a previewer
    /// that ended for the build starts again on the build that stands.</summary>
    private void PreviewsBuilt(BuildOutcome outcome)
    {
        foreach (ShownPreview shown in previews.Values)
        {
            IdeErrors.Observe(outcome.Status == BuildStatus.Succeeded ? shown.Preview.BuiltAsync(Shows(shown.Pane)) : shown.Preview.BuildFailedAsync(Shows(shown.Pane)));
        }
    }

    /// <summary>A project's framework chosen (null: every project's): its previews start again on that framework's build,
    /// one resting doing so as it is shown.</summary>
    private void RestartPreviews(string? project)
    {
        foreach (XamlPreview preview in previews.Values.Select(p => p.Preview)
            .Where(p => project is null || Passenger.Core.PathNames.Same(p.Document.Loading.ProjectPath, project)))
        {
            IdeErrors.Observe(preview.RestartAsync());
        }
    }

    private void EndPreview(OpenDocument document)
    {
        if (!previews.Remove(document, out ShownPreview? shown))
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

    /// <summary>A file's preview while it is kept: its pane, the pause in typing that sends the previewer the text, and
    /// the handler of the text's changes that restarts it.</summary>
    private sealed record ShownPreview(XamlPreview Preview, XamlPreviewPane Pane, Debounce Typing, EventHandler<DocumentChangedEventArgs> Changed)
    {
        /// <summary>When it was last shown, in <see cref="previewsFollowed"/>'s count.</summary>
        public long Seen { get; set; }
    }
}
