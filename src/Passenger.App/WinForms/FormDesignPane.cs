using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.WinForms;

/// <summary>A form's design beside its code, or alone in the editor's place: a header naming what the designer is doing, what it
/// could not make of the code over the frame, and the frame as Windows Forms draws it, a pixel a unit of the code (at the
/// window's scale, its pixels kept sharp). A click on the frame chooses the component under it, outlined with grab handles;
/// a drag moves it (into the container it is dropped on), a handle's drag resizes it, each asked of the window as an edit
/// (<see cref="EditRequested"/>), its outline drawn meanwhile. With the keys, Tab and Shift+Tab go from component to
/// component and Escape to the one holding it, the arrows move it a unit and Shift with them sizes it.</summary>
public sealed class FormDesignPane : DockPanel
{
    /// <summary>How far the outline stands outside the box it outlines.</summary>
    public const double OutlineWidth = 2;

    /// <summary>A grab handle's side.</summary>
    public const double HandleSize = 7;

    /// <summary>How far a press moves before it is a drag.</summary>
    private const double DragThreshold = 4;

    /// <summary>The handles' places on a box, as fractions of its width and height: the corners and the middles of its sides.</summary>
    private static readonly (double X, double Y)[] Places = [(0, 0), (0.5, 0), (1, 0), (1, 0.5), (1, 1), (0.5, 1), (0, 1), (0, 0.5)];

    private readonly FormDesign design;
    private readonly TextBlock status;
    private readonly Banner notes = Banner.Wrapping(error: true);
    private readonly Image image;
    private readonly Canvas marks;
    private readonly Border outline;
    private readonly Border edge;
    private readonly ToolWindows.EmptyState waiting;
    private readonly Border ghost;
    private readonly Border[] handles = new Border[Places.Length];

    /// <summary>A press on the frame: what it took (a component, or one of its handles), where, and whether it has become a drag.</summary>
    private (DesignedBox Box, int Handle, Point Start, bool Dragging)? pressed;

    public FormDesignPane(FormDesign design)
    {
        this.design = design;
        Focusable = true;
        AutomationProperties.SetName(this, $"Design of {design.Files.Class[(design.Files.Class.LastIndexOf('.') + 1)..]}");
        status = new TextBlock { Classes = { StyleClasses.Muted, StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Border header = ToolWindows.ToolBars.OverEditor(new DockPanel
        {
            Margin = new Thickness(ListRow.TextStart, 0),
            Children =
            {
                new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = "Design", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Gaps.Normal, 0) },
                status,
            },
        });
        image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        outline = new Border { BorderThickness = new Thickness(OutlineWidth), IsVisible = false, IsHitTestVisible = false };
        outline.Bind(Border.BorderBrushProperty, outline.GetResourceObservable(TokenKeys.Accent));
        ghost = new Border { BorderThickness = new Thickness(Hairlines.Width), IsVisible = false, IsHitTestVisible = false };
        ghost.Bind(Border.BorderBrushProperty, ghost.GetResourceObservable(TokenKeys.Accent));
        marks = new Canvas { IsHitTestVisible = false, Children = { outline, ghost } };
        for (int i = 0; i < handles.Length; i++)
        {
            Border handle = new() { Width = HandleSize, Height = HandleSize, BorderThickness = new Thickness(Hairlines.Width), IsVisible = false };
            handle.Bind(Border.BorderBrushProperty, handle.GetResourceObservable(TokenKeys.Accent));
            handle.Bind(Border.BackgroundProperty, handle.GetResourceObservable(TokenKeys.Editor_Background));
            handles[i] = handle;
            marks.Children.Add(handle);
        }
        Panel framed = new() { Children = { image, marks } };
        // An edge, so a form of the editor's own colour still shows where it ends.
        edge = new Border { Child = framed, BorderThickness = Hairlines.Round, Margin = new Thickness(Gaps.Group), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        edge.Bind(Border.BorderBrushProperty, edge.GetResourceObservable(TokenKeys.Divider));
        waiting = new ToolWindows.EmptyState { MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Center };
        Panel view = new() { ClipToBounds = true, Background = Brushes.Transparent, Children = { new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = edge }, waiting } };
        // The view takes the presses, so a handle standing past the frame's edge is grabbed too.
        view.PointerPressed += OnFramePressed;
        view.PointerMoved += OnFrameMoved;
        view.PointerReleased += OnFrameReleased;
        view.PointerCaptureLost += (_, _) => EndDrag();
        SetDock(header, Dock.Top);
        SetDock(notes, Dock.Top);
        Children.Add(header);
        Children.Add(notes);
        Children.Add(view);
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Editor_Background));
        design.Changed += (_, _) => Show();
        Show();
    }

    /// <summary>A component was chosen on the frame, by the pointer or the keys; raised on the UI thread.</summary>
    public event EventHandler<DesignedBox>? ComponentChosen;

    /// <summary>A gesture or a key asks for an edit of the design; raised on the UI thread.</summary>
    public event EventHandler<DesignEdit>? EditRequested;

    public FormDesign Design => design;

    /// <summary>The frame shown.</summary>
    public Image Frame => image;

    /// <summary>The component chosen, outlined; null while none is.</summary>
    public DesignedBox? Chosen { get; private set; }

    /// <summary>What the pane says over the frame of the code it could not draw; null while it says nothing.</summary>
    public string? NotesShown => notes.Said;

    /// <summary>A point of the window's (<paramref name="pointer"/>'s) as a point of the frame, where the frame is shown there;
    /// null elsewhere.</summary>
    public Point? FrameAt(PointerEventArgs pointer)
    {
        if (design.Frame is null || !image.IsEffectivelyVisible)
        {
            return null;
        }
        Point at = pointer.GetPosition(image);
        return at.X >= 0 && at.Y >= 0 && at.X < image.Width && at.Y < image.Height ? new Point(Math.Round(at.X), Math.Round(at.Y)) : null;
    }

    /// <summary>Marks where a Toolbox item dragged over the frame would go, a box of a button's size there; none hides the mark.</summary>
    public void ShowDrop(Point? at)
    {
        ghost.IsVisible = at is not null;
        if (at is { } place)
        {
            Canvas.SetLeft(ghost, place.X);
            Canvas.SetTop(ghost, place.Y);
            ghost.Width = 75;
            ghost.Height = 23;
        }
    }

    /// <summary>Chooses a component by its field (empty for the root), outlining it; none where the frame has no such one.</summary>
    public void Choose(string? field)
    {
        Chosen = field is null ? null : design.Components.FirstOrDefault(c => c.Field == field && c.Width > 0);
        ShowOutline();
    }

    /// <summary>The deepest component drawn at a point of the frame, in its pixels; the root where none other is.</summary>
    public DesignedBox? ComponentAt(Point point) =>
        design.Components.LastOrDefault(c => c.Width > 0 && new Rect(c.X, c.Y, c.Width, c.Height).Contains(point));

    private void OnFramePressed(object? sender, PointerPressedEventArgs e)
    {
        if (design.Frame is null)
        {
            return;
        }
        if (e.GetCurrentPoint(image).Properties.IsRightButtonPressed)
        {
            // The menu that follows is of the component pressed, as the design has the keys.
            if (ComponentAt(e.GetPosition(image)) is { } under && under.Field != Chosen?.Field)
            {
                Picked(under);
            }
            Focus();
            return;
        }
        if (!e.GetCurrentPoint(image).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Point at = e.GetPosition(image);
        int handle = HandleAt(at);
        if (handle < 0 && ComponentAt(at) is { } chosen)
        {
            Picked(chosen);
        }
        else if (handle < 0)
        {
            return;
        }
        e.Handled = true;
        Focus();
        pressed = (Chosen!, handle, at, false);
        e.Pointer.Capture((IInputElement?)sender);
    }

    private void OnFrameMoved(object? sender, PointerEventArgs e)
    {
        if (pressed is not { } press)
        {
            return;
        }
        Point at = e.GetPosition(image);
        Vector moved = at - press.Start;
        if (!press.Dragging && (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold || press.Handle < 0 && press.Box.Parent is null))
        {
            return;
        }
        pressed = press with { Dragging = true };
        Rect box = Dragged(press, at);
        Canvas.SetLeft(ghost, box.X);
        Canvas.SetTop(ghost, box.Y);
        ghost.Width = Math.Max(1, box.Width);
        ghost.Height = Math.Max(1, box.Height);
        ghost.IsVisible = true;
    }

    private void OnFrameReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (pressed is not { Dragging: true } press)
        {
            EndDrag();
            return;
        }
        Point at = e.GetPosition(image);
        Rect box = Dragged(press, at);
        EndDrag();
        e.Handled = true;
        if (press.Handle >= 0)
        {
            EditRequested?.Invoke(this, new DesignEdit(DesignEditKinds.Resize)
            {
                Components = [press.Box.Field], X = (int)box.X, Y = (int)box.Y, Width = (int)Math.Max(1, box.Width), Height = (int)Math.Max(1, box.Height),
            });
            return;
        }
        string? into = ContainerAt(at, press.Box) is { } container && container.Field != press.Box.Parent ? container.Field : null;
        EditRequested?.Invoke(this, new DesignEdit(DesignEditKinds.Move) { Components = [press.Box.Field], X = (int)(box.X - press.Box.X), Y = (int)(box.Y - press.Box.Y), Parent = into });
    }

    private void EndDrag()
    {
        pressed = null;
        ghost.IsVisible = false;
    }

    /// <summary>A pressed box as the pointer has dragged it: moved whole, or by the handle taken, in whole units of the frame.</summary>
    private static Rect Dragged((DesignedBox Box, int Handle, Point Start, bool Dragging) press, Point at)
    {
        double dx = Math.Round(at.X - press.Start.X);
        double dy = Math.Round(at.Y - press.Start.Y);
        DesignedBox box = press.Box;
        if (press.Handle < 0)
        {
            return new Rect(box.X + dx, box.Y + dy, box.Width, box.Height);
        }
        (double x, double y) = Places[press.Handle];
        double left = box.X + (x == 0 ? dx : 0);
        double top = box.Y + (y == 0 ? dy : 0);
        double right = box.X + box.Width + (x == 1 ? dx : 0);
        double bottom = box.Y + box.Height + (y == 1 ? dy : 0);
        return new Rect(Math.Min(left, right - 1), Math.Min(top, bottom - 1), Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>The chosen component's handle at a point of the frame; -1 where none is.</summary>
    private int HandleAt(Point at)
    {
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i].IsVisible && new Rect(Canvas.GetLeft(handles[i]), Canvas.GetTop(handles[i]), HandleSize, HandleSize).Inflate(1).Contains(at))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The deepest component holding controls at a point of the frame that a box dropped there may go into: never the
    /// box itself nor one it holds.</summary>
    private DesignedBox? ContainerAt(Point at, DesignedBox moved) =>
        design.Components.LastOrDefault(c => c.Holds && c.Width > 0 && new Rect(c.X, c.Y, c.Width, c.Height).Contains(at) && !Within(c, moved.Field));

    /// <summary>Whether a box is the component named or one it holds.</summary>
    private bool Within(DesignedBox box, string field)
    {
        for (DesignedBox? at = box; at is not null; at = at.Parent is { } parent ? design.Components.FirstOrDefault(c => c.Field == parent) : null)
        {
            if (at.Field == field)
            {
                return true;
            }
            if (at.Parent is null)
            {
                return false;
            }
        }
        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        List<DesignedBox> drawn = [.. design.Components.Where(c => c.Width > 0)];
        if (e.Handled || drawn.Count == 0)
        {
            return;
        }
        int at = Chosen is { } chosen ? drawn.FindIndex(c => c.Field == chosen.Field) : -1;
        DesignedBox? next = (e.Key, e.KeyModifiers) switch
        {
            (Key.Tab, KeyModifiers.None) => drawn[(at + 1) % drawn.Count],
            (Key.Tab, KeyModifiers.Shift) => drawn[(at <= 0 ? drawn.Count : at) - 1],
            (Key.Escape, KeyModifiers.None) when Chosen?.Parent is { } parent => drawn.FirstOrDefault(c => c.Field == parent),
            _ => null,
        };
        if (next is not null)
        {
            e.Handled = true;
            Picked(next);
            return;
        }
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            Key.Down => (0, 1),
            _ => (0, 0),
        };
        if (Chosen is not { } box || (dx, dy) == (0, 0) || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return;
        }
        e.Handled = true;
        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            EditRequested?.Invoke(this, new DesignEdit(DesignEditKinds.Resize)
            {
                Components = [box.Field], X = (int)box.X, Y = (int)box.Y, Width = (int)Math.Max(1, box.Width + dx), Height = (int)Math.Max(1, box.Height + dy),
            });
        }
        else if (box.Parent is not null)
        {
            EditRequested?.Invoke(this, new DesignEdit(DesignEditKinds.Move) { Components = [box.Field], X = dx, Y = dy });
        }
    }

    private void Picked(DesignedBox chosen)
    {
        Chosen = chosen;
        ShowOutline();
        ComponentChosen?.Invoke(this, chosen);
    }

    private void ShowOutline()
    {
        if (Chosen is not { } box || design.Frame is null)
        {
            outline.IsVisible = false;
            foreach (Border handle in handles)
            {
                handle.IsVisible = false;
            }
            return;
        }
        // Around the box rather than on its edge, so it shows on a control drawn in the accent's own colour.
        Canvas.SetLeft(outline, box.X - OutlineWidth);
        Canvas.SetTop(outline, box.Y - OutlineWidth);
        outline.Width = box.Width + (2 * OutlineWidth);
        outline.Height = box.Height + (2 * OutlineWidth);
        outline.IsVisible = true;
        for (int i = 0; i < handles.Length; i++)
        {
            (double x, double y) = Places[i];
            Canvas.SetLeft(handles[i], box.X - OutlineWidth + (x * (box.Width + (2 * OutlineWidth))) - (HandleSize / 2));
            Canvas.SetTop(handles[i], box.Y - OutlineWidth + (y * (box.Height + (2 * OutlineWidth))) - (HandleSize / 2));
            // The root sizes from its far sides alone, as its place is the frame's.
            handles[i].IsVisible = box.Width > 0 && (box.Parent is not null || x == 1 || y == 1);
        }
    }

    private void Show()
    {
        status.Text = design.Status;
        Bitmap? frame = design.Frame;
        image.Source = frame;
        image.Width = frame?.PixelSize.Width ?? 0;
        image.Height = frame?.PixelSize.Height ?? 0;
        marks.Width = image.Width;
        marks.Height = image.Height;
        edge.IsVisible = frame is not null;
        waiting.Text = design.Status;
        waiting.IsVisible = frame is null;
        notes.Say(design.Notes.Count == 0 ? null : string.Join("\n", design.Notes.Take(5)) + (design.Notes.Count > 5 ? $"\n… and {design.Notes.Count - 5} more" : string.Empty));
        Choose(Chosen?.Field);
    }
}
