using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Debugging;
using Passenger.Dap;

namespace Passenger.App.ToolWindows;

/// <summary>The Modules window: the modules the chosen debug session's program has loaded, in the order they loaded, each
/// with its version, whether its symbols are loaded (its own, or decompiled), Just My Code's verdict and its file, its tip
/// saying where its symbols came from, whether the runtime compiles it optimized and where its image lies. Its titles sort,
/// its filter narrows (Find's keys); a session whose debugger tells of no modules says why in their place.</summary>
public sealed class ModulesView : DockPanel
{
    /// <summary>The columns' widths: the name's and the path's share what the version's, symbols' and verdict's leave.</summary>
    private const string Columns = "2*,96,96,80,3*";

    private readonly ListBox list;
    private readonly EmptyState placeholder;
    private readonly TextBox filter = SearchBox.Finding("Filter modules");
    private readonly ColumnTitles titles;
    private readonly Panel table;
    private DebugSession? session;
    private bool listing;

    /// <summary>The column the rows are ordered by (none: the order they loaded in), and whether the last comes first.</summary>
    private (int? Column, bool Descending) order;

    public ModulesView()
    {
        filter.TextChanged += (_, _) => List();
        filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list!.ItemCount > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                ListKeys.Hold(list, chooseFirst: true);
                e.Handled = true;
            }
        };
        filter.Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0);
        Children.Add(ToolBars.Top(filter));
        titles = ColumnTitles.Of(Columns, ["Name", "Version", "Symbols", "User code", "Path"], ColumnTitles.OverList).Sorting(Sort);
        list = new ListBox { Classes = { StyleClasses.Tree } };
        ReusedRows.Use(list, Row);
        ListMenus.Attach(list, () => list.SelectedItem is not DapModule chosen ? [] :
        [
            ListMenus.Item("Copy Name", () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Name))),
            ListMenus.Item("Copy Path", () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Path!)), chosen.Path is null ? Refusals.ModuleUnplaced : null),
        ]);
        placeholder = new EmptyState();
        table = new DockPanel { Children = { titles, list } };
        Children.Add(new Panel { Children = { table, placeholder } });
        List();
    }

    /// <summary>The rows listed, as the filter and the order leave them.</summary>
    public IReadOnlyList<DapModule> Rows => list.ItemsSource as IReadOnlyList<DapModule> ?? [];

    /// <summary>What the window says in place of rows; null while it lists some.</summary>
    public string? Said => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>The filter's words.</summary>
    public string Filter
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>Gives the filter the keys, its text chosen: Find's keys while the window has them.</summary>
    public void FocusFilter()
    {
        filter.Focus();
        filter.SelectAll();
    }

    /// <summary>Lists a session's modules from now on, as they load and unload; none while nothing is debugged.</summary>
    public void Show(DebugSession? shown)
    {
        UiThread.Check();
        if (session == shown)
        {
            return;
        }
        if (session is not null)
        {
            session.ModulesChanged -= OnModulesChanged;
        }
        session = shown;
        if (session is not null)
        {
            session.ModulesChanged += OnModulesChanged;
        }
        List();
    }

    /// <summary>Orders the rows by a column, as its title's click does: again by the same one the other way.</summary>
    public void Sort(int column)
    {
        order = (column, order.Column == column && !order.Descending);
        List();
    }

    /// <summary>A run of modules loading is listed once, after the last of the run the window's thread hears.</summary>
    private void OnModulesChanged(object? sender, EventArgs e)
    {
        if (!listing)
        {
            listing = true;
            UiThread.Post(() =>
            {
                listing = false;
                List();
            });
        }
    }

    /// <summary>The modules the filter leaves, in the order chosen, the row chosen kept chosen.</summary>
    private void List()
    {
        string[] words = Filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<DapModule> all = session?.Modules ?? [];
        IEnumerable<DapModule> fitting = all.Where(m => words.All(w => m.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
            || m.Path?.Contains(w, StringComparison.OrdinalIgnoreCase) == true));
        List<DapModule> rows = [.. order.Column is int column ? Ordered(fitting, column) : order.Descending ? fitting.Reverse() : fitting];
        string? chosen = (list.SelectedItem as DapModule)?.Id;
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(m => m.Id == chosen);
        placeholder.Text = session is null ? "The modules of a program being debugged show here"
            : session.ModulesUnlisted ?? (all.Count == 0 ? "The program has loaded no module yet" : "No module fits the filter");
        placeholder.IsVisible = rows.Count == 0;
        table.IsVisible = rows.Count > 0;
    }

    private IEnumerable<DapModule> Ordered(IEnumerable<DapModule> modules, int column)
    {
        Func<DapModule, string> key = column switch
        {
            1 => m => m.Version ?? string.Empty,
            2 => m => m.SymbolStatus ?? string.Empty,
            3 => m => UserCode(m),
            4 => m => m.Path ?? string.Empty,
            _ => m => m.Name,
        };
        return order.Descending ? modules.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : modules.OrderBy(key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Just My Code's verdict on a module in a word.</summary>
    private static string UserCode(DapModule module) => module.IsUserCode == true ? "Yes" : "No";

    /// <summary>A module's tip: its file, where its symbols came from, how the runtime compiles it and where its image lies.</summary>
    private static string Tip(DapModule module) => string.Join('\n', new[]
    {
        module.Path ?? (module.IsDynamic == true ? $"{module.Name}: made as the program runs, with no file" : module.Name),
        module.SymbolStatus == DapSymbolStatus.None ? "No symbols: a frame in it shows no source until it is decompiled"
            : $"Symbols {(module.SymbolStatus == DapSymbolStatus.Decompiled ? "decompiled" : "loaded")}{(module.SymbolFilePath is { } from ? $", {(Path.IsPathRooted(from) ? $"from {from}" : from)}" : string.Empty)}",
        module.IsUserCode == true ? "User code: Just My Code steps into it and stops in it" : "Not user code: Just My Code steps over it",
        module.IsOptimized == true ? "Compiled optimized" : "Compiled unoptimized, for debugging",
        module.AddressRange is { } address ? $"Loaded at {address}" : null,
    }.OfType<string>());

    /// <summary>A module's row: its name, version, symbols, verdict and path, each cut short where the window is narrow.</summary>
    private Control Row()
    {
        TextBlock name = Cell();
        TextBlock version = Cell();
        TextBlock symbols = Cell();
        TextBlock user = Cell();
        TextBlock path = Cell();
        path.Classes.Add(StyleClasses.Muted);
        Grid row = titles.Row(name, version, symbols, user, path);
        return row.Showing<DapModule>(module =>
        {
            name.Text = module.Name;
            version.Text = module.Version ?? string.Empty;
            symbols.Text = module.SymbolStatus ?? string.Empty;
            symbols.Classes.Set(StyleClasses.Muted, module.SymbolStatus == DapSymbolStatus.None);
            user.Text = UserCode(module);
            user.Classes.Set(StyleClasses.Muted, module.IsUserCode != true);
            path.Text = module.Path ?? string.Empty;
            ToolTip.SetTip(row, Tip(module));
        });

        static TextBlock Cell() => new()
        {
            Margin = new Thickness(Gaps.Normal, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }
}
