using Passenger.App.Bdd;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace.Gherkin;

namespace Passenger.App;

/// <summary>The window's Step Library: its tool window over the folder's step definitions and hooks as the session's
/// <see cref="StepCatalog"/> last read them, following each read, and how many feature steps use each definition, counted
/// again whole as the bindings are read again and one feature at a time as it changes, while the window is shown; Insert
/// Step into Feature writes the chosen phrase at the open feature's caret.</summary>
public partial class MainWindow
{
    private StepLibraryView? stepLibrary;
    private StepUses? stepUses;

    /// <summary>The definitions as last listed, and the read of the bindings they were listed from.</summary>
    private (int Version, IReadOnlyList<LibraryStep> Steps, IReadOnlyList<StepHook> Hooks)? stepsListed;

    /// <summary>A count is due that the window, hidden, put off until it shows again.</summary>
    private bool stepUsesDue;

    /// <summary>The Step Library tool window, built as it is first shown or asked for.</summary>
    public StepLibraryView StepLibraryWindow => stepLibrary ?? (StepLibraryView)Dock[ToolWindowIds.StepLibrary].Content;

    private void AddStepLibrary()
    {
        // Built as it is first shown or asked for, over the session's bindings then.
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.StepLibrary, "Step Library", IconKeys.StepLibrary, () =>
        {
            StepLibraryView view = new(RowMenuItem);
            stepLibrary = view;
            view.InsertRequested += (_, step) =>
            {
                view.Choose(step);
                Request(AppActions.InsertStep, actions!.Context().In(Dock[ToolWindowIds.StepLibrary]));
            };
            view.GoToRequested += (_, place) => NavigateTo(place.Path, new Lsp.LspPosition(place.Line, place.Column));
            view.UsagesRequested += (_, step) => IdeErrors.Observe(assist.Languages.Gherkin.ShowUsesAsync([step.Binding]));
            view.AttachedToVisualTree += (_, _) =>
            {
                ShowStepLibrary();
                WantSteps(opening: true);
                if (stepUsesDue)
                {
                    CountStepUses();
                }
            };
            FollowStepLibrary(session);
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Serves = ToolWindowServes.StepDefinitions,
            Action = AppActions.ShowStepLibrary,
            Menus = [new MenuPlace("Tools", 0, 5)],
            Chosen = () => stepLibrary?.Chosen,
        });
        actions!.Add(new IdeAction(AppActions.InsertStep, ActionTitles.InsertStep, ActionGroups.StepLibrary,
            context => IdeErrors.Observe(InsertStepAsync(stepLibrary!.Chosen!, context)))
        {
            WhyNot = _ => stepLibrary?.Chosen is null ? Refusals.NoStepChosen : assist.Languages.Gherkin.WhyNotInsertStep(),
            Words = ["Reqnroll", "SpecFlow", "Gherkin", "Step Definition"],
        });
    }

    /// <summary>The chosen definition's phrase written into the open feature, the editor given the keys to fill its slot
    /// where a person asked.</summary>
    private async Task InsertStepAsync(LibraryStep step, ActionContext context)
    {
        if (await assist.Languages.Gherkin.InsertStepAsync(step.Binding) && !context.ByAgent)
        {
            Editors.CodeEditor.Focus();
        }
    }

    /// <summary>A session's bindings and features in the Step Library, while it is the window's and the window is built.</summary>
    private void BindStepLibrary(ProjectSession next)
    {
        if (stepLibrary is not null)
        {
            FollowStepLibrary(next);
        }
    }

    private void FollowStepLibrary(ProjectSession next)
    {
        StepUses uses = stepUses = new StepUses(assist.Bdd!()!);
        stepsListed = null;
        uses.Changed += (_, _) =>
        {
            if (session == next && stepUses == uses)
            {
                ShowStepLibrary();
            }
        };
        next.Steps.Changed += (_, _) =>
        {
            if (session == next && stepUses == uses)
            {
                ShowStepLibrary();
                CountStepUses();
            }
        };
        next.Features.Analysed += (_, document) =>
        {
            if (session == next && stepUses == uses && next.Features.Of(document) is { } analysis)
            {
                uses.Took(document.Path, analysis);
            }
        };
        ShowStepLibrary();
        CountStepUses();
    }

    /// <summary>Every feature counted again while the window is shown, else once it shows.</summary>
    private void CountStepUses()
    {
        if (stepUses is not { } uses || !session.Steps.HasRead)
        {
            return;
        }
        stepUsesDue = !Dock.IsShown(ToolWindowIds.StepLibrary);
        if (!stepUsesDue)
        {
            IdeErrors.Observe(uses.CountAllAsync());
        }
    }

    /// <summary>Feature files changed on disk: each counted again while the window is shown, else every one once it shows.</summary>
    private void StepLibraryFilesChanged(FolderChanges changes)
    {
        if (stepUses is not { } uses || uses.CountedAs < 0)
        {
            return;
        }
        string[] features = [.. changes.Paths.Where(FeatureChecks.IsFeature).Where(p => Editors.Find(p) is null)];
        if (features.Length == 0)
        {
            return;
        }
        if (Dock.IsShown(ToolWindowIds.StepLibrary))
        {
            IdeErrors.Observe(uses.ChangedOnDiskAsync(features));
        }
        else
        {
            stepUsesDue = true;
        }
    }

    /// <summary>The definitions and hooks as the bindings stand, each definition with its count where one has landed.</summary>
    private void ShowStepLibrary()
    {
        if (stepLibrary is not { } view || stepUses is not { } uses)
        {
            return;
        }
        StepCatalog catalog = session.Steps;
        if (stepsListed is not { } listed || listed.Version != catalog.Version)
        {
            listed = (catalog.Version, StepLibrary.Definitions(catalog.Projects), StepLibrary.Hooks(catalog.Projects));
            stepsListed = listed;
        }
        string? none = (catalog.HasRead && !catalog.Projects.Any()) || session.Workspace is { Projects.Count: 0 } ? "No Reqnroll or SpecFlow project is in the folder"
            : !catalog.HasRead ? (session.CSharp is { IsReady: true } ? "Reading the step definitions…" : "The step definitions are read once C# has loaded")
            : null;
        view.Show(uses.CountedAs == catalog.Version ? StepLibrary.Counted(listed.Steps, uses.Bound) : listed.Steps, listed.Hooks, none);
    }
}
