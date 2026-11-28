using Passenger.App.CSharp;
using Passenger.App.Docking;
using Passenger.App.IlViewer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

public partial class MainWindow
{
    private IlViewerPane? ilViewer;
    private CompiledRequest? ilRequest;
    private CancellationTokenSource? ilRead;
    private TextSnapshot? ilSourceText;

    private void AddIlViewer()
    {
        ToolWindow window = new(ToolWindowIds.IlViewer, "Compiled Code", IconKeys.CompiledCode,
            () => ilViewer = new IlViewerPane(() => FollowIlViewer(), action => Request(action),
                symbol => IdeErrors.Observe(OpenCompiledSymbolAsync(symbol))), ToolAnchor.RightTop)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowIlViewer,
        };
        Dock.Add(window);
        actions!.Add(new IdeAction(AppActions.IlViewerRefresh, ActionTitles.IlViewerRefresh, "Compiled Code", _ => FollowIlViewer(refresh: true))
        {
            WhyNot = c => NeedsFolder(c) ?? Unserved(ToolWindowIds.IlViewer),
            Words = ["Read the build's IL and low-level C# again"],
        });
        window.Built += (_, _) => FollowIlViewer();
        window.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ToolWindow.IsShown))
            {
                FollowIlViewer();
            }
        };
        Editors.ActiveChanged += (_, _) => FollowIlViewer();
        Editors.CaretMoved += (_, _) => FollowIlCaret();
        Editors.Opened += (_, document) =>
        {
            document.PropertyChanged += (_, change) =>
            {
                if (Editors.Active == document && change.PropertyName is nameof(OpenDocument.ProjectContext) or nameof(OpenDocument.Path))
                {
                    FollowIlViewer();
                }
            };
            document.Text.Changed += (_, _) =>
            {
                if (Editors.Active == document)
                {
                    FollowIlCaret();
                }
            };
        };
        Closed += (_, _) => ilRead?.Cancel();
    }

    private async Task OpenCompiledSymbolAsync(CompiledSymbol symbol)
    {
        ProjectSession current = session;
        ToolWindows.FileLink? target = await BackgroundWork.Run(() => LowLevelSymbols.Definition(symbol));
        if (current != session)
        {
            return;
        }
        if (target is not null)
        {
            OpenLink(target);
        }
        else
        {
            Say($"Could not open {symbol.Name}. Try refreshing Compiled Code.");
        }
    }

    private void BindIlViewer(ProjectSession next)
    {
        next.WorkspaceChanged += (_, _) => Follow();
        next.ProjectsEvaluated += (_, _) => Follow();
        next.FrameworkChosen += (_, _) => Follow();
        buildRun.BuildFinished += (_, outcome) =>
        {
            if (outcome.Succeeded)
            {
                Follow(refresh: true);
            }
        };
        Follow();

        void Follow(bool refresh = false)
        {
            if (session == next)
            {
                FollowIlViewer(refresh);
            }
        }
    }

    private void FollowIlViewer(bool refresh = false)
    {
        if (ilViewer is not { } pane)
        {
            return;
        }

        if (!Dock.IsShown(ToolWindowIds.IlViewer))
        {
            ilRead?.Cancel();
            ilRequest = null;
            ilSourceText = null;
            return;
        }

        OpenDocument? document = Editors.Active;
        (CompiledRequest? request, string? reason) = IlRequestFor(document, pane.Mode);
        if (request is not null && request == ilRequest && !refresh)
        {
            FollowIlCaret();
            return;
        }

        ilRead?.Cancel();
        ilSourceText = null;
        ilRequest = request;
        if (request is not null)
        {
            pane.Say("Reading compiled code…", Path.GetFileName(request.Source));
            IdeErrors.Observe(ReadCompiledAsync(cancel => ReadIlAsync(pane, request, document!.Text.Snapshot, cancel)));
        }
        else if (reason is not null)
        {
            pane.Say(reason);
        }
    }

    private async Task ReadCompiledAsync(Func<CancellationToken, Task> load)
    {
        using CancellationTokenSource read = new();
        ilRead = read;
        try
        {
            await load(read.Token);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(ilRead, read))
            {
                ilRead = null;
            }
        }
    }

    private (CompiledRequest? Request, string? Message) IlRequestFor(OpenDocument? document, CompiledView mode)
    {
        if (document is null || document.Language != Language.CSharp)
        {
            return (null, "Open a C# file to see its compiled code.");
        }
        string? context = document.ProjectContext;
        ProjectModel? project = CompilationSymbols.Named(session.Projects, document.Path, context) ?? session.ProjectOf(document.Path);
        if (project is null)
        {
            return (null, "Open a C# file that belongs to a loaded project.");
        }
        string? framework = (context is null ? null : ProjectContext.FrameworkOf(context)) ?? session.Frameworks.Of(project);
        string? target = ProjectSession.BuildOutputOf(project, framework);
        return string.IsNullOrEmpty(target)
            ? (null, "Waiting for the project to provide its build output path.")
            : (new CompiledRequest(document.Path, Path.GetFullPath(target, project.Folder), mode), null);
    }

    private async Task ReadIlAsync(IlViewerPane pane, CompiledRequest request, TextSnapshot sourceText, CancellationToken cancel)
    {
        CompiledSource result = await BackgroundWork.Run(() => CompiledSource.Read(request, cancel), cancel);
        if (!cancel.IsCancellationRequested)
        {
            ilSourceText = sourceText;
            pane.Show(request, result);
            FollowIlCaret();
        }
    }

    private void FollowIlCaret()
    {
        if (ilRequest is not null && ilViewer is { } pane && Editors.Active is { } document)
        {
            bool sourceMatches = ReferenceEquals(document.Text.Snapshot, ilSourceText) && !document.IsModified;
            pane.FollowCaret(sourceMatches ? Editors.CodeEditor.CaretPosition : null);
        }
    }
}
