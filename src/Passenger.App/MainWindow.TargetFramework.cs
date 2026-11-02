using Avalonia.Controls;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Target Framework: for a file of a multi-targeted project, or one the language server compiles in several
/// projects, the editor's context switcher beside its problem counts, and the action it runs, which offers the others.
/// A C# file's are the server's contexts (Roslyn's <c>_vs_projectContext</c>), a XAML file's its project's frameworks
/// Passenger runs. A framework chosen in either is its project's (<see cref="TargetFrameworkChoices"/>): its XAML preview
/// draws that framework's build, its XAML is read against that framework's restore, and the server answers for its C#
/// files in it.</summary>
public partial class MainWindow
{
    private void AddTargetFramework()
    {
        actions!.Add(new IdeAction(A.TargetFramework, "Target Framework", "Code", context => ShowTargetFrameworks(context.File!, context.Editor))
        {
            WhyNot = context => context.File is not { } file ? Refusals.NoFile
                : file.ProjectContexts.Count < 2 ? $"{file.Name} is compiled for one target framework only"
                : null,
        });
        Editors.ForEachEditor(editor => editor.ContextSwitchRequested += (_, _) => Request(A.TargetFramework));
        Editors.Opened += (_, document) => FollowXamlFrameworks(document);
    }

    /// <summary>The session's choices of framework shown in its XAML files' switchers, and followed by their previews.</summary>
    private void BindTargetFrameworks(ProjectSession next)
    {
        next.FrameworkChosen += (_, project) =>
        {
            if (session == next)
            {
                FollowXamlFrameworks();
                RestartPreviews(project);
            }
        };
        next.WorkspaceChanged += (_, _) =>
        {
            if (session == next)
            {
                FollowXamlFrameworks();
            }
        };
        next.ProjectsEvaluated += (_, _) =>
        {
            if (session == next)
            {
                FollowXamlFrameworks();
            }
        };
        FollowXamlFrameworks();
    }

    /// <summary>Every open XAML file's switcher showing its project's frameworks and the one chosen.</summary>
    private void FollowXamlFrameworks()
    {
        foreach (OpenDocument document in Editors.Documents)
        {
            FollowXamlFrameworks(document);
        }
    }

    /// <summary>A XAML file's switcher: its project's frameworks Passenger runs, each named as the language server names a
    /// framework of a project ("App (net10.0)"), and the one chosen; none for a project of one.</summary>
    private void FollowXamlFrameworks(OpenDocument document)
    {
        if (document.Language != Languages.Language.Xaml)
        {
            return;
        }
        if (ProjectModel.Owning(session.Projects, document.Path) is not { } project || session.Frameworks.Of(project) is not { } chosen)
        {
            document.ProjectContexts = [];
            document.ProjectContext = null;
            return;
        }
        string name = Path.GetFileNameWithoutExtension(project.Path);
        IReadOnlyList<string> offered = TargetFrameworkChoices.Offered(project);
        if (!document.ProjectContexts.SequenceEqual(offered.Select(f => Label(name, f)), StringComparer.Ordinal))
        {
            document.ProjectContexts = [.. offered.Select(f => Label(name, f))];
        }
        document.ProjectContext = Label(name, chosen);

        static string Label(string project, string framework) => $"{project} ({framework})";
    }

    /// <summary>The file's contexts under the editor's switcher, the one the server answers in checked; a XAML file's
    /// project's frameworks, the one chosen checked.</summary>
    private void ShowTargetFrameworks(OpenDocument document, Passenger.Editor.CodeEditor editor)
    {
        List<Control> items = [];
        if (CSharpService?.For(document) is { } features)
        {
            foreach (ProjectContext context in features.Contexts)
            {
                items.Add(Item(context.Label, () => features.Choose(context)));
            }
        }
        else if (ProjectModel.Owning(session.Projects, document.Path) is { } project)
        {
            foreach (string label in document.ProjectContexts)
            {
                if (ProjectContext.FrameworkOf(label) is { } framework)
                {
                    items.Add(Item(label, () => session.Frameworks.Choose(project, framework)));
                }
            }
        }
        if (items.Count == 0)
        {
            return;
        }
        ContextMenu menu = new()
        {
            ItemsSource = items,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            PlacementRect = editor.ContextSwitcherBounds,
        };
        Open(menu, editor);

        MenuItem Item(string label, Action choose)
        {
            MenuItem item = new()
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = label == document.ProjectContext,
            };
            item.Click += (_, _) => choose();
            return item;
        }
    }
}
