using System.Globalization;
using System.Runtime.CompilerServices;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.App.State;
using Passenger.App.WinForms;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>A Windows Forms form's or user control's design (<see cref="FormDesignPane"/>): its code file and its designer
/// file show its design beside their code or alone in the editor's place, as the form layout on the tab strip and under View
/// says (View Code, Code and Design, View Designer), the choice kept in the store's previews.json; the design follows the
/// designer code as it is typed, saved or changed on disk, and the project's builds; each file's design lasts while it is
/// open, its designer running while the design is shown.</summary>
public partial class MainWindow
{
    /// <summary>How long typing pauses before the designer is sent the code.</summary>
    private static readonly TimeSpan DesignSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>Each open form's design, by the file shown, while it lasts.</summary>
    private readonly Dictionary<OpenDocument, ShownDesign> designs = [];

    /// <summary>The form each open file is one of the files of (none for any other), read once as it is first asked.</summary>
    private readonly ConditionalWeakTable<OpenDocument, StrongBox<FormFiles?>> formsOf = [];

    /// <summary>A file's design while it lasts.</summary>
    public FormDesignPane? DesignPaneOf(OpenDocument document) => designs.TryGetValue(document, out ShownDesign? shown) ? shown.Pane : null;

    private void AddFormDesigner()
    {
        AddFormLayout(A.FormCodeOnly, "View Code", PaneView.Editor, ["Code Only", "Form's Code", "Hide Form Designer"]);
        AddFormLayout(A.FormCodeAndDesign, "Code and Design", PaneView.Split, ["Form Designer Beside the Code", "Form Designer Under the Code"]);
        AddFormLayout(A.FormDesignOnly, "View Designer", PaneView.Preview, ["Design Only", "Windows Forms Designer", "Form Designer"]);
        Editors.Designs = document => FormOf(document) is not null;
        Editors.DesignOf = DesignOf;
        Editors.FormLayoutChanged += (_, _) =>
        {
            if (keepsWorkspace && state is not null)
            {
                state.Previews.Set(state.Previews.Value with { FormLayout = Editors.FormLayout });
            }
            FollowDesigners();
        };
        Editors.ActiveChanged += (_, _) => FollowDesigners();
        Editors.Closed += (_, document) => EndDesign(document);
        Closed += (_, _) =>
        {
            foreach (OpenDocument document in designs.Keys.ToList())
            {
                EndDesign(document);
            }
        };
    }

    /// <summary>The designs follow a session from now: its builds, its trust and what changes its files on disk.</summary>
    private void BindDesigns(ProjectSession next)
    {
        foreach (OpenDocument document in designs.Keys.ToList())
        {
            EndDesign(document);
        }
        next.TrustChanged += (_, _) =>
        {
            if (session == next)
            {
                foreach (ShownDesign shown in designs.Values)
                {
                    IdeErrors.Observe(shown.Design.RestartAsync());
                }
            }
        };
        buildRun.BuildFinished += (_, _) =>
        {
            if (session == next)
            {
                IdeErrors.Observe(DesignsBuiltAsync());
            }
        };
    }

    /// <summary>A form's layout as an action, checked while it is in force: Code and Design chosen while it is turns the
    /// split, the design under the code or beside it, back to as the group's shape stands it.</summary>
    private void AddFormLayout(string id, string title, PaneView view, IReadOnlyList<string> words) =>
        actions!.Add(new IdeAction(id, title, "Editor", _ =>
        {
            PaneLayout layout = Editors.FormLayout;
            Editors.FormLayout = view == PaneView.Split && layout.View == view
                ? layout with { Stacked = Editors.ActiveGroup.TurnedSplit }
                : layout with { View = view };
        })
        {
            WhyNot = context => context.File is null ? Refusals.NoFile : FormOf(context.File) is null ? Refusals.NoForm : null,
            IsChecked = context => context.File is { } file && FormOf(file) is not null && Editors.FormLayout.View == view,
            Menus = view == PaneView.Preview ? [new MenuPlace("View", 4, 4)] : [],
            Words = words,
        });

    /// <summary>The form a file is one of the files of, read the first time it is asked; null for any other file.</summary>
    private FormFiles? FormOf(OpenDocument document)
    {
        if (!document.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || document.Diff is not null || document.Tool is not null)
        {
            return null;
        }
        if (!formsOf.TryGetValue(document, out StrongBox<FormFiles?>? known))
        {
            known = new StrongBox<FormFiles?>(FormFiles.Of(document.Path, path => Editors.TextOf(path)?.ToString()));
            formsOf.Add(document, known);
        }
        return known.Value;
    }

    /// <summary>A form's file's design, made as it is first asked for; null for any other file.</summary>
    private FormDesignPane? DesignOf(OpenDocument document)
    {
        if (designs.TryGetValue(document, out ShownDesign? kept))
        {
            return kept.Pane;
        }
        if (FormOf(document) is not { } files)
        {
            return null;
        }
        IReadOnlyList<ProjectModel> projects = session.Projects;
        FormDesign design = new(files, path => Editors.TextOf(path), form => BuiltFor(projects, form), form => GlobalUsingsOf(projects, form));
        FormDesignPane pane = new(design);
        pane.ComponentChosen += (_, chosen) => announcer.Tell(Said(chosen), Urgency.Polite, pane);
        Debounce typing = new(DesignSettle, design.UpdateAsync);
        EventHandler<DocumentChangedEventArgs> changed = (_, _) => typing.Restart();
        foreach (OpenDocument open in Editors.Documents.Where(d => PathNames.Same(d.Path, files.Designer) || PathNames.Same(d.Path, files.Code)))
        {
            open.Text.Changed += changed;
        }
        designs[document] = new ShownDesign(design, pane, typing, changed);
        IdeErrors.Observe(design.StartAsync());
        return pane;
    }

    /// <summary>A component chosen in a design as a screen reader hears it: its field, its type and its box.</summary>
    private static string Said(DesignedBox chosen) => string.Create(CultureInfo.InvariantCulture,
        $"{(chosen.Field.Length == 0 ? "The form" : chosen.Field)}, {chosen.Type}, at {chosen.X:0}, {chosen.Y:0}, {chosen.Width:0} by {chosen.Height:0}");

    /// <summary>The designers run for the designs shown; every other one rests, its last frame kept.</summary>
    private void FollowDesigners()
    {
        foreach (ShownDesign shown in designs.Values)
        {
            if (Editors.Groups.Any(g => g.Side == shown.Pane))
            {
                IdeErrors.Observe(shown.Design.WakeAsync());
            }
            else
            {
                shown.Design.Rest();
            }
        }
    }

    /// <summary>Files changed on disk: the designs reading them draw them again (an open file's change is its text's own).</summary>
    private void FormFilesChanged(IReadOnlyCollection<string> paths, bool every)
    {
        foreach (ShownDesign shown in designs.Values)
        {
            if (every || paths.Any(p => PathNames.Same(p, shown.Design.Files.Designer) || PathNames.Same(p, shown.Design.Files.Code)))
            {
                shown.Typing.Restart();
            }
        }
    }

    /// <summary>A build ended: each design whose project's build changed starts its designer again on it.</summary>
    private async Task DesignsBuiltAsync()
    {
        IReadOnlyList<ProjectModel> projects = session.Projects;
        foreach (ShownDesign shown in designs.Values.ToList())
        {
            FormFiles files = shown.Design.Files;
            string? newest = await BackgroundWork.Run(() => BuiltFor(projects, files));
            if (shown.Design.Outdated(newest))
            {
                await shown.Design.RestartAsync();
            }
        }
    }

    /// <summary>The assembly a form's project's last build made; null where it has none. Read off the window's thread.</summary>
    private static string? BuiltFor(IReadOnlyList<ProjectModel> projects, FormFiles files) =>
        ProjectModel.Owning(projects, files.Designer)?.Property(Passenger.Host.CarriedProperties.TargetPath) is { } built && File.Exists(built) ? built : null;

    /// <summary>The namespaces a form's project's global usings bring, as its build last wrote them (its obj folder's
    /// GlobalUsings.g.cs); none where it has written none. Read off the window's thread.</summary>
    private static IReadOnlyList<string> GlobalUsingsOf(IReadOnlyList<ProjectModel> projects, FormFiles files)
    {
        if (ProjectModel.Owning(projects, files.Designer)?.Property(Passenger.Host.CarriedProperties.ProjectAssetsFile) is not { } assets
            || Path.GetDirectoryName(assets) is not { } obj || !Directory.Exists(obj))
        {
            return [];
        }
        try
        {
            FileInfo? newest = new DirectoryInfo(obj).EnumerateFiles("*.GlobalUsings.g.cs", SearchOption.AllDirectories).MaxBy(f => f.LastWriteTimeUtc);
            if (newest is null)
            {
                return [];
            }
            const string Lead = "global using global::";
            return [.. File.ReadLines(newest.FullName).Select(l => l.Trim()).Where(l => l.StartsWith(Lead, StringComparison.Ordinal) && !l.Contains('='))
                .Select(l => l[Lead.Length..].TrimEnd(';').Trim())];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void EndDesign(OpenDocument document)
    {
        if (!designs.Remove(document, out ShownDesign? shown))
        {
            return;
        }
        shown.Typing.Stop();
        foreach (OpenDocument open in Editors.Documents)
        {
            open.Text.Changed -= shown.Changed;
        }
        document.Text.Changed -= shown.Changed;
        shown.Design.Dispose();
    }

    /// <summary>A file's design while it lasts: the design, its pane, the pause typing waits for, and its handler of the files' changes.</summary>
    private sealed record ShownDesign(FormDesign Design, FormDesignPane Pane, Debounce Typing, EventHandler<DocumentChangedEventArgs> Changed);
}
