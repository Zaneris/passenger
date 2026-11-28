using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Lsp;
using Passenger.Profiler;
using Passenger.Text;

namespace Passenger.App.LiveTree;

/// <summary>The places a running app reports of its XAML (an element's XamlSourceInfo, a style's, a setter's, a failed
/// binding's control, a laid-out control's) at the editor's lines: Avalonia's and WPF's XAML compilers count a file's lines
/// as an XML reader does (<see cref="ServerPlaces.CountedAsXml"/>), ending one at a lone '\r' too, so below one theirs run
/// ahead of the editor's. Read in the file's text as the editor has it, else as on disk.</summary>
public static class AppPlaces
{
    /// <summary>A place the app reports (line and column from one) as the editor's line and column (from zero) in its file's
    /// text; as reported in a text that cannot be read.</summary>
    public static TextPosition PositionOf(string file, int line, int column, TextSnapshot? text) =>
        ServerPlaces.PositionOfCompiled(file, text, new LspPosition(Math.Max(0, line - 1), Math.Max(0, column - 1)));

    /// <summary>A place the app reports with the editor's line and column (from one) in place of its compiler's; a root's,
    /// recorded by its file alone (line 0), and one in a file not on this machine as reported.</summary>
    public static SourcePlace InEditor(SourcePlace place, Func<string, TextSnapshot?> texts)
    {
        if (place.Line <= 0 || !ServerPlaces.CountedAsXml(place.File) || !File.Exists(place.File))
        {
            return place;
        }
        TextPosition at = PositionOf(place.File, place.Line, place.Column, texts(PathNames.Canonical(place.File)));
        return place with { Line = at.Line + 1, Column = at.Column + 1 };
    }

    /// <summary>An element's details with every place in them at the editor's line, each file read once; a Windows Forms one's
    /// read from its designer code (<see cref="DesignerPlaces"/>).</summary>
    public static ElementDetails InEditor(ElementDetails details, EditorArea editors)
    {
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(editors);
        details = DesignerPlaces.Placed(details, texts);
        SourcePlace? Placed(SourcePlace? place) => place is null ? null : InEditor(place, texts);
        return details with
        {
            Source = Placed(details.Source),
            Properties = [.. details.Properties.Select(p => p with { Place = Placed(p.Place) })],
            Styles = [.. details.Styles.Select(s => s with
            {
                Place = Placed(s.Place),
                Setters = [.. s.Setters.Select(setter => setter with { Place = Placed(setter.Place), ByPlace = Placed(setter.ByPlace) })],
            })],
        };
    }

    /// <summary>What overrides a setter, with where it is written when the app says (its line as the details have it):
    /// "Button.danger (MainWindow.axaml:7)"; null for a setter nothing overrides.</summary>
    public static string? OverriddenBy(SetterReport setter) =>
        setter.By is { } by && setter.ByPlace is { } at ? $"{by} ({Path.GetFileName(at.File)}:{at.Line})" : setter.By;

    /// <summary>Where what set a watched property is written: " at MainWindow.axaml:7", the editor's line; empty for
    /// none.</summary>
    public static string At(SourcePlace? place, Func<string, TextSnapshot?> texts) =>
        place is null ? string.Empty : $" at {Path.GetFileName(place.File)}:{InEditor(place, texts).Line}";

    /// <summary>The editor's line (from one) of a laid-out control's element, which the app reports at its compiler's line;
    /// as reported where its file's text cannot be read.</summary>
    public static int LineOf(UiControl control, Func<string, TextSnapshot?> texts) =>
        control.File is { } file ? InEditor(new SourcePlace(file, control.Line, 1), texts).Line : control.Line;
}
