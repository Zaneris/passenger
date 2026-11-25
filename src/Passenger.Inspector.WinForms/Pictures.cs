using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>A picture of one of a Windows Forms app's windows as the app itself draws it, through each window's own WM_PRINT,
/// so it is the same on the screen, behind other windows or on a desktop of its own: the window's client area with every
/// control in it, each drawn after those behind it and clipped to what holds it (a window's WM_PRINT of its children draws
/// them the other way round), then the drop-downs it has open, where they are over it. Cropped to an element's part, or a
/// part asked for.</summary>
internal static class Pictures
{
    /// <summary>The longer side of a picture when no scale is asked for: the window's own pixels, fewer for a large one.</summary>
    private const int Longest = 1600;

    public static Picture Take(Inspector inspector, Component shown, Request request)
    {
        Control top = Shown.TopOf(shown) is { } held && inspector.TopLevels().Contains(held) ? held : throw new Refusal($"{Shown.Called(shown)} is in no window");
        Rectangle client = Shown.ClientOf(top);
        Rectangle area = request.Area is { } asked
            ? Rectangle.Intersect(Rectangle.Round(new RectangleF((float)asked.X, (float)asked.Y, (float)asked.Width, (float)asked.Height)), client)
            : Rectangle.Intersect(Shown.BoxIn(shown, top) ?? client, client);
        if (area.Width < 1 || area.Height < 1)
        {
            throw new Refusal($"{Shown.Called(shown)} shows nothing of itself to draw");
        }
        using Bitmap whole = new(Math.Max(1, client.Width), Math.Max(1, client.Height), PixelFormat.Format32bppArgb);
        using (Graphics drawing = Graphics.FromImage(whole))
        {
            drawing.Clear(top.BackColor);
            Point origin = top.PointToScreen(Point.Empty);
            Draw(drawing, top, origin, client, own: false);
            foreach (Control open in inspector.TopLevels().Where(t => t is ToolStripDropDown && t.Visible && Inspector.OwnerOf(t) == top))
            {
                Point at = open.PointToScreen(Point.Empty);
                Draw(drawing, open, origin, new Rectangle(new Point(at.X - origin.X, at.Y - origin.Y), open.ClientSize), own: true);
            }
        }
        double scale = Math.Clamp(request.Scale ?? Math.Min(1, (double)Longest / Math.Max(area.Width, area.Height)), 0.05, 8);
        int width = Math.Max(1, (int)Math.Round(area.Width * scale));
        int height = Math.Max(1, (int)Math.Round(area.Height * scale));
        using Bitmap cropped = new(width, height, PixelFormat.Format32bppArgb);
        using (Graphics drawing = Graphics.FromImage(cropped))
        {
            drawing.InterpolationMode = scale == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            drawing.PixelOffsetMode = PixelOffsetMode.Half;
            drawing.DrawImage(whole, new Rectangle(0, 0, width, height), area, GraphicsUnit.Pixel);
        }
        using MemoryStream png = new();
        cropped.Save(png, ImageFormat.Png);
        return new Picture(png.ToArray(), width, height, scale, Shown.Boxed(area));
    }

    /// <summary>A control and what it holds into the picture, at its place from the top level's client area's corner on the
    /// screen (<paramref name="origin"/>), clipped to <paramref name="clip"/> in the picture: the control drawn alone where it
    /// holds controls of Windows Forms' own, each of those drawn after it from the back to the front; with its own windows
    /// where it holds none (a combo box's edit box).</summary>
    private static void Draw(Graphics drawing, Control control, Point origin, Rectangle clip, bool own)
    {
        if (!control.IsHandleCreated || !control.Visible || clip.Width < 1 || clip.Height < 1)
        {
            return;
        }
        Rectangle window = Window(control, origin, own);
        Control[] held = [.. control.Controls.Cast<Control>().Where(c => c.Visible && c.IsHandleCreated)];
        Print(drawing, control, window, Rectangle.Intersect(clip, window), held.Length > 0 ? 0 : Native.PrintChildren);
        Point inside = control.PointToScreen(Point.Empty);
        Rectangle holds = Rectangle.Intersect(clip, new Rectangle(new Point(inside.X - origin.X, inside.Y - origin.Y), control.ClientSize));
        for (int i = held.Length - 1; i >= 0; i--)
        {
            Draw(drawing, held[i], origin, holds, own: false);
        }
    }

    /// <summary>A control's window in the picture: a top level's own client area, a child's window by its parent's.</summary>
    private static Rectangle Window(Control control, Point origin, bool top)
    {
        if (top || control.Parent is null)
        {
            Point client = control.PointToScreen(Point.Empty);
            return new Rectangle(new Point(client.X - origin.X, client.Y - origin.Y), control.ClientSize);
        }
        Point corner = control.Parent.PointToScreen(control.Location);
        return new Rectangle(new Point(corner.X - origin.X, corner.Y - origin.Y), control.Size);
    }

    /// <summary>One window's own drawing into its place, kept to the part shown: a top level's client area and background
    /// (WM_PRINTCLIENT, drawn at its corner), a child's frame too, with its own windows by <paramref name="children"/>.</summary>
    private static void Print(Graphics drawing, Control control, Rectangle window, Rectangle shown, int children)
    {
        if (shown.Width < 1 || shown.Height < 1)
        {
            return;
        }
        bool client = control.Parent is null;
        using Bitmap own = new(Math.Max(1, window.Width), Math.Max(1, window.Height), PixelFormat.Format32bppArgb);
        using (Graphics into = Graphics.FromImage(own))
        {
            nint device = into.GetHdc();
            try
            {
                Native.SendMessage(control.Handle, client ? Native.PrintClientOnly : Native.Print, device,
                    Native.PrintClient | Native.PrintBackground | (client ? 0 : Native.PrintFrame | children));
            }
            finally
            {
                into.ReleaseHdc(device);
            }
        }
        Rectangle from = new(shown.X - window.X, shown.Y - window.Y, shown.Width, shown.Height);
        drawing.DrawImage(own, shown, from, GraphicsUnit.Pixel);
    }
}
