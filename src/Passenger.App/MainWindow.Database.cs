using Passenger.App.Database;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
    private DatabaseController? database;
    private DatabaseExplorer? databaseExplorer;
    private SqlConsoleChecks? sqlChecks;

    public DatabaseController Database => database!;
    public DatabaseExplorer DatabaseExplorer => databaseExplorer ?? (DatabaseExplorer)Dock[ToolWindowIds.Database].Content;

    private void AddDatabase()
    {
        DatabaseController controller = database = new DatabaseController(work, text => status.Text = text);
        Docking.ToolWindow shown = new(ToolWindowIds.Database, "Database", IconKeys.Database, () =>
        {
            DatabaseExplorer view = databaseExplorer = new DatabaseExplorer(controller, WhyNotOpened);
            // What the window asks runs where it was asked, on its chosen row, wherever the keys are.
            view.ActionRequested += (_, action) =>
            {
                ActionContext now = actions!.Context();
                Request(action, new ActionContext(now.File, now.Editor, view, Dock[ToolWindowIds.Database], now.Debugging));
            };
            ActionButtons.ShowMarked(view, actions!);
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Action = AppActions.ShowDatabase,
            Serves = ToolWindowServes.EveryFolder,
            Menus = [new MenuPlace("Tools", 0, 6)],
        };
        Dock.Add(shown);
        Editors.ConsoleOf = document => controller.ConsoleOf(document.Path);
        SqlConsoleChecks checks = sqlChecks = new SqlConsoleChecks(document => controller.ConsoleOf(document.Path) is { Connection: { } id } && controller.Find(id) is { } source
            ? (source.Kind, controller.CatalogOf(source)) : null);
        Editors.Opened += (_, document) => checks.Attach(document);
        Editors.Closed += (_, document) => checks.Detach(document);
        controller.Changed += (_, _) => checks.SchemaChanged();
        Editors.MarkOf = document => document.DatabaseTab is { } table ? controller.Find(table.Connection)?.Mark ?? DatabaseMark.None
            : document.Language == Languages.Language.Sql && controller.ConsoleOf(document.Path) is { Connection: { } id } ? controller.Find(id)?.Mark ?? DatabaseMark.None
            : DatabaseMark.None;
        Editors.AsksBeforeClosing = AsksBeforeClosingDatabase;
        Editors.MakeConsoleView = () =>
        {
            SqlConsoleView view = new(() => controller.Sources, console => console.Connection is { } id && controller.Find(id) is { } source ? controller.HistoryOf(source) : [],
                action => Request(action));
            view.HistoryChosen += (_, chosen) => WriteIntoConsole(chosen.Console, chosen.Ran.Statement.TrimEnd() + (chosen.Ran.Statement.TrimEnd().EndsWith(';') ? string.Empty : ";") + "\n");
            view.ConnectionChosen += (_, chosen) =>
            {
                if (chosen.Console.InTransaction && chosen.Console.Connection != chosen.Source.Id)
                {
                    Refuse(Refusals.TransactionOpen);
                    view.Refresh();
                    return;
                }
                controller.Bind(chosen.Console.Path, chosen.Source);
                Editors.FollowSidePanes();
            };
            view.CommitChosen += (_, chosen) =>
            {
                if (controller.SetManual(chosen.Console, chosen.Manual) is { } why)
                {
                    Refuse(why);
                }
            };
            ActionButtons.ShowMarked(view, actions!);
            return view;
        };
        controller.Changed += (_, _) =>
        {
            foreach (Editors.EditorGroup group in Editors.Groups)
            {
                group.Console?.Refresh();
                if (group.Active is { } sql && sql.Language == Languages.Language.Sql && controller.ConsoleOf(sql.Path) is not null)
                {
                    sql.DatabaseTitleChanged();
                }
            }
        };
        shown.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(Docking.ToolWindow.IsShown) && shown.IsShown)
            {
                UiThread.Post(DiscoverDatabasesIfShown);
            }
        };
        Closed += (_, _) => controller.Stop();
        actions!.Add(new IdeAction(AppActions.DatabaseCancel, "Stop Database Operation", "Database", _ => controller.CancelOperation())
        {
            WhyNot = _ => controller.Busy ? null : Refusals.NoDatabaseOperation,
        });
        Add(AppActions.DatabaseAdd, "New Database Connection…", () => EditDatabaseConnectionAsync(null), () => Folder is null ? Refusals.NoFolder : PersonsOwn());
        Add(AppActions.DatabaseConnectionEdit, "Edit Database Connection…", () => EditDatabaseConnectionAsync(ChosenConnection()),
            () => ChosenConnection() is not { } chosen ? Refusals.NoDatabaseConnection : controller.InTransaction(chosen) ? Refusals.TransactionOpen : PersonsOwn());
        Add(AppActions.DatabaseConnectionRename, "Rename Database Connection…", RenameDatabaseConnectionAsync,
            () => ChosenConnection() is null ? Refusals.NoDatabaseConnection : PersonsOwn());
        Add(AppActions.DatabaseConnectionRemove, "Remove Database Connection…", RemoveDatabaseConnectionAsync,
            () => ChosenConnection() is not { } chosen ? Refusals.NoDatabaseConnection : controller.InTransaction(chosen) ? Refusals.TransactionOpen : PersonsOwn());
        Add(AppActions.DatabaseOpenTable, "Open Table Data", OpenDatabaseTableAsync,
            () => databaseExplorer?.Chosen is not { Table: not null } chosen ? Refusals.NoDatabaseTable : WhyNotOpened(chosen.Source) ?? PersonsOwn());
        Add(AppActions.DatabaseRefresh, "Refresh Database Connections", controller.RefreshAsync, () => Folder is null ? Refusals.NoFolder : null);
        Add(AppActions.DatabaseConnect, "Open Database Connection", OpenChosenConnectionAsync,
            () => ChosenConnection() is not { } chosen ? Refusals.NoDatabaseConnection : WhyNotOpened(chosen) ?? PersonsOwn());
        Add(AppActions.DatabaseConsole, "Open SQL Console", () => OpenSqlConsoleAsync(),
            () => ChosenConnection() is not { } chosen ? Refusals.NoDatabaseConnection : WhyNotOpened(chosen) ?? PersonsOwn());
        Add(AppActions.DatabaseShowDdl, "Show DDL", ShowDdlAsync, WhyNotObject, menu: new MenuPlace("Tools/Database", 4, 0));
        Add(AppActions.DatabaseScriptCreate, "Generate Create Script", () => ScriptIntoConsoleAsync(DdlScript.Create), WhyNotObject, menu: new MenuPlace("Tools/Database", 4, 1));
        Add(AppActions.DatabaseScriptAlter, "Generate Alter Script", () => ScriptIntoConsoleAsync(DdlScript.Alter), WhyNotObject, menu: new MenuPlace("Tools/Database", 4, 2));
        Add(AppActions.DatabaseScriptDrop, "Generate Drop Script", () => ScriptIntoConsoleAsync(DdlScript.Drop), WhyNotObject, menu: new MenuPlace("Tools/Database", 4, 3));
        Add(AppActions.DatabaseDiagram, "Show Schema Diagram", ShowDiagramAsync, WhyNotSchema, menu: new MenuPlace("Tools/Database", 6, 0));
        Add(AppActions.DatabaseCompare, "Compare Schemas…", CompareSchemasAsync, WhyNotSchema, menu: new MenuPlace("Tools/Database", 6, 1));
        actions!.Add(new IdeAction(AppActions.DatabaseRun, "Run Statement", "Database", _ => IdeErrors.Observe(RunSqlAsync()))
        {
            WhyNot = _ => Editors.Active is not { Language: var language } || language != Languages.Language.Sql ? Refusals.NoSqlFile
                : controller.ConsoleOf(Editors.Active.Path) is { Running: true } ? Refusals.SqlRunning : PersonsOwn(),
            Menus = [new MenuPlace("Tools/Database", 1, 0)],
            KeysOf = () => AppActions.KeysIn(AppActions.DatabaseRun) is var keys && keys != AppActions.DatabaseRun ? keys : null,
        });
        actions!.Add(new IdeAction(AppActions.DatabaseRunFile, "Run SQL File", "Database", context => IdeErrors.Observe(RunSqlFileAsync(SqlFileOf(context))))
        {
            WhyNot = context => SqlFileOf(context) is not { } file ? Refusals.NoSqlFile : controller.ConsoleOf(file) is { Running: true } ? Refusals.SqlRunning : PersonsOwn(),
            Menus = [new MenuPlace("Tools/Database", 1, 4)],
        });
        actions!.Add(new IdeAction(AppActions.DatabaseExplain, "Explain Plan", "Database", _ => IdeErrors.Observe(ExplainSqlAsync()))
        {
            WhyNot = _ => Editors.Active is not { Language: var language } || language != Languages.Language.Sql ? Refusals.NoSqlFile
                : controller.ConsoleOf(Editors.Active.Path) is { Running: true } ? Refusals.SqlRunning : PersonsOwn(),
            Menus = [new MenuPlace("Tools/Database", 1, 6)],
        });
        actions!.Add(new IdeAction(AppActions.DatabaseHistory, "Query History", "Database", _ => Editors.ActiveGroup.Console?.ShowHistory())
        {
            WhyNot = _ => Editors.Active is not { Language: var language } file || language != Languages.Language.Sql || controller.ConsoleOf(file.Path) is not { Connection: not null }
                || Editors.ActiveGroup.Console is null ? Refusals.NoSqlConsole : null,
            Menus = [new MenuPlace("Tools/Database", 1, 5)],
        });
        actions!.Add(new IdeAction(AppActions.DatabaseRunStop, "Stop Statement", "Database", _ => (Editors.Active is { } file ? controller.ConsoleOf(file.Path) : null)?.Stop())
        {
            WhyNot = _ => Editors.Active is { } file && controller.ConsoleOf(file.Path) is { Running: true } ? null : Refusals.NoSqlRunning,
            Menus = [new MenuPlace("Tools/Database", 1, 1)],
            KeysOf = () => AppActions.KeysIn(AppActions.DatabaseRunStop) is var keys && keys != AppActions.DatabaseRunStop ? keys : null,
        });
        Transaction(AppActions.DatabaseCommit, "Commit", commit: true, 2);
        Transaction(AppActions.DatabaseRollback, "Roll Back", commit: false, 3);
        Table(AppActions.DatabaseSubmit, "Submit Changes", table => IdeErrors.Observe(table.SubmitAsync()), table => table.WhyNotSubmit, 0);
        Table(AppActions.DatabaseRevert, "Revert Changes", table => table.Revert(), table => table.WhyNotRevert, 1);
        Table(AppActions.DatabaseAddRow, "Add Row", table => table.AddRow(), table => table.WhyNotAddRow, 2);
        Table(AppActions.DatabaseDeleteRows, "Delete Rows", table => table.DeleteRows(), table => table.WhyNotDeleteRows, 3);
        Mark(AppActions.DatabaseReadOnly, "Read-Only Connection", DatabaseMark.ReadOnly);
        Mark(AppActions.DatabaseProduction, "Production Connection", DatabaseMark.Production);
        actions!.Add(new IdeAction(AppActions.DatabaseExport, "Export Data…", "Database", context => IdeErrors.Observe(ExportDataAsync(context)))
        {
            WhyNot = context => WhyNotExport(context) ?? PersonsOwn(),
            Menus = [new MenuPlace("Tools/Database", 5, 0)],
            Words = ["CSV", "JSON", "SQL inserts", "Save Rows"],
        });
        actions!.Add(new IdeAction(AppActions.DatabaseImport, "Import Data…", "Database", context => IdeErrors.Observe(ImportDataAsync(context)))
        {
            WhyNot = context => WhyNotImport(context) ?? PersonsOwn(),
            Menus = [new MenuPlace("Tools/Database", 5, 1)],
            Words = ["CSV", "JSON", "Load Rows"],
        });
        foreach ((string id, string format) in Tables.CellGrid.CopyFormats)
        {
            actions!.Add(new IdeAction(id, $"Copy as {format}", "Edit", context =>
            {
                if (context.Focused is Tables.CellGrid grid)
                {
                    grid.Execute(id);
                }
            })
            {
                WhyNot = context => context.Focused is Tables.CellGrid grid ? grid.WhyNotCopy(id) : Refusals.NoTableCells,
                Words = ["Copy Rows", "Copy Cells"],
            });
        }

        void Transaction(string id, string title, bool commit, int at)
        {
            actions!.Add(new IdeAction(id, title, "Database", _ =>
            {
                if (Editors.Active is { } file && controller.ConsoleOf(file.Path) is { } console)
                {
                    IdeErrors.Observe(controller.EndTransactionAsync(console, commit));
                }
            })
            {
                WhyNot = _ => Editors.Active is not { Language: var language } file || language != Languages.Language.Sql || controller.ConsoleOf(file.Path) is not { } console
                    ? Refusals.NoSqlFile : !console.Manual ? Refusals.AutoCommits : console.Running ? Refusals.SqlRunning : !console.InTransaction ? Refusals.NoTransaction : PersonsOwn(),
                Menus = [new MenuPlace("Tools/Database", 1, at)],
            });
        }

        void Table(string id, string title, Action<DatabaseTableTab> run, Func<DatabaseTableTab, string?> why, int at)
        {
            actions!.Add(new IdeAction(id, title, "Database", _ =>
            {
                if (Editors.Active?.DatabaseTable is { } table)
                {
                    run(table);
                }
            })
            {
                WhyNot = _ => Editors.Active?.DatabaseTable is not { } table ? Refusals.NoDatabaseTable : why(table) ?? PersonsOwn(),
                Menus = [new MenuPlace("Tools/Database", 2, at)],
            });
        }

        void Mark(string id, string title, DatabaseMark mark)
        {
            actions!.Add(new IdeAction(id, title, "Database", _ =>
            {
                if (ChosenConnection() is { } chosen)
                {
                    controller.SetMark(chosen, chosen.Mark == mark ? DatabaseMark.None : mark);
                }
            })
            {
                WhyNot = _ => ChosenConnection() is null ? Refusals.NoDatabaseConnection : PersonsOwn(),
                Menus = [new MenuPlace("Tools/Database", 3, mark == DatabaseMark.ReadOnly ? 0 : 1)],
            });
        }

        void Add(string id, string title, Func<Task> run, Func<string?> why, MenuPlace? menu = null)
        {
            actions!.Add(new IdeAction(id, title, "Database", _ => IdeErrors.Observe(run()))
            {
                WhyNot = _ => controller.Busy ? Refusals.DatabaseBusy : why(),
                Menus = [menu ?? new MenuPlace("Tools/Database", 0, 0)],
            });
        }

        string? WhyNotObject() => databaseExplorer?.Chosen is not { Target: not null } chosen ? Refusals.NoDatabaseObject : WhyNotOpened(chosen.Source) ?? PersonsOwn();

        string? WhyNotSchema() => databaseExplorer?.Chosen is not { Kind: not DatabaseNodeKind.Note } chosen ? Refusals.NoDatabaseConnection : WhyNotOpened(chosen.Source) ?? PersonsOwn();

        string? PersonsOwn() => AgentsOwn || Mcp.AgentCall.Current is not null ? Refusals.AsksThePerson : null;
    }

    /// <summary>The action of a table's data whose keys (<see cref="AppActions.KeysIn"/>) a key the keymap read as
    /// <paramref name="action"/> presses, or null.</summary>
    private static string? TableTakes(string action) =>
        new[] { AppActions.DatabaseSubmit, AppActions.DatabaseAddRow, AppActions.DatabaseDeleteRows, AppActions.DatabaseRevert }
            .FirstOrDefault(own => AppActions.KeysIn(own) == action);

    /// <summary>A SQL file's completion asks its console's connection, once the assists are made.</summary>
    private void FollowSqlConsoles() => assist.SqlLive = document => database?.ConsoleOf(document.Path) is { Connection: { } id } && database.Find(id) is { } source
        ? (source.Kind, database.CatalogOf(source)) : null;

    /// <summary>Why a connection may not be opened: one the folder's own files name waits for the folder to be trusted.</summary>
    internal string? WhyNotOpened(DatabaseSource source) => source.Found is not null && session.Folder is not null && !session.Trusted ? Refusals.Untrusted : null;

    /// <summary>Opens the chosen row's connection in the explorer, reading its databases, as its chevron does.</summary>
    private async Task OpenChosenConnectionAsync()
    {
        if (ChosenConnection() is { } source && DatabaseExplorer.Rows.FirstOrDefault(r => r.Kind == DatabaseNodeKind.Connection && r.Source.Id == source.Id) is { IsExpanded: false } row)
        {
            DatabaseExplorer.Choose(row);
            await DatabaseExplorer.ToggleAsync(row);
        }
    }

    /// <summary>The connection of the explorer's chosen row.</summary>
    private DatabaseSource? ChosenConnection() => databaseExplorer?.Chosen?.Source is { } source ? database?.Find(source.Id) : null;

    private async Task EditDatabaseConnectionAsync(DatabaseSource? original)
    {
        ProjectSession current = session;
        if (current.Folder is not { } folder)
        {
            return;
        }
        DatabaseConnectionDialog dialog = new(folder, original);
        if (await dialog.ShowDialog<DatabaseSource?>(this) is not { } source || session != current)
        {
            return;
        }
        if (original is null)
        {
            database!.Add(source);
        }
        else
        {
            database!.Replace(original, source);
            DatabaseExplorer.Forget(original);
        }
        ShowToolWindow(ToolWindowIds.Database);
        if (DatabaseExplorer.Rows.FirstOrDefault(r => r.Kind == DatabaseNodeKind.Connection && r.Source.Id == source.Id) is { } root)
        {
            DatabaseExplorer.Choose(root);
            await DatabaseExplorer.ToggleAsync(root);
        }
    }

    private async Task RenameDatabaseConnectionAsync()
    {
        if (ChosenConnection() is not { } source)
        {
            return;
        }
        Avalonia.Controls.TextBox name = new() { Name = "DatabaseRenameName", Text = source.Name };
        ConfirmDialog dialog = new("Rename Database Connection", "A name for this connection:", "Rename", detail: FormRows.Of("Name", name));
        if (await dialog.ShowDialog<bool>(this) && name.Text?.Trim() is { Length: > 0 } title && database!.Find(source.Id) is not null)
        {
            database.Replace(source, source with { Name = title });
        }
    }

    private async Task RemoveDatabaseConnectionAsync()
    {
        if (ChosenConnection() is not { } source)
        {
            return;
        }
        string more = source.Found is null ? "Its password is taken out of the system's credential store." : "It is found again as the folder's settings name it.";
        if (await new ConfirmDialog("Remove Database Connection", $"Remove {source.Name}? {more}", "Remove").ShowDialog<bool>(this))
        {
            database!.Remove(source);
        }
    }

    private Task OpenDatabaseTableAsync() =>
        databaseExplorer?.Chosen is { Table: { } table } node ? OpenTableAsync(node.Source, node.Database, table) : Task.CompletedTask;

    /// <summary>Opens a table's rows among the editors, or the tab already showing them.</summary>
    private async Task OpenTableAsync(DatabaseSource source, string within, DatabaseTable table)
    {
        if (database is not { } controller)
        {
            return;
        }
        Workspace.OpenDocument? existing = Editors.Groups.SelectMany(g => g.Tabs).Select(t => t.Document)
            .FirstOrDefault(d => d.DatabaseTable?.Matches(source, within, table) == true);
        if (existing is not null)
        {
            Editors.Activate(existing);
            return;
        }
        DatabaseTableTab tab = new(controller, work, source, within, table, action => Request(action), AskProductionAsync);
        Editors.Activate(tab.Document);
        await tab.LoadAsync();
    }

    /// <summary>Shows the chosen object's definition, as its database gives it, in a read-only tab of its own, read anew each time.</summary>
    private async Task ShowDdlAsync()
    {
        if (databaseExplorer?.Chosen is not { Target: { } chosen } node || database is not { } controller)
        {
            return;
        }
        // A column's definition is its table's.
        DatabaseObjectRef target = chosen.Kind == DatabaseObjectKind.Column ? chosen with { Kind = DatabaseObjectKind.Table, Name = chosen.Table, Table = string.Empty } : chosen;
        DatabaseSource within = node.Source.In(node.Database);
        if (await controller.ReadAsync(node.Source, $"Reading {target}'s definition…", cancel => DatabaseDdl.ReadAsync(within, target, cancel)) is not { } text)
        {
            return;
        }
        string path = DatabaseDdl.CopyOf(node.Source, node.Database, target);
        await BackgroundWork.Run(() =>
        {
            Containers.OwnFiles.Made(Path.GetDirectoryName(path)!);
            Containers.OwnFiles.Write(path, text);
        });
        Editors.Find(path)?.Reload(overUnsaved: true);
        if (Editors.Open(path) is { } failed)
        {
            Refuse(failed);
        }
    }

    /// <summary>Writes a script that makes, changes or drops the chosen object at the end of its connection's console,
    /// chosen there and never run, so the person reads it before running it.</summary>
    private async Task ScriptIntoConsoleAsync(DdlScript script)
    {
        if (databaseExplorer?.Chosen is not { Target: { } target } node || database is not { } controller)
        {
            return;
        }
        DatabaseSource source = node.Source;
        DatabaseSource within = source.In(node.Database);
        if (await controller.ReadAsync(source, $"Writing {script.ToString().ToLowerInvariant()} script of {target}…",
            cancel => DatabaseDdl.ScriptAsync(within, target, script, cancel)) is not { } text)
        {
            return;
        }
        await IntoConsoleAsync(source, node.Database, text);
    }

    /// <summary>Writes a script at the end of a connection's console, opened, chosen there and never run, so the person reads
    /// it before running it: into another database than the connection's after its USE, or a comment saying so.</summary>
    private async Task IntoConsoleAsync(DatabaseSource source, string within, string text)
    {
        await OpenSqlConsoleAsync(source);
        if (Editors.Active is not { } console || database?.ConsoleOf(console.Path) is not { Connection: var id } shown || id != source.Id)
        {
            return;
        }
        string elsewhere = within == source.Database || source.Kind == DatabaseKind.Sqlite ? string.Empty : source.Kind switch
        {
            DatabaseKind.SqlServer => $"USE {DatabaseClient.Quote(source.Kind, within)}\nGO\n",
            DatabaseKind.MySql => $"USE {DatabaseClient.Quote(source.Kind, within)};\n",
            _ => $"-- Of database {within}, which this console's connection does not open\n",
        };
        WriteIntoConsole(shown, elsewhere + text);
    }

    /// <summary>The database and schema a chosen row stands in: its own, else its connection's database and that database's
    /// usual schema (PostgreSQL's public, SQL Server's dbo, MySQL's the database itself, SQLite's file its one schema).</summary>
    private static (string Database, string Schema) SchemaOf(DatabaseNode node)
    {
        string within = node.Database is { Length: > 0 } named ? named : node.Source.Database;
        string schema = node.Source.Kind switch
        {
            DatabaseKind.MySql => within,
            DatabaseKind.Sqlite => string.Empty,
            _ => node.Schema is { Length: > 0 } own ? own : node.Source.Kind == DatabaseKind.SqlServer ? "dbo" : "public",
        };
        return (within, schema);
    }

    /// <summary>Shows the chosen schema's tables and foreign keys as a diagram among the editors, read anew each time.</summary>
    private async Task ShowDiagramAsync()
    {
        if (databaseExplorer?.Chosen is not { } node || database is not { } controller)
        {
            return;
        }
        (string within, string schema) = SchemaOf(node);
        DatabaseSource source = node.Source;
        string named = schema.Length > 0 ? schema : within;
        Func<Task<SchemaShape?>> read = () => controller.ReadAsync(source, $"Reading {named}'s tables…", cancel => SchemaReads.ReadAsync(source.In(within), schema, cancel));
        if (Editors.TabDocuments.FirstOrDefault(d => d.DatabaseTab is SchemaDiagramTab diagram && diagram.Matches(source, within, source.Kind == DatabaseKind.Sqlite ? string.Empty : schema))
            is { DatabaseTab: SchemaDiagramTab open })
        {
            Editors.Activate(open.Document);
            await open.ReadAgainAsync();
            return;
        }
        if (await read() is not { } shape)
        {
            return;
        }
        string key = DatabaseController.DiagramKey(source, within, shape.Schema);
        SchemaDiagramTab tab = new(source, within, shape, read, table => IdeErrors.Observe(OpenTableAsync(source, within, new DatabaseTable(shape.Schema, table.Name))),
            () => controller.DiagramPlaces(key), places => controller.KeepDiagramPlaces(key, places));
        Editors.Activate(tab.Document);
    }

    /// <summary>Compares the chosen schema with one the person picks of the same kind of database, and writes the script that
    /// makes that one as the chosen one is into its connection's console, chosen and never run.</summary>
    private async Task CompareSchemasAsync()
    {
        if (databaseExplorer?.Chosen is not { } node || database is not { } controller)
        {
            return;
        }
        (string within, string schema) = SchemaOf(node);
        DatabaseSource source = node.Source;
        SchemaCompareDialog dialog = new(source, within, schema, [.. controller.Sources.Where(s => s.Kind == source.Kind)]);
        if (await dialog.ShowDialog<SchemaCompareDialog.Answer?>(this) is not { } answer
            || await controller.CompareAsync(source.In(within), schema, answer.Target.In(answer.Database), answer.Schema) is not { Count: > 0 } differences)
        {
            return;
        }
        string from = schema.Length > 0 && schema != within ? $"{within}.{schema} of {source.Name}" : $"{within} of {source.Name}";
        string to = answer.Schema.Length > 0 && answer.Schema != answer.Database ? $"{answer.Database}.{answer.Schema} of {answer.Target.Name}" : $"{answer.Database} of {answer.Target.Name}";
        await IntoConsoleAsync(answer.Target, answer.Database, SchemaCompare.Script(source.Kind, differences, from, to));
    }

    /// <summary>Opens the chosen connection's console, or <paramref name="of"/>'s, a .sql file kept with the folder's state, made
    /// as first opened.</summary>
    private async Task OpenSqlConsoleAsync(DatabaseSource? of = null)
    {
        if ((of ?? ChosenConnection()) is not { } source || session.Folder is not { } folder || database is not { } controller)
        {
            return;
        }
        string consoles = Path.Combine(State.ProjectState.FolderOf(StateFolder, folder), "consoles");
        string path = Path.Combine(consoles, FileNames.Safe(source.Name) + ".sql");
        if (!File.Exists(path))
        {
            await BackgroundWork.Run(() =>
            {
                Containers.OwnFiles.Made(consoles);
                FileText.SaveNewOnly(path, string.Empty);
            });
        }
        controller.Bind(path, source);
        Editors.Open(path);
        Editors.FollowSidePanes();
    }

    /// <summary>Runs the SQL file's chosen statements, or the one the caret stands in, or <paramref name="whole"/> every
    /// statement in turn, against its console's connection, asking which connection where it has none yet, and the
    /// values of their parameters where they have any.</summary>
    private async Task RunSqlAsync(bool whole = false)
    {
        if (await ConsoleToRunAsync() is not { } run || database is not { } controller)
        {
            return;
        }
        (SqlConsole console, DatabaseSource source) = run;
        Passenger.Editor.CodeEditor editor = Editors.CodeEditor;
        Selection chosen = editor.Selections.Primary;
        string text = editor.Document.Snapshot.ToString();
        IReadOnlyList<string> runs = whole ? SqlText.Runs(text, source.Kind)
            : !chosen.IsEmpty ? SqlText.Runs(text[chosen.Start..chosen.End], source.Kind)
            : SqlText.At(text, source.Kind, chosen.Caret) is { } at ? SqlText.Runs(text[at.Start..at.End], source.Kind) : [];
        if (runs.Count == 0)
        {
            Refuse(Refusals.NoSqlStatement);
            return;
        }
        string[] changing = [.. runs.Where(r => SqlText.EffectOf(r, source.Kind) >= SqlEffect.ChangesData)];
        if (changing.Length > 0 && source.Mark == DatabaseMark.ReadOnly)
        {
            console.Ran(console.Results, Refusals.ReadOnlyConnection(source.Name));
            Refuse(Refusals.ReadOnlyConnection(source.Name));
            return;
        }
        if (changing.Length > 0 && source.Mark == DatabaseMark.Production && !await AskProductionAsync("Run on Production",
            $"{source.Name} is marked production. {(changing.Length == 1 ? "This statement changes" : $"These {changing.Length} statements change")} its data or schema:\n\n"
            + string.Join("\n\n", changing.Select(Shortened)), "Run"))
        {
            console.Ran(console.Results, "Not run.");
            return;
        }
        if (await ValuesAsync(console, runs, source.Kind) is not { } values)
        {
            console.Ran(console.Results, "Not run.");
            return;
        }
        await controller.RunAsync(console, source, runs, values.Count == 0 ? null : values);

        static string Shortened(string statement) => Containers.ContainerSecrets.InSaid(statement.Length > 400 ? statement[..400] + "…" : statement);
    }

    /// <summary>The SQL file Run SQL File runs where it is asked: the Explorer's row (its menu's, or the row chosen while it
    /// has the keys), else the file shown; null where that is no SQL file.</summary>
    private string? SqlFileOf(ActionContext context) => RowOf(context) is { } row
        ? row is { Kind: Passenger.App.Explorer.NodeKind.File, Path: { } path } && Languages.Language.ForPath(path) == Languages.Language.Sql ? path : null
        : Editors.Active is { } shown && shown.Language == Languages.Language.Sql ? shown.Path : null;

    /// <summary>Run SQL File: the file opened where it is not shown, then every statement of it run in turn.</summary>
    private async Task RunSqlFileAsync(string? path)
    {
        if (path is null)
        {
            return;
        }
        if (Editors.Active is not { } shown || !PathNames.Same(shown.Path, path))
        {
            if (Editors.Open(path) is { } failed)
            {
                Refuse(failed);
                return;
            }
            Editors.FollowSidePanes();
        }
        await RunSqlAsync(whole: true);
    }

    /// <summary>Explains the SQL file's statement the caret stands in, or the first chosen, as a plan's tree under it, never
    /// running it, asking its parameters' values where it has any.</summary>
    private async Task ExplainSqlAsync()
    {
        if (await ConsoleToRunAsync() is not { } run || database is not { } controller)
        {
            return;
        }
        (SqlConsole console, DatabaseSource source) = run;
        Passenger.Editor.CodeEditor editor = Editors.CodeEditor;
        Selection chosen = editor.Selections.Primary;
        string text = editor.Document.Snapshot.ToString();
        (int Start, int End)? at = SqlText.At(text, source.Kind, chosen.IsEmpty ? chosen.Caret : chosen.Start);
        string? statement = at is { } found ? SqlText.Runs(text[found.Start..found.End], source.Kind).FirstOrDefault() : null;
        if (statement is null || !SqlPlans.Explains(statement, source.Kind))
        {
            Refuse(statement is null ? Refusals.NoSqlStatement : Refusals.NoPlan);
            return;
        }
        if (await ValuesAsync(console, [statement], source.Kind) is not { } values)
        {
            return;
        }
        await controller.ExplainAsync(console, source, statement, values.Count == 0 ? null : values);
    }

    /// <summary>The values of what runs' parameters, asked of the person; empty where it has none, null where the person
    /// cancelled.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> ValuesAsync(SqlConsole console, IReadOnlyList<string> runs, DatabaseKind kind) =>
        SqlParameters.Asked(SqlParameters.Of(runs, kind)) is { Count: > 0 } asked ? await AskParametersAsync(console, asked, runs.Count) : new Dictionary<string, string>();

    /// <summary>The SQL file shown as a console and its connection, asking which connection where it has none yet; null
    /// where none is chosen or it may not be opened, said.</summary>
    private async Task<(SqlConsole Console, DatabaseSource Source)?> ConsoleToRunAsync()
    {
        if (Editors.Active is not { } file || file.Language != Languages.Language.Sql || database is not { } controller)
        {
            return null;
        }
        SqlConsole? console = controller.ConsoleOf(file.Path);
        if (console?.Connection is not { } id || controller.Find(id) is null)
        {
            IReadOnlyList<DatabaseSource> sources = controller.Sources;
            if (sources.Count == 0)
            {
                Refuse(Refusals.NoDatabaseConnection);
                return null;
            }
            string? name = await ChooseAsync("Run Statement", $"Which database does {file.Name} run against?", [.. sources.Select(s => s.ToString())], null);
            if (name is null || sources.FirstOrDefault(s => s.ToString() == name) is not { } picked)
            {
                return null;
            }
            console = controller.Bind(file.Path, picked);
            Editors.FollowSidePanes();
        }
        DatabaseSource source = controller.Find(console.Connection!.Value)!;
        if (WhyNotOpened(source) is { } why)
        {
            Refuse(why);
            return null;
        }
        return (console, source);
    }

    /// <summary>Asks the values of a run's parameters, each given the value the console last gave it, and keeps them for
    /// the console; null where the person cancelled, or an agent asked, which the person alone answers.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> AskParametersAsync(SqlConsole console, IReadOnlyList<string> asked, int statements)
    {
        if (AgentsOwn || Mcp.AgentCall.Current is not null || database is not { } controller)
        {
            Refuse(Refusals.AsksThePerson);
            return null;
        }
        IReadOnlyDictionary<string, string> last = controller.ParametersOf(console);
        Avalonia.Controls.StackPanel rows = new() { Spacing = FormRows.Between };
        FormRows.Scope(rows);
        List<(string Name, Avalonia.Controls.TextBox Box)> boxes = [];
        foreach (string name in asked)
        {
            Avalonia.Controls.TextBox box = new() { Name = $"SqlParameter{boxes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}", Text = last.GetValueOrDefault(name, string.Empty) };
            boxes.Add((name, box));
            rows.Children.Add(FormRows.Of(name, box));
        }
        ConfirmDialog dialog = new("Run with Parameters", $"{(statements == 1 ? "The statement takes" : "The statements take")} {(asked.Count == 1 ? "a parameter" : $"{asked.Count} parameters")}. "
            + "Give each a value: a number, text, 'quoted text' or NULL.", "Run", detail: rows);
        // The first value is typed as the question opens, as a question asking one is.
        dialog.Opened += (_, _) =>
        {
            boxes[0].Box.Focus();
            boxes[0].Box.SelectAll();
        };
        if (!await dialog.ShowDialog<bool>(this))
        {
            return null;
        }
        Dictionary<string, string> values = boxes.ToDictionary(b => b.Name, b => b.Box.Text ?? string.Empty, StringComparer.Ordinal);
        controller.KeepParameters(console, values);
        return values;
    }

    /// <summary>What Export Data writes, by where it is asked: a table or view the Database window chooses (while it has the
    /// keys, or nothing else is shown), the table shown, or the console's result shown.</summary>
    private sealed record ExportFrom(DatabaseNode? Node, DatabaseTableTab? Table, SqlConsoleView? Console);

    /// <summary>The choice the last export of the window made, which the next one offers again.</summary>
    private ExportChoice? lastExport;

    private ExportFrom? ExportOf(ActionContext context)
    {
        DatabaseNode? chosen = databaseExplorer?.Chosen is { Table: not null } node ? node : null;
        if (context.ToolWindow?.Id == ToolWindowIds.Database && chosen is not null)
        {
            return new ExportFrom(chosen, null, null);
        }
        if (context.File?.DatabaseTable is { } table)
        {
            return new ExportFrom(null, table, null);
        }
        if (context.File?.Language == Languages.Language.Sql && Editors.ActiveGroup.Console is { ChosenResult: not null, Console.Connection: not null } console)
        {
            return new ExportFrom(null, null, console);
        }
        return chosen is null ? null : new ExportFrom(chosen, null, null);
    }

    private string? WhyNotExport(ActionContext context) => ExportOf(context) switch
    {
        null => Refusals.NothingToExport,
        { Table: { } table } => table.Exported() is null ? Refusals.DatabaseBusy : null,
        { Node: { } node } => WhyNotOpened(node.Source) ?? (database?.Busy == true ? Refusals.DatabaseBusy : null),
        { Console.Console.Connection: { } id } => database?.Find(id) is { } source ? WhyNotOpened(source) : Refusals.NoDatabaseConnection,
        _ => null,
    };

    /// <summary>Export Data: what it writes asked of the person with the format and file (<see cref="DatabaseExportDialog"/>),
    /// then written there row by row (<see cref="DatabaseController.ExportAsync"/>), once the file may be written.</summary>
    private async Task ExportDataAsync(ActionContext context)
    {
        if (ExportOf(context) is not { } from || database is not { } controller || session.Folder is not { } folder)
        {
            return;
        }
        ExportSource? source = null;
        string stem = "result";
        if (from.Node is { Table: { } table } node)
        {
            DatabaseSource within = node.Source.In(node.Database);
            if (await controller.ReadAsync(node.Source, $"Reading {table}…", cancel => DatabaseClient.SchemaAsync(within, table, cancel)) is not { } schema)
            {
                return;
            }
            (string sql, List<(string Name, object? Value)> parameters) = DatabaseClient.Selecting(within.Kind, schema, null, null);
            source = new ExportSource(within, table.ToString(), new InsertTarget(within.Kind, DatabaseClient.Full(within.Kind, table)))
            {
                Sql = sql,
                Parameters = parameters,
                Said = $"Writes every row of {table} to a file, reading them from {within.Name} a row at a time.",
            };
            stem = table.Name;
        }
        else if (from.Table is { } shown)
        {
            source = shown.Exported();
            stem = shown.Table.Name;
        }
        else if (from.Console is { ChosenResult: { } result, Console: { Connection: { } id } console } && controller.Find(id) is { } connection)
        {
            source = Exported(console, connection, result.Result, result.Rows, result.Title);
            stem = source.Into.Table == "result" ? "result" : string.Concat(source.Into.Table.Where(c => c is not ('"' or '`' or '[' or ']')));
        }
        if (source is null)
        {
            return;
        }
        if (await new DatabaseExportDialog(source, folder, stem, lastExport).ShowDialog<ExportChoice?>(this) is not { } choice)
        {
            return;
        }
        lastExport = choice;
        if (await WhyNotWrittenAsync(choice.Path) is { } why)
        {
            Refuse(why);
            return;
        }
        if (await controller.ExportAsync(source, choice) is null)
        {
            Editors.Find(choice.Path)?.Reload();
        }

        // A result read whole is written as held; one cut short is run again where its statement only reads.
        ExportSource Exported(SqlConsole console, DatabaseSource connection, SqlResult run, DatabaseRows rows, string title)
        {
            bool again = rows.More && SqlText.EffectOf(run.Statement, connection.Kind) == SqlEffect.Reads;
            bool asks = SqlParameters.Of([run.Statement], connection.Kind)[0].Count > 0;
            string said = !rows.More ? $"Writes the {Plural.Of(rows.RowCount, "row")} of {title} to a file."
                : again ? $"Writes every row its statement returns to a file, running it again on {connection.Name}: {Shortened(run.Statement)}"
                : $"Writes the first {Plural.Of(rows.RowCount, "row")} {title} holds to a file: its statement changes data, so it is not run again.";
            return new ExportSource(connection, title, rows.Into ?? new InsertTarget(connection.Kind, SqlInserts.TableOf(run.Statement, connection.Kind)))
            {
                Sql = run.Statement,
                Held = again ? null : rows,
                Values = again && asks ? controller.ParametersOf(console) : null,
                Said = said,
            };
        }

        static string Shortened(string statement) => statement.Length > 200 ? statement[..200] + "…" : statement;
    }

    /// <summary>Where Import Data writes, by where it is asked: the table shown (unless the Database window has the keys), else
    /// the database of the window's chosen row, into its table or schema where it is one or under one.</summary>
    private sealed record ImportTo(DatabaseSource Source, string Database, string Schema, DatabaseTable? Table);

    private ImportTo? ImportOf(ActionContext context)
    {
        if (context.ToolWindow?.Id != ToolWindowIds.Database && context.File?.DatabaseTable is { } shown && database?.Find(shown.Connection) is { } connection)
        {
            return new ImportTo(connection, shown.Database, shown.Table.Schema, shown.Table);
        }
        return databaseExplorer?.Chosen is { Kind: not DatabaseNodeKind.Note } node && database?.Find(node.Source.Id) is { } source
            ? new ImportTo(source, node.Database, node.Schema, node.Kind == DatabaseNodeKind.Table ? node.Table : null) : null;
    }

    private string? WhyNotImport(ActionContext context) => ImportOf(context) is not { } to ? Refusals.NothingToImportInto
        : to.Source.Mark == DatabaseMark.ReadOnly ? Refusals.ReadOnlyConnection(to.Source.Name)
        : WhyNotOpened(to.Source) ?? (database?.Busy == true ? Refusals.DatabaseBusy : null);

    /// <summary>Import Data: the database's tables read, the file, the table and the columns asked of the person
    /// (<see cref="DatabaseImportDialog"/>), asking first on a production connection, then the rows written
    /// (<see cref="DatabaseController.ImportAsync"/>), the tree reading a new table and a tab of the table its rows again.</summary>
    private async Task ImportDataAsync(ActionContext context)
    {
        if (ImportOf(context) is not { } to || database is not { } controller || session.Folder is not { } folder)
        {
            return;
        }
        DatabaseSource within = to.Source.In(to.Database);
        if (await controller.ReadAsync(to.Source, $"Reading {to.Database}'s tables…", cancel => DatabaseClient.TablesAsync(within, cancel)) is not { } tables)
        {
            return;
        }
        DatabaseImportDialog dialog = new(within.Kind, folder, to.Schema, tables, to.Table,
            (table, cancel) => BackgroundWork.Run(() => DatabaseClient.SchemaAsync(within, table, cancel), cancel));
        if (await dialog.ShowDialog<ImportChoice?>(this) is not { } choice)
        {
            return;
        }
        string file = Path.GetFileName(choice.File.Path);
        if (to.Source.Mark == DatabaseMark.Production && !await AskProductionAsync("Import into Production",
            $"{to.Source.Name} is marked production. Write the rows of {file} into {(choice.Made ? "a new table " : string.Empty)}{choice.Table}?", "Import"))
        {
            return;
        }
        if (await controller.ImportAsync(within, choice) is not null)
        {
            return;
        }
        if (choice.Made)
        {
            await DatabaseExplorer.ReadAgainAsync(to.Source, to.Database);
        }
        foreach (DatabaseTableTab tab in Editors.TabDocuments.Select(d => d.DatabaseTable).OfType<DatabaseTableTab>()
            .Where(t => t.Connection == to.Source.Id && t.Database == to.Database && t.Table == choice.Table && t.Edits.IsEmpty))
        {
            await tab.ReadAgainAsync();
        }
    }

    /// <summary>Why a file the person named may not be written now: its folder gone, it open with changes not saved, it
    /// read-only, or held by what the window's writes wait for (<see cref="Editors.EditorArea.ClearAsync"/>); null where it may.</summary>
    private async Task<string?> WhyNotWrittenAsync(string path)
    {
        string name = Path.GetFileName(path);
        string within = Path.GetDirectoryName(path) ?? path;
        if (!await BackgroundWork.Run(() => Directory.Exists(within)))
        {
            return Refusals.NoFolderThere(PathNames.Shown(within, Folder));
        }
        string canonical = PathNames.Canonical(path);
        if (Editors.Find(canonical) is { IsModified: true })
        {
            return Refusals.UnsavedThere(name);
        }
        if (Workspace.ReadOnlyFiles.Holds(canonical))
        {
            return Workspace.ReadOnlyFiles.Refused(canonical, name);
        }
        if (await BackgroundWork.Run(() => FileText.IsReadOnly(canonical)))
        {
            return Refusals.ReadOnly(name);
        }
        return await Editors.ClearAsync([canonical]);
    }

    /// <summary>Writes a statement at the end of a console's file shown, on a line of its own, chosen there and not run.</summary>
    private void WriteIntoConsole(SqlConsole console, string statement)
    {
        if (Editors.Active is not { } file || !PathNames.Same(file.Path, console.Path))
        {
            return;
        }
        Passenger.Editor.CodeEditor editor = Editors.CodeEditor;
        string now = editor.Document.Snapshot.ToString();
        string lead = now.Length == 0 ? string.Empty : now.EndsWith('\n') ? "\n" : "\n\n";
        int start = now.Length + lead.Length;
        editor.Focus();
        editor.ApplyEdits([new TextEdit(now.Length, 0, lead + statement)], SelectionSet.Single(new Selection(start, start + statement.TrimEnd().Length)));
    }

    /// <summary>Asks before a change on a connection marked production, Cancel taking Enter so no stray key runs it; never
    /// for an agent, which may not change a production database.</summary>
    private async Task<bool> AskProductionAsync(string title, string question, string yes) =>
        !AgentsOwn && Mcp.AgentCall.Current is null && await new ConfirmDialog(title, question, yes) { EnterAgrees = false }.ShowDialog<bool>(this);

    private const string WriteAndCommit = "Write and Commit";
    private const string DiscardChanges = "Discard";

    /// <summary>Whether the window asks the person, closed, about its database changes not written.</summary>
    private bool askingDatabase;

    /// <summary>Whether leaving the folder asks first about what it holds of its databases unwritten: a table's changes not
    /// submitted, a console's transaction open, and a person there to answer.</summary>
    private bool AsksOfDatabase => !Unseen && !AgentsOwn && database is { } controller && (PendingTables().Count > 0 || controller.OpenTransactions.Count > 0);

    private List<DatabaseTableTab> PendingTables() => [.. Editors.TabDocuments.Select(d => d.DatabaseTable).OfType<DatabaseTableTab>().Where(t => !t.Edits.IsEmpty)];

    /// <summary>Asks, <paramref name="leaving"/> the folder, whether to submit its tables' changes and commit its consoles'
    /// transactions, or discard and roll them back: true to go on once that is done, false for Cancel or a write refused
    /// (said where it was refused).</summary>
    private async Task<bool> DatabaseAskedAsync(string leaving)
    {
        ProjectSession asked = session;
        List<DatabaseTableTab> tables = PendingTables();
        IReadOnlyList<SqlConsole> consoles = database?.OpenTransactions ?? [];
        if (tables.Count == 0 && consoles.Count == 0)
        {
            return true;
        }
        string held = Plural.Listed([.. tables.Select(t => $"{t.Table}'s {t.Edits.Said.Replace(" pending", string.Empty, StringComparison.Ordinal)}"),
            .. consoles.Select(c => $"the transaction open in {Path.GetFileName(c.Path)}")]);
        string? answer = await Choose("Database Changes Not Written", $"{Containers.ContainerSecrets.InSaid(held)} would be lost {leaving}.\n\n"
            + "Write and Commit submits the tables' changes and commits the transactions; Discard drops the changes and rolls the transactions back.",
            [WriteAndCommit, DiscardChanges], null);
        if (answer is null || session != asked || database is not { } controller)
        {
            return false;
        }
        bool write = answer == WriteAndCommit;
        foreach (DatabaseTableTab table in tables)
        {
            if (write)
            {
                await table.SubmitAsync();
            }
            else
            {
                table.Discard();
            }
        }
        foreach (SqlConsole console in consoles)
        {
            await controller.EndTransactionAsync(console, write);
        }
        return session == asked && tables.All(t => t.Edits.IsEmpty) && consoles.All(c => !c.InTransaction);
    }

    /// <summary>The window was closed holding database changes not written: asked, it closes (a quit it held carrying on)
    /// once they are written or dropped, or stays open, the quit given up (<see cref="PassengerApp.Answered"/>).</summary>
    private async Task CloseDatabaseAsync(bool quit)
    {
        if (askingDatabase)
        {
            return;
        }
        askingDatabase = true;
        ClosesLater = true;
        bool goOn;
        try
        {
            goOn = await DatabaseAskedAsync(quit ? "as Passenger quits" : "as the window closes");
        }
        finally
        {
            askingDatabase = false;
            ClosesLater = false;
        }
        if (quit)
        {
            PassengerApp.Answered(carryOn: goOn);
        }
        else if (goOn)
        {
            CloseAsAnswered();
        }
    }

    /// <summary>Close Solution once its database changes not written are answered for.</summary>
    private async Task CloseSolutionDatabaseAsync()
    {
        if (await DatabaseAskedAsync("as the solution closes"))
        {
            CloseSolution();
        }
    }

    /// <summary>Another folder opened here over database changes not written: asked, it opens once answered, else not at all.</summary>
    private async Task OpenDatabaseAsync(string path, string folder, Action? opened)
    {
        if (await DatabaseAskedAsync($"as {Path.GetFileName(folder)} opens here"))
        {
            await OpenFolder(path, opened: opened);
        }
    }

    /// <summary>Asks before a tab that holds what its close would lose (a table's changes not submitted, a console's open
    /// transaction) closes, closing it once answered, as leaving the folder asks (<see cref="DatabaseAskedAsync"/>); false
    /// where the tab may close now, a console letting go of its connection as it does.</summary>
    private bool AsksBeforeClosingDatabase(Editors.EditorGroup group, Workspace.OpenDocument document)
    {
        if (database is not { } controller)
        {
            return false;
        }
        // Nobody is at a Passenger nobody sees, or an agent's own, to answer: what the tab holds goes with it.
        bool asks = !Unseen && !AgentsOwn;
        if (asks && document.DatabaseTable is { Edits.IsEmpty: false } table)
        {
            IdeErrors.Observe(CloseTableAsync(group, document, table));
            return true;
        }
        if (document.Language == Languages.Language.Sql && controller.ConsoleOf(document.Path) is { } console)
        {
            if (asks && console.InTransaction)
            {
                IdeErrors.Observe(CloseConsoleAsync(group, document, console));
                return true;
            }
            controller.Close(console);
        }
        return false;
    }

    private async Task CloseTableAsync(Editors.EditorGroup group, Workspace.OpenDocument document, DatabaseTableTab table)
    {
        string? chosen = await new ChoiceDialog("Close Table", $"{table.Table} has changes not yet written: {table.Edits.Said.Replace(" pending", string.Empty, StringComparison.Ordinal)}. "
            + "Submit them before it closes, or discard them?", ["Submit", "Discard"], null).ShowDialog<string?>(this);
        if (chosen == "Submit")
        {
            await table.SubmitAsync();
        }
        else if (chosen == "Discard")
        {
            table.Discard();
        }
        if (chosen is not null && table.Edits.IsEmpty && group.Has(document))
        {
            Editors.CloseTab(group, document);
        }
    }

    private async Task CloseConsoleAsync(Editors.EditorGroup group, Workspace.OpenDocument document, SqlConsole console)
    {
        string? chosen = await new ChoiceDialog("Close Console", $"A transaction is open in {document.Name}. Commit it or roll it back before the console closes?",
            ["Commit", "Roll Back"], null).ShowDialog<string?>(this);
        if (chosen is not null && database is { } controller)
        {
            await controller.EndTransactionAsync(console, commit: chosen == "Commit");
            if (!console.InTransaction && group.Has(document))
            {
                Editors.CloseTab(group, document);
            }
        }
    }

    /// <summary>Shows a connection another window uses in the Database window and opens its row as a click would, the folder's
    /// connections found first: one listed with its connection string taken, else <paramref name="wanted"/> listed beside them
    /// (until the folder's end where <paramref name="lasting"/>). Why not once refused, said.</summary>
    internal async Task<(DatabaseSource? Used, string? Why)> ShowDatabaseOfAsync(DatabaseSource wanted, bool lasting)
    {
        ProjectSession asked = session;
        DatabaseController controller = Database;
        ShowToolWindow(ToolWindowIds.Database);
        await DatabasesFoundAsync();
        if (session != asked)
        {
            return (null, Refusals.SolutionClosed);
        }
        DatabaseSource used = controller.Use(wanted, lasting);
        if (WhyNotOpened(used) is { } why)
        {
            Refuse(why);
            return (null, why);
        }
        if (DatabaseExplorer.Rows.FirstOrDefault(r => r.Kind == DatabaseNodeKind.Connection && r.Source.Id == used.Id) is { } row)
        {
            DatabaseExplorer.Choose(row);
            if (!row.IsExpanded)
            {
                await DatabaseExplorer.ToggleAsync(row);
            }
        }
        return (used, null);
    }

    /// <summary>Ends once the folder's connections are found as the folder now stands: found first where they never were, or
    /// again where what runs changed since, shown or not (an agent's read, another window's ask).</summary>
    internal async Task DatabasesFoundAsync()
    {
        DatabaseController controller = Database;
        await controller.Idle;
        if (Folder is not null && (!controller.Discovered || databasesMoved))
        {
            databasesMoved = false;
            await controller.RefreshAsync();
        }
        await controller.Idle;
    }

    /// <summary>What of the folder's compose run and AppHosts runs, as last heard, whose databases are connections while they run.</summary>
    private string runningDatabases = string.Empty;

    /// <summary>Whether what runs changed since the connections were last found.</summary>
    private bool databasesMoved;

    /// <summary>What of a folder's compose run and AppHosts runs: the run coming up or up, and each AppHost's service started.</summary>
    private static string RunningDatabases(ProjectSession of) => string.Join('\n',
        (of.ContainerRun is { IsStopped: false } run ? [$"{run.Name}|{run.IsComingUp}"] : Array.Empty<string>())
            .Concat(of.Aspire.Apps.Where(a => !a.Stopping).SelectMany(a => a.Services).Select(s => s.InstanceId)));

    /// <summary>A compose run's and an AppHost's databases are connections while they run: what runs changed, the connections
    /// are found again once the window shows and is free.</summary>
    private void FollowRunningDatabases(ProjectSession next)
    {
        if (session != next || RunningDatabases(next) is var now && now == runningDatabases)
        {
            return;
        }
        runningDatabases = now;
        databasesMoved = true;
        IdeErrors.Observe(DiscoverMovedDatabasesAsync(next));
    }

    private async Task DiscoverMovedDatabasesAsync(ProjectSession next)
    {
        if (database is not { } controller)
        {
            return;
        }
        await controller.Idle;
        if (session == next)
        {
            DiscoverDatabasesIfShown();
        }
    }

    private void BindDatabase(ProjectSession next)
    {
        database?.Bind(next);
        runningDatabases = RunningDatabases(next);
        databasesMoved = false;
        buildRun.RunningChanged += (_, _) => FollowRunningDatabases(next);
        next.ProjectsEvaluated += (_, _) => Rediscover();
        next.WorkspaceChanged += (_, _) => Rediscover();

        // Projects read or evaluated again may name connections a discovery before them could not read.
        void Rediscover()
        {
            if (session == next && Folder is not null && database is { Busy: false } && Dock.IsShown(ToolWindowIds.Database))
            {
                Request(AppActions.DatabaseRefresh);
            }
        }
        UiThread.Post(DiscoverDatabasesIfShown);
    }

    private void DiscoverDatabasesIfShown()
    {
        if (Folder is not null && database is { Busy: false } controller && (!controller.Discovered || databasesMoved) && Dock.IsShown(ToolWindowIds.Database))
        {
            databasesMoved = false;
            Request(AppActions.DatabaseRefresh);
        }
    }
}
