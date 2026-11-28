using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>The Migrations window: the project, startup project, environment (one typed taken too) and context chosen
/// across its top, its commands down its side, what keeps dotnet ef from working each with its fix as buttons, then the
/// context's migrations, oldest first, each with whether the database has it. A view of <see cref="MigrationsController"/>,
/// which it asks to choose and fix, built as the window is first shown over what the controller found meanwhile; its
/// buttons and row menu ask for the window's actions.</summary>
public sealed class MigrationsView : DockPanel
{
    private readonly MigrationsController controller;
    private readonly ComboBox projects;
    private readonly ComboBox startups;
    private readonly ComboBox environments;
    private readonly ComboBox contexts;
    private readonly StackPanel gaps;
    private readonly ListBox list;
    private readonly EmptyState placeholder;

    /// <summary>Where the last command reached the database, when a compose run or an AppHost gave it.</summary>
    private readonly TextBlock against;
    private bool showing;

    /// <summary>The gaps the rows show, each its words, weight and fixes, so a change that leaves them as they are keeps the
    /// rows and the buttons a person may be pressing.</summary>
    private IReadOnlyList<(string Said, bool Blocks, string Fixes)> shownGaps = [];

    public MigrationsView(MigrationsController controller)
    {
        this.controller = controller;
        projects = Chooser("MigrationsProject", "The project whose contexts and migrations are listed");
        startups = Chooser("MigrationsStartup", "The project dotnet ef starts to make the context, as its run does");
        environments = Chooser("MigrationsEnvironment", "The environment dotnet ef runs the startup project in, whose appsettings.{Environment}.json "
            + "its configuration reads: none chosen, the one its run configuration names. A name typed is taken too.");
        environments.IsEditable = true;
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
        // Taken as the list closes on a choice, or as Enter or leaving the field takes what is typed; Escape puts it back.
        environments.DropDownOpened += (_, _) => controller.ReadEnvironments();
        environments.DropDownClosed += (_, _) => Take();
        environments.LostFocus += (_, _) =>
        {
            if (!environments.IsDropDownOpen)
            {
                Take();
            }
        };
        environments.KeyDown += (_, e) =>
        {
            if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None || environments.IsDropDownOpen)
            {
                return;
            }
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Take();
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Escape && environments.Text != controller.Environment?.Name)
            {
                ShowEnvironment();
                e.Handled = true;
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
            Margin = new Thickness(Gaps.Normal, Gaps.Hair),
            ItemSpacing = Gaps.Wide,
            Children =
            {
                ToolDropDown.Labelled("Project", projects), ToolDropDown.Labelled("Startup", startups), ToolDropDown.Labelled("Environment", environments),
                ToolDropDown.Labelled("Context", contexts), against,
            },
        };
        Border topBar = ToolBars.Top(top);

        Border leftBar = ToolBars.Side(
            ToolButtons.Of(AppActions.MigrationsRefresh, IconKeys.Rerun, "Refresh Migrations", Request),
            ToolButtons.Of(AppActions.AddMigration, IconKeys.Add, "Add Migration…", Request),
            ToolButtons.Of(AppActions.UpdateDatabase, IconKeys.Update, "Update Database", Request),
            ToolButtons.Of(AppActions.RemoveLastMigration, IconKeys.Clear, "Remove Last Migration", Request),
            ToolBars.Gap(),
            ToolButtons.Of(AppActions.ScriptMigrations, IconKeys.DocumentText, "Script Migrations…", Request),
            ToolButtons.Of(AppActions.BundleMigrations, IconKeys.Package, "Bundle Migrations", Request),
            ToolBars.Gap(),
            ToolButtons.Of(AppActions.MigrationsDatabase, IconKeys.Database, "Open Context's Database", Request));

        gaps = new StackPanel { [DockProperty] = Dock.Top, Name = "MigrationsGaps", Spacing = Gaps.Tight, Margin = new Thickness(Gaps.Normal, Gaps.Small, Gaps.Normal, 0) };
        list = new ListBox
        {
            Name = "MigrationsList",
        };
        ReusedRows.Use(list, Row);
        ListMenus.Attach(list, () => list.SelectedItem is not MigrationRow chosen ? [] :
        [
            ListMenus.Item($"Update Database to {chosen.Name}", () => ActionRequested?.Invoke(this, AppActions.UpdateDatabaseTo)),
            ListMenus.Item("Update Database", () => ActionRequested?.Invoke(this, AppActions.UpdateDatabase)),
            ListMenus.Item("Copy Id", () => IdeErrors.Observe(ListMenus.CopyAsync(list, chosen.Id))),
        ]);
        placeholder = new EmptyState();
        Children.Add(leftBar);
        Children.Add(topBar);
        Children.Add(gaps);
        Children.Add(new Panel { Children = { list, placeholder } });
        controller.Changed += (_, _) => Show();
        Show();
    }

    /// <summary>Opens the startup project's or the context's chooser, as a gap's Choose fix asks.</summary>
    public void OpenChooser(EfFixKind kind) => (kind == EfFixKind.ChooseContext ? contexts : startups).IsDropDownOpen = true;

    /// <summary>Opens the environment chooser with the keys in its field, where a name may be typed too.</summary>
    public void OpenEnvironments()
    {
        environments.Focus();
        environments.IsDropDownOpen = true;
    }

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
            environments.IsEnabled = controller.Startup is not null;
            // Left alone while it is typed into, which Enter or leaving the field takes.
            if (!environments.IsKeyboardFocusWithin || environments.IsDropDownOpen)
            {
                ShowEnvironment();
            }
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
        IEnumerable<EfGap> pending = controller.Pending.Select(p => new EfGap($"{p.Context}'s model has changes no migration covers",
            [new EfFix(EfFixKind.AddMigration, "Add Migration…")], Blocks: false));
        List<EfGap> shown = [.. controller.Gaps.Concat(pending).Concat(controller.Failure is { } failed ? [failed] : [])];
        List<(string Said, bool Blocks, string Fixes)> keys = [.. shown.Select(g => (g.Said, g.Blocks, string.Join('\n', g.Fixes.Select(f => $"{f.Kind} {f.Title}"))))];
        if (!keys.SequenceEqual(shownGaps))
        {
            shownGaps = keys;
            gaps.Children.Clear();
            foreach (EfGap gap in shown)
            {
                gaps.Children.Add(GapRow(gap));
            }
        }
        placeholder.Text = (controller.Projects.Count == 0 ? Refusals.NoEfCore
            : !controller.Listed ? "Refresh to list the migrations, as dotnet ef reads them and the database has them"
            : controller.Migrations.Count == 0 ? $"{controller.Context ?? controller.Project?.Name} has no migrations: Add Migration… makes its first"
            : string.Empty).Shown;
        placeholder.IsVisible = placeholder.Text.Length > 0;
    }

    /// <summary>The environment chooser as the controller stands: its offers, and the one dotnet ef runs in shown in its field.</summary>
    private void ShowEnvironment()
    {
        bool was = showing;
        showing = true;
        try
        {
            if (environments.ItemsSource is not IReadOnlyList<EfEnvironment> offered || !offered.SequenceEqual(controller.Environments))
            {
                environments.ItemsSource = controller.Environments;
            }
            environments.SelectedItem = controller.Environments.FirstOrDefault(e => e.Name == controller.Environment?.Name);
            environments.Text = controller.Environment?.Name ?? string.Empty;
        }
        finally
        {
            showing = was;
        }
    }

    /// <summary>The environment chosen or typed taken as the one dotnet ef runs in: a listed one by its own spelling, a name
    /// typed as typed; what names none put back, the status saying why.</summary>
    private void Take()
    {
        if (showing || controller.Startup is null)
        {
            return;
        }
        string typed = environments.Text?.Trim() ?? string.Empty;
        string name = environments.SelectedItem is EfEnvironment listed && listed.Name.Equals(typed, StringComparison.OrdinalIgnoreCase) ? listed.Name : typed;
        if (name.Length == 0 || name == controller.Environment?.Name || controller.ChooseEnvironment(name) is not null)
        {
            ShowEnvironment();
        }
    }

    private Control GapRow(EfGap gap)
    {
        PathIcon icon = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Gaps.Hair, Gaps.Small, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(gap.Blocks ? IconKeys.Error : IconKeys.Problems));
        StackPanel fixes = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, Margin = new Thickness(0, Gaps.Tight, 0, 0) };
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

    /// <summary>A migration's row: its name, when it was made, and whether the database has it at the right (Pending
    /// alone not muted).</summary>
    private static Control Row()
    {
        TextBlock state = MutedText.Of(string.Empty);
        state.Margin = new Thickness(Gaps.Wide, 0, Gaps.Normal, 0);
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock when = MutedText.Of(string.Empty);
        when.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        DockPanel line = new() { Height = ListRow.Height, Children = { state, name, when } };
        SetDock(state, Dock.Right);
        DockPanel.SetDock(name, Dock.Left);
        return line.Showing<MigrationRow>(row =>
        {
            // Its id's timestamp (yyyyMMddHHmmss, UTC as dotnet ef writes it), said as a date.
            string stamp = row.Id.Length > 15 && row.Id[14] == '_' && DateTime.TryParseExact(row.Id[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime made)
                ? made.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            state.Text = row.Applied switch { true => "Applied", false => "Pending", null => "Not read" };
            state.Classes.Set(StyleClasses.Muted, row.Applied != false);
            state.TextTrimming = row.Applied == false ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            name.Text = row.Name;
            when.Text = stamp;
        });
    }

    private static ComboBox Chooser(string name, string tip)
    {
        ComboBox chooser = new ToolDropDown
        {
            Name = name,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ReusedRows.Use(chooser, () =>
        {
            ProjectLabel shown = new();
            TextBlock note = MutedText.Of(string.Empty);
            note.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
            StackPanel row = new() { Orientation = Orientation.Horizontal, Children = { shown, note } };
            AutomationProperties.SetLabeledBy(row, shown);
            return row.Showing<object>(item =>
            {
                shown.Show(item switch { ProjectModel project => project, EfContext context => context.Name, EfEnvironment environment => environment.Name, _ => (object)string.Empty });
                note.Text = (item as EfEnvironment)?.Note ?? string.Empty;
                note.IsVisible = note.Text.Length > 0;
            });
        });
        ToolTip.SetTip(chooser, tip);
        return chooser;
    }
}
