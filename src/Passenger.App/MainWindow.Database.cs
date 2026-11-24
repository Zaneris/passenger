using Passenger.App.Database;

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
