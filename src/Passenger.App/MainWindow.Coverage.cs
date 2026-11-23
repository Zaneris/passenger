using Avalonia.Platform.Storage;
using Passenger.App.Testing;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Coverage in the window: runs of tests with their coverage measured, each open file's lines marked in its gutter
/// as the run left them (<see cref="CoveredLines"/>), the Coverage window's tree of what each project, namespace, type and
/// member came to, and the coverage exported as one Cobertura report where the person names.</summary>
public partial class MainWindow
{
    private CoverageView? coverage;

    /// <summary>Whether the person's own run with coverage is under way, whose end brings the Coverage window forward.</summary>
    private bool coverageAsked;

    /// <summary>The Coverage tool window, built as it is first shown or asked for.</summary>
    public CoverageView CoverageWindow => coverage ?? (CoverageView)Dock[ToolWindowIds.Coverage].Content;

    /// <summary>Asks the person where to export the coverage, in the system's own panel, starting in the open folder; null
    /// where they chose nowhere.</summary>
    public Func<Task<string?>> PickCoverageExport
    {
        get => pickCoverageExport ?? PickCoverageExportAsync;
        set => pickCoverageExport = value;
    }

    private Func<Task<string?>>? pickCoverageExport;

    private void AddCoverage()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Coverage, "Coverage", IconKeys.Coverage, () =>
        {
            CoverageView view = new();
            coverage = view;
            view.ActionRequested += (_, action) => Request(action);
            view.GoToRequested += (_, node) =>
            {
                if (node.Path is { } path && File.Exists(path))
                {
                    NavigateTo(path, new TextPosition(Math.Max(0, node.Line - 1), 0));
                }
            };
            view.Show(session.Tests.Coverage, session.Tests.Unmeasured);
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowCoverage,
            Menus = [new MenuPlace("Tests", 2, 1)],
            ExpandAll = () => CoverageWindow.ExpandAll(),
            CollapseAll = () => CoverageWindow.CollapseAll(),
        });
        Editors.Opened += (_, document) => MarkCoverage(session, document);
    }

    /// <summary>A session's coverage marks the open files and fills the Coverage window while it is the window's.</summary>
    private void BindCoverage(ProjectSession next)
    {
        next.Tests.CoverageChanged += (_, _) =>
        {
            if (session == next)
            {
                ShowCoverage(next);
            }
        };
        ShowCoverage(next);
    }

    private void ShowCoverage(ProjectSession of)
    {
        foreach (OpenDocument document in Editors.Documents)
        {
            MarkCoverage(of, document);
        }
        coverage?.Show(of.Tests.Coverage, of.Tests.Unmeasured);
    }

    /// <summary>A file's lines marked as the session's coverage measured them; none where it measured none of them.</summary>
    private static void MarkCoverage(ProjectSession of, OpenDocument document) =>
        document.Coverage = document.HoldsText && of.Tests.Coverage?.In(document.Path) is { Count: > 0 } lines ? new CoveredLines(document.Text, lines) : null;

    /// <summary>A person's run with coverage, the Coverage window brought forward once it has measured some.</summary>
    private void CoverTests(Action run)
    {
        coverageAsked = true;
        run();
    }

    /// <summary>The Coverage window forward after the person's own run with coverage, once it measured some.</summary>
    private void ShowMeasured(bool asked, ProjectSession running)
    {
        if (asked && session == running && running.Tests.Coverage is { Files.Count: > 0 })
        {
            ShowToolWindow(ToolWindowIds.Coverage);
        }
    }

    /// <summary>Export Coverage as Cobertura: the coverage measured written as one Cobertura report where the person names,
    /// as one step of Undo where that is the folder's.</summary>
    private async Task ExportCoverageAsync()
    {
        if (session.Tests.Coverage is not { } measured || await PickCoverageExport() is not { } path)
        {
            return;
        }
        string text = measured.ToCobertura();
        string? refused = await ChangeFileAsync("Export Coverage", [path], () =>
        {
            if (File.Exists(path) && FileText.Load(path) is { } had)
            {
                _ = FileText.SaveOver(path, had, text);
            }
            else
            {
                _ = FileText.SaveNew(path, text);
            }
            return null;
        });
        Say(refused is null ? $"Coverage exported to {PathNames.Shown(path, Folder)}" : $"The coverage could not be exported: {refused}");
    }

    private async Task<string?> PickCoverageExportAsync()
    {
        IStorageFile? picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Coverage as Cobertura",
            SuggestedFileName = "coverage.cobertura.xml",
            DefaultExtension = "xml",
            FileTypeChoices = [new FilePickerFileType("Cobertura reports") { Patterns = ["*.xml"] }],
            SuggestedStartLocation = Folder is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        return picked?.TryGetLocalPath();
    }
}
