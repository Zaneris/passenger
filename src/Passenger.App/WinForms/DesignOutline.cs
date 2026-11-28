using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.WinForms;

/// <summary>A form's components as the Structure window outlines them while its design is shown: the form, then each
/// component under the one holding it, by its field and its type, those in the tray under the form after its controls. Its
/// rows hold no line of the file, so the caret chooses none; the design's choice does.</summary>
internal sealed class DesignOutline(FormDesign design) : FileOutline
{
    public override Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
    {
        IReadOnlyList<DesignedBox> boxes = design.Components;
        if (boxes.FirstOrDefault(b => b.Field.Length == 0) is not { } root)
        {
            return Task.FromResult(Outline<SymbolRow>.Empty);
        }
        string named = design.Files.Class[(design.Files.Class.LastIndexOfAny(['.', '+']) + 1)..];
        ILookup<string, DesignedBox> under = boxes.Where(b => b.Field.Length > 0).ToLookup(b => b.Parent ?? string.Empty, StringComparer.Ordinal);
        return Task.FromResult(Outline<SymbolRow>.Of<DesignedBox>([root], box => [.. under[box.Field]],
            (box, depth) => (new SymbolRow(box.Field.Length == 0 ? named : box.Field, $": {box.Type}", null, SymbolRow.ComponentKind, path, default, depth), -1, -2),
            before, cancel));
    }

    /// <summary>The field of a row of this outline (the form's empty), or null for a row of any other.</summary>
    public static string? FieldOf(SymbolRow row) => row.Kind != SymbolRow.ComponentKind ? null : row.Depth == 0 ? string.Empty : row.Name;
}
