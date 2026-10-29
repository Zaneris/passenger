using Avalonia;
using Avalonia.Controls;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App;

/// <summary>The gutter's colour swatches: a click on one opens the colour picker beside it, and the colour chosen is
/// written back as the colour was written (a hex value, a name, a C# call's arguments), where it is written: the line's
/// own value, or for a resource a brush names, its definition in another file. One undo step. Where that is a file shown
/// read-only, the picker shows the colour with Choose greyed, saying why.</summary>
public partial class MainWindow
{
    private ColourPicker? picker;

    /// <summary>The colour the open picker has, as hex; null while none is open.</summary>
    public string? PickingColour => picker?.Colour.Hex;

    /// <summary>A press on a line's swatch opens the picker for it; whether the press was on one.</summary>
    private bool PickColour(CodeEditor editor, OpenDocument document, GutterClick click)
    {
        if (click.Context || document.ColourMarks.FirstOrDefault(m => m.Line == click.Line) is not { Data: ColourUse use })
        {
            return false;
        }
        if (use.WriteRange is null)
        {
            status.Text = "A brush's own colour cannot be changed here";
            return true;
        }
        ClosePicker();
        ColourPicker opened = new(use.Colour, Unwritable(document, use));
        picker = opened;
        Canvas layer = Editors.AssistLayer;
        layer.Children.Add(opened);
        Point at = editor.TranslatePoint(click.Position, layer) ?? click.Position;
        opened.Measure(Size.Infinity);
        Canvas.SetLeft(opened, Math.Clamp(at.X + 12, 0, Math.Max(0, layer.Bounds.Width - opened.DesiredSize.Width - 8)));
        Canvas.SetTop(opened, Math.Clamp(at.Y + 12, 0, Math.Max(0, layer.Bounds.Height - opened.DesiredSize.Height - 8)));
        opened.Chosen += (_, colour) =>
        {
            ClosePicker();
            editor.Focus();
            if (colour is { } chosen)
            {
                IdeErrors.Observe(WriteColourAsync(document, use, chosen));
            }
        };
        return true;
    }

    private void ClosePicker()
    {
        if (picker is not null)
        {
            Editors.AssistLayer.Children.Remove(picker);
            picker = null;
        }
    }

    /// <summary>Why a swatch's colour cannot be written where it is: that file is shown read-only; null when it can.</summary>
    private string? Unwritable(OpenDocument document, ColourUse use) =>
        Editors.Find(use.WritePath ?? document.Path) is { IsReadOnly: true } open ? Refusals.ReadOnly(open.Name) : null;

    /// <summary>A colour written where a swatch's colour is, in its form, unless that text changed meanwhile; in a file not
    /// open, said once the write has landed.</summary>
    private async Task WriteColourAsync(OpenDocument document, ColourUse use, Argb colour)
    {
        string path = use.WritePath ?? document.Path;
        OpenDocument? open = Editors.Find(path);
        if (Unwritable(document, use) is { } why)
        {
            status.Text = $"{why}: the colour is left as it was";
            return;
        }
        if (Editors.TextOf(path)?.ToString() is not { } text)
        {
            status.Text = $"{Path.GetFileName(path)} could not be read";
            return;
        }
        TextRange range = use.WriteRange!.Value;
        string? written = range.End <= text.Length ? text[range.Start..range.End] : null;
        if (written is null || (use.Form == ColourForm.Text && KnownColours.Parse(written) != use.Colour))
        {
            status.Text = "The colour was edited meanwhile: choose it again";
            return;
        }
        string replacement = Colours.Written(use, colour, written);
        if (open is not null)
        {
            WorkspaceEdits.ApplyTo(Editors, open, [new TextEdit(range.Start, range.Length, replacement)]);
        }
        else
        {
            List<Lsp.LspTextEdit> edits = ServerPlaces.EditsOf(TextSnapshot.From(text), [new TextEdit(range.Start, range.Length, replacement)]);
            System.Text.Json.JsonElement change = System.Text.Json.JsonSerializer.SerializeToElement(
                new { changes = new Dictionary<string, List<Lsp.LspTextEdit>> { [Lsp.DocumentUris.FromPath(path)] = edits } }, Lsp.Json.Options);
            string name = $"Set colour to {colour.Hex}";
            using CommandHistory.Recording recording = Editors.Commands.Record(name);
            try
            {
                TypeRename.Apply(Editors, change, [], null, [], recording);
            }
            catch (WorkspaceEditFailed failed)
            {
                status.Text = $"The colour could not be set: {failed.Message}";
                return;
            }
            if (await recording.LandedAsync(waiting => status.Text = waiting) is [(string unwritten, Exception refused), ..])
            {
                status.Text = $"The colour could not be set: {WorkspaceEdits.Unwritten(unwritten, refused)}";
                return;
            }
        }
        status.Text = $"Colour set to {colour.Hex}{(path != document.Path ? $" in {Path.GetFileName(path)}" : string.Empty)}";
    }
}
