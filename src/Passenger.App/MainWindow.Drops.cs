using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Passenger.Terminal;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Files dropped on the window from the system's file manager (Files, Finder, Explorer): a folder, solution or
/// project opens as File, Open opens one, any other file in the editor of the window it was dropped on, and on the
/// terminal each path is typed for the shell.</summary>
public partial class MainWindow
{
    private void AcceptDrops(Window window)
    {
        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble);
        window.AddHandler(DragDrop.DropEvent, (sender, e) => OnDrop(window, e), RoutingStrategies.Bubble);
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(Window window, DragEventArgs e)
    {
        List<string> paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>()];
        if (paths.Count == 0)
        {
            return;
        }
        e.Handled = true;
        e.DragEffects = DragDropEffects.Copy;
        if ((e.Source as Visual)?.FindAncestorOfType<TerminalView>(includeSelf: true) is { Session: { } shell } terminal)
        {
            terminal.Focus();
            shell.Paste(string.Join(' ', paths.Select(path => ShellWords.Quoted(path, shell.Shell))) + " ");
            return;
        }
        Editors.Front(window);
        string? workspace = null;
        foreach (string path in paths)
        {
            if (Directory.Exists(path) || WorkspaceModel.Opens(path))
            {
                workspace ??= path;
            }
            else if (File.Exists(path))
            {
                OpenFile(path);
            }
        }
        if (workspace is not null)
        {
            Open(workspace);
        }
    }
}
