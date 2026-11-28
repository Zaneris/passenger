using Passenger.App.DependencyMap;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>The fix of a use breaking the folder's layer rules (<see cref="CSharpDocument.LayerFindingsAsync"/>, a project
/// file's reference too): the layer of the code using let use the layer of the code used, in passenger-layers.json, one
/// command of global undo.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>The fixes of a C# file's warnings of broken layer rules on an offset's line, each pair of layers once.</summary>
    public IReadOnlyList<EditorFix> LayerFixes(OpenDocument file, int offset) =>
        context.CSharp?.For(file) is { Lsp: { } lsp } features && context.CSharp.Layers is { } rules
            ? LetUseFixes(rules, features.LayersBrokenOn(lsp.PositionOf(offset).Line))
            : [];

    /// <summary>The fixes letting each layer use another where those break the rules at the ranges given, each pair once.</summary>
    public IReadOnlyList<EditorFix> LetUseFixes(LayerRules rules, IReadOnlyList<(TextRange Range, string From, string To)> broken) =>
        [.. broken.DistinctBy(m => (m.From, m.To)).Select(m => LetUseFix(rules, m.Range, m.From, m.To))];

    private EditorFix LetUseFix(LayerRules rules, TextRange range, string from, string to) =>
        new($"Let {from} Use {to} in {LayerRules.FileName}", "a layer rule broken", range, string.Empty)
        {
            Elsewhere = (_, _) => LetUseAsync(rules, from, to),
        };

    /// <summary>The rules' file written with one layer let use another, as the editor has it where it is open; refused where
    /// the file no longer has the layer, or it may already. The rules are read again once it is written.</summary>
    private async Task<RefactorOutcome> LetUseAsync(LayerRules rules, string from, string to)
    {
        string path = LayersFile.PathIn(rules.Folder);
        OpenTexts texts = context.Editors.Texts();
        IReadOnlyList<TextEdit> Edits(string text) => LayersFile.LetUse(text, from, to) is { } made ? [TextEdit.Between(text, made)] : [];
        if (await BackgroundWork.Run(() => texts.Text(path) is { } text && Edits(text).Count > 0) is false)
        {
            return RefactorOutcome.Refused($"{LayerRules.FileName} has no layer {from} that may not use {to}");
        }
        string title = $"Let {from} Use {to}";
        if (await actions.ApplyFileChangesAsync(title, [new XamlFileChange(path, Edits)]) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        context.CSharp?.ReadLayersAgain?.Invoke();
        return new RefactorOutcome($"{LayerRules.FileName} lets {from} use {to}", null, [new RefactoredFile(path, RefactoredChange.Changed, [])], []);
    }
}
