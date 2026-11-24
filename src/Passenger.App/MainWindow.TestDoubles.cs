using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Test doubles: Create Fake writes a fake of the interface or class whose name the caret is on into a test project
/// that sees it, offered among Show Context Actions for each such project, one command of global Undo; a sealed or static
/// class of the solution's own is offered with the seam a fake needs, by the refactorings' titles.</summary>
public partial class MainWindow
{
    private void AddTestDoubles()
    {
        actions!.Add(new IdeAction(AppActions.CreateFake, "Create Fake…", "Code", _ => IdeErrors.Observe(DoublesAtCaretAsync(fakes: true)))
        {
            WhyNot = context => WhyNoDoubles(context) ?? (TestProjects().Count == 0 ? Refusals.NoTestProject : null),
            Menus = [new MenuPlace("Code", 4, 0)],
            Words = ["stub", "test double", "mock", "isolate"],
        });
    }

    private string? WhyNoDoubles(ActionContext context) => Refusals.NeedsFile(context)
        ?? (context.File!.Language != Languages.Language.CSharp ? Refusals.DoublesAreCSharp : session.CSharp is null ? session.WhyNoCSharp : null);

    /// <summary>The folder's C# test projects, by their files.</summary>
    private IReadOnlyList<string> TestProjects() =>
        [.. session.Projects.Where(p => p.IsTestProject && p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Select(p => p.Path)];

    /// <summary>An action's title as the registry has it, without the dots of one that asks more.</summary>
    private string TitleOf(string id) => actions?[id]?.Title.TrimEnd('…') ?? id;

    /// <summary>The test doubles offered at an offset of a C# file the server answers for (<see cref="DoubleFixesAsync"/>),
    /// fakes then fills.</summary>
    internal async Task<IReadOnlyList<EditorFix>> TestDoubleFixesAsync(OpenDocument file, int offset)
    {
        (IReadOnlyList<EditorFix> fakes, IReadOnlyList<EditorFix> fills) = await DoubleFixesAsync(file, offset);
        return [.. fakes, .. fills];
    }

    /// <summary>The test doubles offered at an offset of a C# file the server answers for: a fake for each test project that
    /// sees the type there, or the seam a sealed or static class of the solution's own needs, which refuses saying so.</summary>
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
        return (FakeFixes(file, offered, new TextRange(offset, 0)), []);
    }

    private IReadOnlyList<EditorFix> FakeFixes(OpenDocument file, DoublesOffered offered, TextRange here)
    {
        if (offered is not { Type: { } type, Fakes.Count: > 0 })
        {
            return [];
        }
        string title = TitleOf(AppActions.CreateFake);
        if (offered.Seam is { } seam)
        {
            string extract = TitleOf(AppActions.ExtractInterface);
            string introduce = TitleOf(AppActions.IntroduceParameter);
            string why = Refusals.FakeNeedsSeam(type, seam, extract, introduce);
            return [new EditorFix($"{title} of {type}", $"{Refusals.SeamOf(seam)}: {extract}, then {introduce}", here, string.Empty)
            {
                Elsewhere = (_, _) => Task.FromResult(RefactorOutcome.Refused(why)),
            }];
        }
        string kept = offered.KeepsOwn ? $"its members that are not virtual stay {type}'s own" : string.Empty;
        return [.. offered.Fakes.Select(place => new EditorFix($"{title} of {type} in {place.Name}",
            place.Exists ? $"Fakes/{Path.GetFileName(place.File)} is there already" : kept, here, string.Empty)
        {
            Elsewhere = (range, patience) => place.Exists
                ? Task.FromResult(RefactorOutcome.Refused(Refusals.FakeThere(Path.GetFileName(place.File), place.Name)))
                : Assist.Actions.MakeAsync(title, file, range.Start,
                    now => now.ExtensionAsync<Refactored>(Handlers.CreateFake, new FakeAsked(now.PlaceOf(range.Start), place.Project)),
                    _ => $"Made Fakes/{Path.GetFileName(place.File)} in {place.Name}", patience),
        })];
    }

    /// <summary>Create Fake from its keys or a menu: the one offered at the caret made, a list where there are several test
    /// projects, refused saying why where none is offered.</summary>
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
                Refuse(Refusals.NoFakeHere);
                break;
            case 1:
                await Assist.Actions.ApplyOwnFix(file, fixes[0]);
                break;
            default:
                Assist.Actions.ShowOwnFixes(TitleOf(AppActions.CreateFake), file, fixes);
                break;
        }
    }
}
