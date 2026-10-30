using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App.Debugging;
using Passenger.Editor;
using E = Passenger.Editor.EditorActions;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Right-click menus: the editor's for what is under the click (the caret goes there first, unless the click
/// is in the selection), built from the window's actions with the keymap's shortcuts, items that cannot run disabled;
/// and the menu key (Shift+F10) opening the focused place's menu at its caret or chosen row.</summary>
public partial class MainWindow
{
    /// <summary>The context menu open now, if any.</summary>
    public ContextMenu? OpenContextMenu { get; private set; }

    private void AddContextMenus()
    {
        Editors.ForEachEditor(editor => editor.ContextMenuRequested += (_, at) => ShowEditorMenu(editor, at));
        actions!.Add(new IdeAction(A.ShowContextMenu, "Show Context Menu", "IDE", context => ShowFocusedContextMenu(context.Focused)));
    }

    /// <summary>The editor's menu: context actions and the clipboard, Find Usages, Refactor, Go To, Generate and
    /// Folding, Reformat and Cleanup, an HTML page's Open in Browser, the test at the caret, the paused program's actions,
    /// breakpoints, Local History and Git.</summary>
    private void ShowEditorMenu(CodeEditor editor, Point? at)
    {
        if (Editors.Groups.FirstOrDefault(g => g.Editor == editor) is { } group)
        {
            Editors.SetActiveGroup(group);
        }
        if (Editors.Active is not { } active || editor != Editors.CodeEditor)
        {
            return;
        }
        // The items read what can run from where the menu opened, before it takes the keys.
        ActionContext context = actions!.Context();
        List<Control> items = [];
        void Add(params MenuEntry[] entries) => items.AddRange(entries.Select(entry => FlyoutEntry(entry, context)));
        MenuEntry separator = new(null);
        Add(Item(A.QuickFix), separator, Item(E.Cut), Item(E.Copy), Item(E.Paste), separator,
            Item(A.FindUsages),
            Sub("Refactor", Item(A.RefactorThis), separator, Item(A.Rename), Item(A.ExtractMethod), Item(A.IntroduceVariable), Item(A.Inline)),
            Sub("Go To", Item(A.GoToDeclaration), Item(A.DerivedSymbols), Item(A.GoToImplementation), Item(A.GoToTypeDeclaration), Item(A.BaseSymbols),
                separator, Item(A.CallHierarchy), Item(A.TypeHierarchy)),
            Item(A.Generate),
            Sub("Folding", Item(E.Unfold), Item(E.UnfoldAll), separator, Item(E.Fold), Item(E.FoldToDefinitions), Item(E.FoldAll), separator,
                Item(E.UnfoldDocComments), Item(E.FoldDocComments), separator, Item(E.ToggleFold)),
            separator,
            Item(A.Reformat), Item(A.CodeCleanup));
        if (Web.WebLanguages.LanguageOf(active.Path) is not null || session.Web?.Prettier.PackageFor(active.Path) is not null)
        {
            Add(Item(A.ReformatWithPrettier), Item(A.FixEslintProblems));
        }
        if (Languages.Language.ForPath(active.Path) == Languages.Language.Html)
        {
            Add(separator, Item(A.OpenInBrowser));
        }
        if (TestAt(active, editor.CaretPosition.Line) is { } test)
        {
            string name = test.Method ?? test.Type[(test.Type.LastIndexOfAny(['.', '+']) + 1)..];
            items.Add(new Separator());
            items.Add(Command($"Run '{name}'", () => RunTestsAt(test.Type, test.Method, debug: false)));
            items.Add(Command($"Debug '{name}'", () => RunTestsAt(test.Type, test.Method, debug: true)));
            items.Add(Command($"Run '{name}' Until Failure", () => RunTestsAt(test.Type, test.Method, debug: false, untilFailure: true)));
        }
        Add(separator);
        if (session.Debug.Session is { State: DebugState.Paused })
        {
            Add(Item(A.RunToCursor), Item(A.SetNextStatement), Item(A.Evaluate), Item(A.AddToWatches), separator);
        }
        Add(Item(A.ToggleBreakpoint));
        Add(separator, Sub("Local History", Item(A.ShowLocalHistory), separator, Item(A.ShowProjectHistory), Item(A.RecentChanges), Item(A.PutLabel)));
        if (session.Vcs.Repository is not null)
        {
            Add(Sub("Git", Item(A.Annotate), Item(A.ShowDiff), Item(A.CompareWithRevision), Item(A.CompareWithBranch), separator,
                Item(A.ShowHistory), Item(A.ShowHistoryForSelection), separator, Item(A.RollbackLines)));
        }
        ContextMenu menu = new() { ItemsSource = items, PlacementTarget = editor };
        if (at is null)
        {
            // From the keyboard: under the caret's line.
            Point caret = editor.PointOf(editor.Selections.Primary.Caret);
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.PlacementRect = new Rect(caret, new Size(1, editor.LineHeight));
        }
        Open(menu, editor);

        static MenuEntry Item(string action) => new(action);
        static MenuEntry Sub(string title, params MenuEntry[] entries) => new(null, title, entries);
    }

    /// <summary>The menu key (Shift+F10): the menu of the place with the focus, in whichever of the IDE's windows it was
    /// pressed, at its caret or under its chosen row.</summary>
    private static void ShowFocusedContextMenu(object? focused)
    {
        switch (focused)
        {
            case CodeEditor editor:
                editor.RequestContextMenu();
                break;
            case Control control:
                // The menu a request from it opens (its own, or its list's), placed under it; one its owner makes is placed there.
                Action? back = control.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.ContextMenu is not null)?.ContextMenu is { } menu
                    ? ToolWindows.ListMenus.PlaceForKeys(menu, control)
                    : null;
                control.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
                if (back is not null && control.GetSelfAndVisualAncestors().OfType<Control>().All(c => c.ContextMenu is not { IsOpen: true }))
                {
                    back();
                }
                break;
        }
    }

    /// <summary>Opens a context menu, remembering it while it is open, the keys kept in it (<see cref="KeepKeys"/>). A
    /// widget's (the project's, the branch's, the run configuration's) drops from under it, where one at the pointer
    /// covered the widget.</summary>
    private void Open(ContextMenu menu, Control target)
    {
        if (target is Button && !menu.IsSet(ContextMenu.PlacementProperty))
        {
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        }
        OpenContextMenu?.Close();
        OpenContextMenu = menu;
        Action release = KeepKeys(target, () => menu.IsOpen ? menu : null);
        menu.Closed += (_, _) =>
        {
            release();
            if (OpenContextMenu == menu)
            {
                OpenContextMenu = null;
            }
        };
        menu.Open(target);
    }

    /// <summary>Keeps the keys with a menu while it is open (it takes them as it opens): a window brought back to the front,
    /// a dialog over it answered (an agent's question), gives its focus back to what had it before the menu, since a menu
    /// keeps its own, so the menu or its chosen item takes it again. A desktop closes the menu as the dialog takes the
    /// front; headless, nothing does. Gives what ends the keeping.</summary>
    private static Action KeepKeys(Control target, Func<MenuBase?> open)
    {
        if (TopLevel.GetTopLevel(target) is not WindowBase window)
        {
            return () => { };
        }
        // Posted: the window puts its own focus back after its Activated.
        void Back(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (open() is not { IsKeyboardFocusWithin: false } menu)
            {
                return;
            }
            Control? chosen = menu.SelectedIndex >= 0 ? menu.ContainerFromIndex(menu.SelectedIndex) : null;
            if (chosen?.Focus() != true && !menu.Focus())
            {
                menu.GetLogicalChildren().OfType<MenuItem>().FirstOrDefault(i => i.IsEffectivelyEnabled)?.Focus();
            }
        });
        window.Activated += Back;
        return () => window.Activated -= Back;
    }

    /// <summary>A menu item that runs something other than one of the window's actions (a test by name).</summary>
    private static MenuItem Command(string header, Action run)
    {
        MenuItem item = new() { Header = header };
        item.Click += (_, _) => run();
        return item;
    }
}
