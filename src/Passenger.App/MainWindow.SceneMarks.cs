using Avalonia.Controls;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The marks beside a Godot script's code leading into its scenes (<see cref="SceneMark"/>): a press on one
/// opens its menu of where it leads, a scene a class runs in or a connection calling a handler, each item going to that
/// place of its scene; the pointer resting on one names them.</summary>
public partial class MainWindow
{
    /// <summary>The scene mark's menu last opened.</summary>
    public MenuFlyout? SceneMarkMenu { get; private set; }

    /// <summary>The menu of a line's scene mark: an item for each place it leads to; false where the line has none.</summary>
    private bool ShowSceneMarkMenu(CodeEditor editor, OpenDocument document, int line)
    {
        if (document.SceneMarks.FirstOrDefault(m => m.Line == line) is not { } mark)
        {
            return false;
        }
        MenuFlyout menu = new();
        foreach (SceneLead lead in mark.Leads)
        {
            MenuItem item = new() { Header = lead.Words };
            item.Click += (_, _) => NavigateTo(lead.File, new TextPosition(lead.Line, 0));
            menu.Items.Add(item);
        }
        SceneMarkMenu = menu;
        menu.Placement = Avalonia.Controls.PlacementMode.Pointer;
        menu.ShowAt(editor, showAtPointer: true);
        return true;
    }

    /// <summary>What the gutter says beside a line of a file: its breakpoint's state, then where its scene mark leads.</summary>
    private IReadOnlyList<string>? GutterWordsOf(OpenDocument document, int line)
    {
        IReadOnlyList<string>? debug = session.Debug.GutterWords(document.Path, line);
        return document.SceneMarks.FirstOrDefault(m => m.Line == line) is { } mark ? [.. debug ?? [], .. mark.Leads.Select(l => l.Words)] : debug;
    }
}
