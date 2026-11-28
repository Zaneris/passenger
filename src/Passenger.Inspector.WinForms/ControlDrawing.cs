using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>A window of Windows Forms drawn as the app itself draws it, through each window's own WM_PRINT, so it is the
/// same on the screen, behind other windows, on a desktop of its own or never shown: its client area with every control in
/// it, each drawn after those behind it and clipped to what holds it (a window's WM_PRINT of its children draws them the
/// other way round). One drawing for the inspector's pictures of a running app and the designer's of a form.</summary>
internal static partial class ControlDrawing
{
    private const int Print = 0x0317;
    private const int PrintClientOnly = 0x0318;
    private const int PrintFrame = 0x02;
    private const int PrintClient = 0x04;
    private const int PrintBackground = 0x08;
    private const int PrintChildren = 0x10;

    /// <summary>A window's style's bit for the window showing itself, whatever its parents do.</summary>
    private const long Visible = 0x10000000;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint window, int index);

    /// <summary>Whether a control's window shows itself, as its own style says: a window never shown (the designer's) holds
    /// controls that show though none of them is visible on any screen.</summary>
    public static bool Shows(Control control) => control.IsHandleCreated && (GetWindowLongPtr(control.Handle, -16) & Visible) != 0;

    /// <summary>A control's client area and all it holds into the picture at <paramref name="at"/>, as its window's corner:
    /// the control's own client area drawn alone (WM_PRINTCLIENT, its frame left out), then what it holds.</summary>
    public static void Client(Graphics drawing, Control control, Point at)
    {
        Point origin = control.PointToScreen(Point.Empty);
        origin.Offset(-at.X, -at.Y);
        Draw(drawing, control, origin, new Rectangle(at, control.ClientSize), alone: true);
    }

    /// <summary>A control and what it holds into the picture, at its place from <paramref name="origin"/> (the screen's point
    /// at the picture's corner), clipped to <paramref name="clip"/> in the picture: the control drawn alone where it holds
    /// controls of Windows Forms' own, each of those drawn after it from the back to the front; with its own windows where it
    /// holds none (a combo box's edit box). <paramref name="alone"/> draws a control's client area alone, as a top level's.</summary>
    public static void Draw(Graphics drawing, Control control, Point origin, Rectangle clip, bool alone)
    {
        if (!Shows(control) || clip.Width < 1 || clip.Height < 1)
        {
            return;
        }
        Rectangle window = Window(control, origin, alone);
        Control[] held = [.. control.Controls.Cast<Control>().Where(Shows)];
        Draw(drawing, control, window, Rectangle.Intersect(clip, window), alone, held.Length > 0 ? 0 : PrintChildren);
        Point inside = control.PointToScreen(Point.Empty);
        Rectangle holds = Rectangle.Intersect(clip, new Rectangle(new Point(inside.X - origin.X, inside.Y - origin.Y), control.ClientSize));
        for (int i = held.Length - 1; i >= 0; i--)
        {
            Draw(drawing, held[i], origin, holds, alone: false);
        }
    }

    /// <summary>A control's window in the picture: its client area alone, or its window by its parent's.</summary>
    private static Rectangle Window(Control control, Point origin, bool alone)
    {
        if (alone || control.Parent is null)
        {
            Point client = control.PointToScreen(Point.Empty);
            return new Rectangle(new Point(client.X - origin.X, client.Y - origin.Y), control.ClientSize);
        }
        Point corner = control.Parent.PointToScreen(control.Location);
        return new Rectangle(new Point(corner.X - origin.X, corner.Y - origin.Y), control.Size);
    }

    /// <summary>One window's own drawing into its place, kept to the part shown: a client area alone (WM_PRINTCLIENT, drawn at
    /// its corner), else its frame too, with its own windows by <paramref name="children"/>.</summary>
    private static void Draw(Graphics drawing, Control control, Rectangle window, Rectangle shown, bool alone, int children)
    {
        if (shown.Width < 1 || shown.Height < 1)
        {
            return;
        }
        bool client = alone || control.Parent is null;
        using Bitmap own = new(Math.Max(1, window.Width), Math.Max(1, window.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics into = Graphics.FromImage(own))
        {
            nint device = into.GetHdc();
            try
            {
                SendMessage(control.Handle, client ? PrintClientOnly : Print, device, PrintClient | PrintBackground | (client ? 0 : PrintFrame | children));
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
