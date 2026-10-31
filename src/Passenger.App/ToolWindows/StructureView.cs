using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>The Structure window: the active file's outline (a C# file's types and members, a JSON file's keys),
/// indented, from its language server, asked as it is shown and, while it is, following the editor (a moment after
/// typing stops) and choosing the member the caret is in. A click or Enter goes to a member. The rows are made off the
/// window's thread, the list kept as it is where the outline did not change; a file with none says why.</summary>
public sealed class StructureView : DockPanel
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(600);

    private readonly ListBox list;
    private readonly TextBlock placeholder;
    private readonly TextBlock unoutlined;
    private readonly Debounce settle;
    private Func<(string Path, string Name, LspDocument? Lsp, int CaretLine)?>? source;
    private CancellationTokenSource? asking;
    private bool choosing;

    /// <summary>The file whose outline the rows are, from its server; null while they are no file's.</summary>
    private string? outlined;

    private Outline<SymbolRow> outline = Outline<SymbolRow>.Empty;

    /// <summary>In the visual tree, as a tool window's content is only while it is open in its place or its own window.</summary>
    private bool shown;

    public StructureView()
    {
        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(list, SymbolPopup.ReusedRow);
        list.SelectionChanged += (_, _) =>
        {
            if (!choosing && list.SelectedItem is SymbolRow row && list.IsKeyboardFocusWithin)
            {
                GoToRequested?.Invoke(this, row);
            }
        };
        list.Tapped += (_, _) =>
        {
            if (list.SelectedItem is SymbolRow row)
            {
                GoToRequested?.Invoke(this, row);
            }
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is SymbolRow row)
            {
                GoToRequested?.Invoke(this, row);
                e.Handled = true;
            }
        };
        placeholder = new TextBlock { Classes = { StyleClasses.EmptyText } };
        ActionTips.SetWords(placeholder, AppActions.GoToFile, keys => $"The open file's outline shows here: open one with {ActionTips.Named("Go to File", keys)}");
        unoutlined = new TextBlock { Classes = { StyleClasses.EmptyText }, IsVisible = false };
        settle = new Debounce(Pause, Refresh);
        ListMenus.Attach(list, () => list.SelectedItem is not SymbolRow chosen ? [] :
        [
            ListMenus.Item("Go to", "F4", () => GoToRequested?.Invoke(this, chosen)),
            ListMenus.Item("Find Usages", null, () => UsagesRequested?.Invoke(this, chosen)),
            ListMenus.Item("Copy Name", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Name))),
        ]);
        Children.Add(new Panel { Children = { list, placeholder, unoutlined } });
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));
    }

    /// <summary>A member to go to.</summary>
    public event EventHandler<SymbolRow>? GoToRequested;

    /// <summary>A member whose usages to find, from its menu.</summary>
    public event EventHandler<SymbolRow>? UsagesRequested;

    public IReadOnlyList<SymbolRow> Rows => list.ItemsSource as IReadOnlyList<SymbolRow> ?? [];

    public SymbolRow? Selected => list.SelectedItem as SymbolRow;

    /// <summary>What the window says over its rows when it lists none: no file is shown, or why the file shown has no
    /// outline.</summary>
    public string? Said => unoutlined.IsVisible ? unoutlined.Text : placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>What the window shows: the active file, its name as shown, its language server document, the caret's line.</summary>
    public void Follow(Func<(string Path, string Name, LspDocument? Lsp, int CaretLine)?> active) => source = active;

    /// <summary>The file changed, or a C# file is shown: while the window is shown the outline is asked again once typing
    /// pauses; a hidden one asks as it is shown.</summary>
    public void Invalidate()
    {
        if (shown)
        {
            settle.Restart();
        }
    }

    /// <summary>Another file is active: a shown window asks for its outline at once once its server has the file, the pause
    /// being for typing's keys; a file its server has yet to connect is asked for once things settle.</summary>
    public void FollowActive()
    {
        if (!shown)
        {
            return;
        }
        if (source?.Invoke() is { Lsp: not null })
        {
            settle.Stop();
            IdeErrors.Observe(Refresh());
        }
        else
        {
            settle.Restart();
        }
    }

    /// <summary>A file was opened in the language server, or its state changed (the projects loaded): a shown window without
    /// the shown file's outline asks for it, since what it asked before the server had the file found nothing.</summary>
    public void FollowServer()
    {
        if (shown && asking is null && source?.Invoke() is { Lsp: not null } now && outlined != now.Path)
        {
            IdeErrors.Observe(Refresh());
        }
    }

    /// <summary>The caret moved: the member it is in is chosen, without going anywhere.</summary>
    public void FollowCaret()
    {
        if (source?.Invoke() is { } now && now.Path == outlined)
        {
            Choose(now.CaretLine);
        }
    }

    /// <summary>Shown (its stripe button, its action, a layout that has it open), or moved to another place: what it shows
    /// then is the file shown now.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        shown = true;
        IdeErrors.Observe(Refresh());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        shown = false;
        settle.Stop();
        asking?.Cancel();
        asking = null;
    }

    private async Task Refresh()
    {
        asking?.Cancel();
        asking = null;
        (string Path, string Name, LspDocument? Lsp, int CaretLine)? active = source?.Invoke();
        if (active is not { Lsp: { } lsp } now)
        {
            Show(Outline<SymbolRow>.Empty, null, active is { } file ? Refusals.NoOutline(file.Name) : null);
            return;
        }
        CancellationTokenSource mine = asking = new CancellationTokenSource();
        // Held, so a screenshot or a test settles on the outline rather than on the window before it came.
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            IReadOnlyList<DocumentSymbol> symbols = await lsp.OutlineAsync(mine.Token);
            if (mine.IsCancellationRequested)
            {
                return;
            }
            ServerLines lines = ServerLines.Of(lsp.Text.Snapshot);
            Outline<SymbolRow>? before = outlined == now.Path ? outline : null;
            Outline<SymbolRow> made = await BackgroundWork.Run(() => OutlineOf(symbols, now.Path, lines, before, mine.Token), mine.Token);
            if (mine.IsCancellationRequested)
            {
                return;
            }
            Show(made, now.Path, made.Rows.Count == 0 ? $"{now.Name} has nothing to outline" : null);
            Choose(source?.Invoke() is { } shown && shown.Path == now.Path ? shown.CaretLine : now.CaretLine);
        }
        catch (OperationCanceledException)
        {
            // Asked again since, or hidden.
        }
        catch (LanguageServerFailed)
        {
            // The outline shown stands; the next change, or a server started again, asks afresh.
        }
        finally
        {
            if (asking == mine)
            {
                asking = null;
            }
        }
    }

    /// <summary>A file's outline listed (none for <paramref name="path"/> null), the list handed its rows only when they are
    /// not those it has, and why it lists none when it says.</summary>
    private void Show(Outline<SymbolRow> rows, string? path, string? why)
    {
        outlined = path;
        if (!ReferenceEquals(rows, outline))
        {
            outline = rows;
            list.ItemsSource = rows.Rows;
        }
        unoutlined.Text = why;
        unoutlined.IsVisible = why is not null;
        placeholder.IsVisible = path is null && why is null;
    }

    private void Choose(int line)
    {
        choosing = true;
        int chosen = outline.InnermostAt(line);
        list.SelectedIndex = chosen;
        if (chosen >= 0)
        {
            list.ScrollIntoView(chosen);
        }
        choosing = false;
    }

    /// <summary>The outline as rows, a member's type after its name as Roslyn names it ("Main() : void"), at the editor's
    /// lines of the text the server's lines are of; a row the outline <paramref name="before"/> has in its place unchanged
    /// is that one. Made off the window's thread.</summary>
    public static Outline<SymbolRow> OutlineOf(IReadOnlyList<DocumentSymbol> symbols, string path, ServerLines lines, Outline<SymbolRow>? before = null,
        CancellationToken cancel = default) =>
        Outline<SymbolRow>.Of(symbols, symbol => symbol.Children, (symbol, depth) =>
        {
            SymbolRow row = Row(symbol, depth, path, lines);
            return (row, row.Position.Line, row.EndLine ?? row.Position.Line);
        }, before, cancel);

    private static SymbolRow Row(DocumentSymbol symbol, int depth, string path, ServerLines lines)
    {
        string name = symbol.Name;
        string? type = string.IsNullOrEmpty(symbol.Detail) || symbol.Detail == symbol.Name ? null : symbol.Detail;
        int typed = name.LastIndexOf(" : ", StringComparison.Ordinal);
        if (typed > 0)
        {
            type = name[(typed + 3)..];
            name = name[..typed];
        }
        return new SymbolRow(name, type is null ? null : $": {type}", null, symbol.Kind, path,
            lines.Text.GetPosition(lines.OffsetOf(symbol.SelectionRange.Start)), depth, lines.Text.GetLineFromOffset(lines.OffsetOf(symbol.Range.End)));
    }
}
