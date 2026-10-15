using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Passenger.App.Navigation;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>Rider's Structure window: the active file's types and members, indented, from the language server's
/// outline, following the editor (a moment after typing stops) and choosing the member the caret is in. A click or
/// Enter goes to a member.</summary>
public sealed class StructureView : DockPanel
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(600);

    private readonly ListBox list;
    private readonly TextBlock placeholder;
    private readonly DispatcherTimer settle;
    private Func<(string Path, LspDocument? Lsp, int CaretLine)?>? source;
    private CancellationTokenSource? asking;
    private bool choosing;

    public StructureView()
    {
        list = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<SymbolRow>((row, _) => SymbolPopup.Row(row), supportsRecycling: false),
        };
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
        placeholder = new TextBlock { Classes = { "emptyHint" }, Text = "A C# file's members show here" };
        settle = new DispatcherTimer { Interval = Pause };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            _ = Refresh();
        };
        ListMenus.Attach(list, () => list.SelectedItem is not SymbolRow chosen ? [] :
        [
            ListMenus.Item("Go to", "F4", () => GoToRequested?.Invoke(this, chosen)),
            ListMenus.Item("Find Usages", null, () => UsagesRequested?.Invoke(this, chosen)),
            ListMenus.Item("Copy Name", null, () => _ = ListMenus.CopyAsync(this, chosen.Name)),
        ]);
        Children.Add(new Panel { Children = { list, placeholder } });
        this.Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Chrome"));
    }

    /// <summary>A member to go to.</summary>
    public event EventHandler<SymbolRow>? GoToRequested;

    /// <summary>A member whose usages to find, from its menu.</summary>
    public event EventHandler<SymbolRow>? UsagesRequested;

    public IReadOnlyList<SymbolRow> Rows => list.ItemsSource as IReadOnlyList<SymbolRow> ?? [];

    public SymbolRow? Selected => list.SelectedItem as SymbolRow;

    /// <summary>What the window shows: the active file, its language server document, the caret's line.</summary>
    public void Follow(Func<(string Path, LspDocument? Lsp, int CaretLine)?> active) => source = active;

    /// <summary>The file changed, or another is active: the outline is asked again once things settle.</summary>
    public void Invalidate()
    {
        settle.Stop();
        settle.Start();
    }

    /// <summary>The caret moved: the member it is in is chosen, without going anywhere.</summary>
    public void FollowCaret()
    {
        if (source?.Invoke() is { } now)
        {
            Choose(now.CaretLine);
        }
    }

    public async Task Refresh()
    {
        asking?.Cancel();
        CancellationTokenSource mine = asking = new CancellationTokenSource();
        if (source?.Invoke() is not { Lsp: { } lsp } now)
        {
            list.ItemsSource = null;
            placeholder.IsVisible = true;
            return;
        }
        try
        {
            IReadOnlyList<DocumentSymbol> outline = await lsp.OutlineAsync(mine.Token);
            if (mine.IsCancellationRequested)
            {
                return;
            }
            list.ItemsSource = Flatten(outline, 0, now.Path, ServerLines.Of(lsp.Text.Snapshot));
            placeholder.IsVisible = Rows.Count == 0;
            Choose(now.CaretLine);
        }
        catch (OperationCanceledException)
        {
            // Asked again since.
        }
        catch (LanguageServerFailed)
        {
            // The outline shown stands; the next change, or a server started again, asks afresh.
        }
    }

    private void Choose(int line)
    {
        choosing = true;
        list.SelectedItem = Rows.LastOrDefault(r => r.Spans(line));
        if (list.SelectedItem is not null)
        {
            list.ScrollIntoView(list.SelectedItem);
        }
        choosing = false;
    }

    /// <summary>The outline as rows, a member's type after its name as Roslyn names it ("Main() : void"), at the editor's
    /// lines of the text the server's lines are of.</summary>
    public static IReadOnlyList<SymbolRow> Flatten(IReadOnlyList<DocumentSymbol> symbols, int depth, string path, ServerLines lines) =>
        [.. symbols.SelectMany(symbol => (IEnumerable<SymbolRow>)[Row(symbol, depth, path, lines), .. Flatten(symbol.Children ?? [], depth + 1, path, lines)])];

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
