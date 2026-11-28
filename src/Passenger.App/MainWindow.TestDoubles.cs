using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Test doubles: Create Fake writes a fake of the interface or class whose name the caret is on into a test project
/// that sees it, offered among Show Context Actions for each such project, one command of global Undo, a sealed or static
/// class of the solution's own offered with the seam a fake needs, by the refactorings' titles; Fill Arguments with Doubles
/// fills a call's missing arguments with doubles of a mocking library its project references, never one it does not.</summary>
public partial class MainWindow
{
    private void AddTestDoubles()
    {
        actions!.Add(new IdeAction(AppActions.CreateFake, ActionTitles.CreateFake, ActionGroups.Code, _ => IdeErrors.Observe(DoublesAtCaretAsync(fakes: true)))
        {
            WhyNot = context => WhyNoDoubles(context) ?? (TestProjects().Count == 0 ? Refusals.NoTestProject : null),
            Menus = [new MenuPlace("Code", 4, 0)],
            Words = ["stub", "test double", "mock", "isolate"],
        });
        actions.Add(new IdeAction(AppActions.FillArgumentsWithDoubles, ActionTitles.FillArgumentsWithDoubles, ActionGroups.Code, _ => IdeErrors.Observe(DoublesAtCaretAsync(fakes: false)))
        {
            WhyNot = context => WhyNoDoubles(context) ?? context.ReadOnlyWhy,
            Menus = [new MenuPlace("Code", 4, 1)],
            Words = ["mock", "substitute", "fake", "NSubstitute", "Moq", "FakeItEasy"],
        });
    }

    private Words? WhyNoDoubles(ActionContext context) => Refusals.NeedsFile(context)
        ?? (context.File!.Language != Languages.Language.CSharp ? Refusals.DoublesAreCSharp : session.CSharp is null ? session.WhyNoCSharp : null);

    /// <summary>The folder's C# test projects, by their files.</summary>
    private IReadOnlyList<string> TestProjects() =>
        [.. session.Projects.Where(p => p.IsTestProject && p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Select(p => p.Path)];

    /// <summary>An action's title as the registry has it, without the dots of one that asks more (<see cref="IdeAction.PlainTitle"/>).</summary>
    private Words TitleOf(string id) => actions?[id]?.PlainTitle ?? Words.Verbatim(id);

    /// <summary>The test doubles offered at an offset of a C# file the server answers for (<see cref="DoubleFixesAsync"/>),
    /// fakes then fills.</summary>
    internal async Task<IReadOnlyList<EditorFix>> TestDoubleFixesAsync(OpenDocument file, int offset)
    {
        (IReadOnlyList<EditorFix> fakes, IReadOnlyList<EditorFix> fills) = await DoubleFixesAsync(file, offset);
        return [.. fakes, .. fills];
    }

    /// <summary>The test doubles offered at an offset of a C# file the server answers for: a fake for each test project that
    /// sees the type there, or the seam a sealed or static class of the solution's own needs, which refuses saying so; and,
    /// in a call missing arguments, a fill for each mocking library its project references.</summary>
    private async Task<(IReadOnlyList<EditorFix> Fakes, IReadOnlyList<EditorFix> Fills)> DoubleFixesAsync(OpenDocument file, int offset)
    {
        if (file.Language != Languages.Language.CSharp || session.CSharp is not { IsReady: true } csharp || csharp.For(file)?.Lsp is not { } lsp)
        {
            return ([], []);
        }
        DoublesOffered offered;
        try
        {
            offered = await lsp.ExtensionAsync<DoublesOffered>(Handlers.TestDoublesOffered, new DoublesAsked(lsp.PlaceOf(offset), TestProjects()));
        }
        catch (LanguageServerFailed)
        {
            // The fixes the server offers itself are asked next, and say why where it fails.
            return ([], []);
        }
        TextRange here = new(offset, 0);
        Words fill = TitleOf(AppActions.FillArgumentsWithDoubles);
        IReadOnlyList<EditorFix> fills = file.ReadOnlyWhy is not null ? [] : [.. offered.Libraries.Select(library =>
            new EditorFix(offered.Libraries.Count == 1 ? fill : AssistWords.FillOf(fill, Words.Verbatim(library)), Words.Verbatim(library), here, string.Empty)
            {
                Elsewhere = (range, patience) => Assist.Actions.MakeAsync(fill.Shown, file, range.Start,
                    now => now.ExtensionAsync<Refactored>(Handlers.FillArguments, new ArgumentsAsked(now.PlaceOf(range.Start), library)),
                    _ => $"Filled the call's missing arguments with {library}'s doubles", patience),
            })];
        return (FakeFixes(file, offered, here), fills);
    }

    private IReadOnlyList<EditorFix> FakeFixes(OpenDocument file, DoublesOffered offered, TextRange here)
    {
        if (offered is not { Type: { } type, Fakes.Count: > 0 })
        {
            return [];
        }
        Words title = TitleOf(AppActions.CreateFake);
        Words named = Words.Verbatim(type);
        if (offered.Seam is { } seam)
        {
            Words extract = TitleOf(AppActions.ExtractInterface);
            Words introduce = TitleOf(AppActions.IntroduceParameter);
            Words why = Refusals.FakeNeedsSeam(named, seam, extract, introduce);
            return [new EditorFix(AssistWords.TitleOfType(title, named), AssistWords.SeamThen(Refusals.SeamOf(seam), extract, introduce), here, string.Empty)
            {
                Elsewhere = (_, _) => Task.FromResult(RefactorOutcome.Refused(why)),
            }];
        }
        Words kept = offered.KeepsOwn ? AssistWords.NotVirtualStay(named) : Words.Verbatim(string.Empty);
        return [.. offered.Fakes.Select(place => new EditorFix(AssistWords.FakeIn(title, named, Words.Verbatim(place.Name)),
            place.Exists ? AssistWords.FakeExists(Words.Verbatim(Path.GetFileName(place.File))) : kept, here, string.Empty)
        {
            Elsewhere = (range, patience) => place.Exists
                ? Task.FromResult(RefactorOutcome.Refused(Refusals.FakeThere(Path.GetFileName(place.File), place.Name)))
                : Assist.Actions.MakeAsync(title.Shown, file, range.Start,
                    now => now.ExtensionAsync<Refactored>(Handlers.CreateFake, new FakeAsked(now.PlaceOf(range.Start), place.Project)),
                    _ => $"Made Fakes/{Path.GetFileName(place.File)} in {place.Name}", patience),
        })];
    }

    /// <summary>Create Fake, or Fill Arguments with Doubles, from its keys or a menu: the one offered at the caret made, a
    /// list where there are several (test projects, or libraries), refused saying why where none is offered.</summary>
    private async Task DoublesAtCaretAsync(bool fakes)
    {
        if (Editors.Active is not { } file)
        {
            return;
        }
        (IReadOnlyList<EditorFix> made, IReadOnlyList<EditorFix> filled) = await DoubleFixesAsync(file, Editors.CodeEditor.Selections.Primary.Caret);
        IReadOnlyList<EditorFix> fixes = fakes ? made : filled;
        if (Editors.Active != file)
        {
            return;
        }
        switch (fixes.Count)
        {
            case 0:
                Refuse(fakes ? Refusals.NoFakeHere : Refusals.NothingToFill);
                break;
            case 1:
                await Assist.Actions.ApplyOwnFix(file, fixes[0]);
                break;
            default:
                Assist.Actions.ShowOwnFixes(TitleOf(fakes ? AppActions.CreateFake : AppActions.FillArgumentsWithDoubles), file, fixes);
                break;
        }
    }
}
