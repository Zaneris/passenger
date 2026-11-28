using System.Text;
using System.Text.Json;
using Passenger.App.Editors;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Core;

namespace Passenger.App.CSharp;

/// <summary>The folder's linters' fixes and its formatters' layout: Show Context Actions lists the fixes of ESLint, Oxlint
/// and Biome for the caret's line first ("ESLint: Fix 'prefer-const'"), Fix ESLint Problems fixes the whole file as
/// <c>eslint --fix</c> would, Reformat Code lays a file out with the formatter the folder names for it (Oxfmt, Biome) or
/// with Prettier for the files its glob covers (Reformat with Prettier any file it reads), and an explicit save runs ESLint's
/// fixes and Prettier first as Settings ask. Each is one undoable edit, the caret kept where the code it was in went.</summary>
internal sealed partial class WebAssist
{
    private ToolDocument? LintOf(OpenDocument document) => context.Web?.Invoke()?.Tools.For(document, WebTool.Eslint);

    /// <summary>Reformat with Prettier and Fix ESLint Problems, in the editor's menu.</summary>
    public void AddLintingActions(ActionRegistry registry)
    {
        registry.Add(new IdeAction(AppActions.ReformatWithPrettier, ActionTitles.ReformatWithPrettier, "Code", context => IdeErrors.Observe(ReformatWithPrettier()))
        {
            WhyNot = action => action.File is null ? Refusals.NoFile
                : action.ReadOnlyWhy is { } readOnly ? readOnly
                : context.Web?.Invoke() is not { } web ? Refusals.NoFolder
                : web.Prettier.PackageFor(action.File.Path) is null ? $"No prettier package formats {action.File.Name}: install it in the project, or name one in {PrettierService.Place}"
                : null,
        });
        registry.Add(new IdeAction(AppActions.FixEslintProblems, ActionTitles.FixEslintProblems, "Code", context => IdeErrors.Observe(FixEslintProblems()))
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : context.ReadOnlyWhy is { } readOnly ? readOnly
                : LintOf(context.File) is null ? $"ESLint does not check {context.File.Name}: it needs the project's eslint package and a file Settings' Run for files covers"
                : null,
        });
    }

    /// <summary>Whether Reformat Code formats a file with Prettier ("Run on 'Reformat Code' action").</summary>
    internal bool PrettierReformats(OpenDocument? file) => file is not null && context.Web?.Invoke()?.Prettier.OnReformat(file.Path) == true;

    /// <summary>Whether Reformat Code lays a file out with one of the folder's formatters, Prettier or one its configuration
    /// names, rather than its language server.</summary>
    internal bool FolderReformats(OpenDocument? file) => PrettierReformats(file) || file is not null && context.Web?.Invoke() is { } web && FormattersOf(web, file).Count > 0;

    /// <summary>The formatters the folder names for a file beside Prettier, in the order Reformat Code asks them: Oxfmt, then
    /// Biome, which leaves to Prettier a file the folder names Prettier for too (Biome then its linter alone).</summary>
    private static IReadOnlyList<ToolDocument> FormattersOf(WebLanguages web, OpenDocument document)
    {
        IReadOnlyList<ToolDocument> tools = web.Tools.For(document);
        return [.. ((WebTool[])[WebTool.Oxfmt, WebTool.Biome])
            .Where(t => t != WebTool.Biome || !web.Prettier.OnReformat(document.Path) || !PrettierService.NamedFor(document.Path))
            .Select(t => tools.FirstOrDefault(d => d.Tool == t)).OfType<ToolDocument>()];
    }

    /// <summary>How long a formatter's server starting (as a run over a folder reads a file without a tab) is waited for.</summary>
    private static TimeSpan FormatterStart => Waits.Of(TimeSpan.FromSeconds(3));

    /// <summary>A file laid out by the first of the folder's formatters that formats it (<see cref="FormattersOf"/>), as one
    /// undoable edit, its server waited for while it starts (<see cref="FormatterStart"/>), else refused; null where none lays
    /// it out, which leaves it to Prettier or its language server.</summary>
    private async Task<CodeActions.Reformatted?> FormatWithToolsAsync(WebLanguages web, OpenDocument document, string what, TextRange? within, CancellationToken cancel = default)
    {
        foreach (ToolDocument formatter in FormattersOf(web, document))
        {
            if (formatter.Lsp is null)
            {
                try
                {
                    await formatter.Reached.WaitAsync(FormatterStart, cancel);
                }
                catch (TimeoutException)
                {
                    // Still starting: refused below, saying so.
                }
            }
            if (formatter.Lsp is null)
            {
                return CodeActions.Reformatted.Refused(what, $"{formatter.Tool.Name}'s language server is not running for {document.Name}");
            }
            if (await actions.ToolFormatAsync(formatter, document, what, within, cancel) is { } done)
            {
                return done with { Said = done.Changed ? $"{formatter.Tool.Name}: {done.Said}" : done.Said };
            }
        }
        return null;
    }

    /// <summary>The fixes of each linter that checks the file for the problems on the caret's line, ESLint's first; none
    /// where none checks it.</summary>
    internal async Task<IReadOnlyList<LintFix>> LintFixes(OpenDocument document, int caret)
    {
        List<LintFix> fixes = [];
        foreach (ToolDocument tool in context.Web?.Invoke()?.Tools.For(document) ?? [])
        {
            try
            {
                fixes.AddRange(await tool.FixesAsync(caret));
            }
            catch (LanguageServerFailed)
            {
                // The others' fixes, and the language server's own actions, are still offered.
            }
        }
        return fixes;
    }

    /// <summary>One of a linter's fixes chosen, as <see cref="MakeLintFixAsync"/> makes it, what came of it said; its
    /// rule's page opened for the documentation.</summary>
    internal async Task RunLintFix(OpenDocument document, LintFix fix)
    {
        if (fix.OpensDocumentation)
        {
            if (fix.From.Diagnostics.FirstOrDefault(d => !d.CodeIsNumber && d.Code == fix.Rule)?.CodePage is { } page)
            {
                context.Browse(page);
            }
            return;
        }
        using IDisposable held = BackgroundWork.Scope();
        RefactorOutcome made = await MakeLintFixAsync(context.Editors, document, fix, fix.Title, said => context.Say(said));
        if (made.Left.Count > 0)
        {
            context.Say(Refusals.NotAppliedEditedMeanwhile(fix.Title, made.Left));
        }
        else if ((made.Problem ?? made.Said) is { IsEmpty: false } said)
        {
            context.Say(said);
        }
    }

    /// <summary>A linter's fix made, for the window and an agent alike: the edit its action carries (asked of the server
    /// where it left it for later), then its command, which hands the window its edit (<see cref="ServerCommands"/>); refused
    /// where the tool no longer serves the file.</summary>
    internal static async Task<RefactorOutcome> MakeLintFixAsync(EditorArea editors, OpenDocument document, LintFix fix, string what, Action<string>? waiting)
    {
        if (fix.From.Lsp is null || !fix.From.Web.Tools.Contains(fix.From))
        {
            return RefactorOutcome.Refused($"{what} was not applied: {fix.From.Tool.Name} no longer checks {document.Name}");
        }
        JsonElement? edit;
        try
        {
            edit = await fix.From.EditOfAsync(fix);
        }
        catch (LanguageServerFailed failed)
        {
            return RefactorOutcome.Refused($"{what} was not applied: {failed.Reason}");
        }
        return await ServerCommands.RunAsync(editors, fix.Title, what, edit, run => fix.From.RunAsync(fix, run), null, null, waiting);
    }

    private async Task FixEslintProblems()
    {
        if (context.Editors.Active is not { } document)
        {
            return;
        }
        if (LintOf(document) is not { } lint)
        {
            context.Say($"ESLint does not check {document.Name}");
            return;
        }
        using (BackgroundWork.Scope())
        {
            if (await FixAll(document, lint, "Fix ESLint Problems") is { } changed)
            {
                context.Say(changed ? "ESLint: fixed what it can fix" : "ESLint: nothing to fix");
            }
        }
    }

    /// <summary>Every problem ESLint can fix in a file fixed, one undo step: whether the text changed, null when ESLint gave
    /// nothing or the edit no longer fits what was typed meanwhile (said in the status bar).</summary>
    private async Task<bool?> FixAll(OpenDocument document, ToolDocument lint, string what)
    {
        string before = document.Text.Snapshot.ToString();
        try
        {
            using Asked asked = WorkspaceEdits.Track(context.Editors);
            if (await lint.FixAllAsync() is not { } edit)
            {
                context.Say($"{what}: ESLint has nothing to give for {document.Name} yet");
                return null;
            }
            if (!await actions.ApplyWorkspaceAsync(edit, asked, what))
            {
                return null;
            }
            return document.Text.Snapshot.ToString() != before;
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(what, failed);
            return null;
        }
    }

    private async Task ReformatWithPrettier()
    {
        if (context.Editors.Active is not { } document || context.Web?.Invoke() is not { } web)
        {
            return;
        }
        using (BackgroundWork.Scope())
        {
            await Prettify(web, document, "Reformat with Prettier");
        }
    }

    /// <summary>What a save does first to a changed file, as Settings ask: <c>eslint --fix</c>, then Prettier. Each its
    /// own undoable edit. Whether the file is Prettier's to lay out on a save, whatever it made of it.</summary>
    public async Task<bool> BeforeSave(OpenDocument document)
    {
        if (context.Web?.Invoke() is not { } web || document.IsReadOnly)
        {
            return false;
        }
        if (LintOf(document) is { } lint && web.Tools.FixesOnSave)
        {
            await FixAll(document, lint, "Run eslint --fix on save");
        }
        // A file another formatter lays out is Reformat on save's, one formatter at most a file.
        if (!web.Prettier.OnSave(document.Path) || FormattersOf(web, document).Count > 0)
        {
            return false;
        }
        await Prettify(web, document, "Prettier on save");
        return true;
    }

    /// <summary>What came of handing a file to Prettier.</summary>
    internal enum Prettified
    {
        Formatted,
        Unchanged,
        Ignored,
        Unsupported,
        Failed,
    }

    /// <summary>A file formatted by its project's Prettier (<see cref="PrettifyAsync"/>), what came of it said in the status bar.</summary>
    private async Task<Prettified> Prettify(WebLanguages web, OpenDocument document, string what)
    {
        (Prettified outcome, string said) = await PrettifyAsync(web, document, what);
        context.Say(said);
        return outcome;
    }

    /// <summary>A file formatted by its project's Prettier as one undoable edit, only <paramref name="within"/> when given,
    /// only the lines that changed replaced, the caret where Prettier carried it, the file's own line breaks kept; what
    /// came of it.</summary>
    internal async Task<(Prettified Outcome, string Said)> PrettifyAsync(WebLanguages web, OpenDocument document, string what, TextRange? within = null)
    {
        if (CodeActions.Reformatted.ReadOnly(document, what) is { } refused)
        {
            return (Prettified.Failed, refused.Said);
        }
        string text = document.Text.Snapshot.ToString();
        int version = document.Text.Version;
        int caret = context.Editors.Active == document ? context.Editor.Selections.Primary.Caret : document.Selections.Primary.Caret;
        PrettierAnswer answer = await BackgroundWork.Run(() => web.Prettier.FormatAsync(document.Path, text, caret, within));
        if (answer.Ignored)
        {
            return (Prettified.Ignored, $"Prettier: file {document.Name} was ignored with .prettierignore");
        }
        if (answer.Unsupported)
        {
            return (Prettified.Unsupported, $"Prettier: file {document.Name} has unsupported type");
        }
        if (answer.Formatted is not { } formatted)
        {
            // A syntax error's first line says where it is ("Unexpected token (1:9)"); the code frame after it is for a console.
            return (Prettified.Failed, $"Prettier: {ChildResult.FirstLineOf(answer.Error ?? "it gave nothing back")}");
        }
        if (document.Text.Version != version)
        {
            return (Prettified.Failed, $"{what} was not applied: {document.Name} changed while Prettier formatted it");
        }
        (string after, int cursor) = InLineEndings(formatted, answer.Cursor, document.Text.LineEnding);
        IReadOnlyList<TextEdit> edits = LineDiff.Edits(text, after);
        if (edits.Count == 0)
        {
            return (Prettified.Unchanged, "Prettier: no lines changed. Content is already properly formatted");
        }
        int lines = LineDiff.Hunks(LineDiff.Lines(text), LineDiff.Lines(after)).Sum(h => Math.Max(h.OldCount, h.NewCount));
        SelectionSet carried = SelectionSet.Single(Math.Clamp(cursor, 0, after.Length));
        if (context.Editors.Active == document)
        {
            context.Editor.ApplyEdits(edits, carried);
        }
        else
        {
            document.Text.BreakUndoGroup();
            document.Text.Edit(edits, document.Selections, carried);
            document.Text.BreakUndoGroup();
        }
        return (Prettified.Formatted, $"Prettier: formatted {Plural.Of(lines, "line")}");
    }

    /// <summary>Prettier's text in the file's own line breaks (its endOfLine is LF unless a configuration says otherwise,
    /// and the file's separator is the status bar's to change), the caret's offset carried with them.</summary>
    private static (string Text, int Cursor) InLineEndings(string formatted, int cursor, string eol)
    {
        StringBuilder built = new(formatted.Length);
        int at = -1;
        for (int i = 0; i < formatted.Length;)
        {
            if (i == cursor)
            {
                at = built.Length;
            }
            if (formatted[i] is '\r' or '\n')
            {
                int length = formatted[i] == '\r' && i + 1 < formatted.Length && formatted[i + 1] == '\n' ? 2 : 1;
                built.Append(eol);
                i += length;
            }
            else
            {
                built.Append(formatted[i]);
                i++;
            }
        }
        return (built.ToString(), at < 0 ? built.Length : at);
    }
}
