using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Choice = Passenger.App.Vcs.Choice;
using ChoicePopup = Passenger.App.Vcs.ChoicePopup;

namespace Passenger.App;

/// <summary>Run Inspection by Name: one rule of the solution's analyzers, chosen from every rule they report by its title or
/// id, run over every C# project whatever severity it is set to, the places it reports listed in Problems' Inspection tab
/// under what it ran.</summary>
public partial class MainWindow
{
    private void AddInspections() =>
        Add(AppActions.RunInspectionByName, ActionTitles.RunInspectionByName, ActionGroups.Code, _ => IdeErrors.Observe(ShowInspectionsAsync()),
            _ => session.CSharp is null ? session.WhyNoCSharp : null, ("Code", 4, 2), words: ["Analyzer Rule", "Diagnostic", "Run Rule"]);

    /// <summary>Every rule the solution's analyzers report, in a popup narrowed as words of its title or id are typed; the one
    /// chosen runs over the solution. Null where the C# server cannot list them yet, the status bar saying why.</summary>
    public async Task<ChoicePopup?> ShowInspectionsAsync()
    {
        Words title = TitleOf(AppActions.RunInspectionByName);
        if (session.CSharp is not { } csharp || !csharp.IsReady || csharp.Session is not { } server)
        {
            status.Text = session.CSharp is { } unready ? unready.Unready(title.Shown) : session.WhyNoCSharp;
            return null;
        }
        // Held, so what comes next in the window finds the rules offered.
        using IDisposable held = BackgroundWork.Scope();
        InspectionsFound found;
        using (Working? reading = BeginWork(AppActions.RunInspectionByName, WindowWords.ReadingRules))
        {
            if (reading is null)
            {
                return null;
            }
            try
            {
                found = await server.ExtensionAsync<InspectionsFound>(Handlers.Inspections, new InspectionsAsked());
            }
            catch (LanguageServerFailed failed)
            {
                Refuse(SharedWords.Failed(title, Words.Verbatim(failed.Reason)));
                reading.Tell(false, SharedWords.Failed(title, Words.Verbatim(failed.Reason)));
                return null;
            }
        }
        ChoicePopup popup = new(title, WindowWords.SearchRules, [.. found.Rules.Select(r => new Choice(r.Title, $"{r.Id} · {r.Category}", r))], Refusals.NoInspections);
        popups.Show(popup, chosen =>
        {
            if (chosen?.Value is EditorConfigDiagnostic rule)
            {
                IdeErrors.Observe(RunInspectionAsync(rule));
            }
        });
        return popup;
    }

    /// <summary>One rule run over the solution, the places it reports listed in Problems' Inspection tab, which shows, and what
    /// the run came to said.</summary>
    public async Task RunInspectionAsync(EditorConfigDiagnostic rule)
    {
        // Held, so what comes next in the window finds the rule's places listed.
        using IDisposable held = BackgroundWork.Scope();
        using Working? running = BeginWork(AppActions.RunInspectionByName, $"Running {rule.Id} over the solution…");
        if (running is null)
        {
            return;
        }
        if (session.CSharp?.Session is not { } server)
        {
            Refuse(Refusals.CSharpNotRunningSentence);
            running.Tell(false, Refusals.CSharpNotRunningSentence);
            return;
        }
        InspectionFound found;
        try
        {
            found = await server.ExtensionAsync<InspectionFound>(Handlers.RunInspection, new InspectionAsked(rule.Id));
        }
        catch (LanguageServerFailed failed)
        {
            Refuse($"{rule.Id} could not be run: {failed.Reason}");
            running.Tell(false, $"{rule.Id} could not be run: {failed.Reason}");
            return;
        }
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(Editors);
        Problem[] problems = [.. found.Places.Select(p => new Problem(SeverityOf(p.Severity), p.Message, rule.Id, p.Path,
            ServerPlaces.PositionOf(texts(p.Path), new LspPosition(p.At.Line, p.At.Character)), p.Project))];
        int files = problems.Select(p => p.Path).Distinct(PathNames.Comparer).Count();
        string said = problems.Length == 0
            ? $"{rule.Id} {rule.Title}: no place in {Plural.Of(found.Projects, "project")} running it"
            : $"{rule.Id} {rule.Title}: {Plural.Of(problems.Length, "place")} in {Plural.Of(files, "file")}";
        Bottom.Problems.ShowInspection(said, problems);
        ShowToolWindow(ToolWindowIds.Problems);
        Say(said);
        running.Tell(true, said);
    }

    /// <summary>A rule's severity, as .editorconfig writes it, as Problems lists it: a suggestion, or a rule turned off, a message.</summary>
    private static ProblemSeverity SeverityOf(string severity) => severity switch
    {
        "error" => ProblemSeverity.Error,
        "warning" => ProblemSeverity.Warning,
        _ => ProblemSeverity.Message,
    };
}
