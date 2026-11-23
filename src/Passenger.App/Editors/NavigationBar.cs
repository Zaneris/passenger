using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.App.CSharp;
using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>A list a navigation bar's part opens: what it is called, the rows it offers to go to, the one chosen as it
/// opens, and the group whose bar asked and what it opens under.</summary>
public sealed record NavigationList(EditorGroup Group, string Title, IReadOnlyList<SymbolRow> Rows, int Chosen, Control Under);

/// <summary>The bar over a group's editor naming where its caret is in the file's outline, outermost first (a C# file's
/// namespace, type and member; a Markdown file's headings; a JSON file's keys), each part a button listing what stands
/// beside it to go to, and after them, where the caret is in none of what the innermost holds, a mark listing that. The
/// outline is the file's (<see cref="EditorArea.NavigationOutlineOf"/>): a C# file's read from each of its parses as it
/// lands, any other's once typing pauses; the bar follows the caret once it rests in another part, so a key moving within
/// one costs nothing. Shown while the setting has it and the file has an outline with something in it, a narrow editor
/// shortening its parts, the outermost going first (<see cref="PathCrumbs"/>). Left and Right move along it, Down opens
/// a part's list as Enter and Space do, Escape gives the editor the keys back.</summary>
public sealed class NavigationBar
{
    /// <summary>What a screen reader and the window's regions call the bar.</summary>
    public const string Said = "Navigation bar";

    private readonly EditorArea area;
    private readonly EditorGroup group;
    private readonly PathCrumbs crumbs = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Margin = new Thickness(Gaps.Small, 0) };
    private readonly List<Part> parts = [];
    private readonly Debounce follow;
    private readonly Debounce readAgain;
    private OpenDocument? document;
    private Outline<SymbolRow> outline = Outline<SymbolRow>.Empty;

    /// <summary>The file whose outline <see cref="outline"/> is, null while it is none's.</summary>
    private OpenDocument? outlined;
    private CancellationTokenSource? asking;

    /// <summary>The innermost row holding the caret's line as the path shows it, or will once the caret has rested.</summary>
    private int awaited = -2;

    /// <summary>The rows the path shows, outermost first, and the row whose own rows its last mark lists (-1 the file's
    /// top), null where it shows no such mark.</summary>
    private SymbolRow[] path = [];
    private int[] pathAt = [];
    private int? listed;

    /// <summary>Whether the keys, rather than the pointer, last pressed in the bar: a list they open is over the part that
    /// opened it, which has the keys back as it is given up, one the pointer opened over the editor's keys.</summary>
    private bool keyed;

    public NavigationBar(EditorArea area, EditorGroup group)
    {
        this.area = area;
        this.group = group;
        Bar = ToolWindows.ToolBars.OverEditor(crumbs);
        Bar.IsVisible = false;
        AutomationProperties.SetName(Bar, Said);
        Bar.AddHandler(InputElement.KeyDownEvent, (_, _) => keyed = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Bar.AddHandler(InputElement.PointerPressedEvent, (_, _) => keyed = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        Bar.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
        follow = new Debounce(TypingPauses.NavigationBar, () =>
        {
            ShowPath();
            return Task.CompletedTask;
        });
        readAgain = new Debounce(TypingPauses.Outline, ReadAsync);
        group.Editor.SelectionChanged += (_, _) => CaretMoved();
    }

    /// <summary>The bar, docked over the editor.</summary>
    public Border Bar { get; }

    /// <summary>The names of the parts the path shows, outermost first.</summary>
    public IReadOnlyList<string> Shown => [.. path.Select(row => row.Name)];

    /// <summary>The buttons the bar shows, its parts' in their order and then its last mark's where it shows one.</summary>
    public IReadOnlyList<Button> Buttons => [.. parts.Where(p => p.Button.IsVisible).Select(p => p.Button)];

    /// <summary>Whether the bar's last mark lists what the innermost part holds (the file's top where it shows no part).</summary>
    public bool ListsMore => listed is not null;

    /// <summary>The file the group's editor shows now; null for none, or for what shows in the editor's place (a diff).</summary>
    public void Show(OpenDocument? next)
    {
        if (next == document)
        {
            return;
        }
        if (document is { } before)
        {
            before.Parsed -= OnParsed;
            before.Text.Changed -= OnTextChanged;
        }
        document = next;
        asking?.Cancel();
        asking = null;
        readAgain.Stop();
        follow.Stop();
        outline = Outline<SymbolRow>.Empty;
        outlined = null;
        awaited = -2;
        if (next is not null)
        {
            // A C# file's outline is read from its parses as they land; any other's from its text once typing pauses.
            if (next.Syntax is not null)
            {
                next.Parsed += OnParsed;
            }
            else
            {
                next.Text.Changed += OnTextChanged;
            }
        }
        ShowPath();
        FollowSource();
    }

    /// <summary>The bar shows or hides as the setting, the file and its outline say now; one shown without the file's
    /// outline asks for it.</summary>
    public void FollowSource()
    {
        bool has = document is { } shown && area.NamesWhereInBar(shown);
        if (!has && outlined is not null)
        {
            asking?.Cancel();
            asking = null;
            readAgain.Stop();
            follow.Stop();
            outline = Outline<SymbolRow>.Empty;
            outlined = null;
            ShowPath();
        }
        else if (outlined != document && asking is null)
        {
            IdeErrors.Observe(ReadAsync());
        }
        Bar.IsVisible = has && (outlined != document || outline.Rows.Count > 0);
    }

    private void OnParsed(object? sender, ParsedText parsed)
    {
        if (FollowsText)
        {
            IdeErrors.Observe(ReadAsync());
        }
    }

    private void OnTextChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (FollowsText)
        {
            readAgain.Restart();
        }
    }

    /// <summary>Whether the file's outline is read again as its text changes: while the bar names where the caret is, hidden
    /// too while the outline holds nothing, so the bar comes as the file comes to declare something.</summary>
    private bool FollowsText => document is { } shown && area.NamesWhereInBar(shown);

    /// <summary>Reads the file's outline again, the rows that did not change kept, and shows the path in it.</summary>
    private async Task ReadAsync()
    {
        asking?.Cancel();
        asking = null;
        if (document is not { } reading || !area.ShowsNavigationBar || area.NavigationOutlineOf(reading) is not { } source)
        {
            return;
        }
        CancellationTokenSource mine = asking = new CancellationTokenSource();
        // Held, so a screenshot or a test settles on the bar as the outline makes it.
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            Outline<SymbolRow> read = await source.ReadAsync(reading.Path, outlined == reading ? outline : null, mine.Token);
            if (mine.IsCancellationRequested || document != reading)
            {
                return;
            }
            outline = read;
            outlined = reading;
            ShowPath();
            FollowSource();
        }
        catch (OperationCanceledException)
        {
            // Asked again since, or another file shown.
        }
        catch (LanguageServerFailed)
        {
            // The path shown stands; the next change, or a server started again, asks afresh.
        }
        finally
        {
            if (asking == mine)
            {
                asking = null;
            }
        }
    }

    /// <summary>The caret moved: once it rests in another part of the outline, the path follows it.</summary>
    private void CaretMoved()
    {
        if (outlined is null || outlined != document || group.Editor.Document != outlined.Text)
        {
            return;
        }
        int innermost = outline.InnermostAt(CaretLine());
        if (innermost != awaited)
        {
            awaited = innermost;
            follow.Restart();
        }
    }

    private int CaretLine() => group.Editor.Document.Snapshot.GetLineFromOffset(group.Editor.Selections.Primary.Caret);

    /// <summary>Where the caret's line is in an outline: the rows holding it, outermost first, and the row whose own rows
    /// the last mark lists, where the innermost holds rows (the file's top, -1, where none holds the line).</summary>
    private static (int[] Path, int? Listed) Where(Outline<SymbolRow> outline, int line)
    {
        int innermost = outline.InnermostAt(line);
        int depth = 0;
        for (int i = innermost; i >= 0; i = outline.ParentOf(i))
        {
            depth++;
        }
        int[] at = new int[depth];
        for (int i = innermost, place = depth - 1; i >= 0; i = outline.ParentOf(i), place--)
        {
            at[place] = i;
        }
        bool holds = innermost + 1 < outline.Rows.Count && outline.ParentOf(innermost + 1) == innermost;
        return (at, holds ? innermost : null);
    }

    /// <summary>The path to where the caret is, the bar's buttons left as they are where it is the one shown.</summary>
    private void ShowPath()
    {
        bool read = outlined is not null && outlined == document && group.Editor.Document == outlined.Text;
        (int[] at, int? lists) = read ? Where(outline, CaretLine()) : ([], null);
        awaited = at.Length > 0 ? at[^1] : -1;
        if (lists == listed && at.Length == path.Length && at.AsSpan().SequenceEqual(pathAt) && Same(at))
        {
            return;
        }
        pathAt = at;
        path = [.. at.Select(i => outline.Rows[i])];
        listed = lists;
        for (int i = 0; i < path.Length; i++)
        {
            PartAt(i).Show(path[i], ListTitle(outline, outline.ParentOf(at[i])), first: i == 0);
        }
        if (lists is { } parent)
        {
            PartAt(path.Length).ShowMore(ListTitle(outline, parent), after: path.Length > 0);
        }
        for (int i = path.Length + (lists is null ? 0 : 1); i < parts.Count; i++)
        {
            parts[i].Hide();
        }

        bool Same(int[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (!ReferenceEquals(outline.Rows[rows[i]], path[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>What the list of a row's own rows is called: a type's members, else what is in it; the file's top level by
    /// the file's name.</summary>
    private string ListTitle(Outline<SymbolRow> from, int parent)
    {
        if (parent < 0)
        {
            return document?.Name ?? string.Empty;
        }
        SymbolRow row = from.Rows[parent];
        return row.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum ? $"Members of {row.Name}" : $"In {row.Name}";
    }

    /// <summary>A part of the bar, made as the longest path shown needs it, with the mark before it.</summary>
    private Part PartAt(int index)
    {
        while (parts.Count <= index)
        {
            int at = parts.Count;
            Part made = new();
            made.Button.Click += (_, _) => Open(at, made.Button);
            crumbs.Children.Add(made.Mark);
            crumbs.Children.Add(made.Button);
            parts.Add(made);
        }
        return parts[index];
    }

    /// <summary>A part's list: the rows beside the part's own, it chosen; or those the last mark lists.</summary>
    private void Open(int index, Control under)
    {
        if (outlined is null || (index < pathAt.Length ? outline.ParentOf(pathAt[index]) : listed) is not { } parent)
        {
            return;
        }
        Offer(outline, parent, index < pathAt.Length ? pathAt[index] : -1, under, keyed);
    }

    /// <summary>Asks the window to list a row's own rows (the file's top level for -1) under a control, one of them chosen.</summary>
    private void Offer(Outline<SymbolRow> from, int parent, int chosen, Control under, bool byKeys)
    {
        if (!byKeys)
        {
            group.FocusShown();
        }
        IReadOnlyList<int> children = from.ChildrenOf(parent);
        SymbolRow[] rows = [.. children.Select(i => from.Rows[i] with { Depth = 0 })];
        int first = 0;
        for (int i = 0; i < children.Count; i++)
        {
            first = children[i] == chosen ? i : first;
        }
        area.NavigationListAsked(new NavigationList(group, ListTitle(from, parent), rows, first, under));
    }

    /// <summary>Gives the keys to the bar's last button, as by Tab; false where it shows none.</summary>
    public bool TakeKeys() => Bar.IsEffectivelyVisible && Buttons is { Count: > 0 } shown && shown[^1].Focus(NavigationMethod.Tab);

    /// <summary>Jump to Navigation Bar: the keys to its last button; with the bar hidden (the setting off), the list that
    /// button would open, under the editor's top, the file's outline read for it. False where that outline holds nothing
    /// to go to, for the window to say so.</summary>
    public async Task<bool> JumpToAsync()
    {
        if (TakeKeys() || document is not { } reading || area.NavigationOutlineOf(reading) is not { } source)
        {
            return true;
        }
        Outline<SymbolRow> read;
        try
        {
            read = outlined == reading ? outline : await source.ReadAsync(reading.Path, null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The file changed as it was read: the next jump asks again.
            return true;
        }
        catch (LanguageServerFailed)
        {
            // Its server could not answer: the next jump asks again.
            return true;
        }
        if (document != reading || group.Editor.Document != reading.Text)
        {
            return true;
        }
        if (read.Rows.Count == 0)
        {
            return false;
        }
        (int[] at, int? lists) = Where(read, CaretLine());
        if (lists is { } parent)
        {
            Offer(read, parent, -1, group.Editor, byKeys: false);
        }
        else if (at.Length > 0)
        {
            Offer(read, read.ParentOf(at[^1]), at[^1], group.Editor, byKeys: false);
        }
        return true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        IReadOnlyList<Button> shown = Buttons;
        int at = -1;
        for (int i = 0; i < shown.Count; i++)
        {
            at = shown[i].IsKeyboardFocusWithin ? i : at;
        }
        if (at < 0 || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        int to = e.Key switch
        {
            Key.Left => Math.Max(0, at - 1),
            Key.Right => Math.Min(shown.Count - 1, at + 1),
            Key.Home => 0,
            Key.End => shown.Count - 1,
            _ => -1,
        };
        if (to >= 0)
        {
            shown[to].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            int index = parts.FindIndex(p => p.Button == shown[at]);
            Open(index, shown[at]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            group.FocusShown(NavigationMethod.Tab);
            e.Handled = true;
        }
    }

    /// <summary>A part's mark and button: a row's kind badge and name, its tip and accessible words naming the list it
    /// opens; or, as the last mark, the mark alone, named by its list.</summary>
    private sealed class Part
    {
        private readonly Border badge;
        private readonly TextBlock letter;
        private readonly TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };

        public Part()
        {
            badge = Tags.Kind(out letter);
            badge.Margin = new Thickness(0, 0, Gaps.Tight, 0);
            Button = new Button { Classes = { StyleClasses.Crumb }, Content = new DockPanel { Children = { badge, name } } };
        }

        public TextBlock Mark { get; } = new() { Text = "›", Classes = { StyleClasses.Status }, Margin = new Thickness(Gaps.Tight, 0) };

        public Button Button { get; }

        public void Show(SymbolRow row, string list, bool first)
        {
            (string kind, string brush) = SymbolPopup.Badge(row.Kind);
            letter.Text = kind;
            badge.Background = InfoCard.Brush(brush);
            badge.IsVisible = true;
            name.Text = row.Name;
            Mark.IsVisible = !first;
            Button.IsVisible = true;
            Button.Margin = default;
            AutomationProperties.SetName(Button, $"{row.Name}, {KindWords(row.Kind)}");
            AutomationProperties.SetHelpText(Button, list);
            ToolTip.SetTip(Button, list);
        }

        public void ShowMore(string list, bool after)
        {
            badge.IsVisible = false;
            name.Text = "›";
            // The mark is the button itself, standing where a part's mark would.
            Mark.IsVisible = false;
            Button.IsVisible = true;
            Button.Margin = after ? new Thickness(Gaps.Tight, 0, 0, 0) : default;
            AutomationProperties.SetName(Button, list);
            AutomationProperties.SetHelpText(Button, list);
            ToolTip.SetTip(Button, list);
        }

        public void Hide()
        {
            Mark.IsVisible = false;
            Button.IsVisible = false;
        }

        /// <summary>A row's kind in words, as a screen reader says it after the row's name.</summary>
        private static string KindWords(SymbolKind kind) => kind switch
        {
            SymbolRow.HeadingKind => "heading",
            SymbolRow.NodeKind => "node",
            SymbolRow.FileKind => "file",
            _ => SymbolKinds.Name(kind),
        };
    }
}
