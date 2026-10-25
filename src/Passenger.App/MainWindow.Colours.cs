using Avalonia;
using Avalonia.Controls;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App;

/// <summary>The gutter's colour swatches: a click on one opens the colour picker beside it, and the colour chosen is
/// written back as the colour was written (a hex value, a name, a C# call's arguments), where it is written: the line's
/// own value, or for a resource a brush names, its definition in another file. One undo step.</summary>
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
        ColourPicker opened = new(use.Colour);
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
                WriteColour(document, use, chosen);
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

    /// <summary>A colour written where a swatch's colour is, in its form, unless that text changed meanwhile.</summary>
    private void WriteColour(OpenDocument document, ColourUse use, Argb colour)
    {
        string path = use.WritePath ?? document.Path;
        OpenDocument? open = Editors.Find(path);
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
            TextSnapshot snapshot = TextSnapshot.From(text);
            TextPosition from = snapshot.GetPosition(range.Start);
            TextPosition to = snapshot.GetPosition(range.End);
            Lsp.LspTextEdit edit = new(new Lsp.LspRange(new Lsp.LspPosition(from.Line, from.Column), new Lsp.LspPosition(to.Line, to.Column)), replacement);
            System.Text.Json.JsonElement change = System.Text.Json.JsonSerializer.SerializeToElement(
                new { changes = new Dictionary<string, List<Lsp.LspTextEdit>> { [Lsp.DocumentUris.FromPath(path)] = [edit] } }, Lsp.Json.Options);
            TypeRename.Apply(Editors, change, [], $"Set colour to {colour.Hex}", null, []);
        }
        status.Text = $"Colour set to {colour.Hex}{(path != document.Path ? $" in {Path.GetFileName(path)}" : string.Empty)}";
    }
}
