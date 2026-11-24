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
        Editors.MakeConsoleView = () =>
        {
            SqlConsoleView view = new(() => controller.Sources, action => Request(action));
            view.ConnectionChosen += (_, chosen) =>
            {
                controller.Bind(chosen.Console.Path, chosen.Source);
                Editors.FollowSidePanes();
            };
            ActionButtons.ShowMarked(view, actions!);
            return view;
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
            () => ChosenConnection() is null ? Refusals.NoDatabaseConnection : PersonsOwn());
        Add(AppActions.DatabaseConnectionRename, "Rename Database Connection…", RenameDatabaseConnectionAsync,
            () => ChosenConnection() is null ? Refusals.NoDatabaseConnection : PersonsOwn());
        Add(AppActions.DatabaseConnectionRemove, "Remove Database Connection…", RemoveDatabaseConnectionAsync,
            () => ChosenConnection() is null ? Refusals.NoDatabaseConnection : PersonsOwn());
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
        DatabaseTableTab tab = new(controller, work, node.Source, node.Database, table, async (title, question) =>
            !AgentsOwn && Mcp.AgentCall.Current is null && await ChooseAsync(title, question, ["Write"], null) is not null);
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
        await controller.RunAsync(console, source, runs);
    }

    private void BindDatabase(ProjectSession next)
    {
        database?.Bind(next);
        next.ProjectsEvaluated += (_, _) =>
        {
            if (session == next)
            {
                DiscoverDatabasesIfShown();
            }
        };
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
