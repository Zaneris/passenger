using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.IlViewer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Dap;
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
    private (DebugSession Session, DapStackFrame Frame, int Stop)? assemblyRequest;

    private void AddIlViewer()
    {
        ToolWindow window = new(ToolWindowIds.IlViewer, "Compiled Code", IconKeys.Reference,
            () => ilViewer = new IlViewerPane(() => FollowIlViewer(), () => FollowIlViewer(refresh: true),
                symbol => IdeErrors.Observe(OpenCompiledSymbolAsync(symbol))), ToolAnchor.RightTop)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowIlViewer,
        };
        Dock.Add(window);
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
        next.Debug.AssemblyFrameChanged += (_, _) => FollowAssembly();
        next.Debug.StateChanged += (_, _) => FollowAssembly();
        next.Debug.SelectionChanged += (_, _) => FollowAssembly();
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

        void FollowAssembly()
        {
            if (ilViewer?.Mode == CompiledView.Assembly)
            {
                Follow();
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
            assemblyRequest = null;
            return;
        }
        if (pane.Mode == CompiledView.Assembly)
        {
            FollowAssembly(pane, refresh);
            return;
        }

        assemblyRequest = null;
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

    private void FollowAssembly(IlViewerPane pane, bool refresh)
    {
        ilRequest = null;
        (DebugSession Session, DapStackFrame Frame, int Stop)? request = Debugger.AssemblyFrame;
        if (request is not null && request == assemblyRequest && !refresh)
        {
            return;
        }

        ilRead?.Cancel();
        ilSourceText = null;
        assemblyRequest = request;
        if (request is { } stopped)
        {
            pane.Say("Reading assembly…", stopped.Frame.Name);
            IdeErrors.Observe(ReadCompiledAsync(cancel => ReadAssemblyAsync(pane, stopped, cancel)));
        }
        else
        {
            pane.Say("Pause a .NET program to see the selected stack frame's assembly.");
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

    private async Task ReadAssemblyAsync(IlViewerPane pane, (DebugSession Session, DapStackFrame Frame, int Stop) request, CancellationToken cancel)
    {
        TextSnapshot? sourceText = Editors.Active is { IsModified: false } document && request.Frame.Source?.Path is { } path
            && PathNames.Same(document.Path, path) ? document.Text.Snapshot : null;
        try
        {
            DisassembleFrameResponse result = await request.Session.DisassembleAsync(request.Frame, cancel);
            CompiledSource compiled = await BackgroundWork.Run(() => CompiledSource.Assembly(result, cancel), cancel);
            if (!cancel.IsCancellationRequested && Debugger.AssemblyFrame == request)
            {
                ilSourceText = sourceText;
                pane.ShowAssembly($"{result.Architecture} · {request.Frame.Name}", compiled);
            }
        }
        catch (DapException error)
        {
            if (!cancel.IsCancellationRequested && Debugger.AssemblyFrame == request)
            {
                pane.Say(error.Reason, request.Frame.Name);
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
        if ((ilRequest is not null || assemblyRequest is not null) && ilViewer is { } pane && Editors.Active is { } document)
        {
            bool sourceMatches = ReferenceEquals(document.Text.Snapshot, ilSourceText) && !document.IsModified;
            pane.FollowCaret(sourceMatches ? Editors.CodeEditor.CaretPosition : null);
        }
    }
}
