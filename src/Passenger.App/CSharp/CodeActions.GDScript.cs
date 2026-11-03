using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A GDScript refactoring, as the refactor menu's commands name them.</summary>
internal enum GDScriptRefactoring
{
    ExtractVariable,
    ExtractFunction,
    IntroduceParameter,
    Inline,
    SafeDelete,
    ChangeSignature,
}

/// <summary>GDScript's refactorings (<see cref="GDScriptRefactorings"/>) over Passenger's reading of every script of the
/// project, the open ones as the editor has them: a name asked in a box for what one makes, Change Signature's dialog,
/// the scenes' connections and the C# strings naming a member followed through a rename and refusing what they would
/// leave wrong; each made as one command of global Undo through the write gate, every script it touches parsing clean
/// first. The window's commands and an agent's refactor tool alike.</summary>
internal sealed partial class CodeActions
{
    public static bool IsGDScript(OpenDocument? file) => file is not null && Godot.GodotScriptTypes.IsScript(file.Path);

    private static string TitleOf(GDScriptRefactoring kind) => kind switch
    {
        GDScriptRefactoring.ExtractVariable => "Extract Variable",
        GDScriptRefactoring.ExtractFunction => "Extract Function",
        GDScriptRefactoring.IntroduceParameter => "Introduce Parameter",
        GDScriptRefactoring.Inline => "Inline",
        GDScriptRefactoring.SafeDelete => "Safe Delete",
        _ => "Change Signature",
    };

    /// <summary>A GDScript refactoring at the caret or selection of the file shown, as its command runs it.</summary>
    public async Task GDScriptRefactor(GDScriptRefactoring kind)
    {
        if (context.Editors.Active is not { } document)
        {
            return;
        }
        TextRange selection = Editor.Selections.Primary.Range;
        string title = TitleOf(kind);
        switch (kind)
        {
            case GDScriptRefactoring.ExtractVariable or GDScriptRefactoring.ExtractFunction or GDScriptRefactoring.IntroduceParameter:
                RenameBox box = new(kind == GDScriptRefactoring.ExtractFunction ? "new_function" : "value", title);
                ChangeTracker opened = new(Editor.Document);
                popups.ShowModal(box, selection.Start, async name =>
                {
                    TextRange mapped = TextRange.FromBounds(opened.MapOffset(selection.Start), opened.MapOffset(selection.End));
                    if (name is not null)
                    {
                        RefactorOutcome made = await GDScriptRefactorAsync(document, kind, mapped, name, null);
                        context.Say(made.Problem ?? made.Said);
                    }
                });
                popups.WhenClosed(opened.Dispose);
                return;
            case GDScriptRefactoring.ChangeSignature:
                (Signature? signature, string? why) = await GDScriptSignatureAsync(document, selection.Start);
                if (signature is null)
                {
                    context.Say($"{title} is not available here: {why}");
                    return;
                }
                await ShowAsync(new ChangeSignatureDialog(signature, (name, _, parameters) => Shown(GDScriptRefactorAsync(document, kind, selection, name,
                    [.. parameters.Select(p => new GDScriptParameterChange(p.From, p.Name, p.Type, p.DefaultValue, p.CallValue))]))));
                return;
            default:
                using (BackgroundWork.Scope())
                {
                    RefactorOutcome made = await GDScriptRefactorAsync(document, kind, selection, null, null);
                    context.Say(made.Problem ?? made.Said);
                }
                return;
        }
    }

    /// <summary>The project's scripts as a refactoring reads them, the open ones as the editor has them, and the file's own;
    /// a script outside a Godot project alone.</summary>
    private async Task<(IReadOnlyList<GDScriptRead> Scripts, GDScriptRead Own, string? Folder)?> GDScriptsAsync(OpenDocument document)
    {
        if (context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return null;
        }
        Dictionary<string, string> open = new(PathNames.Comparer);
        foreach (OpenDocument script in context.Editors.Documents.Where(IsGDScript))
        {
            open[script.Path] = script.Text.Snapshot.ToString();
        }
        string? folder = context.GodotOf(document.Path)?.Folder;
        if (folder is not null && await types.ProjectReadAsync(document.Path, open) is { } scripts && GodotScenes.ResPathOf(folder, document.Path) is { } res
            && scripts.FirstOrDefault(s => s.Script == res) is { } own)
        {
            return (scripts, own, folder);
        }
        if (await types.TypedAsync(document) is not { } typed)
        {
            return null;
        }
        GDScriptRead alone = new(typed.Analysis.Path, typed.Analysis);
        return ([alone], alone, null);
    }

    /// <summary>Change Signature's dialog's model of the function at an offset of a script: its name, return type and
    /// parameters as written; else why there is none.</summary>
    internal async Task<(Signature? Signature, string? Why)> GDScriptSignatureAsync(OpenDocument document, int offset)
    {
        if (await GDScriptsAsync(document) is not ({ } scripts, { } own, _))
        {
            return (null, "the script is not read yet");
        }
        if (GDScriptRefactorings.MemberAt(scripts, own, offset) is not { Node: GDScriptFunction function } target)
        {
            return (null, "put the caret on a function");
        }
        GDScriptRead declaring = scripts.First(s => s.Script == target.Script);
        string returns = function.ReturnType is { } written ? declaring.Text[(target.Offset + written.Start)..(target.Offset + written.End)] : string.Empty;
        return (new Signature(null, ChangeSignatureDialog.GDScriptKind, Path.GetFileName(target.Script), string.Empty, target.Named, returns,
            [.. GDScriptRefactorings.ParametersOf(declaring, target).Select(p => new Parameter(string.Empty, p.Type, p.Name, p.Default))], false), null);
    }

    /// <summary>A GDScript refactoring made as one command: the scripts it changes (and, for Change Signature's new name,
    /// the scenes' connections and C# strings naming the function) once every script it touches parses clean; what it
    /// came to, or why not.</summary>
    internal async Task<RefactorOutcome> GDScriptRefactorAsync(OpenDocument document, GDScriptRefactoring kind, TextRange selection, string? name,
        IReadOnlyList<GDScriptParameterChange>? parameters)
    {
        string title = TitleOf(kind);
        if (document.IsReadOnly)
        {
            return RefactorOutcome.Refused($"{title} was not applied: {Refusals.ReadOnly(document.Name)}");
        }
        if (await GDScriptsAsync(document) is not ({ } scripts, { } own, var folder))
        {
            return RefactorOutcome.Refused($"{title} was not applied: the script is not read yet");
        }
        int caret = selection.Start;
        GDScriptTarget? target = GDScriptRefactorings.MemberAt(scripts, own, caret);
        // A member of this script is asked about at its own name; one of another, at the name here.
        IReadOnlyList<GDScriptPlace> elsewhere = target is null || folder is null || kind is not (GDScriptRefactoring.Inline or GDScriptRefactoring.SafeDelete or GDScriptRefactoring.ChangeSignature) ? []
            : await context.Languages.CSharp.GodotScriptElsewhereAsync(document, target.Script == own.Script ? target.Start : caret);
        IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)> beside = [];
        GDScriptRefactored made;
        switch (kind)
        {
            case GDScriptRefactoring.ExtractVariable:
                made = GDScriptRefactorings.ExtractVariable(own.Analysis, selection, name!);
                break;
            case GDScriptRefactoring.ExtractFunction:
                made = GDScriptRefactorings.ExtractFunction(own.Analysis, Lines(own.Text, selection), name!);
                break;
            case GDScriptRefactoring.IntroduceParameter:
                made = GDScriptRefactorings.IntroduceParameter(own.Analysis, selection, name!);
                break;
            case GDScriptRefactoring.Inline:
                made = GDScriptRefactorings.InlineVariable(own.Analysis, caret);
                if (made.Refused is not null && made.Places.Count == 0 && target is { Node: GDScriptFunction })
                {
                    made = GDScriptRefactorings.InlineFunction(scripts, target, elsewhere);
                }
                break;
            case GDScriptRefactoring.SafeDelete:
                made = target is null ? GDScriptRefactored.No("put the caret on a member of a script") : GDScriptRefactorings.SafeDelete(scripts, target, elsewhere);
                break;
            default:
                if (target is null)
                {
                    made = GDScriptRefactored.No("put the caret on a function");
                    break;
                }
                made = GDScriptRefactorings.ChangeSignature(scripts, target, name ?? target.Named, parameters ?? GDScriptRefactorings.ParametersOf(own, target), elsewhere);
                if (made.Refused is null && name is not null && name != target.Named && folder is not null)
                {
                    beside = await context.Languages.CSharp.GodotScriptRenameAsync(document, target.Script == own.Script ? target.Start : caret, name);
                }
                break;
        }
        if (made.Refused is { } refused)
        {
            return RefactorOutcome.Refused($"{title} was not applied: {refused}");
        }
        // Every script it touches must read clean after it.
        List<XamlFileChange> changes = [];
        List<RefactoredFile> files = [];
        foreach (GDScriptEdits edits in made.Scripts)
        {
            GDScriptRead read = scripts.First(s => s.Script == edits.Script);
            string after = TextSnapshot.From(read.Text).Apply(edits.Edits).ToString();
            if (GDScriptTree.Parse(after).Errors is [{ } error, ..] && read.Tree.Errors.Count == 0)
            {
                return RefactorOutcome.Refused($"{title} was not applied: it would leave {Path.GetFileName(read.Script)} not parsing ({error.Message})");
            }
            string path = folder is not null && GodotScenes.FileOf(folder, read.Script) is { } file ? file : document.Path;
            string found = read.Text;
            changes.Add(new XamlFileChange(path, text => text == found ? edits.Edits : []));
            TextSnapshot before = TextSnapshot.From(found);
            files.Add(new RefactoredFile(path, "changed", [.. edits.Edits.Select(e => before.GetLineFromOffset(e.Start) + 1).Distinct()]));
        }
        foreach ((string path, IReadOnlyList<LspTextEdit> edits) in beside)
        {
            changes.Add(new XamlFileChange(path, text =>
            {
                ServerLines lines = ServerLines.Of(TextSnapshot.From(text));
                return [.. edits.Select(e => new TextEdit(lines.OffsetOf(e.Range.Start), lines.OffsetOf(e.Range.End) - lines.OffsetOf(e.Range.Start), e.NewText)).OrderBy(e => e.Start)];
            }));
            files.Add(new RefactoredFile(path, "changed", [.. edits.Select(e => e.Range.Start.Line + 1).Distinct()]));
        }
        if (await ApplyFileChangesAsync(title, changes) is { } failed)
        {
            return RefactorOutcome.Refused($"{title} was not applied: {failed}");
        }
        return new RefactorOutcome(files.Count > 1 ? $"{title} done in {files.Count} files" : $"{title} done", null, files, []);
    }

    /// <summary>A selection taken to the whole lines it touches, as Extract Function takes statements.</summary>
    private static TextRange Lines(string text, TextRange selection)
    {
        int start = selection.Start <= 0 ? 0 : text.LastIndexOf('\n', selection.Start - 1) + 1;
        int end = selection.End;
        if (end > start && end <= text.Length && (end == text.Length || text[end - 1] == '\n'))
        {
            return TextRange.FromBounds(start, end);
        }
        int newline = text.IndexOf('\n', Math.Min(end, text.Length - 1 < 0 ? 0 : end));
        return TextRange.FromBounds(start, newline < 0 ? text.Length : newline);
    }
}
