using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>Settings' Keymap page: the keymap chosen at its top (Visual Studio's, VS Code's, IntelliJ IDEA's or Emacs's), every
/// action with its shortcuts in it, each in a box of its own (a chord as "⌘K, ⌘C"), an action the person changed in the
/// modified colour, its mouse shortcuts after its keys ("⌥Click"). An action's Edit Shortcuts menu (a double-click, a
/// right-click or its button) has Add Keyboard Shortcut, Add Mouse Shortcut, a Remove for each shortcut and Reset
/// Shortcuts; Find Actions by Shortcut keeps only the actions a keystroke or chord pressed into it starts.</summary>
public sealed partial class SettingsDialog
{
    /// <summary>What the keymap in force does with a keystroke, as Settings names it beside Vim mode's Ctrl keys: the
    /// action's title, the chords it begins, or nothing.</summary>
    public static string KeymapDoes(ActionRegistry actions, Keystroke stroke) =>
        actions.Keymap.ActionFor(new Shortcut(stroke)) is { } id ? actions[id]?.Title ?? id
            : actions.Keymap.StartsChord(stroke) ? "the chords it begins" : "nothing";

    private const string ConflictWarning = "This shortcut is already assigned to other actions. Do you want to remove the other assignments?";

    /// <summary>Each action the Keymap page lists, by its id: its option, the shortcuts its row shows, and its row once made.</summary>
    private readonly Dictionary<string, KeymapAction> keymapActions = [];
    private readonly DockPanel keymapHeader = new() { Margin = new Thickness(0, 0, 0, Gaps.Wide) };
    private readonly TextBlock keymapNote = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { StyleClasses.Muted } };
    private readonly ShortcutPanel filterKeys = new("Second stroke");
    private readonly FilterChip filterChip = new("Clear Filtering");
    private readonly Popup findPopup = new() { Placement = PlacementMode.BottomEdgeAlignedRight, IsLightDismissEnabled = true };
    private readonly Dictionary<KeymapPreset, Keymap> defaults = [];
    private Shortcut? shortcutFilter;

    /// <summary>The keymaps Settings offers, in its order.</summary>
    public static readonly KeymapPreset[] KeymapChoices = [KeymapPreset.VisualStudio, KeymapPreset.VSCode, KeymapPreset.IntelliJ, KeymapPreset.Emacs];

    /// <summary>What a search finds the keymap's choice by, beside the keymaps' names.</summary>
    private const string KeymapWords = "keymap keymaps preset keyboard shortcuts keys bindings visual studio vs code vscode intellij idea emacs";

    /// <summary>The keymap drop-down at the Keymap page's top: the keymap the person's keys follow, their own shortcuts
    /// kept over each apart.</summary>
    public ComboBox KeymapChoice { get; } = new FormDropDown { Name = "KeymapChoice" };

    /// <summary>Find Actions by Shortcut, the magnifier over a keyboard at the Keymap page's top.</summary>
    public Button FindByShortcut { get; } = ToolButtons.Give(new Button(), KeyboardSearch(), "Find Actions by Shortcut", keys: true);

    /// <summary>Find Actions by Shortcut's popup: the keystrokes the page is narrowed to.</summary>
    public ShortcutPanel FindShortcut => filterKeys;

    /// <summary>The x on the shortcut the page is narrowed to (Clear Filtering).</summary>
    public Button ClearFiltering => filterChip.Clear;

    /// <summary>The shortcut the Keymap page is narrowed to, or null while it lists every action.</summary>
    public Shortcut? ShortcutFilter => shortcutFilter;

    /// <summary>The chosen keymap with nothing of the person's: what Reset Shortcuts gives back.</summary>
    private Keymap Defaults => defaults.TryGetValue(Working.Keymap, out Keymap? made) ? made
        : defaults[Working.Keymap] = AppActions.Keymap(Working.Keymap, new Dictionary<string, string[]>());

    /// <summary>The chosen keymap with the person's shortcuts over it, as the options now stand, made once for each.</summary>
    private Keymap Current => keymapNow is { } made && ReferenceEquals(made.Of, Working) ? made.Keymap : (keymapNow = (Working, AppActions.Keymap(Working))).Value.Keymap;

    private (AppSettings Of, Keymap Keymap)? keymapNow;

    /// <summary>An action's row on the Keymap page, whose menu is Edit Shortcuts, made if the page has not shown it yet.</summary>
    public Control KeymapRow(string action) => keymapActions[action].Option.Row;

    /// <summary>The shortcuts an action's row shows, as it writes them.</summary>
    public IReadOnlyList<string> ShortcutsShown(string action) => KeysShown(MadeRow(action));

    /// <summary>Whether an action's row shows it in the colour of one whose shortcuts the person changed.</summary>
    public bool ShowsChanged(string action) => MadeRow(action).Title.Classes.Contains(StyleClasses.Changed);

    /// <summary>An action's row, made if the page has not shown it yet.</summary>
    private ActionRow MadeRow(string action) => KeymapRow(action) is not null ? keymapActions[action].Row! : throw new InvalidOperationException(action);

    /// <summary>The shortcuts a row shows, as it writes them.</summary>
    private static string[] KeysShown(ActionRow row) => [.. row.Keys.Children.OfType<Border>().Select(b => ((TextBlock)b.Child!).Text ?? string.Empty)];

    /// <summary>Gives an action exactly these shortcuts and mouse shortcuts (keymap notation, "meta shift K", "alt button1");
    /// none unbinds it. Each other action's shortcut one clashes with (<see cref="Keymap.Conflicts"/>,
    /// <see cref="Keymap.Conflict"/>) is taken from it, and the page says so.</summary>
    public void Rebind(string action, params string[] shortcuts)
    {
        Dictionary<string, string[]> next = Taken(Current, action, [.. shortcuts.Where(s => !MouseShortcut.IsMouse(s)).Select(Shortcut.Parse)],
            [.. shortcuts.Where(MouseShortcut.IsMouse).Select(MouseShortcut.Parse)], keepDefaults: false, out List<string> taken);
        next[action] = shortcuts;
        Working = Working.WithOwnShortcuts(next);
        ShowShortcuts();
        Say(taken);
    }

    /// <summary>Reset Shortcuts: an action's shortcuts back as the keymap has them, each taken back from an action the
    /// person gave it to.</summary>
    public void ResetShortcuts(string action)
    {
        Dictionary<string, string[]> next = Taken(Current, action, Defaults.ShortcutsFor(action), Defaults.MouseShortcutsFor(action), keepDefaults: true, out List<string> taken);
        next.Remove(action);
        Working = Working.WithOwnShortcuts(next);
        ShowShortcuts();
        Say(taken);
    }

    /// <summary>Add Keyboard Shortcut: the Keyboard Shortcut dialog, then, when the shortcut is already assigned to
    /// other actions, its warning, whose Remove takes it from them and whose Cancel keeps the keymap as it was. It
    /// offers no Keep: a shortcut here runs one action, and a chord's first key bound alone would run before
    /// it.</summary>
    public async Task AddKeyboardShortcut(string action)
    {
        Keymap current = Current;
        KeyboardShortcutDialog dialog = new(TitleOf(action), Where(action), shortcut =>
            [.. current.Conflicts(action, shortcut).Select(c => c.Action).Distinct().Select(id => (TitleOf(id), Where(id)))]);
        if (await dialog.ShowDialog<Shortcut?>(this) is not { } chosen)
        {
            return;
        }
        if (current.ShortcutsFor(action).Contains(chosen))
        {
            return;
        }
        if (current.Conflicts(action, chosen).Count > 0 && !await new ConfirmDialog("Warning", ConflictWarning, "Remove").ShowDialog<bool>(this))
        {
            return;
        }
        Rebind(action, [.. Own(current, action), chosen.Notation()]);
    }

    /// <summary>Add Mouse Shortcut: the Mouse Shortcut dialog, then, when the gesture runs another action, the warning
    /// a key's conflict has, whose Remove takes it from that action and whose Cancel keeps the keymap as it was.</summary>
    public async Task AddMouseShortcut(string action)
    {
        Keymap current = Current;
        MouseShortcutDialog dialog = new(TitleOf(action), Where(action), gesture =>
            current.Conflict(action, gesture) is { } other ? [(TitleOf(other), Where(other))] : []);
        if (await dialog.ShowDialog<MouseShortcut?>(this) is not { } chosen)
        {
            return;
        }
        if (current.MouseShortcutsFor(action).Contains(chosen))
        {
            return;
        }
        if (current.Conflict(action, chosen) is not null && !await new ConfirmDialog("Warning", ConflictWarning, "Remove").ShowDialog<bool>(this))
        {
            return;
        }
        Rebind(action, [.. Own(current, action), chosen.Notation()]);
    }

    /// <summary>An action's shortcuts and mouse shortcuts in a keymap, as the person's keymap keeps them.</summary>
    private static IEnumerable<string> Own(Keymap keymap, string action) =>
        [.. keymap.ShortcutsFor(action).Select(s => s.Notation()), .. keymap.MouseShortcutsFor(action).Select(g => g.Notation())];

    /// <summary>What an action's row shows of it: its shortcuts, then its mouse shortcuts.</summary>
    private static string[] ShownOf(Keymap keymap, string action) =>
        [.. keymap.ShortcutsFor(action).Select(KeyWords.Of), .. keymap.MouseShortcutsFor(action).Select(KeyWords.Of)];

    /// <summary>An action's name as the page and its dialogs say it: the IDE's, else the editor's for its mouse's own.</summary>
    private string TitleOf(string id) => actions[id]?.Title ?? EditorActions.Gestures.FirstOrDefault(g => g.Id == id).Title ?? id;

    /// <summary>The Keymap page: its header (the keymap, Find Actions by Shortcut) and a row for every action, each found by a
    /// search from its words and made as the page is first shown.</summary>
    private void AddKeymap()
    {
        AddKeymapHeader();
        Keymap current = Current;
        foreach (IdeAction action in actions.All.OrderBy(a => a.Title, StringComparer.Ordinal))
        {
            string[] shown = ShownOf(current, action.Id);
            KeymapAction listed = new(action, shown);
            WrapPanel keys = null!;
            Button edit = null!;
            listed.Option = Add(KeymapPage, action.Title, () =>
            {
                keys = new() { VerticalAlignment = VerticalAlignment.Center };
                edit = ToolButtons.Of(IconKeys.More, "Edit Shortcuts");
                return new NarrowingField(keys, edit) { MaxWidth = OptionRow.EditorWidth };
            }, RowWords(action, shown), action: action.Id, made: row =>
            {
                // Transparent so a click anywhere along the row is the row's.
                row.Background = Brushes.Transparent;
                ContextMenu menu = new();
                menu.Opening += (_, _) => FillEditShortcuts(action.Id, menu);
                row.ContextMenu = menu;
                row.DoubleTapped += (_, _) => OpenEditShortcuts(action.Id, menu, row, row);
                edit.Click += (_, _) => OpenEditShortcuts(action.Id, menu, row, edit);
                listed.Row = new ActionRow(row, (TextBlock)row.Children[0], keys);
                Show(listed, Current);
            });
            keymapActions[action.Id] = listed;
        }
    }

    /// <summary>The keymap drop-down: another keymap shows its own shortcuts with the person's kept over it, and Reset
    /// Shortcuts gives back its own; what the person set over the one left stays, for when it is chosen
    /// again.</summary>
    private void ChooseKeymap(KeymapPreset chosen)
    {
        if (chosen == Working.Keymap)
        {
            return;
        }
        Change(s => s with { Keymap = chosen });
        Say([]);
        ShowShortcuts();
        if (shortcutFilter is null)
        {
            // The search runs again over the new keymap's shortcuts.
            Filter();
        }
    }

    /// <summary>A shortcut as the Keymap page shows it, in a box of its own, which the first start's keymaps show too.</summary>
    public static Border KeyBox(string keys)
    {
        Border box = new()
        {
            Classes = { StyleClasses.Box },
            Padding = new Thickness(Gaps.Small, 0),
            Margin = new Thickness(0, Gaps.Hair, Gaps.Tight, Gaps.Hair),
            // Wrapped after a chord's first key where a narrow place has no room for both.
            Child = new TextBlock { Classes = { StyleClasses.Caption }, Text = keys, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
        };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable(TokenKeys.Selected));
        return box;
    }

    /// <summary>Whether a search's words find the keymap's choice at the Keymap page's top.</summary>
    private static bool FindsKeymapChoice(string[] words) => words.Length > 0
        && words.All(w => KeymapWords.Contains(w, StringComparison.OrdinalIgnoreCase)
            || KeymapChoices.Any(k => DefaultKeymaps.NameOf(k).Contains(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>What a search finds an action's row by: its name, group and shortcuts as shown.</summary>
    private static string RowWords(IdeAction action, IEnumerable<string> shown) => $"{action.Group} {string.Join(' ', shown)} keymap shortcut";

    private void AddKeymapHeader()
    {
        KeymapChoice.ItemsSource = KeymapChoices.Select(DefaultKeymaps.NameOf).ToArray();
        KeymapChoice.SelectedIndex = Array.IndexOf(KeymapChoices, Working.Keymap);
        KeymapChoice.HorizontalAlignment = HorizontalAlignment.Left;
        KeymapChoice.SelectionChanged += (_, _) => ChooseKeymap(KeymapChoices[Math.Max(0, KeymapChoice.SelectedIndex)]);

        FindByShortcut.Click += (_, _) => findPopup.IsOpen = true;
        filterChip.Cleared += (_, _) => ClearShortcutFilter();

        TextBlock title = new() { Classes = { StyleClasses.PaneTitle }, Text = "Find Shortcut" };
        Border popup = new()
        {
            Width = PopupFrame.Narrow,
            Padding = PopupFrame.Inset,
            Classes = { StyleClasses.Popup },
            Child = new StackPanel { Spacing = Gaps.Normal, Children = { title, filterKeys } },
        };
        // Tab goes round the popup's own fields, never on to the page under it.
        KeyboardNavigation.SetTabNavigation(popup, KeyboardNavigationMode.Cycle);
        popup.AddHandler(KeyDownEvent, (_, e) =>
        {
            // Escape, and Enter once a keystroke is in, close it; the shortcut stays the filter.
            if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Escape or Key.Enter)
            {
                findPopup.IsOpen = false;
                e.Handled = true;
            }
        });
        findPopup.Child = popup;
        findPopup.PlacementTarget = FindByShortcut;
        findPopup.Opened += (_, _) => Dispatcher.UIThread.Post(() => filterKeys.First.Focus(), DispatcherPriority.Loaded);
        filterKeys.Changed += (_, _) => FilterByShortcut(filterKeys.Shortcut);

        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, VerticalAlignment = VerticalAlignment.Top, Children = { filterChip, FindByShortcut } };
        StackPanel named = new()
        {
            Spacing = Gaps.Tight,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { KeymapChoice, keymapNote },
        };
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(findPopup, Dock.Right);
        keymapHeader.Children.Add(findPopup);
        keymapHeader.Children.Add(tools);
        keymapHeader.Children.Add(named);
    }

    /// <summary>Edit Shortcuts: Add Keyboard Shortcut and Add Mouse Shortcut, a Remove for each shortcut and mouse shortcut
    /// (and Remove All Shortcuts past two), and Reset Shortcuts once they differ from the keymap's. The keymap binds keys
    /// and gestures alone, so it offers no abbreviation.</summary>
    private void FillEditShortcuts(string action, ContextMenu menu)
    {
        Keymap current = Current;
        List<(string Shown, string Notation)> own = [.. current.ShortcutsFor(action).Select(s => (KeyWords.Of(s), s.Notation())),
            .. current.MouseShortcutsFor(action).Select(g => (KeyWords.Of(g), g.Notation()))];
        List<Control> items =
        [
            ToolWindows.ListMenus.Item("Add Keyboard Shortcut", () => IdeErrors.Observe(AddKeyboardShortcut(action))),
            ToolWindows.ListMenus.Item("Add Mouse Shortcut", () => IdeErrors.Observe(AddMouseShortcut(action))),
        ];
        if (own.Count > 0)
        {
            items.Add(new Separator());
            items.AddRange(own.Select(shortcut => ToolWindows.ListMenus.Item($"Remove {shortcut.Shown}", () => Rebind(action, [.. own.Where(o => o != shortcut).Select(o => o.Notation)]))));
        }
        if (own.Count > 2)
        {
            items.Add(new Separator());
            items.Add(ToolWindows.ListMenus.Item("Remove All Shortcuts", () => Rebind(action)));
        }
        if (IsChanged(action, current))
        {
            items.Add(new Separator());
            items.Add(ToolWindows.ListMenus.Item("Reset Shortcuts", () => ResetShortcuts(action)));
        }
        menu.ItemsSource = items;
    }

    /// <summary>The row's Edit Shortcuts menu, under what was clicked (the row, or its ⋮): a menu opens only on the
    /// control it belongs to, the row, so it is placed at the ⋮ until it closes.</summary>
    private void OpenEditShortcuts(string action, ContextMenu menu, Control row, Control at)
    {
        FillEditShortcuts(action, menu);
        menu.PlacementTarget = at;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Closed += Reset;
        menu.Open(row);

        void Reset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            menu.Closed -= Reset;
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
            menu.ClearValue(ContextMenu.PlacementProperty);
        }
    }

    /// <summary>The shortcuts a change gives the other actions: each one's list without the shortcuts and mouse
    /// shortcuts that clash with these, or, keeping defaults, without only those the person gave it. What was taken is
    /// said in words.</summary>
    private Dictionary<string, string[]> Taken(Keymap from, string action, IEnumerable<Shortcut> shortcuts, IEnumerable<MouseShortcut> gestures, bool keepDefaults,
        out List<string> taken)
    {
        Dictionary<string, (List<Shortcut> Keys, List<MouseShortcut> Gestures)> left = [];
        List<string> said = [];
        foreach (Shortcut shortcut in shortcuts)
        {
            foreach ((string other, Shortcut theirs) in from.Conflicts(action, shortcut))
            {
                if ((!keepDefaults || !Defaults.ShortcutsFor(other).Contains(theirs)) && Left(other).Keys.Remove(theirs))
                {
                    said.Add($"{KeyWords.Of(theirs)} was {TitleOf(other)}'s");
                }
            }
        }
        foreach (MouseShortcut gesture in gestures)
        {
            if (from.Conflict(action, gesture) is { } other && (!keepDefaults || !Defaults.MouseShortcutsFor(other).Contains(gesture)) && Left(other).Gestures.Remove(gesture))
            {
                said.Add($"{KeyWords.Of(gesture)} was {TitleOf(other)}'s");
            }
        }
        taken = said;
        Dictionary<string, string[]> next = new(Working.OwnShortcuts());
        foreach ((string other, (List<Shortcut> keys, List<MouseShortcut> kept)) in left)
        {
            next[other] = [.. keys.Select(s => s.Notation()), .. kept.Select(g => g.Notation())];
        }
        return next;

        (List<Shortcut> Keys, List<MouseShortcut> Gestures) Left(string other)
        {
            if (!left.TryGetValue(other, out (List<Shortcut> Keys, List<MouseShortcut> Gestures) keep))
            {
                left[other] = keep = ([.. from.ShortcutsFor(other)], [.. from.MouseShortcutsFor(other)]);
            }
            return keep;
        }
    }

    private void Say(List<string> taken)
    {
        keymapNote.Text = taken.Count > 0 ? string.Join("; ", taken) + "." : string.Empty;
        keymapNote.IsVisible = taken.Count > 0;
    }

    private bool IsChanged(string action, Keymap current) => !current.ShortcutsFor(action).ToHashSet().SetEquals(Defaults.ShortcutsFor(action))
        || !current.MouseShortcutsFor(action).ToHashSet().SetEquals(Defaults.MouseShortcutsFor(action));

    /// <summary>Each row's shortcuts as the chosen keymap and the person's own over it have them, which a search then
    /// finds the row by.</summary>
    private void ShowShortcuts()
    {
        Keymap current = Current;
        foreach (KeymapAction listed in keymapActions.Values)
        {
            string[] shown = ShownOf(current, listed.Action.Id);
            if (!shown.SequenceEqual(listed.Shown))
            {
                listed.Shown = shown;
                listed.Option.Searched = $"{KeymapPage} {listed.Action.Title} {RowWords(listed.Action, shown)}";
            }
            Show(listed, current);
        }
        if (shortcutFilter is not null)
        {
            Filter();
        }
    }

    /// <summary>An action's row, once made, showing its shortcuts as listed, in the colour of one the person changed or not.</summary>
    private void Show(KeymapAction listed, Keymap current)
    {
        if (listed.Row is not { } row)
        {
            return;
        }
        if (!listed.Shown.SequenceEqual(KeysShown(row)))
        {
            row.Keys.Children.Clear();
            foreach (string keys in listed.Shown)
            {
                row.Keys.Children.Add(KeyBox(keys));
            }
        }
        row.Title.Classes.Set(StyleClasses.Changed, IsChanged(listed.Action.Id, current));
    }

    /// <summary>Narrows the Keymap page to the actions a shortcut starts (Find Actions by Shortcut), or lists them all
    /// again for null.</summary>
    private void FilterByShortcut(Shortcut? shortcut)
    {
        shortcutFilter = shortcut;
        filterChip.Words = shortcut is null ? null : KeyWords.Of(shortcut);
        Filter();
        if (shortcut is not null)
        {
            Page = KeymapPage;
        }
    }

    private void ClearShortcutFilter()
    {
        findPopup.IsOpen = false;
        filterKeys.SecondStroke.IsChecked = false;
        filterKeys.Second.Stroke = null;
        filterKeys.First.Stroke = null;
        FilterByShortcut(null);
    }

    /// <summary>Which options a shortcut filter keeps: every one but the Keymap page's actions it does not start and its own
    /// settings, which no keystroke starts.</summary>
    private Func<Option, bool> ShortcutFiltered()
    {
        if (shortcutFilter is not { } filter)
        {
            return _ => true;
        }
        Keymap current = Current;
        return option => option.Action is null ? option.Page != KeymapPage : current.ShortcutsFor(option.Action).Any(s => s.StartsWith(filter));
    }

    /// <summary>Where an action is listed: its main menu's path, Editor Actions for the editor's own (its mouse's too),
    /// else Other.</summary>
    private string Where(string id) => actions[id] switch
    {
        { Menus.Count: > 0 } placed => "Main Menu | " + placed.Menus[0].Path.Replace("/", " | ", StringComparison.Ordinal),
        { InEditor: true } => "Editor Actions",
        null when EditorActions.Gestures.Any(g => g.Id == id) => "Editor Actions",
        _ => "Other",
    };

    /// <summary>Find Actions by Shortcut's mark: a keyboard, a glyph's magnifier at its foot.</summary>
    private static Control KeyboardSearch()
    {
        PathIcon keyboard = new();
        keyboard.Bind(PathIcon.DataProperty, keyboard.GetResourceObservable(IconKeys.Keyboard));
        PathIcon magnifier = new() { Classes = { StyleClasses.Small }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        magnifier.Bind(PathIcon.DataProperty, magnifier.GetResourceObservable(IconKeys.Search));
        return new Grid { Width = 18, Height = 18, Children = { keyboard, magnifier } };
    }

    /// <summary>An action's row as made: the row, its title and its shortcuts.</summary>
    private sealed record ActionRow(Control Row, TextBlock Title, WrapPanel Keys);

    /// <summary>An action the Keymap page lists: its option, the shortcuts its row shows, and its row once made.</summary>
    private sealed class KeymapAction(IdeAction action, string[] shown)
    {
        public IdeAction Action { get; } = action;

        public Option Option { get; set; } = null!;

        public string[] Shown { get; set; } = shown;

        public ActionRow? Row { get; set; }
    }
}
