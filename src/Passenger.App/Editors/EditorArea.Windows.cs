using Avalonia;
using Avalonia.Controls;
using Passenger.App.Docking;
using Passenger.App.Workspace;

namespace Passenger.App.Editors;

/// <summary>The editor windows: tabs taken out of the editors into windows of their own, by Open Tab in New Window or a
/// tab let go outside every window's editors. Each is a frame of the area's, its groups over the same open files; the
/// window in front is the one whose group last worked in is active, so the IDE's actions act there. They close with the
/// main window, and a folder keeps them, where they stood, with its editors.</summary>
public sealed partial class EditorArea
{
    /// <summary>Where a window made by Open Tab in New Window stands from the group the tab left, down and right.</summary>
    private const double NewWindowOffset = 32;

    /// <summary>Where on a new window's first tab the pointer is when a tab dropped out of every window makes one.</summary>
    private static readonly Vector TabGrip = new(48, 18);

    /// <summary>The least a new editor window opens at, however small the group its tab left.</summary>
    private static readonly Size SmallestNew = new(480, 320);

    private Window? host;
    private string? folderName;
    private readonly List<Window> unshown = [];

    /// <summary>An editor window opened, before it shows: what the IDE puts in each of its windows (its keys, its popups)
    /// goes into it.</summary>
    public event EventHandler<EditorWindow>? WindowOpened;

    /// <summary>The editor windows open, in the order they opened.</summary>
    public IReadOnlyList<EditorWindow> Windows => [.. frames.Select(f => f.Window).OfType<EditorWindow>()];

    /// <summary>The editor window the person works in, the active group's, or null while it is the main window.</summary>
    public EditorWindow? InFront => ActiveFrame.Window;

    /// <summary>Open Tab in New Window: the tab leaves its group for a window of its own standing just off it, the
    /// group's size, its carets with it.</summary>
    public void OpenInNewWindow(EditorGroup from, OpenDocument document)
    {
        if (TopLevel.GetTopLevel(from) is not null)
        {
            Detach(from, document, from.PointToScreen(new Point(NewWindowOffset, NewWindowOffset)));
        }
    }

    /// <summary>The window the person works in now, one its keys came from or that came to the front: the group last
    /// worked in there is the active one, which the IDE's actions act on.</summary>
    public void Front(TopLevel? window)
    {
        if (window is not null && window == host)
        {
            Front(main);
        }
        else if (frames.FirstOrDefault(f => f.Window is { } own && own.Window == window) is { } frame)
        {
            Front(frame);
        }
    }

    private void Front(EditorFrame frame) => SetActiveGroup(frame.Current);

    /// <summary>A window's frame brought to the front, as a tab dropped into it from another goes with the keys.</summary>
    private void Raise(EditorFrame frame)
    {
        if (frame.Window is { } window)
        {
            window.Window.Activate();
        }
        else
        {
            host?.Activate();
        }
        Front(frame);
    }

    /// <summary>The main window's activation and close, once the area is in it: a window opened before it showed shows
    /// with it.</summary>
    private void FollowHost()
    {
        if (host is not null || TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }
        host = window;
        host.Activated += (_, _) => Front(main);
        host.Opened += (_, _) => ShowWaiting();
        host.Closed += (_, _) => CloseWindows();
        if (host.IsVisible)
        {
            ShowWaiting();
        }
    }

    private void ShowWaiting()
    {
        foreach (Window waiting in unshown)
        {
            waiting.Show();
        }
        unshown.Clear();
    }

    /// <summary>A tab out of its group into a new window whose place, in screen pixels, is given, the size of the group
    /// it left; that group goes if it was its last tab, and a window it was in with it.</summary>
    private void Detach(EditorGroup from, OpenDocument document, PixelPoint at)
    {
        Size size = new(Math.Max(SmallestNew.Width, from.Bounds.Width), Math.Max(SmallestNew.Height, from.Bounds.Height));
        EditorGroup group = NewGroup();
        if (from.Active == document)
        {
            from.Keep();
        }
        else
        {
            group.Remember(document, from.SelectionsOf(document));
        }
        group.Add(document, pinned: from.IsPinned(document));
        group.Show(document);
        from.Remove(document);
        OpenWindow(new EditorFrame.Leaf(group), group, new PixelRect(at.X, at.Y, (int)size.Width, (int)size.Height), activate: true);
        Collapse();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A window of its own for a tree of groups, standing where it is placed, the keys going to it when asked.</summary>
    private void OpenWindow(EditorFrame.Node root, EditorGroup current, PixelRect? placed, bool activate)
    {
        EditorFrame frame = NewFrame(root, current);
        FloatWindow shown = new(this, placed, placed is { } at ? new Size(at.Width, at.Height) : new Size(800, 600))
        {
            Content = frame.Surface,
            MinWidth = 320,
            MinHeight = 200,
            ShowActivated = activate,
            MayClose = () => CloseTabsOf(frame),
        };
        EditorWindow window = new(frame, shown);
        frame.Window = window;
        shown.Activated += (_, _) => Front(frame);
        shown.Dismissed += (_, _) => Dismissed(frame);
        Rebuild(frame);
        WindowOpened?.Invoke(this, window);
        if (host is { IsVisible: true })
        {
            shown.Show();
        }
        else
        {
            unshown.Add(shown);
        }
        if (activate)
        {
            Front(frame);
            current.Editor.Focus();
        }
    }

    /// <summary>The person closing an editor window closes its tabs, each saved first as a tab's close saves it; one that
    /// cannot be keeps the window open with the tabs left.</summary>
    private bool CloseTabsOf(EditorFrame frame)
    {
        frame.Closing = true;
        foreach (EditorGroup group in frame.Groups.ToList())
        {
            foreach (OpenDocument document in group.Tabs.Select(t => t.Document).ToList())
            {
                if (!CloseTab(group, document))
                {
                    frame.Closing = false;
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>An editor window closed without the person's asking (the application quitting): its tabs go as theirs
    /// would, and the window's place with them.</summary>
    private void Dismissed(EditorFrame frame)
    {
        if (!frames.Contains(frame))
        {
            return;
        }
        CloseTabsOf(frame);
        frames.Remove(frame);
        Collapse();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The main window closed: the editor windows go with it, quietly, kept as they stood by what kept the
    /// folder's editors as it closed.</summary>
    private void CloseWindows()
    {
        foreach (EditorFrame frame in frames.Where(f => f.Window is not null).ToList())
        {
            frames.Remove(frame);
            frame.Window!.CloseQuietly();
        }
        unshown.Clear();
        if (FrameOf(activeGroup) is null)
        {
            activeGroup = main.Current;
            activeGroup.IsCurrent = true;
        }
    }

    /// <summary>The editor windows, each where it stands with its splits, tabs and carets, for the folder to open on next
    /// time.</summary>
    public List<KeptEditorWindow> CaptureWindows()
    {
        activeGroup.Keep();
        return [.. frames.Where(f => f.Window is not null).Select(f =>
        {
            PixelRect at = f.Window!.Placement;
            return new KeptEditorWindow { Bounds = [at.X, at.Y, at.Width, at.Height], Editors = Capture(f.Root, f.Current) };
        })];
    }

    /// <summary>Opens the windows a folder kept, where they stood, behind the main window; whether any opened.</summary>
    private bool RestoreWindows(IReadOnlyList<KeptEditorWindow> windows)
    {
        bool any = false;
        foreach (KeptEditorWindow kept in windows)
        {
            if (kept.Editors is { } editors && Restore(editors, out EditorGroup? current) is { } tree)
            {
                PixelRect? placed = kept.Bounds is [int x, int y, int width, int height] ? new PixelRect(x, y, width, height) : null;
                OpenWindow(tree, current ?? tree.Groups.First(), placed, activate: false);
                any = true;
            }
        }
        return any;
    }

    /// <summary>Each editor window named as the main window is: the folder, and the file its group last worked in shows.</summary>
    private void TitleWindows()
    {
        foreach (EditorFrame frame in frames)
        {
            if (frame.Window is { } window)
            {
                string? file = frame.Current.Active?.Name;
                window.Window.Title = file is null ? folderName ?? "Passenger" : folderName is null ? file : $"{folderName} – {file}";
            }
        }
    }
}
