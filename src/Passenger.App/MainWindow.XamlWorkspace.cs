using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.App;

/// <summary>The XAML workspace (Settings › Editor): an Avalonia or WPF file shown with it on opens with its preview beside
/// it and, while an app runs, the Live Visual Tree beside both, one element chosen across the three. The element at the
/// caret, read in the XAML core's tree of the text as Go to Declaration reads it, is chosen in the tree by where the app's
/// build recorded it and outlined in the preview; a control the person chooses in the tree puts the caret on its XAML, which
/// outlines it in the preview; an element clicked in the preview puts the caret on its tag, which chooses it in the tree.
/// While no app runs, what the caret's element sets itself shows under the preview.</summary>
public partial class MainWindow
{
    /// <summary>How long the caret rests before the element at it is chosen.</summary>
    private static readonly TimeSpan CaretRest = TimeSpan.FromMilliseconds(150);

    /// <summary>The files the workspace has opened its preview for, so a preview the person hides beside one stays hidden.</summary>
    private readonly HashSet<OpenDocument> workspaced = [];

    private Debounce? caretRested;

    /// <summary>The editor whose caret moved last, whose element is chosen once it rests.</summary>
    private CodeEditor? caretMoved;

    /// <summary>Whether the caret is being put on a control's XAML for the tree, which the tree need not hear of.</summary>
    private bool placingCaret;

    private void AddXamlWorkspace()
    {
        caretRested = new Debounce(CaretRest, CaretRestedAsync);
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

    /// <summary>The workspace turned on or off in Settings: on, the file shown opens it; off, no preview shows its values.</summary>
    private void FollowXamlWorkspace(AppSettings before)
    {
        if (settings.XamlWorkspace && !before.XamlWorkspace)
        {
            workspaced.Clear();
            OpenWorkspace();
        }
        else if (!settings.XamlWorkspace && before.XamlWorkspace)
        {
            foreach (ShownPreview shown in previews.Values)
            {
                shown.Pane.ShowProperties(null, []);
            }
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
        caretMoved = Editors.CodeEditor;
        caretRested?.Restart();
        if (LiveApp is { State: LiveAppState.Connected })
        {
            Dock.ShowBehind(ToolWindowIds.LiveTree);
        }
    }

    private void CaretMoved(CodeEditor editor)
    {
        if (placingCaret || !settings.XamlWorkspace)
        {
            return;
        }
        caretMoved = editor;
        caretRested!.Restart();
    }

    /// <summary>The element at the caret that rested: chosen in the Live Visual Tree where the app made it while an app runs,
    /// else what it sets itself shown under the preview.</summary>
    private async Task CaretRestedAsync()
    {
        if (caretMoved is not { } editor || Editors.Groups.FirstOrDefault(g => g.Editor == editor)?.Active is not { } document || session.Xaml.For(document) is null)
        {
            return;
        }
        TextSnapshot text = document.Text.Snapshot;
        XmlTree tree = XmlTree.Parse(text.ToString());
        XmlElement? element = XamlFile.ObjectAt(tree, editor.Selections.Primary.Caret);
        bool live = LiveApp is { State: LiveAppState.Connected };
        PreviewPaneOf(document)?.ShowProperties(live || element is null ? null : Named(element), live || element is null ? [] : ValuesOf(tree, element));
        if (live && element is not null)
        {
            await liveTree!.ChooseMadeAsync(document.Path, SpotOf(document, text, tree, element));
        }
    }

    /// <summary>An element as the preview's values name it: its type, then #name.</summary>
    private static string Named(XmlElement element) =>
        element.Attributes.FirstOrDefault(a => a.LocalName == "Name" && a.HasValue) is { } named ? $"{element.LocalName} #{named.Value}" : element.LocalName;

    /// <summary>What an element sets itself, each value's row as the preview shows it: where it comes from, a binding's path
    /// and a resource's key said.</summary>
    private static List<(string Property, string Value, string From)> ValuesOf(XmlTree tree, XmlElement element) =>
        [.. XamlSetValues.Of(tree, element).Select(v => (v.Property, v.Value, v.Kind switch
        {
            XamlValueKind.Binding => v.Source is { Length: > 0 } path ? $"Binding · {path}" : "Binding",
            XamlValueKind.Resource => v.Source is { Length: > 0 } key ? $"Resource · {key}" : "Resource",
            XamlValueKind.Extension => v.Source ?? "Markup extension",
            XamlValueKind.Element => "Property element",
            _ => "Local",
        }))];

    /// <summary>Where the app's build recorded an element: its name's line and column as the build counted them, from one,
    /// and a root's x:Class, which the app finds a root by.</summary>
    private XamlSpot SpotOf(OpenDocument document, TextSnapshot text, XmlTree tree, XmlElement element)
    {
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
