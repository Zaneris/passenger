using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.App.State;
using Passenger.App.WinForms;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.WinForms;
using Passenger.Text;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>A Windows Forms form's or user control's design (<see cref="FormDesignPane"/>): its code file and its designer
/// file show its design beside their code or alone in the editor's place, as the form layout on the tab strip and under View
/// says (View Code, Code and Design, View Designer), the choice kept in the store's previews.json; the design follows the
/// designer code as it is typed, saved or changed on disk, and the project's builds; each file's design lasts while it is
/// open, its designer running while the design is shown. Its edits (a gesture, a key, the Toolbox, Delete, Bring to Front,
/// Send to Back) are written into the designer file as one command of global Undo each, which Undo in the design takes back.</summary>
public partial class MainWindow
{
    private ToolboxView? toolbox;
    private FormProperties? formProperties;

    /// <summary>The properties and events of the component chosen in a design, under the Structure window's outline of its
    /// components, made as one is first outlined.</summary>
    public FormProperties? FormProperties => formProperties;

    /// <summary>The Toolbox tool window, built as it is first shown or asked for.</summary>
    public ToolboxView ToolboxWindow => toolbox ?? (ToolboxView)Dock[ToolWindowIds.Toolbox].Content;

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
        Editors.FormLayoutChanged += (_, _) => structure?.FollowActive();
        AddDesignEdits();
        // Built as it is first shown, over the design shown then.
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Toolbox, "Toolbox", IconKeys.FormsApp, () =>
        {
            ToolboxView view = new();
            toolbox = view;
            view.AddRequested += (_, entry) => AddFromToolbox(entry, null);
            view.Dragging += (_, dragged) => DesignUnder(dragged.Pointer, out Point? at)?.ShowDrop(at);
            view.Dropped += (_, dropped) =>
            {
                HideDrops();
                if (DesignUnder(dropped.Pointer, out Point? at) is { } pane && at is { } point)
                {
                    AddFromToolbox(dropped.Entry, (pane, point));
                }
            };
            view.DragLost += (_, _) => HideDrops();
            FollowToolbox();
            return view;
        }, Docking.ToolAnchor.LeftBottom)
        {
            Serves = ToolWindowServes.FormsProjects,
            Action = A.ShowToolbox,
            ExpandAll = () => ToolboxWindow.ExpandAll(),
            CollapseAll = () => ToolboxWindow.CollapseAll(),
        });
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
        FormDesign design = new(files, path => Editors.TextOf(path), () =>
        {
            IReadOnlyList<ProjectModel> projects = session.Projects;
            return new FormProject(form => BuiltFor(projects, form), form => GlobalUsingsOf(projects, form),
                form => ProjectModel.Owning(projects, form.Designer)?.Property(Passenger.Host.CarriedProperties.Nullable) is "enable" or "annotations");
        });
        FormDesignPane pane = new(design);
        Debounce typing = new(DesignSettle, design.UpdateAsync);
        EventHandler<DocumentChangedEventArgs> changed = (_, _) => typing.Restart();
        foreach (OpenDocument open in Editors.Documents.Where(d => PathNames.Same(d.Path, files.Designer) || PathNames.Same(d.Path, files.Code)))
        {
            open.Text.Changed += changed;
        }
        ShownDesign shown = new(design, pane, typing, changed);
        designs[document] = shown;
        pane.ComponentChosen += (_, chosen) =>
        {
            announcer.Tell(Said(chosen), Urgency.Polite, pane);
            FollowChoice(shown, chosen.Field);
        };
        pane.EditRequested += (_, edit) => IdeErrors.Observe(EditDesignAsync(shown, edit));
        pane.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowDesignMenu(pane);
        };
        pane.DefaultEventRequested += (_, box) => IdeErrors.Observe(HookAsync(shown, box.Field, box.DefaultEvent!, null));
        design.ToolboxChanged += (_, _) => FollowToolbox();
        design.Changed += (_, _) =>
        {
            FollowToolbox();
            if (Editors.ActiveGroup.Side == pane)
            {
                structure?.Invalidate();
            }
        };
        IdeErrors.Observe(design.StartAsync());
        return pane;
    }

    /// <summary>A component chosen in a design: its properties listed, its row chosen in the Structure window's outline and
    /// its properties shown under it, where the window outlines that design.</summary>
    private void FollowChoice(ShownDesign shown, string field)
    {
        shown.Design.Inspect(field);
        if (structure is { } view && Editors.ActiveGroup.Side == shown.Pane)
        {
            view.ChooseRow(row => DesignOutline.FieldOf(row) == field);
            view.Details = PropertiesOf(shown);
        }
    }

    /// <summary>The Structure window's outline of a file while its design is shown in the active group: its components.</summary>
    private Navigation.FileOutline? DesignOutlineOf(OpenDocument document) =>
        designs.TryGetValue(document, out ShownDesign? shown) && Editors.ActiveGroup.Side == shown.Pane ? new DesignOutline(shown.Design) : null;

    /// <summary>A row of the Structure window chosen: where it is a design's component, chosen in the design too, its properties
    /// and events under the outline; false for a row of any other outline.</summary>
    private bool ComponentRowChosen(Navigation.SymbolRow? row)
    {
        if (row is null || DesignOutline.FieldOf(row) is not { } field || structure is not { } view || Editors.ActiveGroup.Side is not FormDesignPane pane
            || designs.Values.FirstOrDefault(d => d.Pane == pane) is not { } shown)
        {
            return false;
        }
        pane.Choose(field);
        shown.Design.Inspect(field);
        view.Details = PropertiesOf(shown);
        return true;
    }

    /// <summary>The Structure window's rows read again over a design (none chosen as they come): its properties stay under
    /// them, the inspected component's row chosen again; false where it outlines no design.</summary>
    private bool DesignOutlined()
    {
        if (structure is not { } view || Editors.Active is not { } active || !designs.TryGetValue(active, out ShownDesign? shown) || Editors.ActiveGroup.Side != shown.Pane)
        {
            return false;
        }
        view.Details = PropertiesOf(shown);
        if (shown.Design.Inspected is { } field)
        {
            UiThread.Post(() => view.ChooseRow(row => DesignOutline.FieldOf(row) == field));
        }
        return true;
    }

    /// <summary>The properties pane over a design, made once and wired to its edits.</summary>
    private FormProperties PropertiesOf(ShownDesign shown)
    {
        if (formProperties is null)
        {
            formProperties = new FormProperties((popup, from) => popups.Show(popup, popups.PointIn(from, new Avalonia.Point(0, from.Bounds.Height + 4))), () => popups.Close());
            formProperties.SetRequested += (_, set) => DesignShown(shown => EditDesignAsync(shown, Edit(shown, DesignEditKinds.Set, set.Property, set.Text)));
            formProperties.ResetRequested += (_, property) => DesignShown(shown => EditDesignAsync(shown, Edit(shown, DesignEditKinds.Reset, property, null)));
            formProperties.UnhookRequested += (_, raised) => DesignShown(shown => EditDesignAsync(shown, Edit(shown, DesignEditKinds.Unhook, raised, null)));
            formProperties.HookRequested += (_, hook) => DesignShown(shown => HookAsync(shown, shown.Design.Inspected ?? string.Empty, hook.Event, hook.Handler));
            formProperties.RenameRequested += (_, name) => DesignShown(shown => RenameComponentAsync(shown, shown.Design.Inspected ?? string.Empty, name));
        }
        formProperties.Show(shown.Design);
        return formProperties;

        static DesignEdit Edit(ShownDesign shown, string kind, string member, string? text) =>
            new(kind) { Components = [shown.Design.Inspected ?? string.Empty], Member = member, Text = text };

        void DesignShown(Func<ShownDesign, Task> act)
        {
            if (formProperties?.Shown is { } design && designs.Values.FirstOrDefault(d => d.Design == design) is { } now)
            {
                IdeErrors.Observe(act(now));
            }
        }
    }

    /// <summary>A handler hooked to a component's event (the one named, else the one hooked or the designer's name for it),
    /// declared in the form's code where it declares none, all one command of Undo; then gone to in the code, which the
    /// form's file shows.</summary>
    private async Task HookAsync(ShownDesign shown, string field, string raised, string? handler)
    {
        DesignEdited done = await EditDesignAsync(shown, new DesignEdit(DesignEditKinds.Hook) { Components = [field], Member = raised, Text = handler });
        if (done.Refused is null && done.Handler is { } method)
        {
            GoToHandler(shown.Design.Files, method);
        }
    }

    /// <summary>The form's code shown at a handler's body, the design giving way to it as View Code does.</summary>
    private void GoToHandler(FormFiles files, string method)
    {
        if (Editors.TextOf(files.Code) is not { } text || DesignerCode.MethodOf(text.ToString(), files.Class, method) is not { } declared)
        {
            Refuse($"{Path.GetFileName(files.Code)} declares no {method}");
            return;
        }
        if (Editors.FormLayout.View == PaneView.Preview)
        {
            Editors.FormLayout = Editors.FormLayout with { View = PaneView.Editor };
        }
        string written = text.ToString();
        int body = Passenger.Text.TextNavigation.NextLineStart(written, declared.Body);
        NavigateTo(files.Code, text.GetPosition(Passenger.Text.TextNavigation.LineEnd(written, body)));
    }

    /// <summary>A component renamed as (Name) asks: its field through a rename of the code, which takes its Name and its
    /// section's comment along, one command of Undo; the form itself keeps its class's name.</summary>
    private async Task RenameComponentAsync(ShownDesign shown, string field, string name)
    {
        if (field.Length == 0)
        {
            Refuse(Refusals.FormNameIsItsClass);
            return;
        }
        FormFiles files = shown.Design.Files;
        (OpenDocument document, IDisposable hold) = Editors.Lend(files.Designer);
        try
        {
            if (DesignerCode.Read(document.Text.Snapshot.ToString(), files.Class) is not { } source
                || source.Fields.ToList().FindIndex(f => f.Name == field) is not (>= 0 and var at))
            {
                Refuse($"{Path.GetFileName(files.Designer)} declares no field {field}");
                return;
            }
            RefactorOutcome renamed = await Assist.Actions.RenameSymbolAsync(document, source.Layout.Fields[at].Named, name, renameFiles: false, patience: null);
            if (renamed.Problem is { } why)
            {
                Refuse(why);
                return;
            }
            shown.Design.Inspect(name);
            shown.Pane.Choose(name);
            announcer.Tell(renamed.Said, Urgency.Polite, shown.Pane);
        }
        finally
        {
            hold.Dispose();
        }
    }

    /// <summary>Delete, Bring to Front and Send to Back of the components chosen in the design with the keys, in its menu and
    /// Find Action; Delete takes the Delete key there.</summary>
    private void AddDesignEdits()
    {
        actions!.Add(new IdeAction(A.FormDelete, "Delete", "Design", context => Edit(context, DesignEditKinds.Delete))
        {
            WhyNot = context => DesignWhyNot(context),
            Words = ["Delete Component", "Remove Control"],
        });
        actions.Add(new IdeAction(A.FormBringToFront, "Bring to Front", "Design", context => Edit(context, DesignEditKinds.Front))
        {
            WhyNot = context => DesignWhyNot(context, orders: true),
            Words = ["Order", "Z-order"],
        });
        actions.Add(new IdeAction(A.FormSendToBack, "Send to Back", "Design", context => Edit(context, DesignEditKinds.Back))
        {
            WhyNot = context => DesignWhyNot(context, orders: true),
            Words = ["Order", "Z-order"],
        });

        void Edit(ActionContext context, string kind)
        {
            if (DesignWith(context) is { } shown && shown.Pane.Chosen is { } chosen)
            {
                IdeErrors.Observe(EditDesignAsync(shown, new DesignEdit(kind) { Components = [chosen.Field] }));
            }
        }
    }

    /// <summary>The design with the keys, as an action is asked of it.</summary>
    private ShownDesign? DesignWith(ActionContext context) =>
        (context.Focused as Visual)?.FindAncestorOfType<FormDesignPane>(includeSelf: true) is { } pane ? designs.Values.FirstOrDefault(d => d.Pane == pane) : null;

    /// <summary>Why an action on a design's chosen component greys: no design has the keys, none of its components is chosen,
    /// the form itself is, or its designer does not run.</summary>
    private string? DesignWhyNot(ActionContext context, bool orders = false) => DesignWith(context) is not { } shown ? Refusals.NoFormDesign
        : shown.Pane.Chosen is not { } chosen ? Refusals.NoComponentChosen
        : chosen.Field.Length == 0 ? Refusals.FormItselfStays
        : orders && FormDesignPane.InTray(chosen) ? Refusals.TrayHasNoOrder
        : !shown.Design.Running ? shown.Design.Status
        : null;

    /// <summary>A design's menu: Delete, Bring to Front, Send to Back, then View Code.</summary>
    private void ShowDesignMenu(FormDesignPane pane)
    {
        ActionContext context = actions!.Context();
        MenuEntry separator = new(null);
        List<Control> items = [.. new[] { new MenuEntry(A.FormDelete), separator, new MenuEntry(A.FormBringToFront), new MenuEntry(A.FormSendToBack), separator,
            new MenuEntry(A.FormCodeOnly) }.Select(entry => FlyoutEntry(entry, context))];
        Open(new ContextMenu { ItemsSource = items, PlacementTarget = pane }, pane);
    }

    /// <summary>An edit of a design made and written as one command of global Undo, named as the Edit menu names it; said to
    /// a screen reader once made, the component it made or moved chosen; why not in the status bar.</summary>
    private async Task<DesignEdited> EditDesignAsync(ShownDesign shown, DesignEdit edit)
    {
        string named = edit.Components is [var only] ? only.Length == 0 ? "the Form" : only : string.Join(", ", edit.Components);
        string title = edit.Kind switch
        {
            DesignEditKinds.Add => $"Add {edit.Type?.Split(',')[0].Split('.')[^1]}",
            DesignEditKinds.Move => edit.Parent is { } into ? $"Move {named} into {(into.Length == 0 ? "the Form" : into)}" : $"Move {named}",
            DesignEditKinds.Resize => $"Resize {named}",
            DesignEditKinds.Delete => $"Delete {named}",
            DesignEditKinds.Front => $"Bring {named} to Front",
            DesignEditKinds.Back => $"Send {named} to Back",
            DesignEditKinds.Set => $"Set {named}'s {edit.Member}",
            DesignEditKinds.Reset => $"Reset {named}'s {edit.Member}",
            DesignEditKinds.Hook => $"Handle {named}'s {edit.Member}",
            _ => $"Remove {named}'s {edit.Member} Handler",
        };
        DesignEdited done = await shown.Design.EditAsync(edit, title, (name, changes) => Assist.ApplyFileChangesAsync(name, changes));
        if (done.Refused is { } why)
        {
            Refuse(why);
            return done;
        }
        if (done.Chosen is { } chosen)
        {
            shown.Pane.Choose(chosen);
            FollowChoice(shown, chosen);
        }
        announcer.Tell(shown.Pane.Chosen is { } now && edit.Kind != DesignEditKinds.Delete ? $"{title}: {Said(now)}" : title, Urgency.Polite, shown.Pane);
        return done;
    }

    /// <summary>A Toolbox item added to the design shown: where it was dropped on one, else into the chosen container of the
    /// active group's (the chosen component's holder where it holds none), where its container's designer places it.</summary>
    private void AddFromToolbox(ToolboxEntry entry, (FormDesignPane Pane, Point At)? dropped)
    {
        FormDesignPane? pane = dropped?.Pane ?? Editors.ActiveGroup.Side as FormDesignPane;
        if (pane is null || designs.Values.FirstOrDefault(d => d.Pane == pane) is not { } shown)
        {
            Refuse(Refusals.NoFormDesign);
            return;
        }
        DesignEdit edit = dropped is { At: var at }
            ? new DesignEdit(DesignEditKinds.Add) { Type = entry.Type, X = (int)at.X, Y = (int)at.Y }
            : new DesignEdit(DesignEditKinds.Add) { Type = entry.Type, Parent = pane.Chosen is { Holds: true } holder ? holder.Field : pane.Chosen?.Parent ?? string.Empty };
        IdeErrors.Observe(EditDesignAsync(shown, edit));
    }

    /// <summary>The design shown under a pointer, with the point of its frame there; none (and its drop mark hidden) elsewhere.</summary>
    private FormDesignPane? DesignUnder(PointerEventArgs pointer, out Point? at)
    {
        at = null;
        FormDesignPane? found = null;
        foreach (ShownDesign shown in designs.Values)
        {
            if (found is null && shown.Pane.FrameAt(pointer) is { } point)
            {
                found = shown.Pane;
                at = point;
                continue;
            }
            shown.Pane.ShowDrop(null);
        }
        return found;
    }

    private void HideDrops()
    {
        foreach (ShownDesign shown in designs.Values)
        {
            shown.Pane.ShowDrop(null);
        }
    }

    /// <summary>The Toolbox lists the items of the design the active group shows, or says why it lists none.</summary>
    private void FollowToolbox()
    {
        if (toolbox is null)
        {
            return;
        }
        FormDesign? design = Editors.ActiveGroup.Side is FormDesignPane pane ? pane.Design : null;
        toolbox.Show(design?.Toolbox ?? [], design is null ? Refusals.NoFormDesign : design.Status);
    }

    /// <summary>The keys a design with the keys takes from the editor's actions: Undo and Redo take back or make again its
    /// file's changes, Delete deletes its chosen component.</summary>
    private bool DesignTakes(FormDesignPane pane, string action)
    {
        switch (action)
        {
            case Passenger.Editor.EditorActions.Undo or Passenger.Editor.EditorActions.Redo:
                return UndoDesign(pane, action == Passenger.Editor.EditorActions.Redo);
            case Passenger.Editor.EditorActions.Delete:
                Request(A.FormDelete);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Undo or Redo asked in a design: the design's file's own step where it is open, else the last command, where
    /// it changed that file, taken back unasked, as the design shows what it takes back; true once taken.</summary>
    private bool UndoDesign(FormDesignPane pane, bool redo)
    {
        if (designs.Values.FirstOrDefault(d => d.Pane == pane) is not { } shown)
        {
            return false;
        }
        string file = shown.Design.Files.Designer;
        if (Editors.Find(file) is { } open)
        {
            if (!UndoCommand(redo, open, asks: false))
            {
                if (!(redo ? open.Text.CanRedo : open.Text.CanUndo))
                {
                    Refuse(redo ? Refusals.NothingToRedo : Refusals.NothingToUndo);
                }
                Editors.Step(open, redo);
            }
            return true;
        }
        if ((redo ? Editors.Commands.ToRedo(null) : Editors.Commands.ToUndo(null)) is { } command && command.Texts.Any(t => PathNames.Same(t.Path, file)))
        {
            UndoCommand(redo, null, asks: false);
            return true;
        }
        Refuse(redo ? Refusals.NothingToRedo : Refusals.NothingToUndo);
        return true;
    }

    /// <summary>A component chosen in a design as a screen reader hears it: its field, its type and its box.</summary>
    private static string Said(DesignedBox chosen) => string.Create(CultureInfo.InvariantCulture,
        $"{(chosen.Field.Length == 0 ? "The form" : chosen.Field)}, {chosen.Type}, at {chosen.X:0}, {chosen.Y:0}, {chosen.Width:0} by {chosen.Height:0}");

    /// <summary>The designers run for the designs shown; every other one rests, its last frame kept.</summary>
    private void FollowDesigners()
    {
        FollowToolbox();
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
