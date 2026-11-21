using Avalonia;
using Avalonia.Media.TextFormatting;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Vcs;

/// <summary>The sign beside each line a diff's or a merge's pane tints, so how it changed reads without its tint's colour:
/// + added, − removed, ≠ changed, ! in conflict (<see cref="SignOf"/>). A pane's column right of its line numbers, given by
/// <see cref="Give"/>, drawn in the kind's own colour from the editor's change marks.</summary>
public sealed class DiffSigns(CodeEditor pane) : IGutterColumn
{
    /// <summary>Never raised: its width stays, and the editor draws its decorations again as they are set.</summary>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <summary>Gives a pane its signs, the one column right of its numbers.</summary>
    public static void Give(CodeEditor pane) => pane.SetGutterColumns([], [new DiffSigns(pane)]);

    /// <summary>The sign of a kind of change.</summary>
    public static string SignOf(ChangeMarkKind kind) => kind switch
    {
        ChangeMarkKind.Added => "+",
        ChangeMarkKind.Deleted => "−",
        ChangeMarkKind.Conflict => "!",
        _ => "≠",
    };

    public double Width(GutterFont font) => Math.Ceiling(font.CharWidth * 1.5);

    public void Draw(GutterFrame frame)
    {
        LineOrdered<ChangeMark> changes = pane.Decorations.ChangesByLine;
        foreach (GutterLine line in frame.Lines)
        {
            for (int i = changes.From(line.Line); i < changes.Count && changes.LineOf(i) <= line.Line; i++)
            {
                ChangeMark change = changes[i];
                if (line.Line < change.Line || line.Line >= change.Line + change.Count)
                {
                    continue;
                }
                TextLine sign = frame.Label(SignOf(change.Kind), change.Kind switch
                {
                    ChangeMarkKind.Added => TokenKeys.Vcs_Added,
                    ChangeMarkKind.Deleted => TokenKeys.Vcs_Deleted,
                    ChangeMarkKind.Conflict => TokenKeys.Vcs_Conflict,
                    _ => TokenKeys.Vcs_Modified,
                }, bold: true);
                sign.Draw(frame.Context, new Point(frame.Left + ((frame.Width - sign.WidthIncludingTrailingWhitespace) / 2), line.Top + ((frame.LineHeight - sign.Height) / 2)));
                break;
            }
        }
    }

    public GutterAnswer Press(GutterPress press) => GutterAnswer.Passed;
}
