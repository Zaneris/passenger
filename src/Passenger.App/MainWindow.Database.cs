using Passenger.App.Database;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
    private DatabaseController? database;
    private DatabaseExplorer? databaseExplorer;

    public DatabaseController Database => database!;
    public DatabaseExplorer DatabaseExplorer => databaseExplorer ?? (DatabaseExplorer)Dock[ToolWindowIds.Database].Content;

    private void AddDatabase()
    {
        DatabaseController controller = database = new DatabaseController(work, text => status.Text = text);
        Docking.ToolWindow shown = new(ToolWindowIds.Database, "Database", IconKeys.Database, () =>
        {
            DatabaseExplorer view = databaseExplorer = new DatabaseExplorer(controller, WhyNotOpened);
            view.ActionRequested += (_, action) => Request(action);
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
        Editors.MarkOf = document => document.DatabaseTable is { } table ? controller.Find(table.Connection)?.Mark ?? DatabaseMark.None
            : document.Language == Languages.Language.Sql && controller.ConsoleOf(document.Path) is { Connection: { } id } ? controller.Find(id)?.Mark ?? DatabaseMark.None
            : DatabaseMark.None;
        Editors.AsksBeforeClosing = AsksBeforeClosingDatabase;
        Editors.MakeConsoleView = () =>
        {
            SqlConsoleView view = new(() => controller.Sources, action => Request(action));
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
        Add(AppActions.DatabaseConsole, "Open SQL Console", OpenSqlConsoleAsync,
            () => ChosenConnection() is not { } chosen ? Refusals.NoDatabaseConnection : WhyNotOpened(chosen) ?? PersonsOwn());
        actions!.Add(new IdeAction(AppActions.DatabaseRun, "Run Statement", "Database", _ => IdeErrors.Observe(RunSqlAsync()))
        {
            WhyNot = _ => Editors.Active is not { Language: var language } || language != Languages.Language.Sql ? Refusals.NoSqlFile
                : controller.ConsoleOf(Editors.Active.Path) is { Running: true } ? Refusals.SqlRunning : PersonsOwn(),
            Menus = [new MenuPlace("Tools/Database", 1, 0)],
            KeysOf = () => AppActions.KeysIn(AppActions.DatabaseRun) is var keys && keys != AppActions.DatabaseRun ? keys : null,
        });
        actions!.Add(new IdeAction(AppActions.DatabaseRunStop, "Stop Statement", "Database", _ => (Editors.Active is { } file ? controller.ConsoleOf(file.Path) : null)?.Stop())
        {
            WhyNot = _ => Editors.Active is { } file && controller.ConsoleOf(file.Path) is { Running: true } ? null : Refusals.NoSqlRunning,
            Menus = [new MenuPlace("Tools/Database", 1, 1)],
        });
        Transaction(AppActions.DatabaseCommit, "Commit", commit: true, 2);
        Transaction(AppActions.DatabaseRollback, "Roll Back", commit: false, 3);
        Table(AppActions.DatabaseSubmit, "Submit Changes", table => IdeErrors.Observe(table.SubmitAsync()), table => table.WhyNotSubmit, 0);
        Table(AppActions.DatabaseRevert, "Revert Changes", table => table.Revert(), table => table.WhyNotRevert, 1);
        Table(AppActions.DatabaseAddRow, "Add Row", table => table.AddRow(), table => table.WhyNotAddRow, 2);
        Table(AppActions.DatabaseDeleteRows, "Delete Rows", table => table.DeleteRows(), table => table.WhyNotDeleteRows, 3);
        Mark(AppActions.DatabaseReadOnly, "Read-Only Connection", DatabaseMark.ReadOnly);
        Mark(AppActions.DatabaseProduction, "Production Connection", DatabaseMark.Production);

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

        void Add(string id, string title, Func<Task> run, Func<string?> why)
        {
            actions!.Add(new IdeAction(id, title, "Database", _ => IdeErrors.Observe(run()))
            {
                WhyNot = _ => controller.Busy ? Refusals.DatabaseBusy : why(),
                Menus = [new MenuPlace("Tools/Database", 0, 0)],
            });
        }

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
    private string? WhyNotOpened(DatabaseSource source) => source.Found is not null && session.Folder is not null && !session.Trusted ? Refusals.Untrusted : null;

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

    private async Task OpenDatabaseTableAsync()
    {
        if (databaseExplorer?.Chosen is not { Table: { } table } node || database is not { } controller)
        {
            return;
        }
        Workspace.OpenDocument? existing = Editors.Groups.SelectMany(g => g.Tabs).Select(t => t.Document)
            .FirstOrDefault(d => d.DatabaseTable?.Matches(node.Source, node.Database, table) == true);
        if (existing is not null)
        {
            Editors.Activate(existing);
            return;
        }
        DatabaseTableTab tab = new(controller, work, node.Source, node.Database, table, action => Request(action), AskProductionAsync);
        Editors.Activate(tab.Document);
        await tab.LoadAsync();
    }

    /// <summary>Opens the chosen connection's console, a .sql file kept with the folder's state, made as first opened.</summary>
    private async Task OpenSqlConsoleAsync()
    {
        if (ChosenConnection() is not { } source || session.Folder is not { } folder || database is not { } controller)
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

    /// <summary>Runs the SQL file's chosen statements, or the one the caret stands in, against its console's connection,
    /// asking which connection where it has none yet.</summary>
    private async Task RunSqlAsync()
    {
        if (Editors.Active is not { } file || file.Language != Languages.Language.Sql || database is not { } controller)
        {
            return;
        }
        Passenger.Editor.CodeEditor editor = Editors.CodeEditor;
        Selection chosen = editor.Selections.Primary;
        string text = editor.Document.Snapshot.ToString();
        SqlConsole? console = controller.ConsoleOf(file.Path);
        if (console?.Connection is not { } id || controller.Find(id) is null)
        {
            IReadOnlyList<DatabaseSource> sources = controller.Sources;
            if (sources.Count == 0)
            {
                Refuse(Refusals.NoDatabaseConnection);
                return;
            }
            string? name = await ChooseAsync("Run Statement", $"Which database does {file.Name} run against?", [.. sources.Select(s => s.ToString())], null);
            if (name is null || sources.FirstOrDefault(s => s.ToString() == name) is not { } picked)
            {
                return;
            }
            console = controller.Bind(file.Path, picked);
            Editors.FollowSidePanes();
        }
        DatabaseSource source = controller.Find(console.Connection!.Value)!;
        if (WhyNotOpened(source) is { } why)
        {
            Refuse(why);
            return;
        }
        IReadOnlyList<string> runs = !chosen.IsEmpty ? SqlText.Runs(text[chosen.Start..chosen.End], source.Kind)
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
        await controller.RunAsync(console, source, runs);

        static string Shortened(string statement) => Containers.ContainerSecrets.InSaid(statement.Length > 400 ? statement[..400] + "…" : statement);
    }

    /// <summary>Asks before a change on a connection marked production, Cancel taking Enter so no stray key runs it; never
    /// for an agent, which may not change a production database.</summary>
    private async Task<bool> AskProductionAsync(string title, string question, string yes) =>
        !AgentsOwn && Mcp.AgentCall.Current is null && await new ConfirmDialog(title, question, yes) { EnterAgrees = false }.ShowDialog<bool>(this);

    /// <summary>Asks before a tab that holds what its close would lose (a table's changes not submitted, a console's open
    /// transaction) closes, closing it once answered; false where the tab may close now, a console letting go of its
    /// connection as it does.</summary>
    private bool AsksBeforeClosingDatabase(Editors.EditorGroup group, Workspace.OpenDocument document)
    {
        if (database is not { } controller)
        {
            return false;
        }
        if (document.DatabaseTable is { Edits.IsEmpty: false } table)
        {
            IdeErrors.Observe(CloseTableAsync(group, document, table));
            return true;
        }
        if (document.Language == Languages.Language.Sql && controller.ConsoleOf(document.Path) is { } console)
        {
            if (console.InTransaction)
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

    private void BindDatabase(ProjectSession next)
    {
        database?.Bind(next);
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
        if (Folder is not null && database is { Busy: false, Discovered: false } && Dock.IsShown(ToolWindowIds.Database))
        {
            Request(AppActions.DatabaseRefresh);
        }
    }
}
