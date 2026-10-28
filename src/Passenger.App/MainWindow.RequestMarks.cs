using Avalonia.Controls;
using Passenger.App.Editors;
using Passenger.App.Endpoints;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>The send icon on each request line of an open .http or .rest file, read from its text as typed once typing
/// pauses; its menu's item is Test Request on that request, which sends it as the Endpoints window's Test does (nothing
/// sent but on that click, the person asked first when it would take something from their computer).</summary>
public partial class MainWindow
{
    private Debounce? requestMarksLater;

    private void AddRequestMarks()
    {
        requestMarksLater = new Debounce(TimeSpan.FromMilliseconds(300), () =>
        {
            foreach (OpenDocument document in Editors.Documents.Where(d => HttpFile.Is(d.Path)))
            {
                ShowRequestMarks(document);
            }
            return Task.CompletedTask;
        });
        Closed += (_, _) => requestMarksLater.Stop();
        Editors.Opened += (_, document) =>
        {
            if (!HttpFile.Is(document.Path))
            {
                return;
            }
            document.Text.Changed += (_, _) => requestMarksLater.Restart();
            ShowRequestMarks(document);
        };
    }

    private static void ShowRequestMarks(OpenDocument document)
    {
        RunMark[] marks = [.. HttpFile.Parse(document.Path, document.Text.Snapshot.ToString()).Requests.Select(r => new RunMark(r.Line))];
        if (!marks.SequenceEqual(document.RouteMarks))
        {
            document.RouteMarks = marks;
        }
    }

    /// <summary>The send icon's menu on an .http file's request line: Test Request on that request, as the file reads now.</summary>
    private void ShowRequestMarkMenu(CodeEditor editor, OpenDocument document, int line)
    {
        HttpFile file = HttpFile.Parse(document.Path, document.Text.Snapshot.ToString());
        if (file.Requests.FirstOrDefault(r => r.Line == line) is not { } request)
        {
            return;
        }
        ActionContext context = new(Editors.Active, Editors.CodeEditor, FocusManager?.GetFocusedElement(), null,
            session.Debug.IsDebugging ? session.Debug.Session?.State : null) { Request = (request, file) };
        MenuFlyout menu = new();
        menu.Items.Add(FlyoutEntry(new MenuEntry(AppActions.TestEndpoint), context));
        RouteMarkMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.ShowAt(editor, showAtPointer: true);
    }
}
