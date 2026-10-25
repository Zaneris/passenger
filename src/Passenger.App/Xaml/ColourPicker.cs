using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Xaml.Features;

namespace Passenger.App.Xaml;

/// <summary>A colour picker over a swatch: a square of saturation and brightness for the hue chosen on the bar under
/// it, an alpha bar, the colour before and after, and its hex to read or type. Choose (or Enter) gives the colour,
/// Cancel or Escape leaves it. Its gradients are the colours being chosen; its chrome is the theme's.</summary>
internal sealed class ColourPicker : Border
{
    // Styled as the Border it is, so the theme's Border.popup reaches it.
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly ShadeSquare square;
    private readonly HueBar hue;
    private readonly AlphaBar alpha;
    private readonly Border after;
    private readonly TextBox hex;
    private double h;
    private double s;
    private double v;
    private byte a;
    private bool typing;

    public ColourPicker(Argb start)
    {
        Classes.Add("popup");
        Padding = new Thickness(10);
        (h, s, v) = ToHsv(start);
        a = start.A;
        square = new ShadeSquare(this) { Height = 160 };
        hue = new HueBar(this) { Height = 14 };
        alpha = new AlphaBar(this) { Height = 14 };
        // The swatches as tall as the field and buttons beside them, rounded as they are.
        Border before = new() { Width = 28, Height = 28, Background = new SolidColorBrush(ToColor(start)) };
        after = new Border { Width = 28, Height = 28 };
        before.Bind(Border.CornerRadiusProperty, before.GetResourceObservable(TokenKeys.ControlRadius, r => r is CornerRadius c ? new CornerRadius(c.TopLeft, 0, 0, c.BottomLeft) : default(CornerRadius)));
        after.Bind(Border.CornerRadiusProperty, after.GetResourceObservable(TokenKeys.ControlRadius, r => r is CornerRadius c ? new CornerRadius(0, c.TopRight, c.BottomRight, 0) : default(CornerRadius)));
        // The field takes what the swatches and buttons leave.
        hex = new TextBox { Classes = { "search" }, FontFamily = new FontFamily("avares://Passenger.Editor/Fonts#JetBrains Mono") };
        Button choose = new() { Content = "Choose", Classes = { "primary" }, IsDefault = true };
        Button cancel = new() { Content = "Cancel" };
        choose.Click += (_, _) => Chosen?.Invoke(this, Colour);
        cancel.Click += (_, _) => Chosen?.Invoke(this, null);
        hex.TextChanged += (_, _) => FromHex();
        Child = new StackPanel
        {
            Width = 330,
            Spacing = 8,
            Children =
            {
                square,
                hue,
                alpha,
                new DockPanel
                {
                    Children =
                    {
                        new StackPanel { [DockPanel.DockProperty] = Dock.Left, Orientation = Orientation.Horizontal, Children = { before, after } },
                        new StackPanel { [DockPanel.DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, choose } },
                        new Panel { Margin = new Thickness(8, 0), Children = { hex } },
                    },
                },
            },
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Show();
        Dispatcher.UIThread.Post(() =>
        {
            hex.Focus();
            hex.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The colour chosen, or null when the picker was left.</summary>
    public event EventHandler<Argb?>? Chosen;

    /// <summary>The colour as the picker has it now.</summary>
    public Argb Colour
    {
        get
        {
            (byte r, byte g, byte b) = FromHsv(h, s, v);
            return new Argb(a, r, g, b);
        }
    }

    /// <summary>The hue's full colour, the square's corner.</summary>
    private Color HueColour
    {
        get
        {
            (byte r, byte g, byte b) = FromHsv(h, 1, 1);
            return Color.FromRgb(r, g, b);
        }
    }

    /// <summary>Takes a colour as typed in the hex field, once it reads as one.</summary>
    private void FromHex()
    {
        if (typing || KnownColours.Parse(hex.Text) is not { } typed)
        {
            return;
        }
        (h, s, v) = ToHsv(typed);
        a = typed.A;
        typing = true;
        Show();
        typing = false;
    }

    /// <summary>Sets what the pointer chose on the square, the hue or the alpha bar.</summary>
    private void Pick(double? hueAt = null, (double S, double V)? shade = null, double? alphaAt = null)
    {
        h = hueAt ?? h;
        (s, v) = shade ?? (s, v);
        a = alphaAt is { } chosen ? (byte)Math.Round(chosen * 255) : a;
        Show();
    }

    private void Show()
    {
        Argb colour = Colour;
        after.Background = new SolidColorBrush(ToColor(colour));
        if (!typing)
        {
            hex.Text = colour.Hex;
        }
        square.InvalidateVisual();
        hue.InvalidateVisual();
        alpha.InvalidateVisual();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Chosen?.Invoke(this, null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Chosen?.Invoke(this, Colour);
            e.Handled = true;
        }
    }

    private static Color ToColor(Argb colour) => Color.FromArgb(colour.A, colour.R, colour.G, colour.B);

    private static (double H, double S, double V) ToHsv(Argb colour)
    {
        double r = colour.R / 255.0;
        double g = colour.G / 255.0;
        double b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        return ((hue + 360) % 360, max == 0 ? 0 : delta / max, max);
    }

    private static (byte R, byte G, byte B) FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        double m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>A control the pointer drags across, which takes the place it is pressed or dragged to as a fraction of
    /// its width and height.</summary>
    private abstract class Area : Control
    {
        protected Area(ColourPicker picker)
        {
            Picker = picker;
            Cursor = new Cursor(StandardCursorType.Cross);
        }

        protected ColourPicker Picker { get; }

        protected abstract void At(double x, double y);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            e.Pointer.Capture(this);
            Take(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (e.Pointer.Captured == this)
            {
                Take(e.GetPosition(this));
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) => e.Pointer.Capture(null);

        private void Take(Point point) =>
            At(Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1), Math.Clamp(point.Y / Math.Max(1, Bounds.Height), 0, 1));
    }

    /// <summary>Saturation left to right, brightness bottom to top, over the hue chosen.</summary>
    private sealed class ShadeSquare(ColourPicker picker) : Area(picker)
    {
        protected override void At(double x, double y) => Picker.Pick(shade: (x, 1 - y));

        public override void Render(DrawingContext context)
        {
            Rect area = new(Bounds.Size);
            // White to clear and clear to black are the colour model's own saturation and brightness, not the window's.
            context.FillRectangle(new SolidColorBrush(Picker.HueColour), area, 3);
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
            }, area, 3);
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
            }, area, 3);
            Point at = new(Picker.s * area.Width, (1 - Picker.v) * area.Height);
            context.DrawEllipse(null, new Pen(Token(TokenKeys.Picker_RingInner), 2), at, 5, 5);
            context.DrawEllipse(null, new Pen(Token(TokenKeys.Picker_RingOuter), 1), at, 6.5, 6.5);
        }
    }

    /// <summary>The hues, red through the spectrum back to red.</summary>
    private sealed class HueBar(ColourPicker picker) : Area(picker)
    {
        protected override void At(double x, double y) => Picker.Pick(hueAt: Math.Min(359.9, x * 360));

        public override void Render(DrawingContext context)
        {
            Rect area = new(Bounds.Size);
            LinearGradientBrush spectrum = new()
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            };
            for (int i = 0; i <= 6; i++)
            {
                (byte r, byte g, byte b) = FromHsv(i * 60 % 360, 1, 1);
                spectrum.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 6.0));
            }
            context.FillRectangle(spectrum, area, 3);
            Marker(context, area, Picker.h / 360);
        }
    }

    /// <summary>The colour from clear to opaque.</summary>
    private sealed class AlphaBar(ColourPicker picker) : Area(picker)
    {
        protected override void At(double x, double y) => Picker.Pick(alphaAt: x);

        public override void Render(DrawingContext context)
        {
            Rect area = new(Bounds.Size);
            Argb colour = Picker.Colour;
            for (double x = 0; x < area.Width; x += 7)
            {
                for (double y = 0; y < area.Height; y += 7)
                {
                    if (((int)(x / 7) + (int)(y / 7)) % 2 == 0)
                    {
                        context.FillRectangle(Token(TokenKeys.Picker_Checker), new Rect(x, y, Math.Min(7, area.Width - x), Math.Min(7, area.Height - y)));
                    }
                }
            }
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), 0), new GradientStop(Color.FromRgb(colour.R, colour.G, colour.B), 1) },
            }, area, 3);
            Marker(context, area, Picker.a / 255.0);
        }
    }

    private static void Marker(DrawingContext context, Rect area, double at)
    {
        double x = Math.Clamp(at * area.Width, 2, area.Width - 2);
        context.DrawRectangle(null, new Pen(Token(TokenKeys.Picker_RingInner), 2), new Rect(x - 3, -1, 6, area.Height + 2), 2, 2);
        context.DrawRectangle(null, new Pen(Token(TokenKeys.Picker_RingOuter), 1), new Rect(x - 4.5, -2.5, 9, area.Height + 5), 3, 3);
    }

    private static IBrush Token(string key) => CSharp.InfoCard.Brush(key);
}
