using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The errors the last Godot game run raised, pinned to their lines (<see cref="GodotErrorPins"/>): each open file's
/// gutter badges and line-end words, the pins following the text as it is edited, and a badge's press opening its
/// occurrences (<see cref="GodotPinPopup"/>).</summary>
public partial class MainWindow
{
    private bool pinsWired;

    /// <summary>The pin popup open, while one is.</summary>
    public GodotPinPopup? PinPopup { get; private set; }

    /// <summary>A session's pins in the open files, while it is the window's: each game it launches starts them anew.</summary>
    private void BindErrorPins(ProjectSession next)
    {
        next.GameStarted += (_, game) =>
        {
            if (session == next)
            {
                next.ErrorPins.Follow(game, Editors.TextOf, path => Editors.Find(path) is { HoldsText: true } open ? open.Text : null);
            }
        };
        next.ErrorPins.Changed += (_, path) =>
        {
            if (session == next)
            {
                foreach (OpenDocument document in Editors.Documents.Where(d => PathNames.Same(d.Path, path)))
                {
                    document.ErrorPins = PinsOf(next, path);
                }
            }
        };
        foreach (OpenDocument document in Editors.Documents)
        {
            next.ErrorPins.Attach(document.Path, document.Text);
            document.ErrorPins = PinsOf(next, document.Path);
        }
        if (pinsWired)
        {
            return;
        }
        pinsWired = true;
        Editors.Opened += (_, document) =>
        {
            session.ErrorPins.Attach(document.Path, document.Text);
            document.ErrorPins = PinsOf(session, document.Path);
            document.Text.Changed += (_, change) => session.ErrorPins.Edited(document.Path, change);
        };
        Editors.Closed += (_, document) => session.ErrorPins.Detach(document.Path);
        Editors.ForEachGutter(gutter => gutter.Pins.Pressed = click => Editors.Active is { } active && ShowPins(gutter.Editor, active, click));
    }

    private static IReadOnlyList<ErrorPin> PinsOf(ProjectSession of, string path) =>
        [.. of.ErrorPins.In(path).Select(p => new ErrorPin(p.Line, p.Count, p.Last.Said, p.Warnings, p))];

    /// <summary>A line's pin opened under its badge: its occurrences, their stacks, and Clear.</summary>
    private bool ShowPins(CodeEditor editor, OpenDocument document, GutterClick click)
    {
        if (document.ErrorPins.FirstOrDefault(p => p.Line == click.Line) is not { Data: GodotPin pin })
        {
            return false;
        }
        string? game = session.GodotOf(pin.Path) ?? session.Game?.Folder;
        GodotPinPopup popup = new(pin, file => (game is null ? null : Passenger.Workspace.GodotResPaths.FileOf(game, file)) ?? file);
        popup.Done += (_, _) => ClosePins();
        popup.Cleared += (_, all) =>
        {
            ClosePins();
            if (all)
            {
                session.ErrorPins.Clear();
            }
            else
            {
                session.ErrorPins.Remove(pin);
            }
        };
        popup.FrameChosen += (_, frame) =>
        {
            ClosePins();
            if (File.Exists(frame.Path))
            {
                NavigateTo(PathNames.Canonical(frame.Path), new TextPosition(Math.Max(0, frame.Line - 1), 0));
            }
        };
        PinPopup = popup;
        // Closed by the window's own Escape or a click outside it too.
        popup.DetachedFromVisualTree += (_, _) =>
        {
            if (PinPopup == popup)
            {
                PinPopup = null;
            }
        };
        popups.Show(popup, popups.PointIn(editor, new Avalonia.Point(click.Position.X, click.Position.Y + editor.LineHeight)));
        return true;
    }

    private void ClosePins()
    {
        if (PinPopup is not null && popups.Open == PinPopup)
        {
            popups.Close();
        }
        PinPopup = null;
    }
}
