using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Syntax;

namespace Passenger.App;

/// <summary>The XAML workspace (Settings › Editor): an Avalonia or WPF file shown with it on opens with its preview beside
/// it and, while an app runs, the Live Visual Tree beside both, one element chosen across the three. The element at the
/// caret, read in the XAML core's tree of the text as Go to Declaration reads it, is chosen in the tree by where the app's
/// build recorded it and outlined in the preview; a control the person chooses in the tree puts the caret on its XAML, which
/// outlines it in the preview; an element clicked in the preview puts the caret on its tag, which chooses it in the tree.</summary>
public partial class MainWindow
{
    /// <summary>How long the caret rests before the tree is asked for the element at it.</summary>
    private static readonly TimeSpan CaretRest = TimeSpan.FromMilliseconds(150);

    /// <summary>The files the workspace has opened its preview for, so a preview the person hides beside one stays hidden.</summary>
    private readonly HashSet<OpenDocument> workspaced = [];

    private Debounce? caretRested;

    /// <summary>The editor whose caret moved last, which the tree is asked of once it rests.</summary>
    private CodeEditor? caretMoved;

    /// <summary>Whether the caret is being put on a control's XAML for the tree, which the tree need not hear of.</summary>
    private bool placingCaret;

    private void AddXamlWorkspace()
    {
        caretRested = new Debounce(CaretRest, ChooseAtCaretAsync);
        Editors.ActiveChanged += (_, _) => OpenWorkspace();
        Editors.Closed += (_, document) => workspaced.Remove(document);
        Editors.ForEachEditor(editor => editor.SelectionChanged += (_, _) => CaretMoved(editor));
        liveTree!.ElementShown += (_, details) => PlaceCaret(details);
    }

    /// <summary>The workspace follows a session: a file shown before its project's XAML was read opens it once it is.</summary>
    private void BindXamlWorkspace(ProjectSession next)
    {
        workspaced.Clear();
        next.Xaml.ProjectChanged += (_, _) =>
        {
            if (session == next)
            {
                OpenWorkspace();
            }
        };
        OpenWorkspace();
    }

    /// <summary>The workspace turned on or off in Settings: on, the file shown opens it.</summary>
    private void FollowXamlWorkspace(AppSettings before)
    {
        if (settings.XamlWorkspace && !before.XamlWorkspace)
        {
            workspaced.Clear();
            OpenWorkspace();
        }
    }

    /// <summary>The file shown opens the workspace the first time it is shown with it on: its preview beside it, and the Live
    /// Visual Tree beside both while an app runs, shown behind, so the keys stay where they are.</summary>
    private void OpenWorkspace()
    {
        if (!settings.XamlWorkspace || Editors.Active is not { } shown || session.Xaml.For(shown) is null || !workspaced.Add(shown))
        {
            return;
        }
        if (!previewing)
        {
            ShowPreviews(true);
        }
        if (LiveApp is { State: LiveAppState.Connected })
        {
            Dock.ShowBehind(ToolWindowIds.LiveTree);
        }
    }

    private void CaretMoved(CodeEditor editor)
    {
        if (placingCaret || !settings.XamlWorkspace || LiveApp is not { State: LiveAppState.Connected })
        {
            return;
        }
        caretMoved = editor;
        caretRested!.Restart();
    }

    /// <summary>The element at the caret that rested chosen in the Live Visual Tree, where the app made it.</summary>
    private async Task ChooseAtCaretAsync()
    {
        if (caretMoved is not { } editor || Editors.Groups.FirstOrDefault(g => g.Editor == editor)?.Active is not { } document
            || session.Xaml.For(document) is null || SpotAt(document, editor.Selections.Primary.Caret) is not { } spot)
        {
            return;
        }
        await liveTree!.ChooseMadeAsync(document.Path, spot);
    }

    /// <summary>Where the app's build recorded the element that makes the object at an offset of a XAML file (<see
    /// cref="XamlFile.ObjectAt"/>): its name's line and column as the build counted them, from one, and a root's x:Class,
    /// which the app finds a root by.</summary>
    private XamlSpot? SpotAt(OpenDocument document, int offset)
    {
        TextSnapshot text = document.Text.Snapshot;
        XmlTree tree = XmlTree.Parse(text.ToString());
        if (XamlFile.ObjectAt(tree, offset) is not { } element)
        {
            return null;
        }
        int line = text.GetLineFromOffset(element.NameStart);
        TextPosition built = session.Debug.BuiltPlace(document.Path, line, element.NameStart - text.GetLineStart(line));
        string? root = element == tree.Root ? XamlFile.ClassAttribute(tree)?.Value : null;
        return new XamlSpot(built.Line + 1, built.Column + 1, root, []);
    }

    /// <summary>A control the person chose in the Live Visual Tree has the caret put on its XAML, the file shown behind the
    /// tree, which keeps the keys; the preview beside it outlines it as the caret does.</summary>
    private void PlaceCaret(ElementDetails details)
    {
        if (!settings.XamlWorkspace || details.Source is not { Line: > 0 } source || !File.Exists(source.File))
        {
            return;
        }
        string path = PathNames.Canonical(source.File);
        if (Editors.Open(path, focus: false) is not null || Editors.Active is not { } shown || shown.Path != path)
        {
            return;
        }
        placingCaret = true;
        try
        {
            Editors.CodeEditor.GoTo(new TextPosition(source.Line - 1, Math.Max(0, source.Column - 1)));
        }
        finally
        {
            placingCaret = false;
        }
    }
}
