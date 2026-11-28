using Passenger.App.CSharp;
using Passenger.App.DependencyMap;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Show Dependency Map: the Dependency Map window, opened on the solution's projects, or from the editor on the
/// symbol at the caret (else the declaration around it), from the Explorer on a project or a C# file's first type, each
/// level read by Passenger's extension in the C# language server as it is opened.</summary>
public partial class MainWindow
{
    private DependencyMapView? dependencyMap;

    /// <summary>The Dependency Map tool window, built as it is first shown or asked for.</summary>
    public DependencyMapView DependencyMapWindow => dependencyMap ?? (DependencyMapView)Dock[ToolWindowIds.DependencyMap].Content;

    private void AddDependencyMap()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.DependencyMap, "Dependency Map", IconKeys.DependencyMap, () =>
        {
            dependencyMap = new DependencyMapView();
            dependencyMap.GoToRequested += (_, node) => GoToMapNode(node);
            dependencyMap.Failed += (_, why) => status.Text = why;
            return dependencyMap;
        }, Docking.ToolAnchor.BottomLeft)
        {
            Serves = ToolWindowServes.EveryFolder,
            InEditorAtFirst = true,
        });
        // The code changing anywhere: an open C# file typed in, the server's projects loaded, C# files written on disk.
        Editors.Opened += (_, document) =>
        {
            if (document.Language == Languages.Language.CSharp)
            {
                document.Text.Changed += (_, _) => dependencyMap?.CodeMoved();
            }
        };
        actions!.Add(new IdeAction(AppActions.ShowDependencyMap, ActionTitles.ShowDependencyMap, ActionGroups.Ide, context => IdeErrors.Observe(ShowDependencyMapAsync(context)))
        {
            WhyNot = _ => session.CSharp is null ? Refusals.CSharpNotRunningSentence : null,
            Menus = [new MenuPlace("Navigate", 2, 8)],
        });
    }

    /// <summary>Opens the Dependency Map window on the place a context names: the Explorer's project or C# file, the editor's
    /// symbol at the caret, else the projects alone; null once shown, else why not, said in the status bar.</summary>
    public async Task<Words?> ShowDependencyMapAsync(ActionContext context)
    {
        if (session.CSharp is not { } csharp)
        {
            return Said(Refusals.CSharpNotRunningSentence);
        }
        IReadOnlyList<string>? path = null;
        try
        {
            path = context.ExplorerRow is { } row ? await MapPathOfAsync(csharp, row) : await MapPathAtCaretAsync(csharp);
        }
        catch (LanguageServerFailed failed)
        {
            status.Text = $"The dependency map could not find the code at the caret: {failed.Reason}";
        }
        ShowToolWindow(ToolWindowIds.DependencyMap);
        if (!csharp.IsReady)
        {
            DependencyMapWindow.Say(csharp.Unready("The dependency map"));
            FollowServerForMap(csharp, path);
            return null;
        }
        await DependencyMapWindow.ShowAsync(of => MapLevelAsync(of), path);
        return null;
    }

    /// <summary>The map shown once the server is ready, where it was asked before.</summary>
    private void FollowServerForMap(CSharpService csharp, IReadOnlyList<string>? path)
    {
        void Ready(object? sender, EventArgs e)
        {
            if (!csharp.IsReady)
            {
                return;
            }
            csharp.StateChanged -= Ready;
            if (session.CSharp == csharp)
            {
                IdeErrors.Observe(DependencyMapWindow.ShowAsync(of => MapLevelAsync(of), path));
            }
        }
        csharp.StateChanged += Ready;
    }

    /// <summary>A level of the map, of the server running now, refused while it is not ready.</summary>
    private async Task<DependenciesFound> MapLevelAsync(string? of)
    {
        if (session.CSharp is not { IsReady: true } csharp)
        {
            throw new RefusedException(session.CSharp?.Unready("The dependency map") ?? Refusals.CSharpNotRunningSentence);
        }
        return await csharp.DependenciesAsync(of);
    }

    /// <summary>The map's path of the symbol at the active C# editor's caret; none with no C# file shown.</summary>
    private async Task<IReadOnlyList<string>?> MapPathAtCaretAsync(CSharpService csharp)
    {
        if (Editors.Active is not { } active || csharp.For(active)?.Lsp is not { IsRazor: false } lsp)
        {
            return null;
        }
        IReadOnlyList<string> path = await lsp.DependencyAtAsync(Editors.CodeEditor.Selections.Primary.Caret);
        return path.Count > 0 ? path : null;
    }

    /// <summary>The map's path of an Explorer row: a project's own node, a C# file's first type (asked of the server, as the
    /// file is open in it or as it holds it unopened), else its project's.</summary>
    private async Task<IReadOnlyList<string>?> MapPathOfAsync(CSharpService csharp, ExplorerNode row)
    {
        if (row is { Kind: NodeKind.Project, Path: { } project })
        {
            return [DependencyIds.Project(project)];
        }
        if (row is { Kind: NodeKind.File, Path: { } file } && Languages.Language.ForPath(file) == Languages.Language.CSharp
            && (Editors.Documents.FirstOrDefault(d => PathNames.Same(d.Path, file)) is { } open ? csharp.For(open)?.Lsp
                : csharp.Session?.Unopened(DocumentUris.FromPath(file), new TextDocument(string.Empty))) is { IsRazor: false } lsp)
        {
            IReadOnlyList<string> path = await lsp.DependencyAtAsync(0);
            return path.Count > 0 ? path : null;
        }
        return session.ProjectOf(row.Path ?? string.Empty) is { } owner ? [DependencyIds.Project(owner.Path)] : null;
    }

    /// <summary>Reads the folder's layer rules from its passenger-layers.json (an open one's as the editor has it), off the
    /// window's thread: the session keeps them, each open C# file is checked against them again, and the map draws their
    /// broken edges; a file that cannot be read is said in the status bar and holds no rules.</summary>
    private void ReadLayers()
    {
        if (session.Folder is not { } folder)
        {
            return;
        }
        ProjectSession of = session;
        OpenTexts texts = Editors.Texts();
        IdeErrors.Observe(ReadLayersAsync(of, folder, texts));
    }

    private async Task ReadLayersAsync(ProjectSession of, string folder, OpenTexts texts)
    {
        (LayerRules? rules, string? problem) = await BackgroundWork.Run(() => texts.Text(LayersFile.PathIn(folder)) is { } text ? LayersFile.Read(folder, text) : (null, null));
        if (session != of || of.IsEnded)
        {
            return;
        }
        if (problem is not null)
        {
            status.Text = problem;
        }
        of.Layers = rules;
        of.CSharp?.LayersChanged();
        of.MSBuild.LayersChanged();
        dependencyMap?.RulesChanged();
    }

    /// <summary>The fixes of an open project file's references breaking the folder's layer rules on an offset's line: the
    /// project's layer let use the other's, as a C# file's broken use is fixed.</summary>
    private IReadOnlyList<CSharp.EditorFix> ProjectLayerFixes(OpenDocument file, int offset)
    {
        if (session.MSBuild.For(file) is not { LayersBroken.Count: > 0 } project || session.Layers is not { } rules)
        {
            return [];
        }
        TextSnapshot text = file.Text.Snapshot;
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        return assist.LetUseFixes(rules, [.. project.LayersBroken.Where(b => b.Mark.Range.Start <= text.Length && text.GetLineFromOffset(b.Mark.Range.Start) == line)
            .Select(b => (b.Mark.Range, b.From, b.To))]);
    }

    /// <summary>Goes to where a node of the map is written: a project's file, else the declaration's name.</summary>
    private void GoToMapNode(MapNode node)
    {
        if (node.Given.Path is not { } path)
        {
            status.Text = Refusals.MapNodeUnplaced.Shown;
            return;
        }
        NavigateTo(path, new LspPosition(node.Given.At?.Line ?? 0, node.Given.At?.Character ?? 0));
    }
}
