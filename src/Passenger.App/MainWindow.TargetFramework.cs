using Avalonia.Controls;
using Passenger.Core;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Target Framework: for a file of a multi-targeted project, or one the language server compiles in several
/// projects, the status bar's context widget beside its language, and the action it runs, which offers the others.
/// A C# file's are the server's contexts (Roslyn's <c>_vs_projectContext</c>), a XAML file's its project's frameworks
/// Passenger runs. A framework chosen in either is its project's (<see cref="TargetFrameworkChoices"/>): its XAML preview
/// draws that framework's build, its XAML is read against that framework's restore, and the server answers for its C#
/// files in it.</summary>
public partial class MainWindow
{
    private void AddTargetFramework()
    {
        actions!.Add(new IdeAction(A.TargetFramework, ActionTitles.TargetFramework, "Code", context => ShowTargetFrameworks(context.File!))
        {
            WhyNot = context => context.File is not { } file ? Refusals.NoFile
                : file.ProjectContexts.Count < 2 ? $"{file.Name} is compiled for one target framework only"
                : null,
        });
        Editors.Opened += (_, document) => FollowXamlFrameworks(document);
    }

    /// <summary>The status bar's context widget was clicked: Target Framework offers the file's others.</summary>
    private void OnTargetFrameworkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Request(A.TargetFramework);

    /// <summary>What the status bar's context widget says of the file shown (its target framework); null while it shows none.</summary>
    public string? TargetFrameworkShown => status.Context;

    /// <summary>The session's choices of framework shown in its XAML files' context widgets, and followed by their previews.</summary>
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

    /// <summary>Every open XAML file's context widget showing its project's frameworks and the one chosen.</summary>
    private void FollowXamlFrameworks()
    {
        foreach (OpenDocument document in Editors.Documents)
        {
            FollowXamlFrameworks(document);
        }
    }

    /// <summary>A XAML file's contexts: its project's frameworks Passenger runs, each named as the language server names a
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

    /// <summary>Target Framework for an agent, which no menu reaches: <paramref name="input"/> names the framework, after its
    /// project's name and a colon where more than one project targets several (<c>Shop:net9.0</c>), else the active file's
    /// project's or the one project's; chosen as the context widget chooses it, and what follows it said. Why not, thrown.</summary>
    internal string ChooseFrameworkForAgent(string input)
    {
        UiThread.Check();
        int colon = input.LastIndexOf(':');
        string framework = input[(colon + 1)..].Trim();
        string? named = colon < 0 ? null : input[..colon].Trim();
        ProjectModel[] several = [.. session.Projects.Where(p => TargetFrameworkChoices.Offered(p).Count > 1)];
        ProjectModel? project = named is not null
            ? several.FirstOrDefault(p => p.Name.Equals(named, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p.Path).Equals(named, StringComparison.OrdinalIgnoreCase))
            : Editors.Active is { } active && ProjectModel.Owning(several, active.Path) is { } owning ? owning
            : several.Length == 1 ? several[0] : null;
        if (project is null)
        {
            throw new InvalidOperationException(several.Length == 0 ? "No project of the folder targets more than one framework Passenger runs"
                : named is not null ? $"{named} is not a project of the folder that targets several frameworks: {Plural.Listed([.. several.Select(p => p.Name)])} do"
                : $"Name the project before the framework, as {several[0].Name}:{framework}: {Plural.Listed([.. several.Select(p => p.Name)])} target several");
        }
        IReadOnlyList<string> offered = TargetFrameworkChoices.Offered(project);
        if (offered.FirstOrDefault(f => f.Equals(framework, StringComparison.OrdinalIgnoreCase)) is not { } chosen)
        {
            throw new InvalidOperationException($"{project.Name} targets {Plural.Listed(offered)}, not {framework}");
        }
        session.Frameworks.Choose(project, chosen);
        return $"{project.Name}'s framework is {chosen}: its XAML preview draws that framework's build, its XAML is read against it, and the language server answers for its C# in it";
    }

    /// <summary>The file's contexts over the status bar's context widget, the one the server answers in checked; a XAML
    /// file's project's frameworks, the one chosen checked.</summary>
    private void ShowTargetFrameworks(OpenDocument document)
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
        Open(new ContextMenu { ItemsSource = items }, TargetFrameworkButton);

        MenuItem Item(string label, Action choose) => MenuChoice.Of(label, label == document.ProjectContext, choose);
    }
}
