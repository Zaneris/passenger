using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>The fixes of Godot's checks, Passenger's own and listed first, in a C# file and in an open scene alike: a path or
/// name changed to one near it that the scenes answer; a handler, an exported property or a GetNode call's type made by
/// Passenger's extension in the script's code (<see cref="GodotSource"/>); a GDScript's handler made last in its script
/// (<see cref="GodotWrites.WithFunction"/>); and a name the project's files lack defined where Godot looks for it, asked first
/// with the line it writes. Each one command of global undo, worked out again as it is chosen.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>The fixes of the Godot marks on an offset's line of a C# file or an open scene (<paramref name="marks"/>'s).</summary>
    public IReadOnlyList<EditorFix> GodotFixes(OpenDocument file, int offset, GodotSceneMarks marks)
    {
        Func<int?, IReadOnlyList<(TextRange Range, string Message, GodotOffer Offer)>> offers;
        if (GodotFile.IsText(file.Path))
        {
            offers = at => marks.OffersOn(file, at);
        }
        else if (context.CSharp?.For(file) is { Lsp: not null })
        {
            offers = at => context.CSharp?.For(file) is { Lsp: { } now } features ? features.GodotOffersOn(at is { } on ? now.PositionOf(on).Line : null) : [];
        }
        else
        {
            return [];
        }
        TextSnapshot text = file.Text.Snapshot;
        return [.. offers(offset).OrderBy(o => o.Range.Start <= offset && offset <= o.Range.End ? 0 : 1).DistinctBy(o => o.Message)
            .SelectMany(o => Fixes(o.Range, o.Message, o.Offer))];

        IEnumerable<EditorFix> Fixes(TextRange range, string message, GodotOffer offer)
        {
            string written = range.End <= text.Length ? text.GetText(range.Start, range.Length) : string.Empty;
            foreach (string near in offer.Near)
            {
                yield return NearFix(range, message, written, near);
            }
            if (offer.Make is { } make)
            {
                yield return MakeFix(range, message, make, offer.Shown ?? make.Name);
            }
            if (offer.Function is { } function)
            {
                yield return FunctionFix(range, message, function);
            }
            foreach (GodotDefine define in offer.Defines)
            {
                yield return DefineFix(range, message, define);
            }
        }

        // The finding's mark as it now stands, while it does.
        (TextRange Range, string Message, GodotOffer Offer)? Now(string message, TextRange was) =>
            offers(null).Where(o => o.Message == message).OrderBy(o => Math.Abs(o.Range.Start - was.Start)).Cast<(TextRange, string, GodotOffer)?>().FirstOrDefault();

        EditorFix NearFix(TextRange range, string message, string written, string near) =>
            new($"Change to '{near}'", "a name the Godot project answers", range, near)
            {
                Again = now => Now(message, range) is ({ } current, _, { } offer) && offer.Near.Contains(near) && current.End <= now.Length
                    && now.GetText(current.Start, current.Length) == written ? NearFix(current, message, written, near) : null,
            };

        EditorFix MakeFix(TextRange range, string message, GodotFixRequest make, string shown)
        {
            string type = GodotChecks.TypeShown(make.Script);
            (string title, string detail, string done) = make.Kind switch
            {
                GodotFixKind.Handler => ($"Create method {shown} in {type}", "a method a signal is connected to", $"Created {make.Name} in {type}"),
                GodotFixKind.Export => ($"Create exported property {shown} in {type}", "a property a scene sets", $"Created {make.Name} in {type}"),
                _ => ($"Change type to {shown}", "the type of the scene's node", $"Changed the type to {shown}"),
            };
            return new EditorFix(title, detail, range, string.Empty)
            {
                Again = _ => Now(message, range) is ({ } current, _, { Make: { } again }) ? MakeFix(current, message, again, shown) : null,
                Elsewhere = (_, patience) => MakeGodotAsync(title, make, done, patience),
            };
        }

        EditorFix FunctionFix(TextRange range, string message, GodotScriptFunction function)
        {
            string title = $"Create function {function.Name}({string.Join(", ", function.Parameters)}) in {function.Script.Split('/')[^1]}";
            return new EditorFix(title, "a function a signal is connected to", range, string.Empty)
            {
                Again = _ => Now(message, range) is ({ } current, _, { Function: { } again }) && again.Script == function.Script && again.Name == function.Name
                    && again.Parameters.SequenceEqual(function.Parameters) ? FunctionFix(current, message, function) : null,
                Elsewhere = (_, _) => MakeFunctionAsync(file, title, function),
            };
        }

        EditorFix DefineFix(TextRange range, string message, GodotDefine define)
        {
            string title = define.Kind switch
            {
                GodotStringKind.Action => $"Add input action '{define.Name}' to project.godot…",
                GodotStringKind.Autoload => $"Add autoload '{define.Name}' of {define.Value} to project.godot…",
                _ => $"Add group '{define.Name}' to {define.NodeName} in {define.File.Split('/')[^1]}…",
            };
            return new EditorFix(title, $"a {GodotChecks.What(define.Kind)} the Godot project lacks", range, string.Empty)
            {
                Again = _ => Now(message, range) is ({ } current, _, { } offer) && offer.Defines.Contains(define) ? DefineFix(current, message, define) : null,
                Confirms = new FixConfirmed(() => DefineQuestionAsync(file, define), "Add", _ => DefineAsync(file, define)),
            };
        }
    }

    /// <summary>A member made, or a GetNode call's type changed, in a script's code by Passenger's extension, as one command:
    /// through the file's editor where it is open, else as the server read it.</summary>
    private async Task<RefactorOutcome> MakeGodotAsync(string title, GodotFixRequest make, string done, Patience? patience)
    {
        if (context.CSharp is not { } csharp)
        {
            return RefactorOutcome.Refused($"{title} was not applied: {Refusals.CSharpNotRunning}");
        }
        RefactorOutcome made = context.Editors.Documents.FirstOrDefault(d => PathNames.Same(d.Path, PathNames.Canonical(make.Path))) is { } open && csharp.For(open) is { Lsp: not null }
            ? await actions.MakeAsync(title, open, 0, _ => csharp.GodotFixAsync(make, CancellationToken.None), _ => done, patience)
            : await actions.MakeAsync(title, () => csharp.GodotFixAsync(make, CancellationToken.None), _ => done, patience);
        foreach (OpenDocument document in context.Editors.Documents)
        {
            csharp.For(document)?.RefreshDiagnostics();
        }
        return made;
    }

    /// <summary>A GDScript's function made last in its script, as one command, in its text as the editor has it when open;
    /// refused once the script declares it.</summary>
    private async Task<RefactorOutcome> MakeFunctionAsync(OpenDocument file, string title, GodotScriptFunction function)
    {
        if (context.GodotOf(file.Path) is not (_, { } folder) || GodotResPaths.FileOf(folder, function.Script) is not { } path)
        {
            return RefactorOutcome.Refused($"{title} was not applied: {function.Script} is not of the open folder's Godot project");
        }
        string shown = Path.GetFileName(path);
        if (await actions.ApplyFileChangesAsync(title, [new XamlFileChange(path, now => GodotScriptDeclarations.Read(now).Member(GodotScriptKind.Function, function.Name) is null
            ? [TextEdit.Between(now, GodotWrites.WithFunction(now, function.Name, function.Parameters))] : [])]) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        return new RefactorOutcome($"Created {function.Name} in {shown}", null, [new RefactoredFile(path, "changed", [])], []);
    }

    /// <summary>What a name's definition asks before it is written: where it goes and the line it writes, as the file now
    /// stands; null once the file defines it, or has no place for it.</summary>
    private async Task<string?> DefineQuestionAsync(OpenDocument file, GodotDefine define)
    {
        if (await DefinedAsync(file, define) is not ({ } path, { } was, _))
        {
            return null;
        }
        string line = define.Kind == GodotStringKind.Group ? GodotWrites.GroupLine(was, Start(was, define) ?? 0, define.Name) ?? string.Empty
            : $"{GodotWrites.SettingKey(define.Name)}={SettingValue(define, GodotWrites.NewlineOf(was))}";
        string where = define.Kind == GodotStringKind.Group ? $"{define.NodeName}'s header in {Path.GetFileName(path)}" : $"project.godot's [{define.Section}]";
        return $"{where} becomes:\n\n{line.Replace("\r\n", "\n", StringComparison.Ordinal)}";
    }

    /// <summary>A name defined where Godot looks for it, as one command: project.godot's setting, or the node's group in its
    /// scene, written in the file's text as the editor has it when open; refused once the file defines it.</summary>
    private async Task<RefactorOutcome> DefineAsync(OpenDocument file, GodotDefine define)
    {
        if (await DefinedAsync(file, define) is not ({ } path, _, _))
        {
            return RefactorOutcome.Refused($"'{define.Name}' was not added: {define.File.Split('/')[^1]} defines it already, or has no place for it");
        }
        string title = $"Add {GodotChecks.What(define.Kind)} '{define.Name}'";
        if (await actions.ApplyFileChangesAsync(title, [new XamlFileChange(path, now => Written(now, define) is { } then && then != now ? [TextEdit.Between(now, then)] : [])]) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        context.CSharp?.For(file)?.RefreshDiagnostics();
        string where = define.Kind == GodotStringKind.Group ? $"{define.NodeName} in {Path.GetFileName(path)}" : $"project.godot's [{define.Section}]";
        return new RefactorOutcome($"Added {GodotChecks.What(define.Kind)} '{define.Name}' to {where}", null, [new RefactoredFile(path, "changed", [])], []);
    }

    /// <summary>The file a definition is written in, its text as the editor has it when open, and the text it becomes; null
    /// where it is not of the open folder's Godot project, cannot be read, or defines the name already.</summary>
    private async Task<(string Path, string Was, string Now)?> DefinedAsync(OpenDocument file, GodotDefine define)
    {
        if (context.CSharp?.GodotOf?.Invoke(file.Path) is not { } folder || GodotResPaths.FileOf(folder, define.File) is not { } path)
        {
            return null;
        }
        OpenTexts texts = context.Editors.Texts();
        return await BackgroundWork.Run(() => texts.Text(path) is { } was && Written(was, define) is { } now && now != was ? (path, was, now) : ((string, string, string)?)null);
    }

    /// <summary>A file's text with a name defined in it (<see cref="GodotWrites"/>); null where it is so already, or has no place.</summary>
    private static string? Written(string text, GodotDefine define) => define.Kind == GodotStringKind.Group
        ? Start(text, define) is { } start ? GodotWrites.WithGroup(text, start, define.Name) : null
        : define.Section is { } section ? GodotWrites.WithSetting(text, section, GodotWrites.SettingKey(define.Name), SettingValue(define, GodotWrites.NewlineOf(text))) : null;

    /// <summary>Where the section of a group's node starts in its scene's text, found by its path in the scene.</summary>
    private static int? Start(string text, GodotDefine define) =>
        GodotFile.Parse(text).Nodes.FirstOrDefault(n => n.PathInScene == define.NodePath)?.Section.Start;

    private static string SettingValue(GodotDefine define, string newline) =>
        define.Kind == GodotStringKind.Action ? GodotWrites.ActionValue(newline) : GodotWrites.AutoloadValue(define.Value ?? string.Empty);
}
