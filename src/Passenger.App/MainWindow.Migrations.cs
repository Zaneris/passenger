using Avalonia.Controls;
using Passenger.App.Migrations;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The window's Migrations: its tool window over the folder's EF Core contexts (<see cref="MigrationsController"/>),
/// the commands it runs as the window's actions, and the questions they ask (a migration's name, a script's range, a gap's
/// fix before a command goes on).</summary>
public partial class MainWindow
{
    private MigrationsController? migrations;
    private MigrationsView? migrationsView;

    /// <summary>The Migrations window's model, which its actions, fixes and an agent's ide_action read and drive.</summary>
    public MigrationsController Migrations => migrations!;

    /// <summary>The Migrations window's view, built as it is first shown or asked for (a gap's Choose fix opening its chooser).</summary>
    public MigrationsView MigrationsView => migrationsView ?? (MigrationsView)Dock[ToolWindowIds.Migrations].Content;

    private void AddMigrations()
    {
        MigrationsController controller = migrations = new MigrationsController(new MigrationsParts(Editors, Bottom, Dock, work, Say, () => session, () => buildRun,
            ChooseAsync, (project, id, version) => session.Packages.InstallAsync(project, id, version), (project, packages) => session.Packages.InstallSetAsync(project, packages),
            () => session.Packages.FeedAsync(null), FollowWritten, OpenFile, () => StateFolder, ShowPendingModelChanges)
        {
            AsksNoOne = () => AgentsOwn,
        });
        // The NuGet window's update of an EF Core package is weighed against the project's provider first.
        if (nuget is { } built)
        {
            built.BeforeInstall = controller.WeighUpdateAsync;
        }
        controller.AddAsked += (_, _) => Request(A.AddMigration);
        // A gap's Choose fix opens the chooser it names, the window shown for it when it is hidden.
        controller.ChoiceAsked += (_, kind) =>
        {
            if (!Dock.IsShown(ToolWindowIds.Migrations))
            {
                ShowToolWindow(ToolWindowIds.Migrations);
            }
            MigrationsView.OpenChooser(kind);
        };
        Docking.ToolWindow shown = new(ToolWindowIds.Migrations, "Migrations", IconKeys.Database, () =>
        {
            MigrationsView view = migrationsView = new MigrationsView(controller);
            view.ActionRequested += (_, action) => Request(action);
            // Added after the window marks its tool windows' buttons (ShowActionWork): its own marked as it is built.
            ActionButtons.ShowMarked(view, actions!);
            return view;
        }, Docking.ToolAnchor.BottomLeft)
        {
            Action = A.ShowMigrations,
            Menus = [new MenuPlace("Tools", 0, 5)],
            Chosen = () => migrationsView?.Chosen,
        };
        Dock.Add(shown);
        // Shown however it is (its action, its stripe button, a layout), it lists what it can at once.
        shown.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Docking.ToolWindow.IsShown) && shown.IsShown)
            {
                UiThread.Post(ListMigrationsIfShown);
            }
        };
        controller.Changed += (_, _) => ListMigrationsIfShown();
        Func<ActionContext, string?> none = _ => Folder is null ? Refusals.NoFolder : controller.Projects.Count == 0 ? "No project here uses EF Core" : null;
        actions!.Add(new IdeAction(A.MigrationsRefresh, "Refresh Migrations", "Migrations", c => IdeErrors.Observe(controller.ListAsync(A.MigrationsRefresh)))
        {
            WhyNot = none,
            Menus = [new MenuPlace("Tools/Migrations", 0, 0)],
            Words = ["dotnet ef migrations list", "Entity Framework Core", "EF Core"],
        });
        actions.Add(new IdeAction(A.AddMigration, "Add Migration…", "Migrations", c => IdeErrors.Observe(AddMigrationAsync()))
        {
            WhyNot = none,
            Menus = [new MenuPlace("Tools/Migrations", 0, 1)],
            Words = ["dotnet ef migrations add", "Entity Framework Core", "EF Core"],
        });
        actions.Add(new IdeAction(A.RemoveLastMigration, "Remove Last Migration", "Migrations", c => IdeErrors.Observe(controller.RemoveLastAsync(A.RemoveLastMigration)))
        {
            WhyNot = none,
            Menus = [new MenuPlace("Tools/Migrations", 0, 2)],
            Words = ["dotnet ef migrations remove"],
        });
        actions.Add(new IdeAction(A.UpdateDatabase, "Update Database", "Migrations", c => IdeErrors.Observe(controller.UpdateAsync(A.UpdateDatabase, null)))
        {
            WhyNot = none,
            Menus = [new MenuPlace("Tools/Migrations", 1, 0)],
            Words = ["dotnet ef database update", "Apply Migrations"],
        });
        actions.Add(new IdeAction(A.UpdateDatabaseTo, "Update Database to Chosen Migration", "Migrations", c =>
        {
            if (migrationsView?.Chosen is { } chosen)
            {
                IdeErrors.Observe(controller.UpdateAsync(A.UpdateDatabaseTo, chosen.Id));
            }
        })
        {
            WhyNot = context => none(context) ?? (migrationsView?.Chosen is null ? "No migration is chosen in the Migrations window" : null),
            TitleIn = _ => migrationsView?.Chosen is { } chosen ? $"Update Database to {chosen.Name}" : "Update Database to Chosen Migration",
            Menus = [new MenuPlace("Tools/Migrations", 1, 1)],
            Words = ["Roll Back Migration", "dotnet ef database update"],
        });
        actions.Add(new IdeAction(A.ScriptMigrations, "Script Migrations…", "Migrations", c => IdeErrors.Observe(ScriptMigrationsAsync()))
        {
            WhyNot = none,
            Menus = [new MenuPlace("Tools/Migrations", 2, 0)],
            Words = ["dotnet ef migrations script", "SQL Script"],
        });
        actions.Add(new IdeAction(A.BundleMigrations, "Bundle Migrations", "Migrations", c => IdeErrors.Observe(controller.BundleAsync(A.BundleMigrations)))
        {
            WhyNot = none,
            Menus = [new MenuPlace("Tools/Migrations", 2, 1)],
            Words = ["dotnet ef migrations bundle", "efbundle"],
        });
        actions.Add(new IdeAction(A.MigrationsFix, "Fix What Migrations Lack", "Migrations", c =>
        {
            if (FirstFix() is { } fix)
            {
                IdeErrors.Observe(controller.FixAsync(fix));
            }
        })
        {
            WhyNot = context => none(context) ?? (FirstFix() is null ? "The Migrations window shows nothing to fix" : null),
            TitleIn = _ => FirstFix()?.Title ?? "Fix What Migrations Lack",
            Words = ["dotnet tool install dotnet-ef", "Microsoft.EntityFrameworkCore.Design"],
        });

        EfFix? FirstFix() => controller.Gaps.Concat(controller.Failure is { } failed ? [failed] : []).SelectMany(g => g.Fixes).FirstOrDefault();
    }

    /// <summary>The session's projects as the Migrations window follows them, from the session's first moment.</summary>
    private void BindMigrations(ProjectSession next)
    {
        migrations?.Bind(next);
        next.WorkspaceChanged += (_, _) => FollowMigrations(next);
        next.ProjectsEvaluated += (_, _) => FollowMigrations(next);
        buildRun.BuildFinished += (_, built) =>
        {
            FollowMigrations(next);
            if (session == next)
            {
                migrations?.AfterBuild(built);
            }
        };
    }

    /// <summary>The contexts whose model has changes no migration covers, in Problems' Build tab beside the build's own, each
    /// at its class, where Show Context Actions offers Add Migration… (<see cref="MigrationFixes"/>).</summary>
    private void ShowPendingModelChanges(IReadOnlyList<Run.Problem> pending)
    {
        session.Problems.RemoveAll(p => p.Code == MigrationsController.PendingCode);
        session.Problems.AddRange(pending);
        buildRun.ShowProblems();
    }

    /// <summary>Passenger's own fixes of the Migrations window, where Show Context Actions and an agent's code_actions find
    /// them: on a context's declaration, Add Migration… when its model has changes no migration covers; there and on the
    /// startup project's file, the fixes of what keeps dotnet ef from working (the choosers' aside, which are the window's).</summary>
    private IReadOnlyList<CSharp.EditorFix> MigrationFixes(Workspace.OpenDocument file, int offset)
    {
        if (migrations is not { } controller || controller.Projects.Count == 0)
        {
            return [];
        }
        int line = file.Text.Snapshot.GetPosition(offset).Line;
        bool startup = controller.Startup is { } project && Core.PathNames.Same(project.Path, file.Path);
        bool declared = controller.Declared.Any(d => d.File is { } at && Core.PathNames.Same(at, file.Path) && d.Position.Line == line);
        List<CSharp.EditorFix> fixes = [.. controller.Pending.Where(p => p.File is { } at && Core.PathNames.Same(at, file.Path) && p.Position.Line == line)
            .Select(p => new CSharp.EditorFix("Add Migration…", $"for {p.Context}'s model changes", new Text.TextRange(offset, 0), string.Empty)
            {
                Elsewhere = (_, _) =>
                {
                    if (Mcp.AgentCall.Current is not null)
                    {
                        return Task.FromResult(CSharp.RefactorOutcome.Refused($"Add Migration takes its name: {A.AddMigration} with the name as input"));
                    }
                    controller.ChooseContext(p.Context);
                    // Not awaited: its name is asked of the person, which no fix's own work waits on.
                    IdeErrors.Observe(AddMigrationAsync());
                    return Task.FromResult(new CSharp.RefactorOutcome($"Add Migration: a name for {p.Context}'s migration", null, [], []));
                },
            })];
        if (startup || declared)
        {
            foreach (EfGap gap in controller.Gaps.Concat(controller.Failure is { } failed ? [failed] : []))
            {
                foreach (EfFix fix in gap.Fixes.Where(f => f.Kind is not (EfFixKind.ChooseStartup or EfFixKind.ChooseContext or EfFixKind.ScriptPlain)))
                {
                    fixes.Add(new CSharp.EditorFix(fix.Title, gap.Said, new Text.TextRange(offset, 0), string.Empty)
                    {
                        Elsewhere = async (_, _) => await controller.FixAsync(fix) is { } why ? CSharp.RefactorOutcome.Refused(why)
                            : new CSharp.RefactorOutcome($"{fix.Title}: done", null, [.. controller.Touched.Select(t => new CSharp.RefactoredFile(t, "changed", []))], []),
                    });
                }
            }
        }
        return fixes;
    }

    private void FollowMigrations(ProjectSession next)
    {
        if (session == next && migrations is { Busy: null } shown)
        {
            shown.Follow();
        }
    }

    /// <summary>The Migrations window, shown, lists its context's migrations by itself once for the choices as they stand,
    /// as soon as the projects are read and nothing it shows stops dotnet ef: what is missing is said first, never asked.</summary>
    private void ListMigrationsIfShown()
    {
        if (migrations is { WantsListing: true } controller && Dock.IsShown(ToolWindowIds.Migrations) && !IsBuilding)
        {
            controller.ListingTried();
            UiThread.Post(() => Request(A.MigrationsRefresh));
        }
    }

    /// <summary>Add Migration…: its name asked, then added to the chosen context.</summary>
    private async Task AddMigrationAsync()
    {
        TextBox box = new() { Name = "MigrationName", PlaceholderText = "AddCustomerEmail" };
        ConfirmDialog dialog = new("Add Migration", $"A name for the migration of {migrations!.Context ?? migrations.Project?.Name}, as a C# class is named.", "Add", detail: box);
        dialog.Opened += (_, _) => box.Focus();
        if (!await dialog.ShowDialog<bool>(this) || box.Text?.Trim() is not { Length: > 0 } name)
        {
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{Nd}_]*$"))
        {
            status.Text = $"{name} is no C# name: a migration is a class of that name";
            return;
        }
        await migrations.AddAsync(A.AddMigration, name);
    }

    /// <summary>Script Migrations…: its range and idempotence asked, then scripted into an editor tab.</summary>
    private async Task ScriptMigrationsAsync()
    {
        MigrationsController controller = migrations!;
        if (!controller.Listed && !(await controller.ListAsync(A.MigrationsRefresh)).Done)
        {
            return;
        }
        MigrationScriptDialog dialog = new(controller.Migrations, controller.Context ?? controller.Project?.Name ?? "the context");
        if (await dialog.ShowDialog<MigrationScript?>(this) is not { } asked)
        {
            return;
        }
        EfDone done = await controller.ScriptAsync(A.ScriptMigrations, asked.From, asked.To, asked.Idempotent);
        if (!done.Done && controller.Failure?.Fixes.Any(f => f.Kind == EfFixKind.ScriptPlain) == true
            && await ChooseAsync("Script Migrations", $"{controller.Failure.Said}. Script them without idempotence?", ["Script Without Idempotence"], "Script Without Idempotence") is not null)
        {
            await controller.ScriptAsync(A.ScriptMigrations, asked.From, asked.To, idempotent: false);
        }
    }

    /// <summary>A Migrations action an agent runs (ide_action), with what it would ask the person as <paramref name="input"/>:
    /// Refresh's context, Add's name, Update's or Update to's migration (0 for none), Script's FROM..TO. Answers what it came
    /// to with the context's migrations and what keeps dotnet ef from working, each with where its fixes are; refused, says
    /// why and where the fix is.</summary>
    public async Task<string> MigrationsForAgentAsync(string action, string? input, CancellationToken cancel)
    {
        MigrationsController controller = migrations!;
        if (actions![A.MigrationsRefresh]!.WhyNot(actions.Context()) is { } why)
        {
            throw new InvalidOperationException($"{action} did not run: {why}");
        }
        string? given = input?.Trim() is { Length: > 0 } words ? words : null;
        EfDone done;
        switch (action)
        {
            case A.MigrationsRefresh:
                if (given is not null)
                {
                    controller.ChooseContext(given);
                }
                done = await controller.ListAsync(action, cancel);
                break;
            case A.AddMigration:
                done = given is not null && System.Text.RegularExpressions.Regex.IsMatch(given, @"^[\p{L}_][\p{L}\p{Nd}_]*$")
                    ? await controller.AddAsync(action, given, cancel)
                    : throw new InvalidOperationException($"{action} takes the migration's name as input, a C# name");
                break;
            case A.RemoveLastMigration:
                done = await controller.RemoveLastAsync(action, cancel);
                break;
            case A.UpdateDatabase or A.UpdateDatabaseTo:
                done = action == A.UpdateDatabaseTo && given is null
                    ? throw new InvalidOperationException($"{action} takes the migration to update to as input: its name or id, or 0 for none")
                    : await controller.UpdateAsync(action, given, cancel);
                break;
            case A.ScriptMigrations:
                string[] range = (given ?? "..").Split("..", 2);
                done = await controller.ScriptAsync(action, range[0].Trim() is { Length: > 0 } from ? from : null,
                    range.Length > 1 && range[1].Trim() is { Length: > 0 } to ? to : null, idempotent: false, cancel);
                break;
            case A.BundleMigrations:
                done = await controller.BundleAsync(action, cancel);
                break;
            case A.MigrationsFix:
                EfFix fix = controller.Gaps.Concat(controller.Failure is { } failed ? [failed] : []).SelectMany(g => g.Fixes)
                    .FirstOrDefault(f => f.Kind is not (EfFixKind.ChooseStartup or EfFixKind.ChooseContext))
                    ?? throw new InvalidOperationException($"{action} did not run: the Migrations window shows nothing to fix");
                done = await controller.FixAsync(fix) is { } unfixed ? new EfDone(false, unfixed) : new EfDone(true, $"{fix.Title}: done");
                break;
            default:
                return actions.Run(action) is { } refused ? throw new InvalidOperationException($"{action} did not run: {refused}") : $"Ran {action}";
        }
        string shown = MigrationsShown(controller);
        return done.Done ? $"{done.Said}\n{shown}" : throw new InvalidOperationException($"{done.Said}\n{shown}");
    }

    /// <summary>The Migrations window as an agent reads it: the context and its migrations, then what keeps dotnet ef from
    /// working with the titles of its fixes and where code_actions finds them.</summary>
    private string MigrationsShown(MigrationsController controller)
    {
        List<string> lines = [];
        if (controller.Project is { } project)
        {
            lines.Add($"{controller.Context ?? "Its context"} of {project.Name}, started from {controller.Startup?.Name ?? project.Name}"
                + (controller.Contexts.Count > 1 ? $" (contexts: {string.Join(", ", controller.Contexts.Select(c => c.Name))})" : string.Empty)
                + (controller.Listed ? $": {Core.Plural.Of(controller.Migrations.Count, "migration")}" : ": not listed yet")
                + (controller.Database is { } database ? $", against {database}" : string.Empty));
        }
        lines.AddRange(controller.Migrations.Select(m => $"- {m.Id}: {m.Applied switch { true => "applied", false => "pending", null => "not read" }}"));
        IEnumerable<EfGap> gaps = controller.Gaps.Concat(controller.Pending.Select(p => new EfGap($"{p.Context}'s model has changes no migration covers",
            [new EfFix(EfFixKind.AddMigration, $"{A.AddMigration} with the name as input")], Blocks: false)));
        string where = controller.Startup is { } startup ? $"{Mcp.McpTools.CodeActions} on {Path.GetFileName(startup.Path)}:1:1" : Mcp.McpTools.CodeActions;
        foreach (EfGap gap in gaps.Concat(controller.Failure is { } failed ? [failed] : []))
        {
            string[] titles = [.. gap.Fixes.Where(f => f.Kind is not (EfFixKind.ChooseStartup or EfFixKind.ChooseContext or EfFixKind.ScriptPlain)).Select(f => f.Title)];
            lines.Add($"! {gap.Said}{(titles.Length == 0 ? string.Empty : gap.Fixes[0].Kind == EfFixKind.AddMigration ? $" (fix: {titles[0]})" : $" (fix: {string.Join("; ", titles)}, by {where})")}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>Asks which of several ways to go on (<see cref="ChoiceDialog"/>): the words of the one chosen, or null.</summary>
    private Task<string?> ChooseAsync(string title, string text, IReadOnlyList<string> choices, string? chosen) =>
        new ChoiceDialog(title, text, choices, chosen).ShowDialog<string?>(this);
}
