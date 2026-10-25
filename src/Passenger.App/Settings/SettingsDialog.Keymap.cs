using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>Settings' Keymap page: the keymap chosen at its top (Visual Studio's or VS Code's), every action with its
/// shortcuts in it, each in a box of its own (a chord as "⌘K, ⌘C"), an action the person changed in the modified
/// colour. An action's Edit Shortcuts menu (a double-click, a right-click or its button) has Add Keyboard Shortcut, a
/// Remove for each shortcut and Reset Shortcuts; Find Actions by Shortcut keeps only the actions a keystroke or chord
/// pressed into it starts.</summary>
public sealed partial class SettingsDialog
{
    private const string ConflictWarning = "This shortcut is already assigned to other actions. Do you want to remove the other assignments?";

    private readonly Dictionary<string, ActionRow> keymapRows = [];
    private readonly DockPanel keymapHeader = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock keymapNote = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
    private readonly ShortcutPanel filterKeys = new("Second stroke");
    private readonly Popup findPopup = new() { Placement = PlacementMode.BottomEdgeAlignedRight, IsLightDismissEnabled = true };
    private readonly TextBlock filterText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Border filterChip = new() { IsVisible = false, Classes = { "tag" }, Padding = new Thickness(8, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<KeymapPreset, Keymap> defaults = [];
    private Shortcut? shortcutFilter;

    /// <summary>The keymaps Settings offers, in its order.</summary>
    public static readonly KeymapPreset[] KeymapChoices = [KeymapPreset.VisualStudio, KeymapPreset.VSCode];

    /// <summary>What a search finds the keymap's choice by, beside the keymaps' names.</summary>
    private const string KeymapWords = "keymap keymaps preset keyboard shortcuts keys bindings visual studio vs code vscode";

    /// <summary>The keymap drop-down at the Keymap page's top: the keymap the person's keys follow, their own shortcuts
    /// kept over each apart.</summary>
    public ComboBox KeymapChoice { get; } = new() { Name = "KeymapChoice", Classes = { "form" } };

    /// <summary>Find Actions by Shortcut, the magnifier over a keyboard at the Keymap page's top.</summary>
    public Button FindByShortcut { get; } = new() { Classes = { "tool" } };

    /// <summary>Find Actions by Shortcut's popup: the keystrokes the page is narrowed to.</summary>
    public ShortcutPanel FindShortcut => filterKeys;

    /// <summary>The x on the shortcut the page is narrowed to (Clear Filtering).</summary>
    public Button ClearFiltering { get; } = new() { Classes = { "tabPin" }, Margin = new Thickness(6, 0, 0, 0) };

    /// <summary>The shortcut the Keymap page is narrowed to, or null while it lists every action.</summary>
    public Shortcut? ShortcutFilter => shortcutFilter;

    /// <summary>The chosen keymap with nothing of the person's: what Reset Shortcuts gives back.</summary>
    private Keymap Defaults => defaults.TryGetValue(Working.Keymap, out Keymap? made) ? made
        : defaults[Working.Keymap] = AppActions.Keymap(Working.Keymap, new Dictionary<string, string[]>());

    /// <summary>The chosen keymap with the person's shortcuts over it, as the options now stand.</summary>
    private Keymap Current => AppActions.Keymap(Working);

    /// <summary>An action's row on the Keymap page, whose menu is Edit Shortcuts.</summary>
    public Control KeymapRow(string action) => keymapRows[action].Row;

    /// <summary>The shortcuts an action's row shows, as it writes them.</summary>
    public IReadOnlyList<string> ShortcutsShown(string action) =>
        [.. keymapRows[action].Keys.Children.OfType<Border>().Select(b => ((TextBlock)b.Child!).Text ?? string.Empty)];

    /// <summary>Whether an action's row shows it in the colour of one whose shortcuts the person changed.</summary>
    public bool ShowsChanged(string action) => keymapRows[action].Title.Classes.Contains("changed");

    /// <summary>Gives an action exactly these shortcuts (keymap notation, "meta shift K"); none unbinds it. Each other
    /// action's shortcut one clashes with (<see cref="Keymap.Conflicts"/>) is taken from it, and the page says
    /// so.</summary>
    public void Rebind(string action, params string[] shortcuts)
    {
        Dictionary<string, string[]> next = Taken(Current, action, shortcuts.Select(Shortcut.Parse), keepDefaults: false, out List<string> taken);
        next[action] = shortcuts;
        Working = Working.WithOwnShortcuts(next);
        ShowShortcuts();
        Say(taken);
    }

    /// <summary>Reset Shortcuts: an action's shortcuts back as the keymap has them, each taken back from an action the
    /// person gave it to.</summary>
    public void ResetShortcuts(string action)
    {
        Dictionary<string, string[]> next = Taken(Current, action, Defaults.ShortcutsFor(action), keepDefaults: true, out List<string> taken);
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
        KeyboardShortcutDialog dialog = new(actions[action]?.Title ?? action, Where(actions[action]), shortcut =>
            [.. current.Conflicts(action, shortcut).Select(c => c.Action).Distinct().Select(id => (actions[id]?.Title ?? id, Where(actions[id])))]);
        if (await dialog.ShowDialog<Shortcut?>(this) is not { } chosen)
        {
            return;
        }
        IReadOnlyList<Shortcut> own = current.ShortcutsFor(action);
        if (own.Contains(chosen))
        {
            return;
        }
        if (current.Conflicts(action, chosen).Count > 0 && !await new ConfirmDialog("Warning", ConflictWarning, "Remove").ShowDialog<bool>(this))
        {
            return;
        }
        Rebind(action, [.. own.Select(s => s.Notation()), chosen.Notation()]);
    }

    /// <summary>The Keymap page: its header (the keymap, Find Actions by Shortcut) and a row for every action.</summary>
    private void AddKeymap()
    {
        AddKeymapHeader();
        Keymap current = Current;
        foreach (IdeAction action in actions.All.OrderBy(a => a.Title, StringComparer.Ordinal))
        {
            WrapPanel keys = new() { Width = 300, VerticalAlignment = VerticalAlignment.Center };
            Button edit = ToolButtons.Of(IconKeys.More, "Edit Shortcuts");
            StackPanel editor = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { keys, edit } };
            Panel row = (Panel)Add(KeymapPage, action.Title, editor, RowWords(action, current.ShortcutsFor(action.Id).Select(s => s.Display())), action: action.Id);
            // Transparent so a click anywhere along the row is the row's.
            row.Background = Brushes.Transparent;
            ContextMenu menu = new();
            menu.Opening += (_, _) => FillEditShortcuts(action.Id, menu);
            row.ContextMenu = menu;
            row.DoubleTapped += (_, _) => OpenEditShortcuts(action.Id, menu, row, row);
            edit.Click += (_, _) => OpenEditShortcuts(action.Id, menu, row, edit);
            keymapRows[action.Id] = new ActionRow(action, row, (TextBlock)row.Children[0], keys);
        }
        ShowShortcuts();
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

        Grid icon = new() { Width = 18, Height = 18 };
        PathIcon keyboard = Glyph(IconKeys.Keyboard);
        PathIcon magnifier = Glyph(IconKeys.Search);
        magnifier.Width = magnifier.Height = 10;
        magnifier.HorizontalAlignment = HorizontalAlignment.Right;
        magnifier.VerticalAlignment = VerticalAlignment.Bottom;
        icon.Children.Add(keyboard);
        icon.Children.Add(magnifier);
        FindByShortcut.Content = icon;
        ToolTip.SetTip(FindByShortcut, "Find Actions by Shortcut");
        FindByShortcut.Click += (_, _) => findPopup.IsOpen = true;

        ClearFiltering.Content = Glyph(IconKeys.Close);
        ((PathIcon)ClearFiltering.Content).Width = ((PathIcon)ClearFiltering.Content).Height = 10;
        ToolTip.SetTip(ClearFiltering, "Clear Filtering");
        ClearFiltering.Click += (_, _) => ClearShortcutFilter();
        filterChip.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { filterText, ClearFiltering } };
        filterChip.Bind(Border.BackgroundProperty, filterChip.GetResourceObservable(TokenKeys.Selected));

        TextBlock title = new() { Text = "Find Shortcut", FontWeight = FontWeight.SemiBold };
        Border popup = new()
        {
            Width = 320,
            Padding = new Thickness(12, 10, 12, 12),
            Classes = { "popup" },
            Child = new StackPanel { Spacing = 8, Children = { title, filterKeys } },
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

        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top, Children = { filterChip, FindByShortcut } };
        StackPanel named = new()
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { KeymapChoice, keymapNote },
        };
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(findPopup, Dock.Right);
        keymapHeader.Children.Add(findPopup);
        keymapHeader.Children.Add(tools);
        keymapHeader.Children.Add(named);
    }

    /// <summary>Edit Shortcuts: Add Keyboard Shortcut, a Remove for each shortcut (and Remove All Shortcuts past two),
    /// and Reset Shortcuts once they differ from the keymap's. The keymap binds keys alone, so it offers no mouse
    /// shortcut or abbreviation.</summary>
    private void FillEditShortcuts(string action, ContextMenu menu)
    {
        Keymap current = Current;
        IReadOnlyList<Shortcut> keys = current.ShortcutsFor(action);
        List<Control> items = [Item("Add Keyboard Shortcut", () => IdeErrors.Observe(AddKeyboardShortcut(action)))];
        if (keys.Count > 0)
        {
            items.Add(new Separator());
            items.AddRange(keys.Select(key => Item($"Remove {key.Display()}", () => Rebind(action, [.. keys.Where(k => k != key).Select(k => k.Notation())]))));
        }
        if (keys.Count > 2)
        {
            items.Add(new Separator());
            items.Add(Item("Remove All Shortcuts", () => Rebind(action)));
        }
        if (IsChanged(action, current))
        {
            items.Add(new Separator());
            items.Add(Item("Reset Shortcuts", () => ResetShortcuts(action)));
        }
        menu.ItemsSource = items;

        static MenuItem Item(string header, Action run)
        {
            MenuItem item = new() { Header = header };
            item.Click += (_, _) => run();
            return item;
        }
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

    /// <summary>The shortcuts a change gives the other actions: each one's list without the shortcuts that clash with
    /// these, or, keeping defaults, without only those the person gave it. What was taken is said in words.</summary>
    private Dictionary<string, string[]> Taken(Keymap from, string action, IEnumerable<Shortcut> shortcuts, bool keepDefaults, out List<string> taken)
    {
        Dictionary<string, List<Shortcut>> left = [];
        taken = [];
        foreach (Shortcut shortcut in shortcuts)
        {
            foreach ((string other, Shortcut theirs) in from.Conflicts(action, shortcut))
            {
                if (keepDefaults && Defaults.ShortcutsFor(other).Contains(theirs))
                {
                    continue;
                }
                if (!left.TryGetValue(other, out List<Shortcut>? keep))
                {
                    left[other] = keep = [.. from.ShortcutsFor(other)];
                }
                if (keep.Remove(theirs))
                {
                    taken.Add($"{theirs.Display()} was {actions[other]?.Title ?? other}'s");
                }
            }
        }
        Dictionary<string, string[]> next = new(Working.OwnShortcuts());
        foreach ((string other, List<Shortcut> keep) in left)
        {
            next[other] = [.. keep.Select(s => s.Notation())];
        }
        return next;
    }

    private void Say(List<string> taken)
    {
        keymapNote.Text = taken.Count > 0 ? string.Join("; ", taken) + "." : string.Empty;
        keymapNote.IsVisible = taken.Count > 0;
    }

    private bool IsChanged(string action, Keymap current) => !current.ShortcutsFor(action).ToHashSet().SetEquals(Defaults.ShortcutsFor(action));

    /// <summary>Each row's shortcuts as the chosen keymap and the person's own over it have them, which a search then
    /// finds the row by.</summary>
    private void ShowShortcuts()
    {
        Keymap current = Current;
        foreach (ActionRow row in keymapRows.Values)
        {
            string id = row.Action.Id;
            string[] shown = [.. current.ShortcutsFor(id).Select(s => s.Display())];
            if (!shown.SequenceEqual(ShortcutsShown(id)))
            {
                row.Keys.Children.Clear();
                foreach (string keys in shown)
                {
                    Border box = new() { Classes = { "box" }, Padding = new Thickness(6, 1), Margin = new Thickness(0, 2, 4, 2), Child = new TextBlock { Text = keys, FontSize = 12 } };
                    box.Bind(Border.BackgroundProperty, box.GetResourceObservable(TokenKeys.Selected));
                    row.Keys.Children.Add(box);
                }
                int option = options.FindIndex(o => o.Page == KeymapPage && o.Action == id);
                options[option] = options[option] with { Words = $"{KeymapPage} {row.Action.Title} {RowWords(row.Action, shown)}" };
            }
            row.Title.Classes.Set("changed", IsChanged(id, current));
        }
        if (shortcutFilter is not null)
        {
            Filter();
        }
    }

    /// <summary>Narrows the Keymap page to the actions a shortcut starts (Find Actions by Shortcut), or lists them all
    /// again for null.</summary>
    private void FilterByShortcut(Shortcut? shortcut)
    {
        shortcutFilter = shortcut;
        filterText.Text = shortcut?.Display();
        filterChip.IsVisible = shortcut is not null;
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

    /// <summary>Which options a shortcut filter keeps: every one but the Keymap page's actions it does not start.</summary>
    private Func<Option, bool> ShortcutFiltered()
    {
        if (shortcutFilter is not { } filter)
        {
            return _ => true;
        }
        Keymap current = Current;
        return option => option.Action is null || current.ShortcutsFor(option.Action).Any(s => s.StartsWith(filter));
    }

    /// <summary>Where an action is listed: its main menu's path, Editor Actions for the editor's own, else
    /// Other.</summary>
    private static string Where(IdeAction? action) => action switch
    {
        { Menus.Count: > 0 } placed => "Main Menu | " + placed.Menus[0].Path.Replace("/", " | ", StringComparison.Ordinal),
        { InEditor: true } => "Editor Actions",
        _ => "Other",
    };

    private static PathIcon Glyph(string key)
    {
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }

    private sealed record ActionRow(IdeAction Action, Control Row, TextBlock Title, WrapPanel Keys);
}
