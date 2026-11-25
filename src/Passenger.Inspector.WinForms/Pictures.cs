using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>A picture of one of a Windows Forms app's windows as the app itself draws it (<see cref="ControlDrawing"/>), so it
/// is the same on the screen, behind other windows or on a desktop of its own: the window's client area with every control
/// in it, then the drop-downs it has open, where they are over it. Cropped to an element's part, or a part asked for.</summary>
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
            ControlDrawing.Draw(drawing, top, origin, client, alone: false);
            foreach (Control open in inspector.TopLevels().Where(t => t is ToolStripDropDown && t.Visible && Inspector.OwnerOf(t) == top))
            {
                Point at = open.PointToScreen(Point.Empty);
                ControlDrawing.Draw(drawing, open, origin, new Rectangle(new Point(at.X - origin.X, at.Y - origin.Y), open.ClientSize), alone: true);
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
}
