using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>The Migrations window: the project, startup project and context chosen across its top, its commands down its
/// side, what keeps dotnet ef from working each with its fix as buttons, then the context's migrations, oldest first, each
/// with whether the database has it. A view of <see cref="MigrationsController"/>, which it asks to choose and fix, built as
/// the window is first shown over what the controller found meanwhile; its buttons and row menu ask for the window's actions.</summary>
public sealed class MigrationsView : DockPanel
{
    private readonly MigrationsController controller;
    private readonly ComboBox projects;
    private readonly ComboBox startups;
    private readonly ComboBox contexts;
    private readonly StackPanel gaps;
    private readonly ListBox list;
    private readonly TextBlock placeholder;

    /// <summary>Where the last command reached the database, when a compose run or an AppHost gave it.</summary>
    private readonly TextBlock against;
    private bool showing;

    public MigrationsView(MigrationsController controller)
    {
        this.controller = controller;
        projects = Chooser("MigrationsProject", "The project whose contexts and migrations are listed");
        startups = Chooser("MigrationsStartup", "The project dotnet ef starts to make the context, as its run does");
        contexts = Chooser("MigrationsContext", "The context whose migrations are listed");
        projects.SelectionChanged += (_, _) =>
        {
            if (!showing && projects.SelectedItem is ProjectModel chosen)
            {
                controller.ChooseProject(chosen.Path);
            }
        };
        startups.SelectionChanged += (_, _) =>
        {
            if (!showing && startups.SelectedItem is ProjectModel chosen)
            {
                controller.ChooseStartup(chosen.Path);
            }
        };
        contexts.SelectionChanged += (_, _) =>
        {
            if (!showing && contexts.SelectedItem is EfContext chosen)
            {
                controller.ChooseContext(chosen.Name);
                ActionRequested?.Invoke(this, AppActions.MigrationsRefresh);
            }
        };
        against = MutedText.Of(string.Empty);
        against.Name = "MigrationsDatabase";
        WrapPanel top = new()
        {
            Margin = new Thickness(8, 2),
            Children = { Labelled("Project", projects), Labelled("Startup", startups), Labelled("Context", contexts), against },
        };
        Border topBar = ToolBars.Top(top);

        Border leftBar = ToolBars.Side(
            ToolButtons.Of(AppActions.MigrationsRefresh, IconKeys.Rerun, "Refresh Migrations", Request),
            ToolButtons.Of(AppActions.AddMigration, IconKeys.Add, "Add Migration…", Request),
            ToolButtons.Of(AppActions.UpdateDatabase, IconKeys.Update, "Update Database", Request),
            ToolButtons.Of(AppActions.RemoveLastMigration, IconKeys.Clear, "Remove Last Migration", Request),
            ToolBars.Gap(),
            ToolButtons.Of(AppActions.ScriptMigrations, IconKeys.DocumentText, "Script Migrations…", Request),
            ToolButtons.Of(AppActions.BundleMigrations, IconKeys.Package, "Bundle Migrations", Request));

        gaps = new StackPanel { [DockProperty] = Dock.Top, Name = "MigrationsGaps", Spacing = 4, Margin = new Thickness(8, 6, 8, 0) };
        list = new ListBox
        {
            Name = "MigrationsList",
            ItemTemplate = new FuncDataTemplate<MigrationRow?>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        ListMenus.Attach(list, () => list.SelectedItem is not MigrationRow chosen ? [] :
        [
            ListMenus.Item($"Update Database to {chosen.Name}", null, () => ActionRequested?.Invoke(this, AppActions.UpdateDatabaseTo)),
            ListMenus.Item("Update Database", null, () => ActionRequested?.Invoke(this, AppActions.UpdateDatabase)),
            ListMenus.Item("Copy Id", null, () => IdeErrors.Observe(ListMenus.CopyAsync(list, chosen.Id))),
        ]);
        placeholder = new TextBlock { Classes = { "emptyText" } };
        Children.Add(leftBar);
        Children.Add(topBar);
        Children.Add(gaps);
        Children.Add(new Panel { Children = { list, placeholder } });
        controller.Changed += (_, _) => Show();
        Show();
    }

    /// <summary>Opens the startup project's or the context's chooser, as a gap's Choose fix asks.</summary>
    public void OpenChooser(EfFixKind kind) => (kind == EfFixKind.ChooseContext ? contexts : startups).IsDropDownOpen = true;

    /// <summary>A button or a menu item asked for one of the window's actions.</summary>
    public event EventHandler<string>? ActionRequested;

    private void Request(string action) => ActionRequested?.Invoke(this, action);

    /// <summary>The migration chosen in the list, which Update Database to Here updates to.</summary>
    public MigrationRow? Chosen => list.SelectedItem as MigrationRow;

    /// <summary>Chooses a migration in the list by its name or id, as a click would.</summary>
    public void Choose(string migration) =>
        list.SelectedItem = controller.Migrations.FirstOrDefault(m => m.Name == migration || m.Id == migration);

    /// <summary>The words of what keeps dotnet ef from working, as the window shows them, the last command's failure last.</summary>
    public IReadOnlyList<string> GapsShown => [.. gaps.Children.OfType<DockPanel>().Select(row => row.Tag as string ?? string.Empty)];

    private void Show()
    {
        showing = true;
        try
        {
            projects.ItemsSource = controller.Projects;
            projects.SelectedItem = controller.Project;
            projects.IsEnabled = controller.Projects.Count > 1;
            startups.ItemsSource = controller.Startups;
            startups.SelectedItem = controller.Startup;
            startups.IsEnabled = controller.Startups.Count > 1;
            contexts.ItemsSource = controller.Contexts;
            contexts.SelectedItem = controller.Contexts.FirstOrDefault(c => c.Name == controller.Context);
            contexts.IsEnabled = controller.Contexts.Count > 1;
            object? chosen = list.SelectedItem is MigrationRow was ? controller.Migrations.FirstOrDefault(m => m.Id == was.Id) : null;
            list.ItemsSource = controller.Migrations;
            list.SelectedItem = chosen;
        }
        finally
        {
            showing = false;
        }
        against.Text = controller.Database is { } database ? $"Against {database}" : string.Empty;
        against.IsVisible = against.Text.Length > 0;
        gaps.Children.Clear();
        IEnumerable<EfGap> pending = controller.Pending.Select(p => new EfGap($"{p.Context}'s model has changes no migration covers",
            [new EfFix(EfFixKind.AddMigration, "Add Migration…")], Blocks: false));
        foreach (EfGap gap in controller.Gaps.Concat(pending).Concat(controller.Failure is { } failed ? [failed] : []))
        {
            gaps.Children.Add(GapRow(gap));
        }
        placeholder.Text = controller.Projects.Count == 0 ? "No project here uses EF Core"
            : !controller.Listed ? "Refresh to list the migrations, as dotnet ef reads them and the database has them"
            : controller.Migrations.Count == 0 ? $"{controller.Context ?? controller.Project?.Name} has no migrations: Add Migration… makes its first"
            : string.Empty;
        placeholder.IsVisible = placeholder.Text.Length > 0;
    }

    private Control GapRow(EfGap gap)
    {
        PathIcon icon = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(gap.Blocks ? IconKeys.Error : IconKeys.Problems));
        StackPanel fixes = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        foreach (EfFix fix in gap.Fixes)
        {
            Button button = new() { Content = fix.Title };
            button.Click += (_, _) => IdeErrors.Observe(controller.FixAsync(fix));
            fixes.Children.Add(button);
        }
        StackPanel words = new() { Children = { new TextBlock { Text = gap.Said, TextWrapping = TextWrapping.Wrap } } };
        if (fixes.Children.Count > 0)
        {
            words.Children.Add(fixes);
        }
        DockPanel row = new() { Tag = gap.Said, Children = { icon, words } };
        SetDock(icon, Dock.Left);
        return row;
    }

    private static Control Row(MigrationRow row)
    {
        // Its id's timestamp (yyyyMMddHHmmss, UTC as dotnet ef writes it), said as a date.
        string stamp = row.Id.Length > 15 && row.Id[14] == '_' && DateTime.TryParseExact(row.Id[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime made)
            ? made.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        TextBlock state = row.Applied switch
        {
            true => MutedText.Of("Applied"),
            false => new TextBlock { Text = "Pending", VerticalAlignment = VerticalAlignment.Center },
            null => MutedText.Of("Not read"),
        };
        state.Margin = new Thickness(12, 0, 8, 0);
        TextBlock when = MutedText.Of(stamp);
        when.Margin = new Thickness(8, 0, 0, 0);
        DockPanel line = new() { Height = ListRow.Height, Children = { state, new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center }, when } };
        SetDock(state, Dock.Right);
        DockPanel.SetDock(line.Children[1], Dock.Left);
        return line;
    }

    private static ComboBox Chooser(string name, string tip)
    {
        ComboBox chooser = new()
        {
            Name = name,
            MinWidth = 140,
            VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock
            {
                Text = item switch { ProjectModel project => project.Name, EfContext context => context.Name, _ => string.Empty },
            }),
        };
        ToolTip.SetTip(chooser, tip);
        return chooser;
    }

    private static Control Labelled(string label, ComboBox chooser) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Margin = new Thickness(0, 0, 12, 0),
        Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, chooser },
    };
}
