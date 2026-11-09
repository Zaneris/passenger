using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.LiveTree;

/// <summary>A slider along a range: a track, the stretch up to the value filled in the accent, and a thumb, drawn in the
/// look's tokens. Pressed or dragged, it takes the place under the pointer (<see cref="Changing"/> as it moves, then
/// <see cref="Committed"/> as it is let go); Left and Right step it, Page Up and Page Down ten steps. The range turns the
/// place into a value (<see cref="Fraction"/>, <see cref="At"/>).</summary>
public sealed class ValueSlider : Control
{
    private double fraction;
    private bool dragging;

    public ValueSlider()
    {
        Focusable = true;
        Height = 20;
        MinWidth = 60;
        Cursor = new Cursor(StandardCursorType.Hand);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Turns a place along the track (0 to 1) into the value there.</summary>
    public Func<double, double> At { get; set; } = f => f;

    /// <summary>Turns a value into its place along the track.</summary>
    public Func<double, double> Fraction { get; set; } = v => v;

    /// <summary>How far one step of the keys moves the value.</summary>
    public double Step { get; set; } = 0.01;

    /// <summary>The value shown.</summary>
    public double Value { get; private set; }

    /// <summary>The value as the pointer drags it, at each move.</summary>
    public event EventHandler<double>? Changing;

    /// <summary>The value as the pointer let go of it, or a key set it.</summary>
    public event EventHandler<double>? Committed;

    /// <summary>Whether the pointer holds the thumb.</summary>
    public bool IsDragging => dragging;

    /// <summary>Shows a value, unless the pointer is dragging the thumb.</summary>
    public void Show(double value)
    {
        if (dragging)
        {
            return;
        }
        Value = value;
        fraction = Math.Clamp(Fraction(value), 0, 1);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Pointer.Capture(this);
        dragging = true;
        Focus();
        Take(e.GetPosition(this), Changing);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging && e.Pointer.Captured == this)
        {
            Take(e.GetPosition(this), Changing);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!dragging)
        {
            return;
        }
        Take(e.GetPosition(this), null);
        dragging = false;
        e.Pointer.Capture(null);
        Committed?.Invoke(this, Value);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (dragging)
        {
            dragging = false;
            Committed?.Invoke(this, Value);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double steps = e.Key switch
        {
            Key.Left or Key.Down => -1,
            Key.Right or Key.Up => 1,
            Key.PageDown => -10,
            Key.PageUp => 10,
            _ => 0,
        };
        if (steps == 0)
        {
            return;
        }
        e.Handled = true;
        double value = At(Fraction(Value + (steps * Step)));
        Value = value;
        fraction = Math.Clamp(Fraction(value), 0, 1);
        InvalidateVisual();
        Committed?.Invoke(this, value);
    }

    private void Take(Point point, EventHandler<double>? told)
    {
        double inset = Bounds.Height / 2;
        double width = Math.Max(1, Bounds.Width - (inset * 2));
        double value = At(Math.Clamp((point.X - inset) / width, 0, 1));
        bool moved = value != Value;
        Value = value;
        fraction = Math.Clamp(Fraction(value), 0, 1);
        InvalidateVisual();
        if (moved)
        {
            told?.Invoke(this, value);
        }
    }

    public override void Render(DrawingContext context)
    {
        double inset = Bounds.Height / 2;
        double width = Math.Max(1, Bounds.Width - (inset * 2));
        double middle = Bounds.Height / 2;
        double x = inset + (fraction * width);
        context.FillRectangle(Brush(TokenKeys.Divider), new Rect(inset, middle - 2, width, 4), 2);
        context.FillRectangle(Brush(IsEffectivelyEnabled ? TokenKeys.Accent : TokenKeys.TextDisabled), new Rect(inset, middle - 2, x - inset, 4), 2);
        context.DrawEllipse(Brush(TokenKeys.Popup), new Pen(Brush(IsFocused ? TokenKeys.Focus : TokenKeys.Accent), 2), new Point(x, middle), 6, 6);
    }

    private IBrush Brush(string key) => Tokens.Brush(this, key);
}

/// <summary>A number to type or scrub: an axis's name (x, y, z), or a grip where it has none, that the pointer drags left
/// and right to move the value a step a few pixels (<see cref="Changing"/> as it moves, <see cref="Committed"/> as it is
/// let go), and a box to type it in, taken with Enter. Shift scrubs ten times as fast, Alt a tenth as fast. The box shows the
/// value at <see cref="ShownStep"/> as Godot's inspector does, the whole value kept until the person changes it.</summary>
public sealed class ScrubField : DockPanel
{
    private readonly TextBlock label;
    private readonly TextBox box;
    private string? written;
    private Point? pressed;
    private double from;
    private bool scrubbed;

    public ScrubField(string axis)
    {
        label = new TextBlock
        {
            Text = axis.Length > 0 ? axis : "⇔",
            Classes = { StyleClasses.Muted },
            FontSize = 11,
            Padding = new Thickness(1, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        ToolTip.SetTip(label, "Drag left or right to change it: Shift for faster, Alt for finer");
        SetDock(label, Dock.Left);
        box = new TextBox { FontSize = 11.5, MinWidth = 0, Padding = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center }.InCodeFont();
        Children.Add(label);
        Children.Add(box);
        label.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed)
            {
                return;
            }
            pressed = e.GetPosition(this);
            from = Value;
            scrubbed = false;
            e.Pointer.Capture(label);
            e.Handled = true;
        };
        label.PointerMoved += (_, e) =>
        {
            if (pressed is not { } start || e.Pointer.Captured != label)
            {
                return;
            }
            double pixels = e.GetPosition(this).X - start.X;
            double scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0.1 : 1;
            double value = Round(from + (Math.Round(pixels / 4) * Step * scale));
            if (value != Value)
            {
                scrubbed = true;
                Shown(value);
                Changing?.Invoke(this, value);
            }
        };
        label.PointerReleased += (_, e) =>
        {
            if (pressed is null)
            {
                return;
            }
            pressed = null;
            e.Pointer.Capture(null);
            if (scrubbed)
            {
                Committed?.Invoke(this, Value);
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && box.Text == written)
            {
                // The value as shown, rounded: the whole one it stands for stays.
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed))
            {
                e.Handled = true;
                Shown(Round(typed));
                Committed?.Invoke(this, Value);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Shown(Value);
            }
        };
    }

    /// <summary>How far one step of a scrub moves the value.</summary>
    public double Step { get; set; } = 0.1;

    /// <summary>The step whose decimals the box shows the value to (<see cref="Passenger.App.Godot.GodotText.Inspected"/>);
    /// 0 shows it whole.</summary>
    public double ShownStep { get; set; }

    /// <summary>Whether it takes whole numbers alone.</summary>
    public bool Integers { get; set; }

    /// <summary>Its value as shown.</summary>
    public double Value { get; private set; }

    /// <summary>Whether the pointer is scrubbing it.</summary>
    public bool IsScrubbing => pressed is not null;

    /// <summary>The box its value is typed in.</summary>
    public TextBox Box => box;

    /// <summary>The axis's name, which the pointer drags.</summary>
    public TextBlock Grip => label;

    public event EventHandler<double>? Changing;

    public event EventHandler<double>? Committed;

    /// <summary>Shows a value, unless the pointer is scrubbing it or its box is being typed in.</summary>
    public void Show(double value)
    {
        if (IsScrubbing || box.IsFocused)
        {
            return;
        }
        Shown(value);
    }

    private void Shown(double value)
    {
        Value = value;
        box.Text = written = Integers ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : ShownStep > 0 ? Passenger.App.Godot.GodotText.Inspected(value, ShownStep) : Passenger.App.Godot.GodotText.Number(value);
    }

    private double Round(double value) => Integers ? Math.Round(value) : Math.Round(value, 6);
}
