using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Where a menu lists an action: the menu's path from the menu bar ("Git", "Navigate/Bookmarks"), the run of
/// items between two separators it is in, and its place in that run.</summary>
public sealed record MenuPlace(string Path, int Group, int Order);

/// <summary>One of the window's actions, declared once by the feature it belongs to: its id (what keymaps bind and agents
/// name), its title and group as Find Action lists it, where the menus list it, what it does, and whether it can run
/// where it is asked to. Its keys, its menu items, Find Action and an agent's ide_action all run it the same way.</summary>
public sealed record IdeAction(string Id, string Title, string Group, Action<ActionContext> Run)
{
    /// <summary>Null when it can run in this context, else why not in words for the status bar and an agent ("No file is
    /// open"). Its menu items are disabled and Find Action greys it while it says why not.</summary>
    public Func<ActionContext, string?> WhyNot { get; init; } = _ => null;

    public IReadOnlyList<MenuPlace> Menus { get; init; } = [];

    /// <summary>Its title in a menu opened where it would run, when that says more than <see cref="Title"/> (Unpin Tab on
    /// a pinned tab, Close on a tab's own menu); Find Action lists it by its title.</summary>
    public Func<ActionContext, string>? TitleIn { get; init; }

    /// <summary>For an action that turns something on and off (Soft-Wrap), whether it is on where it would run: its
    /// menu item shows a check then.</summary>
    public Func<ActionContext, bool>? IsChecked { get; init; }

    /// <summary>Whether its keys run it while the terminal has them; every other key there is the shell's.</summary>
    public bool FromTerminal { get; init; }

    /// <summary>An editor's own action (a movement, typing, the clipboard): its keys run it only in the editor with the
    /// keys, and anywhere else go on to whatever has them; a menu runs it where the keys are, a text box included.</summary>
    public bool InEditor { get; init; }

    /// <summary>Other names Find Action finds it by, such as an older name a person may still type (Split Vertically
    /// for Split Right).</summary>
    public IReadOnlyList<string> Words { get; init; } = [];

    /// <summary>The action whose work this one starts, which <see cref="ActionWork"/> holds it under while it works: its own
    /// unless it shares another's (Rebuild Solution is a build, as Build Solution is).</summary>
    public string? Works { get; init; }

    /// <summary>The id its work is held under.</summary>
    public string WorkId => Works ?? Id;

    /// <summary>For an action whose keys are another's where it applies, one key running two actions told apart by
    /// where it is pressed (the Explorer's Compare Files takes the editor's Duplicate keys in Visual Studio's keymap):
    /// that action, whose keys its menu items and Find Action show. Null with keys of its own, or none.</summary>
    public Func<string?>? KeysOf { get; init; }

    public bool CanRun(ActionContext context) => WhyNot(context) is null;

    /// <summary>Its title in a menu opened in this context.</summary>
    public string TitleWhere(ActionContext context) => TitleIn?.Invoke(context) ?? Title;
}

/// <summary>Where an action runs: the file the editor shows and its caret, the expression there, what has the keys (an
/// editor, a text box, a tool window and the row chosen in it), and the debugger's state. Made as the action is asked
/// for, before a menu or popup takes the keys.</summary>
public sealed class ActionContext(OpenDocument? file, CodeEditor editor, object? focused, ToolWindow? toolWindow, DebugState? debugging)
{
    private string? symbol;
    private bool symbolRead;

    /// <summary>The file the active editor shows, or null with none open.</summary>
    public OpenDocument? File => file;

    /// <summary>The active editor, which shows <see cref="File"/>.</summary>
    public CodeEditor Editor => editor;

    public TextPosition Caret => editor.CaretPosition;

    /// <summary>The selection, or the name at the caret with what it is a member of ("order.Total"), as the debugger
    /// evaluates it; null with neither.</summary>
    public string? Symbol
    {
        get
        {
            if (!symbolRead)
            {
                symbolRead = true;
                symbol = file is null ? null : SymbolAt(editor);
            }
            return symbol;
        }
    }

    /// <summary>The element with the keys, in the window in front.</summary>
    public object? Focused => focused;

    /// <summary>The tool window with the keys, if one has them.</summary>
    public ToolWindow? ToolWindow => toolWindow;

    /// <summary>What is chosen in the tool window with the keys (its rows, as that window gives them), or null.</summary>
    public object? Row => toolWindow?.Chosen?.Invoke();

    /// <summary>The debugged program's state, or null while nothing is debugged.</summary>
    public DebugState? Debugging => debugging;

    /// <summary>The tab whose own menu this is, which the tab actions act on in place of the one the active group shows.</summary>
    public (Editors.EditorGroup Group, OpenDocument File)? Tab { get; init; }

    /// <summary>The Explorer's row whose own menu this is, which the Explorer's actions act on in place of the one chosen
    /// there.</summary>
    public Explorer.ExplorerNode? ExplorerRow { get; init; }

    /// <summary>The .NET service of a run whose gutter menu this is, which the service actions act on in place of the one
    /// chosen in the Containers or Aspire window.</summary>
    public Run.ChosenService? Service { get; init; }

    /// <summary>The run configuration a gutter icon's menu launches, which Run, Debug and Profile of the program at hand
    /// launch in place of the Explorer row's or the open file's.</summary>
    public Run.RunConfiguration? Configuration { get; init; }

    /// <summary>The request of an .http file whose gutter menu this is, with its file as the editor has it, which Test
    /// Request sends in place of the one chosen in the Endpoints window.</summary>
    public (Endpoints.EndpointRequest Request, Endpoints.HttpFile File)? Request { get; init; }

    /// <summary>The same place with a tool window's own button asking, which runs its action on that window without taking
    /// the keys.</summary>
    public ActionContext In(ToolWindow window) => new(file, editor, focused, window, debugging)
    {
        Tab = Tab,
        ExplorerRow = ExplorerRow,
        Service = Service,
        Configuration = Configuration,
        Request = Request,
    };

    private static string? SymbolAt(CodeEditor editor)
    {
        Selection primary = editor.Selections.Primary;
        TextSnapshot text = editor.Document.Snapshot;
        return !primary.IsEmpty ? text.GetText(primary.Start, primary.Length) : DebugController.ExpressionAt(text, primary.Caret);
    }
}
