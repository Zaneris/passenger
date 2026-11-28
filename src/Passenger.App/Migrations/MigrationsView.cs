using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
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
    private IReadOnlyList<(Words Said, bool Blocks, string Fixes)> shownGaps = [];

    public MigrationsView(MigrationsController controller)
    {
        this.controller = controller;
        projects = Chooser("MigrationsProject", MigrationsWords.ProjectTip);
        startups = Chooser("MigrationsStartup", MigrationsWords.StartupTip);
        environments = Chooser("MigrationsEnvironment", MigrationsWords.EnvironmentTip);
        environments.IsEditable = true;
        contexts = Chooser("MigrationsContext", MigrationsWords.ContextTip);
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
                ToolDropDown.Labelled(MigrationsWords.ProjectLabel, projects), ToolDropDown.Labelled(MigrationsWords.StartupLabel, startups),
                ToolDropDown.Labelled(MigrationsWords.EnvironmentLabel, environments), ToolDropDown.Labelled(MigrationsWords.ContextLabel, contexts), against,
            },
        };
        Border topBar = ToolBars.Top(top);

        Border leftBar = ToolBars.Side(
            ToolButtons.Of(AppActions.MigrationsRefresh, IconKeys.Rerun, ActionTitles.MigrationsRefresh, Request),
            ToolButtons.Of(AppActions.AddMigration, IconKeys.Add, ActionTitles.AddMigration, Request),
            ToolButtons.Of(AppActions.UpdateDatabase, IconKeys.Update, ActionTitles.UpdateDatabase, Request),
            ToolButtons.Of(AppActions.RemoveLastMigration, IconKeys.Clear, ActionTitles.RemoveLastMigration, Request),
            ToolBars.Gap(),
            ToolButtons.Of(AppActions.ScriptMigrations, IconKeys.DocumentText, ActionTitles.ScriptMigrations, Request),
            ToolButtons.Of(AppActions.BundleMigrations, IconKeys.Package, ActionTitles.BundleMigrations, Request),
            ToolBars.Gap(),
            ToolButtons.Of(AppActions.MigrationsDatabase, IconKeys.Database, ActionTitles.MigrationsDatabase, Request));

        gaps = new StackPanel { [DockProperty] = Dock.Top, Name = "MigrationsGaps", Spacing = Gaps.Tight, Margin = new Thickness(Gaps.Normal, Gaps.Small, Gaps.Normal, 0) };
        list = new ListBox
        {
            Name = "MigrationsList",
        };
        ReusedRows.Use(list, Row);
        ListMenus.Attach(list, () => list.SelectedItem is not MigrationRow chosen ? [] :
        [
            ListMenus.Item(ActionTitles.UpdateDatabaseToNamed(chosen.Name), () => ActionRequested?.Invoke(this, AppActions.UpdateDatabaseTo)),
            ListMenus.Item(ActionTitles.UpdateDatabase, () => ActionRequested?.Invoke(this, AppActions.UpdateDatabase)),
            ListMenus.Item(MigrationsWords.CopyId, () => IdeErrors.Observe(ListMenus.CopyAsync(list, chosen.Id))),
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
    public IReadOnlyList<string> GapsShown => [.. gaps.Children.OfType<DockPanel>().Select(row => (row.Tag as Words)?.English ?? string.Empty)];

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
        ShownWords.Set(against, TextBlock.TextProperty, controller.Database is { } database ? MigrationsWords.Against(database) : null);
        against.IsVisible = controller.Database is not null;
        IEnumerable<EfGap> pending = controller.Pending.Select(p => new EfGap(MigrationsWords.ModelChanged(p.Context),
            [new EfFix(EfFixKind.AddMigration, ActionTitles.AddMigration)], Blocks: false));
        List<EfGap> shown = [.. controller.Gaps.Concat(pending).Concat(controller.Failure is { } failed ? [failed] : [])];
        List<(Words Said, bool Blocks, string Fixes)> keys = [.. shown.Select(g => (g.Said, g.Blocks, string.Join('\n', g.Fixes.Select(f => $"{f.Kind} {f.Title.English}"))))];
        if (!keys.SequenceEqual(shownGaps))
        {
            shownGaps = keys;
            gaps.Children.Clear();
            foreach (EfGap gap in shown)
            {
                gaps.Children.Add(GapRow(gap));
            }
        }
        Words? empty = controller.Projects.Count == 0 ? Refusals.NoEfCore
            : !controller.Listed ? MigrationsWords.RefreshToList
            : controller.Migrations.Count == 0 ? MigrationsWords.NoneYet(controller.Context ?? controller.Project?.Name ?? string.Empty)
            : null;
        placeholder.Say(empty);
        placeholder.IsVisible = empty is not null;
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
            Button button = new() { [ShownWords.ContentProperty] = fix.Title };
            button.Click += (_, _) => IdeErrors.Observe(controller.FixAsync(fix));
            fixes.Children.Add(button);
        }
        StackPanel words = new() { Children = { new TextBlock { [ShownWords.TextProperty] = gap.Said, TextWrapping = TextWrapping.Wrap } } };
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
            ShownWords.Set(state, TextBlock.TextProperty, row.Applied switch { true => MigrationsWords.Applied, false => MigrationsWords.Pending, null => MigrationsWords.NotRead });
            state.Classes.Set(StyleClasses.Muted, row.Applied != false);
            state.TextTrimming = row.Applied == false ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            name.Text = row.Name;
            when.Text = stamp;
        });
    }

    private static ComboBox Chooser(string name, Words tip)
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
                ShownWords.Set(note, TextBlock.TextProperty, (item as EfEnvironment)?.Note);
                note.IsVisible = (item as EfEnvironment)?.Note is not null;
            });
        });
        ShownWords.Set(chooser, ToolTip.TipProperty, tip);
        return chooser;
    }
}
