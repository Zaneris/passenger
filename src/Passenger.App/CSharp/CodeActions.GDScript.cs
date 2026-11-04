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
    Move,
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
        GDScriptRefactoring.Move => "Move",
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
            case GDScriptRefactoring.Move:
                if (await GDScriptsAsync(document) is not ({ } scripts, { } own, _) || GDScriptRefactorings.MemberAt(scripts, own, selection.Start) is not { Node: GDScriptFunction } moving)
                {
                    context.Say($"{title} is not available here: put the caret on a function");
                    return;
                }
                popups.ShowModal(new ChoicePopup($"Move {moving.Named} to", [.. scripts.Where(s => s.Script != moving.Script).OrderBy(s => s.Script, StringComparer.Ordinal)
                    .Select(s => new Choice(s.Script, s.Tree.Class.Identifier?.Name ?? string.Empty, s.Script))]), selection.Start, title, async choice =>
                {
                    if (choice?.Value is string destination)
                    {
                        RefactorOutcome made = await GDScriptRefactorAsync(document, kind, selection, destination, null);
                        context.Say(made.Problem ?? made.Said);
                    }
                });
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
        GDScriptTarget? target = GDScriptRefactorings.MemberAt(scripts, own, offset);
        if (target is { Node: GDScriptSignal })
        {
            GDScriptRead signalling = scripts.First(s => s.Script == target.Script);
            return (new Signature(null, ChangeSignatureDialog.GDScriptSignalKind, Path.GetFileName(target.Script), string.Empty, target.Named, string.Empty,
                [.. GDScriptRefactorings.SignalParametersOf(signalling, target).Select(p => new Parameter(string.Empty, p.Type, p.Name, string.Empty))], false), null);
        }
        if (target is not { Node: GDScriptFunction function })
        {
            return (null, "put the caret on a function or a signal");
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
        int asked = target is not null && target.Script == own.Script ? target.Start : caret;
        IReadOnlyList<GDScriptPlace> elsewhere = target is null || folder is null
            || kind is not (GDScriptRefactoring.Inline or GDScriptRefactoring.SafeDelete or GDScriptRefactoring.ChangeSignature or GDScriptRefactoring.Move) ? []
            : await context.Languages.CSharp.GodotScriptElsewhereAsync(document, asked, followingCalls: kind == GDScriptRefactoring.ChangeSignature,
                withScenes: !(kind == GDScriptRefactoring.ChangeSignature && target.Node is GDScriptSignal));
        List<(string Path, IReadOnlyList<LspTextEdit> Edits)> beside = [];
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
                // An exported variable a scene sets is named there, by its property.
                IReadOnlyList<GDScriptPlace> sets = target is { Node: GDScriptVariable exported } && folder is not null
                    && exported.Annotations.Any(a => a.Name.StartsWith("@export", StringComparison.Ordinal))
                    ? await context.Languages.CSharp.GodotSetsAsync(document.Path, target.Script, target.Named) : [];
                made = target is null ? GDScriptRefactored.No("put the caret on a member of a script") : GDScriptRefactorings.SafeDelete(scripts, target, [.. elsewhere, .. sets]);
                break;
            case GDScriptRefactoring.Move:
                made = target is null ? GDScriptRefactored.No("put the caret on a function")
                    : GDScriptRefactorings.Move(scripts, target, name!, context.GodotScriptTypes?.Invoke() is { } types ? await types.AutoloadsAsync(document.Path) : new Dictionary<string, string>(), elsewhere);
                break;
            default:
                if (target is null)
                {
                    made = GDScriptRefactored.No("put the caret on a function or a signal");
                    break;
                }
                GDScriptRead declaring = scripts.First(s => s.Script == target.Script);
                bool signal = target.Node is GDScriptSignal;
                IReadOnlyList<GDScriptParameterChange> was = signal ? GDScriptRefactorings.SignalParametersOf(declaring, target) : GDScriptRefactorings.ParametersOf(declaring, target);
                IReadOnlyList<GDScriptParameterChange> asking = parameters ?? was;
                if (signal)
                {
                    (IReadOnlyList<GDScriptHandler> handlers, IReadOnlyList<GDScriptPlace> unfollowed) = folder is null ? ([], [])
                        : await context.Languages.CSharp.GodotSignalHandlersAsync(document.Path, target.Script, target.Named);
                    made = GDScriptRefactorings.ChangeSignalSignature(scripts, target, name ?? target.Named, asking, handlers, [.. elsewhere, .. unfollowed]);
                }
                else
                {
                    made = GDScriptRefactorings.ChangeSignature(scripts, target, name ?? target.Named, asking, elsewhere);
                }
                if (made.Refused is null && folder is not null)
                {
                    // C#'s calls and emits by the name, their arguments in the new order; then its name where it is renamed.
                    beside.AddRange(await context.Languages.CSharp.GodotScriptCallEditsAsync(document, asked, given => GDScriptRefactorings.Rearranged(given, was, asking)));
                    if (name is not null && name != target.Named)
                    {
                        beside.AddRange(await context.Languages.CSharp.GodotScriptRenameAsync(document, asked, name));
                    }
                }
                break;
        }
        return await ApplyGDScriptAsync(title, title, made, scripts, folder, document, beside);
    }

    /// <summary>A GDScript refactoring's edits made as one command named <paramref name="command"/>: every script it touches
    /// parsing clean after it, the files <paramref name="beside"/> the scripts (the scenes' connections, C# strings) with
    /// them; what it came to, or why not, said as <paramref name="title"/>'s.</summary>
    private async Task<RefactorOutcome> ApplyGDScriptAsync(string title, string command, GDScriptRefactored made, IReadOnlyList<GDScriptRead> scripts, string? folder,
        OpenDocument document, List<(string Path, IReadOnlyList<LspTextEdit> Edits)> beside, string? done = null)
    {
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
        foreach (IGrouping<string, (string Path, IReadOnlyList<LspTextEdit> Edits)> file in beside.GroupBy(b => b.Path, PathNames.Comparer))
        {
            string path = file.Key;
            List<LspTextEdit> edits = [.. file.SelectMany(f => f.Edits).DistinctBy(e => e.Range)];
            changes.Add(new XamlFileChange(path, text =>
            {
                ServerLines lines = ServerLines.Of(TextSnapshot.From(text));
                return [.. edits.Select(e => new TextEdit(lines.OffsetOf(e.Range.Start), lines.OffsetOf(e.Range.End) - lines.OffsetOf(e.Range.Start), e.NewText)).OrderBy(e => e.Start)];
            }));
            files.Add(new RefactoredFile(path, "changed", [.. edits.Select(e => e.Range.Start.Line + 1).Distinct()]));
        }
        if (await ApplyFileChangesAsync(command, changes) is { } failed)
        {
            return RefactorOutcome.Refused($"{title} was not applied: {failed}");
        }
        string said = done ?? $"{title} done";
        return new RefactorOutcome(files.Count > 1 ? $"{said} in {files.Count} files" : said, null, files, []);
    }


    /// <summary>Rename of a GDScript's member (an inner class's or an enum's value too) or local at an offset by Passenger's
    /// reading of every script of the project, the scenes and C# strings naming a member following it, as one command;
    /// refused for uses it can't see unless <paramref name="anyway"/>, offering to make it anyway
    /// (<see cref="RefactorOutcome.Anyway"/>); null where the name there is no member or local of a script's.</summary>
    internal async Task<RefactorOutcome?> GDScriptRenameAsync(OpenDocument document, int offset, string name, bool anyway = false)
    {
        if (!IsGDScript(document) || await GDScriptsAsync(document) is not ({ } scripts, { } own, var folder))
        {
            return null;
        }
        string old = TextNavigation.WordAt(document.Text.Snapshot, offset) is { Length: > 0 } word ? document.Text.Snapshot.GetText(word) : name;
        string title = "Rename";
        if (document.IsReadOnly)
        {
            return RefactorOutcome.Refused($"{title} was not applied: {Refusals.ReadOnly(document.Name)}");
        }
        List<(string Path, IReadOnlyList<LspTextEdit> Edits)> beside = [];
        GDScriptRefactored made;
        if (GDScriptRefactorings.DeclarationAt(scripts, own, offset) is { } target)
        {
            made = GDScriptRefactorings.Rename(scripts, target, name, anyway);
            if (made.Unseen)
            {
                TextSnapshot asked = document.Text.Snapshot;
                IReadOnlyList<string> unseen = Shown(scripts, made.Places);
                bool one = made.Places.Count == 1;
                return RefactorOutcome.Refused($"{title} was not applied: {made.Refused}") with
                {
                    Anyway = new FixConfirmed(
                        () => Task.FromResult(document.Text.Snapshot == asked ? $"Passenger can't see whether {(one ? "this place uses" : "these places use")} {old}:\n\n"
                            + $"{string.Join("\n", unseen)}\n\nRename it to {name} anyway, leaving {(one ? "it as it is" : "them as they are")}?" : null),
                        "Rename Anyway",
                        async _ => document.Text.Snapshot == asked && await GDScriptRenameAsync(document, offset, name, anyway: true) is { } forced ? forced
                            : RefactorOutcome.Refused($"{title} was not applied: the file changed, so it no longer fits")),
                };
            }
            if (made.Refused is null && folder is not null && !target.Nested)
            {
                // An override's family renames together, each declaration's scenes and C# with it.
                foreach (GDScriptTarget member in GDScriptRefactorings.FamilyOf(scripts, target))
                {
                    beside.AddRange(await BesideAsync(document, folder, member, d => context.Languages.CSharp.GodotScriptRenameAsync(d, member.Start, name)));
                }
            }
        }
        else if (GDScriptRefactorings.RenameLocal(own, offset, name) is { } local)
        {
            made = local;
        }
        else
        {
            return null;
        }
        RefactorOutcome renamed = await ApplyGDScriptAsync(title, $"Rename {old} to {name}", made, scripts, folder, document, beside, "Renamed");
        return renamed.Problem is null && made.Places.Count > 0
            ? renamed with { Said = $"{renamed.Said}, leaving {Plural.Of(made.Places.Count, "place")} it can't see as {(made.Places.Count == 1 ? "it is" : "they are")} ({string.Join("; ", Shown(scripts, made.Places))})" }
            : renamed;
    }

    /// <summary>Places in the scripts as a person reads them: each script's path and line, and what is there, a few at most.</summary>
    private static IReadOnlyList<string> Shown(IReadOnlyList<GDScriptRead> scripts, IReadOnlyList<GDScriptPlace> places)
    {
        List<string> shown = [.. places.Take(8).Select(p => scripts.FirstOrDefault(s => s.Script == p.Script) is { } read
            ? $"{p.Script.Replace("res://", string.Empty, StringComparison.Ordinal)}:{read.Text.AsSpan(0, Math.Min(p.Start, read.Text.Length)).Count('\n') + 1}: {p.What}"
            : $"{p.Script}: {p.What}")];
        if (places.Count > 8)
        {
            shown.Add($"and {Plural.Of(places.Count - 8, "more place")}");
        }
        return shown;
    }

    /// <summary>What a member's script's document answers beside the scripts, asked of the script declaring it, lent where
    /// no tab shows it.</summary>
    private async Task<T> BesideAsync<T>(OpenDocument document, string folder, GDScriptTarget target, Func<OpenDocument, Task<T>> ask)
    {
        if (GodotScenes.FileOf(folder, target.Script) is not { } file || PathNames.Same(file, document.Path))
        {
            return await ask(document);
        }
        (OpenDocument declaring, IDisposable hold) = context.Editors.Lend(file);
        using (hold)
        {
            return await ask(declaring);
        }
    }

    /// <summary>Find Usages of a GDScript's member or local at an offset by Passenger's reading of every script of the
    /// project, a member's scenes' connections and C# strings with them, as places for the Find window, with the places
    /// that may use it unseen; null where the name there is no member or local of a script's.</summary>
    internal async Task<(IReadOnlyList<LspLocation> Places, IReadOnlyList<GDScriptPlace> Unsure)?> GDScriptUsesAsync(OpenDocument document, int offset)
    {
        if (!IsGDScript(document) || await GDScriptsAsync(document) is not ({ } scripts, { } own, var folder))
        {
            return null;
        }
        GDScriptTarget? target = GDScriptRefactorings.DeclarationAt(scripts, own, offset);
        GDScriptNamePlaces? found = target is not null ? GDScriptRefactorings.PlacesOf(scripts, target) : GDScriptRefactorings.LocalPlacesOf(own, offset);
        if (found is null)
        {
            return null;
        }
        List<LspLocation> places = [];
        foreach (IGrouping<string, (string Script, int Start, int End)> script in found.Places.GroupBy(p => p.Script, StringComparer.Ordinal))
        {
            GDScriptRead read = scripts.First(s => s.Script == script.Key);
            string path = folder is not null && GodotScenes.FileOf(folder, read.Script) is { } file ? file : document.Path;
            TextSnapshot text = TextSnapshot.From(read.Text);
            foreach ((_, int start, int end) in script)
            {
                LspPosition from = ServerPlaces.ServerPositionOf(text, PositionOf(text, start));
                LspPosition to = ServerPlaces.ServerPositionOf(text, PositionOf(text, end));
                places.Add(new LspLocation(DocumentUris.FromPath(path), new LspRange(from, to)));
            }
        }
        if (target is { Nested: false } && folder is not null)
        {
            places.AddRange(await BesideAsync(document, folder, target, d => context.Languages.CSharp.GodotScriptUsesOfAsync(d, target.Start)));
        }
        return (places, found.Unsure);
    }

    private static TextPosition PositionOf(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length));
        return new TextPosition(line, Math.Clamp(offset, 0, text.Length) - text.GetLineStart(line));
    }

    /// <summary>Rename at the caret of a GDScript: its box, the name renamed by Passenger's reading where it is a member or
    /// local of a script's, else by Godot's server where it serves, else said why not.</summary>
    public void ShowGDScriptRename()
    {
        if (context.Editors.Active is not { } document)
        {
            return;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        TextRange word = TextNavigation.WordAt(text, Editor.Selections.Primary.Caret);
        if (word.Length == 0 || !TextNavigation.IsWordChar(text[word.Start]))
        {
            return;
        }
        RenameBox box = new(text.GetText(word));
        ChangeTracker opened = new(Editor.Document);
        popups.ShowModal(box, word.Start, async name =>
        {
            int at = opened.MapOffset(word.Start);
            if (name is null)
            {
                return;
            }
            Anchor held = HeldAt(document, at);
            try
            {
                RefactorOutcome made = await context.PatientlyAsync(AppActions.Rename, patience => RenameSymbolAsync(document, held, name, false, patience));
                Told("Rename", made);
            }
            catch (LanguageServerFailed failed)
            {
                context.Failed("Rename", failed);
            }
            finally
            {
                held.Remove();
            }
        });
        popups.WhenClosed(opened.Dispose);
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
