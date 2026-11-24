using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;

namespace Passenger.App.Database;

public enum DatabaseNodeKind
{
    Connection,
    Database,
    Schema,
    Group,
    Table,
    View,
    Routine,
    Column,
    Key,
    Index,
    Trigger,
    Note,
}

/// <summary>A row of the explorer: a connection, a database, a schema, a group of a schema's tables, views or routines or
/// of a table's columns, keys, indexes or triggers, one of those, or a note (none, or why a read failed). What is under it
/// is read as it is first opened.</summary>
public sealed class DatabaseNode(DatabaseNodeKind kind, string title, int depth, DatabaseSource source) : Observable, ITreeRow
{
    private bool expanded;
    private DatabaseMark mark = source.Mark;

    public DatabaseNodeKind Kind { get; } = kind;
    public string Title { get; } = title;
    public int Depth { get; } = depth;
    public DatabaseSource Source { get; } = source;

    /// <summary>The database it stands in, its schema and its table or view, as far as it has them.</summary>
    public string Database { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Object { get; init; } = string.Empty;

    /// <summary>Words shown muted after its title: a column's type, a routine's kind.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>A column's, index's or trigger's own name, and a column's type.</summary>
    public string Member { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;

    public List<DatabaseNode>? Children { get; set; }

    public bool Expands => Kind is not (DatabaseNodeKind.Column or DatabaseNodeKind.Key or DatabaseNodeKind.Index or DatabaseNodeKind.Routine or DatabaseNodeKind.Trigger
        or DatabaseNodeKind.Note);
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }

    /// <summary>A connection's mark as it stands, which its row shows.</summary>
    public DatabaseMark Mark { get => mark; set => Set(ref mark, value); }

    /// <summary>The table or view it names, for a table's or view's row.</summary>
    public DatabaseTable? Table => Kind is DatabaseNodeKind.Table or DatabaseNodeKind.View ? new DatabaseTable(Schema, Object) : null;

    /// <summary>The object its row names, whose definition it shows and whose scripts it makes; null for a connection, a
    /// database, a schema, a group, a key or a note.</summary>
    public DatabaseObjectRef? Target => Kind switch
    {
        DatabaseNodeKind.Table => new DatabaseObjectRef(DatabaseObjectKind.Table, Schema, Object),
        DatabaseNodeKind.View => new DatabaseObjectRef(DatabaseObjectKind.View, Schema, Object),
        DatabaseNodeKind.Routine => new DatabaseObjectRef(DatabaseObjectKind.Routine, Schema, Object, Detail: Detail),
        DatabaseNodeKind.Trigger => new DatabaseObjectRef(DatabaseObjectKind.Trigger, Schema, Member, Object),
        DatabaseNodeKind.Index => new DatabaseObjectRef(DatabaseObjectKind.Index, Schema, Member, Object),
        DatabaseNodeKind.Column => new DatabaseObjectRef(DatabaseObjectKind.Column, Schema, Member, Object, Type),
        _ => null,
    };

    public string IconKey => Kind switch
    {
        DatabaseNodeKind.Connection or DatabaseNodeKind.Database => IconKeys.Database,
        DatabaseNodeKind.Schema => IconKeys.DatabaseSchema,
        DatabaseNodeKind.Group => IconKeys.SolutionFolder,
        DatabaseNodeKind.Table => IconKeys.DatabaseTable,
        DatabaseNodeKind.View => IconKeys.DatabaseView,
        DatabaseNodeKind.Routine => IconKeys.DatabaseRoutine,
        DatabaseNodeKind.Column => IconKeys.DatabaseColumn,
        DatabaseNodeKind.Key => IconKeys.DatabaseKey,
        DatabaseNodeKind.Index => IconKeys.DatabaseIndex,
        DatabaseNodeKind.Trigger => IconKeys.DatabaseTrigger,
        _ => IconKeys.Info,
    };
}

/// <summary>The Database window: every connection, found or made by hand, as a tree read as it is opened, down to each
/// table's columns, keys and indexes; a table or view opens its data among the editors, and a connection a console.
/// Typing in the tree, or in its filter, narrows the rows read so far to those whose names hold the words.</summary>
public sealed class DatabaseExplorer : DockPanel
{
    private readonly DatabaseController controller;
    private readonly Func<DatabaseSource, string?> refused;
    private readonly TextBox search = SearchBox.Finding("Filter database objects");
    private readonly TreeList list = new() { Name = "DatabaseTree", Classes = { StyleClasses.Tree } };
    private readonly TreeRows<DatabaseNode> rows = [];
    private readonly EmptyState empty = new();
    private readonly TextBlock said = MutedText.Wrapped(string.Empty);
    private List<DatabaseNode> roots = [];

    /// <param name="refused">Why a connection may not be opened, or null where it may.</param>
    public DatabaseExplorer(DatabaseController controller, Func<DatabaseSource, string?> refused)
    {
        this.controller = controller;
        this.refused = refused;
        search.Name = "DatabaseSearch";
        said.Name = "DatabaseStatus";
        said.Margin = new Thickness(Gaps.Normal, Gaps.Small);
        Border side = ToolBars.Side(
            ToolButtons.Of(AppActions.DatabaseAdd, IconKeys.Add, "New Database Connection…", Request),
            ToolButtons.Of(AppActions.DatabaseRefresh, IconKeys.Rerun, "Refresh Database Connections", Request),
            ToolButtons.Of(AppActions.DatabaseConsole, IconKeys.SqlConsole, "Open SQL Console", Request),
            ToolButtons.Of(AppActions.DatabaseOpenTable, IconKeys.DatabaseTable, "Open Table Data", Request),
            ToolBars.Gap(),
            ToolButtons.Of(AppActions.DatabaseCancel, IconKeys.Stop, "Stop Database Operation", Request));
        Border top = ToolBars.Top(new ToolFlow { Margin = new Thickness(Gaps.Normal, Gaps.Hair), Children = { search } });
        SetDock(said, Dock.Bottom);
        Children.Add(side);
        Children.Add(top);
        Children.Add(said);
        Children.Add(new Panel { Children = { list, empty } });
        list.ItemsSource = rows;
        ReusedRows.Use(list, Row);
        TreeKeys.Attach<DatabaseNode>(list, node => IdeErrors.Observe(ToggleAsync(node)), node =>
        {
            if (node.Table is not null)
            {
                Request(AppActions.DatabaseOpenTable);
                return true;
            }
            if (node.Target is not null && !node.Expands)
            {
                Request(AppActions.DatabaseShowDdl);
                return true;
            }
            return false;
        });
        ListMenus.Attach(list, Menu);
        list.AddHandler(TextInputEvent, (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text) && !e.Text.Any(char.IsControl))
            {
                search.Focus();
                search.Text += e.Text;
                search.CaretIndex = search.Text?.Length ?? 0;
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) =>
        {
            if (ListKeys.Move(list, e))
            {
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && search.Text is { Length: > 0 })
            {
                search.Text = string.Empty;
                ListKeys.Hold(list, chooseFirst: true);
                e.Handled = true;
            }
        };
        controller.Changed += (_, _) => Show();
        Show();
    }

    /// <summary>An action asked of the window by a button, a menu or the keys.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>The row chosen, whose connection, table or view the window's actions act on.</summary>
    public DatabaseNode? Chosen => list.SelectedItem as DatabaseNode;

    /// <summary>The rows shown, for a test or an agent to find a row by.</summary>
    public IReadOnlyList<DatabaseNode> Rows => rows;

    private void Request(string action) => ActionRequested?.Invoke(this, action);

    /// <summary>Chooses a row.</summary>
    public void Choose(DatabaseNode node)
    {
        list.SelectedItem = node;
        list.ScrollIntoView(node);
    }

    /// <summary>Opens or closes a row, reading what is under it as it first opens; a connection opens only as the person asks.</summary>
    public async Task ToggleAsync(DatabaseNode node)
    {
        if (!node.Expands || controller.Busy)
        {
            return;
        }
        list.SelectedItem = node;
        if (!node.IsExpanded && node.Kind == DatabaseNodeKind.Connection && refused(node.Source) is { } why)
        {
            controller.Refuse(why);
            return;
        }
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            rows.Close(node);
            return;
        }
        if (node.Children is null && await ReadUnderAsync(node) is { } children)
        {
            node.Children = children;
        }
        if (node.Children is null || !rows.Contains(node))
        {
            return;
        }
        node.IsExpanded = true;
        rows.Open(node, TreeRows<DatabaseNode>.Shown(node.Children, n => n.Children));
    }

    /// <summary>Reads again what is under a connection, forgetting what was read.</summary>
    public void Forget(DatabaseSource source)
    {
        if (roots.FirstOrDefault(r => r.Source.Id == source.Id) is { } root)
        {
            if (root.IsExpanded)
            {
                root.IsExpanded = false;
                rows.Close(root);
            }
            root.Children = null;
        }
    }

    /// <summary>Reads again what a connection's database holds where the tree read it (a table made in it since): its row
    /// closed, forgotten and opened again with the rows under it that were open; one not read yet is left to be read as it opens.</summary>
    public async Task ReadAgainAsync(DatabaseSource source, string database)
    {
        DatabaseNode? found = Find(roots);
        if (found is not { Children: not null })
        {
            return;
        }
        HashSet<string> open = [];
        Opened(found.Children);
        bool shown = found.IsExpanded;
        if (shown)
        {
            found.IsExpanded = false;
            rows.Close(found);
        }
        found.Children = null;
        if (!shown)
        {
            return;
        }
        await ToggleAsync(found);
        await OpenAgainAsync(found.Children ?? []);

        DatabaseNode? Find(IEnumerable<DatabaseNode> nodes)
        {
            foreach (DatabaseNode node in nodes)
            {
                if (node.Source.Id == source.Id && (node.Kind == DatabaseNodeKind.Database && node.Database == database
                    || node.Kind == DatabaseNodeKind.Connection && node.Source.Kind == DatabaseKind.Sqlite))
                {
                    return node;
                }
                if (Find(node.Children ?? []) is { } under)
                {
                    return under;
                }
            }
            return null;
        }

        void Opened(IEnumerable<DatabaseNode> nodes)
        {
            foreach (DatabaseNode node in nodes.Where(n => n.IsExpanded))
            {
                open.Add(Key(node));
                Opened(node.Children ?? []);
            }
        }

        async Task OpenAgainAsync(IReadOnlyList<DatabaseNode> nodes)
        {
            foreach (DatabaseNode node in nodes.Where(n => n.Expands && !n.IsExpanded && open.Contains(Key(n))).ToList())
            {
                await ToggleAsync(node);
                await OpenAgainAsync(node.Children ?? []);
            }
        }

        static string Key(DatabaseNode node) => $"{node.Kind}|{node.Schema}|{node.Object}|{node.Title}";
    }

    private async Task<List<DatabaseNode>?> ReadUnderAsync(DatabaseNode node)
    {
        DatabaseSource source = node.Source;
        int depth = node.Depth + 1;
        switch (node.Kind)
        {
            case DatabaseNodeKind.Connection when source.Kind == DatabaseKind.Sqlite:
                return await ObjectsAsync(node, source.Database, depth);
            case DatabaseNodeKind.Connection:
                IReadOnlyList<string>? databases = await controller.ReadAsync(source, $"Opening {source.Name}…", cancel => DatabaseClient.DatabasesAsync(source, cancel));
                return databases is null ? null : Notes(depth, [.. databases.OrderBy(d => d != source.Database).Select(d =>
                    new DatabaseNode(DatabaseNodeKind.Database, d, depth, source) { Database = d, Detail = d == source.Database ? "default" : string.Empty })], "No databases", source);
            case DatabaseNodeKind.Database:
                return await ObjectsAsync(node, node.Database, depth);
            case DatabaseNodeKind.Table or DatabaseNodeKind.View:
                DatabaseSource within = source.In(node.Database);
                DatabaseTable table = node.Table!;
                DatabaseSchema? schema = await controller.ReadAsync(source, $"Reading {table}…", cancel => DatabaseClient.SchemaAsync(within, table, cancel));
                if (schema is null)
                {
                    return null;
                }
                List<DatabaseNode> parts =
                [
                    Group("Columns", depth, node, [.. schema.Columns.Select(c => Part(DatabaseNodeKind.Column, c.Name, c.Name,
                        c.Type + (c.Nullable ? string.Empty : " not null") + (c.Primary ? ", primary key" : string.Empty) + (c.Generated ? ", generated" : string.Empty), c.Type))]),
                ];
                if (node.Kind == DatabaseNodeKind.Table)
                {
                    parts.Add(Group("Keys", depth, node, [.. schema.Keys.Select(k => Part(DatabaseNodeKind.Key, k, string.Empty))]));
                    parts.Add(Group("Indexes", depth, node, [.. schema.Indexes.DistinctBy(i => i.Said).Select(i => Part(DatabaseNodeKind.Index, i.Said, i.Name))]));
                }
                if (node.Kind == DatabaseNodeKind.Table || schema.Triggers.Count > 0)
                {
                    parts.Add(Group("Triggers", depth, node, [.. schema.Triggers.Select(t => Part(DatabaseNodeKind.Trigger, t.Name, t.Name, t.Said))]));
                }
                return parts;

                DatabaseNode Part(DatabaseNodeKind kind, string title, string member, string detail = "", string type = "") => new(kind, title, depth + 1, source)
                {
                    Database = node.Database, Schema = node.Schema, Object = node.Object, Member = member, Detail = detail, Type = type,
                };
            default:
                return node.Children ?? [];
        }
    }

    /// <summary>A database's schemas, each with its tables, views and routines grouped; SQLite's and MySQL's one schema is left
    /// out, its groups standing under the database itself.</summary>
    private async Task<List<DatabaseNode>?> ObjectsAsync(DatabaseNode node, string database, int depth)
    {
        DatabaseSource source = node.Source;
        IReadOnlyList<(string Schema, IReadOnlyList<DatabaseObject> Objects)>? read = await controller.ReadAsync(source, $"Reading {database}…",
            cancel => DatabaseClient.ObjectsAsync(source.In(database), cancel));
        if (read is null)
        {
            return null;
        }
        bool flat = source.Kind is DatabaseKind.Sqlite or DatabaseKind.MySql;
        List<DatabaseNode> shown = [];
        foreach ((string schema, IReadOnlyList<DatabaseObject> objects) in read)
        {
            int level = flat ? depth : depth + 1;
            List<DatabaseNode> groups = [];
            foreach ((DatabaseObjectKind kind, string title) in new[] { (DatabaseObjectKind.Table, "Tables"), (DatabaseObjectKind.View, "Views"), (DatabaseObjectKind.Routine, "Routines") })
            {
                List<DatabaseNode> members = [.. objects.Where(o => o.Kind == kind).Select(o => new DatabaseNode(kind switch
                {
                    DatabaseObjectKind.Table => DatabaseNodeKind.Table,
                    DatabaseObjectKind.View => DatabaseNodeKind.View,
                    _ => DatabaseNodeKind.Routine,
                }, o.Name, level + 1, source) { Database = database, Schema = o.Schema, Object = o.Name, Detail = o.Detail })];
                if (members.Count > 0 || kind == DatabaseObjectKind.Table)
                {
                    groups.Add(Group(title, level, node, members));
                }
            }
            if (flat)
            {
                shown.AddRange(groups);
            }
            else
            {
                shown.Add(new DatabaseNode(DatabaseNodeKind.Schema, schema, depth, source) { Database = database, Schema = schema, Children = groups });
            }
        }
        return Notes(depth, shown, "No schemas", source);
    }

    private static DatabaseNode Group(string title, int depth, DatabaseNode under, List<DatabaseNode> members) =>
        new(DatabaseNodeKind.Group, title, depth, under.Source)
        {
            Database = under.Database, Schema = under.Schema, Object = under.Object, Detail = members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Children = Notes(depth + 1, members, $"No {title.ToLowerInvariant()}", under.Source),
        };

    /// <summary>The rows of a level, or a note saying it has none.</summary>
    private static List<DatabaseNode> Notes(int depth, List<DatabaseNode> members, string none, DatabaseSource source) =>
        members.Count > 0 ? members : [new DatabaseNode(DatabaseNodeKind.Note, none, depth, source)];

    private IReadOnlyList<Control> Menu()
    {
        if (Chosen is not { } node)
        {
            return [];
        }
        List<Control> items = [];
        if (node.Table is not null)
        {
            items.Add(ListMenus.Open("Open Table Data", () => Request(AppActions.DatabaseOpenTable)));
            items.Add(ListMenus.Keyed("Export Data…", AppActions.DatabaseExport, () => Request(AppActions.DatabaseExport)));
        }
        if (node.Kind is DatabaseNodeKind.Connection or DatabaseNodeKind.Database or DatabaseNodeKind.Schema or DatabaseNodeKind.Table
            || node.Kind == DatabaseNodeKind.Group && node.Title == "Tables")
        {
            items.Add(ListMenus.Keyed("Import Data…", AppActions.DatabaseImport, () => Request(AppActions.DatabaseImport),
                controller.Find(node.Source.Id) is { Mark: DatabaseMark.ReadOnly } readOnly ? Refusals.ReadOnlyConnection(readOnly.Name) : null));
        }
        if (node.Target is not null)
        {
            items.Add(ListMenus.Keyed("Show DDL", AppActions.DatabaseShowDdl, () => Request(AppActions.DatabaseShowDdl)));
            items.Add(ListMenus.Sub("SQL Scripts",
                ListMenus.Item("Create", () => Request(AppActions.DatabaseScriptCreate)),
                ListMenus.Item("Alter", () => Request(AppActions.DatabaseScriptAlter)),
                ListMenus.Item("Drop", () => Request(AppActions.DatabaseScriptDrop))));
        }
        items.Add(ListMenus.Item("Open SQL Console", () => Request(AppActions.DatabaseConsole), icon: IconKeys.SqlConsole));
        if (node.Kind == DatabaseNodeKind.Connection)
        {
            items.Add(new Separator());
            items.Add(MenuChoice.Of("Read-Only", node.Mark == DatabaseMark.ReadOnly, () => Request(AppActions.DatabaseReadOnly)));
            items.Add(MenuChoice.Of("Production", node.Mark == DatabaseMark.Production, () => Request(AppActions.DatabaseProduction)));
            items.Add(new Separator());
            items.Add(ListMenus.Edit("Edit Connection…", () => Request(AppActions.DatabaseConnectionEdit)));
            items.Add(ListMenus.Item("Rename Connection…", () => Request(AppActions.DatabaseConnectionRename)));
            items.Add(ListMenus.Remove("Remove Connection", () => Request(AppActions.DatabaseConnectionRemove)));
        }
        if (node.Kind is not (DatabaseNodeKind.Note or DatabaseNodeKind.Group))
        {
            items.Add(ListMenus.Item("Copy Name", () => IdeErrors.Observe(ListMenus.CopyAsync(list, node.Title))));
        }
        return items;
    }

    private void Show()
    {
        List<DatabaseNode> wanted = [.. controller.Sources.Select(s => roots.FirstOrDefault(r => r.Source with { Mark = s.Mark } == s)
            ?? new DatabaseNode(DatabaseNodeKind.Connection, s.Name, 0, s)
            {
                Database = s.Database, Detail = (s.Found ?? DatabaseKinds.Title(s.Kind)) + (s.Tunnel is { } tunnel ? $" · through {tunnel.Host}" : string.Empty),
            })];
        foreach ((DatabaseNode root, DatabaseSource source) in wanted.Zip(controller.Sources))
        {
            root.Mark = source.Mark;
        }
        if (!wanted.SequenceEqual(roots))
        {
            roots = wanted;
            Filter();
        }
        said.Text = controller.Said;
        empty.Text = controller.Discovered ? "No database connections. New Connection… adds one." : "Finding this folder's database connections…";
        empty.IsVisible = roots.Count == 0;
    }

    /// <summary>The rows read so far whose names hold the filter's words, with the rows they stand under; all of them where
    /// it is empty.</summary>
    private void Filter()
    {
        string query = search.Text?.Trim() ?? string.Empty;
        DatabaseNode? chosen = Chosen;
        if (query.Length == 0)
        {
            rows.Show([.. TreeRows<DatabaseNode>.Shown(roots, n => n.Children)]);
        }
        else
        {
            List<DatabaseNode> matched = [];
            foreach (DatabaseNode root in roots)
            {
                Match(root, matched);
            }
            rows.Show(matched);
        }
        if (chosen is not null && rows.Contains(chosen))
        {
            list.SelectedItem = chosen;
        }
        else if (query.Length > 0)
        {
            list.SelectedItem = rows.FirstOrDefault(r => r.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        bool Match(DatabaseNode node, List<DatabaseNode> into)
        {
            int at = into.Count;
            into.Add(node);
            bool any = node.Kind != DatabaseNodeKind.Note && node.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
            foreach (DatabaseNode child in node.Children ?? [])
            {
                any |= Match(child, into);
            }
            if (!any)
            {
                into.RemoveRange(at, into.Count - at);
            }
            return any;
        }
    }

    private Control Row()
    {
        RowGlyphs glyphs = new();
        TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock detail = MutedText.Of(string.Empty);
        detail.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        detail.VerticalAlignment = VerticalAlignment.Center;
        Border production = Tags.Of("production", TokenKeys.Error);
        Border readOnly = Tags.Of("read-only");
        production.Margin = readOnly.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        StackPanel words = new() { Orientation = Orientation.Horizontal, Children = { text, production, readOnly, detail } };
        DockPanel line = new() { Height = ListRow.Height, Children = { glyphs, words } };
        SetDock(glyphs, Dock.Left);
        glyphs.ChevronPressed += (_, e) =>
        {
            if (line.DataContext is DatabaseNode node)
            {
                IdeErrors.Observe(ToggleAsync(node));
                e.Handled = true;
            }
        };
        return line.Showing<DatabaseNode>(node =>
        {
            line.Margin = new Thickness(RowExpander.Column * node.Depth, 0, Gaps.Normal, 0);
            glyphs.HasChevron = node.Expands;
            glyphs.Turned = node.IsExpanded;
            glyphs.IconKey = node.IconKey;
            text.Text = ContainerSecrets.InSaid(node.Title);
            detail.Text = ContainerSecrets.InSaid(node.Detail);
            detail.IsVisible = node.Detail.Length > 0;
            production.IsVisible = node.Kind == DatabaseNodeKind.Connection && node.Mark == DatabaseMark.Production;
            readOnly.IsVisible = node.Kind == DatabaseNodeKind.Connection && node.Mark == DatabaseMark.ReadOnly;
        });
    }
}
