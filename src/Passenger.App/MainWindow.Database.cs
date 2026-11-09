using Passenger.App.Database;

namespace Passenger.App;

public partial class MainWindow
{
    private DatabaseController? database;
    private DatabaseView? databaseView;

    public DatabaseController Database => database!;
    public DatabaseView DatabaseView => databaseView ?? (DatabaseView)Dock[ToolWindowIds.Database].Content;

    private void AddDatabase()
    {
        DatabaseController controller = database = new DatabaseController(work, async (title, question) =>
        {
            if (AgentsOwn || Mcp.AgentCall.Current is not null)
            {
                throw new InvalidOperationException(Refusals.AsksThePerson);
            }
            return await ChooseAsync(title, question, ["Run"], null) is not null;
        }, text => status.Text = text);
        Docking.ToolWindow shown = new(ToolWindowIds.Database, "Database", IconKeys.Database, () =>
        {
            DatabaseView view = databaseView = new DatabaseView(controller);
            view.ActionRequested += (_, action) => Request(action);
            view.BindActions(actions!);
            ActionButtons.ShowMarked(view, actions!);
            return view;
        }, Docking.ToolAnchor.RightBottom)
        {
            Action = AppActions.ShowDatabase,
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
        Add(AppActions.DatabaseRefresh, "Refresh Database Connections", controller.RefreshAsync, () => Folder is null ? Refusals.NoFolder : null);
        Add(AppActions.DatabaseConnect, "Connect to Database", controller.ConnectAsync, () => controller.Source is null ? "Choose a database connection first" : null);
        Add(AppActions.DatabaseBrowse, "Browse Database Rows", () => controller.BrowseAsync(), () => !controller.Connected ? "Connect to a database first" : controller.Table is null ? "Choose a table first" : null);
        Add(AppActions.DatabaseQuery, "Run Database SQL…", controller.QueryAsync, () => !controller.Connected ? "Connect to a database first" : null);
        Add(AppActions.DatabaseNext, "Next Database Page", () => controller.BrowseAsync(controller.Rows!.Offset + DatabaseClient.PageSize),
            () => controller.Rows is not { Schema: not null, More: true } ? "No next table page" : null);
        Add(AppActions.DatabasePrevious, "Previous Database Page", () => controller.BrowseAsync(Math.Max(0, controller.Rows!.Offset - DatabaseClient.PageSize)),
            () => controller.Rows is not { Schema: not null, Offset: > 0 } ? "No previous table page" : null);
        Add(AppActions.DatabaseEdit, "Edit Database Cell…", () =>
        {
            if (DatabaseView.PendingWrite is { } write)
            {
                return controller.EditAsync(write);
            }
            DatabaseView.BeginEditing();
            return Task.CompletedTask;
        }, () => controller.Rows is not { Schema: not null } ? "Browse a table before editing a cell" : null);

        void Add(string id, string title, Func<Task> run, Func<string?> why)
        {
            actions!.Add(new IdeAction(id, title, "Database", _ => IdeErrors.Observe(run()))
            {
                WhyNot = _ => controller.Busy ? "A database operation is under way" : why(),
                Menus = [new MenuPlace("Tools/Database", 0, 0)],
            });
        }
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
        if (Folder is not null && database is { Busy: false, Connected: false } controller
            && (!controller.Discovered || controller.Sources.Count == 0) && Dock.IsShown(ToolWindowIds.Database))
        {
            Request(AppActions.DatabaseRefresh);
        }
    }
}
