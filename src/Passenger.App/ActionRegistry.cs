using Avalonia.Controls;
using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A window's actions, each added once by the feature it belongs to, and the keymap that binds them: what every
/// list of actions reads (Find Action, the menus, Settings' keymap, an agent's ide_actions) and what every way of running
/// one goes through (its keys, a menu item, Find Action, ide_action). An action whose work is under way starts nothing
/// more, however it is asked for, and says what is under way.</summary>
public sealed class ActionRegistry(Func<ActionContext> context, Func<Keymap> keymap, ActionWork work)
{
    private readonly Dictionary<string, IdeAction> byId = new(StringComparer.Ordinal);
    private IReadOnlyList<IdeAction>? sorted;

    /// <summary>Adds an action; an id added twice is a mistake in the code, never a choice between two.</summary>
    public void Add(IdeAction action)
    {
        if (!byId.TryAdd(action.Id, action))
        {
            throw new InvalidOperationException($"The action {action.Id} is added twice");
        }
        sorted = null;
    }

    /// <summary>The action of an id, or of an id it had before (<see cref="AppActions.Formerly"/>).</summary>
    public IdeAction? this[string id] => byId.GetValueOrDefault(AppActions.Now(id));

    /// <summary>What the window's actions are doing that takes a while.</summary>
    public ActionWork Work => work;

    /// <summary>An action about to run, however it was asked for: Local History names what a refactoring writes by it.</summary>
    public event EventHandler<IdeAction>? Running;

    /// <summary>Every action, in the order Find Action lists them before anything is typed: by group, then title.</summary>
    public IReadOnlyList<IdeAction> All => sorted ??=
        [.. byId.Values.OrderBy(a => a.Group, StringComparer.Ordinal).ThenBy(a => a.Title, StringComparer.Ordinal)];

    /// <summary>The window's keymap, the person's shortcuts over the platform's.</summary>
    public Keymap Keymap => keymap();

    /// <summary>Where an action asked for now runs: what the window shows and what has its keys.</summary>
    public ActionContext Context() => context();

    /// <summary>An action's first shortcut as menus and tips show it ("⌘K, W"), or null with none.</summary>
    public string? Shortcut(string id) => Keymap.ShortcutsFor(KeysOf(id)) is { Count: > 0 } keys ? keys[0].Display() : null;

    /// <summary>An action's one-keystroke shortcut as a menu shows it; none for a chord, which a menu cannot show.</summary>
    public KeyGesture? Gesture(string id) =>
        Keymap.ShortcutsFor(KeysOf(id)).FirstOrDefault(s => s.Second is null) is { } shortcut ? new KeyGesture(shortcut.First.Key, shortcut.First.Modifiers) : null;

    /// <summary>An action's one-keystroke shortcut as macOS's menu bar takes it, a key equivalent: none on the keypad, since
    /// Avalonia gives the bar a keypad key as its character alone, which the main keys' / or - would press too.</summary>
    public KeyGesture? MenuBarGesture(string id) =>
        Keymap.ShortcutsFor(KeysOf(id)).FirstOrDefault(s => s.Second is null && !s.First.IsOnKeypad) is { } shortcut
            ? new KeyGesture(shortcut.First.Key, shortcut.First.Modifiers) : null;

    /// <summary>An action's shortcut in a menu item's column of keys, whoever builds the menu: its one keystroke as the
    /// item's gesture, a chord as its text. The gesture is shown, not bound: the window's keymap runs the keys.</summary>
    public void ShowKeys(MenuItem item, string id)
    {
        item.InputGesture = Gesture(id);
        if (item.InputGesture is null && Keymap.ShortcutsFor(KeysOf(id)).FirstOrDefault() is { Second: not null } chord)
        {
            ShowChord(item, chord);
        }
    }

    /// <summary>The action whose keys run this one: another's where it shares them (<see cref="IdeAction.KeysOf"/>), else its own.</summary>
    private string KeysOf(string id) => byId.GetValueOrDefault(id)?.KeysOf?.Invoke() ?? id;

    /// <summary>A chord in an item's shortcut column ("⌘E, C"): a KeyGesture holds one keystroke, so the text Fluent's
    /// item draws its gesture with is given the chord. macOS's menu bar gets none, since a key equivalent there would
    /// take the chord's first key before the window could wait for its second.</summary>
    private static void ShowChord(MenuItem item, Shortcut chord) => item.TemplateApplied += (_, e) =>
    {
        if (e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } keys)
        {
            keys.Text = chord.Display();
        }
    };

    /// <summary>The actions whose title, group, id or other names have every word typed, those whose title or other name
    /// starts with the query first.</summary>
    public IReadOnlyList<IdeAction> Find(string query)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return All;
        }
        string typed = query.Trim();
        IEnumerable<string> Names(IdeAction action) => [action.Title, .. action.Words];
        return [.. All
            .Where(a => words.All(w => Names(a).Any(n => n.Contains(w, StringComparison.OrdinalIgnoreCase)) || a.Group.Contains(w, StringComparison.OrdinalIgnoreCase)
                || a.Id.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(a => Names(a).Any(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) ? 0
                : Names(a).Any(n => words.All(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))) ? 1 : 2)
            .ThenBy(a => a.Title.Length)];
    }

    /// <summary>Null when the action can run in this context, else why not: no action has the id, its work is under way
    /// already, or what it needs is not there.</summary>
    public string? WhyNot(string id, ActionContext context) =>
        !byId.TryGetValue(AppActions.Now(id), out IdeAction? action) ? $"No action is called {id}"
        : work.Of(action.WorkId) is { } under ? ActionWork.Busy(under)
        : action.WhyNot(context);

    /// <summary>Runs an action where it is asked for now; null once it has run, else why it could not.</summary>
    public string? Run(string id) => Run(id, Context());

    /// <summary>Runs an action an agent asks for (ide_action) where it is asked for now, without the person's keys or a
    /// window brought forward (<see cref="ActionContext.ByAgent"/>); null once it has run, else why it could not.</summary>
    public string? RunForAgent(string id) => Run(id, Context().ForAgent());

    /// <summary>Runs an action in a context made before something else took the keys (a menu, a popup); null once it has
    /// run, else why it could not.</summary>
    public string? Run(string id, ActionContext context)
    {
        if (WhyNot(id, context) is { } why)
        {
            return why;
        }
        IdeAction action = byId[AppActions.Now(id)];
        Running?.Invoke(this, action);
        action.Run(context);
        return null;
    }
}
