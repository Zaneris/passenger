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
/// window's scale, its pixels kept sharp). A click on the frame chooses the component under it, which is outlined; with the
/// keys, Tab and Shift+Tab go from component to component and Escape to the one holding the chosen one.</summary>
public sealed class FormDesignPane : DockPanel
{
    /// <summary>How far the outline stands outside the box it outlines.</summary>
    public const double OutlineWidth = 2;

    private readonly FormDesign design;
    private readonly TextBlock status;
    private readonly Banner notes = Banner.Wrapping(error: true);
    private readonly Image image;
    private readonly Canvas marks;
    private readonly Border outline;
    private readonly Border edge;
    private readonly ToolWindows.EmptyState waiting;

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
        marks = new Canvas { IsHitTestVisible = false, Children = { outline } };
        Panel framed = new() { Children = { image, marks } };
        framed.PointerPressed += OnFramePressed;
        // An edge, so a form of the editor's own colour still shows where it ends.
        edge = new Border { Child = framed, BorderThickness = Hairlines.Round, Margin = new Thickness(Gaps.Group), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        edge.Bind(Border.BorderBrushProperty, edge.GetResourceObservable(TokenKeys.Divider));
        waiting = new ToolWindows.EmptyState { MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Center };
        Panel view = new() { ClipToBounds = true, Children = { new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = edge }, waiting } };
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

    public FormDesign Design => design;

    /// <summary>The frame shown.</summary>
    public Image Frame => image;

    /// <summary>The component chosen, outlined; null while none is.</summary>
    public DesignedBox? Chosen { get; private set; }

    /// <summary>What the pane says over the frame of the code it could not draw; null while it says nothing.</summary>
    public string? NotesShown => notes.Said;

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
        if (!e.GetCurrentPoint(image).Properties.IsLeftButtonPressed || ComponentAt(e.GetPosition(image)) is not { } chosen)
        {
            return;
        }
        e.Handled = true;
        Focus();
        Picked(chosen);
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
            return;
        }
        // Around the box rather than on its edge, so it shows on a control drawn in the accent's own colour.
        Canvas.SetLeft(outline, box.X - OutlineWidth);
        Canvas.SetTop(outline, box.Y - OutlineWidth);
        outline.Width = box.Width + (2 * OutlineWidth);
        outline.Height = box.Height + (2 * OutlineWidth);
        outline.IsVisible = true;
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
