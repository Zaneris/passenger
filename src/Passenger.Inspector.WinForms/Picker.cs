using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>Select Element: while it is on, a filter of the app's own message loop outlines the control or item under the
/// pointer in any of its windows, and a click chooses it (the app never sees that click) and ends it; Escape ends it choosing
/// nothing. The component chosen in Passenger is outlined the same way: a window of the inspector's own over it, which takes
/// no input and never activates, in colours of its own, since Passenger's theme tokens are not the app's.</summary>
internal sealed class Picker(Inspector inspector, Connection connection) : Picking<Component>(connection), IMessageFilter
{
    private const int Escape = 0x1B;

    private Marker? outline;
    private bool choosing;

    public bool IsOwn(Component component) => component == outline;

    public override void Choose(bool on)
    {
        if (on == choosing)
        {
            return;
        }
        choosing = on;
        if (on)
        {
            Application.AddMessageFilter(this);
        }
        else
        {
            Application.RemoveMessageFilter(this);
        }
    }

    public bool PreFilterMessage(ref Message message)
    {
        switch (message.Msg)
        {
            case Native.MouseMove:
                Outline(At(message));
                return false;
            case Native.LeftDown or Native.RightDown or Native.MiddleDown or Native.LeftDouble:
                Picked(At(message));
                return true;
            case Native.KeyDown when (int)message.WParam == Escape:
                Picked(null);
                return true;
            default:
                return false;
        }
    }

    public override void Outline(Component? component)
    {
        Rectangle? box = null;
        Control? top = component is null ? null : Shown.TopOf(component);
        if (component is not null && top is { IsHandleCreated: true } && Shown.IsShown(component) && Shown.BoxIn(component, top) is { } inTop)
        {
            box = new Rectangle(top.PointToScreen(inTop.Location), inTop.Size);
        }
        if (box is not { Width: > 0, Height: > 0 } shown)
        {
            outline?.Hide();
            return;
        }
        outline ??= new Marker();
        outline.Bounds = shown;
        if (!outline.Visible)
        {
            outline.Show(top);
        }
    }

    /// <summary>The control or item a mouse message is over: the control whose window it went to, or the item of a strip there.</summary>
    private Component? At(Message message)
    {
        if (Control.FromChildHandle(message.HWnd) is not { } control || IsOwn(control))
        {
            return null;
        }
        if (control is ToolStrip strip)
        {
            Point at = strip.PointToClient(Control.FromHandle(message.HWnd)?.PointToScreen(Spot(message.LParam)) ?? Cursor.Position);
            return strip.GetItemAt(at) is { } item ? item : strip;
        }
        return Shown.TopOf(control) is { } top && inspector.TopLevels().Contains(top) ? control : null;
    }

    /// <summary>The point a mouse message's lParam carries, in its window's client area.</summary>
    private static Point Spot(nint words) => new((short)(words & 0xFFFF), (short)((words >> 16) & 0xFFFF));

    /// <summary>The outline: a window of its own over what is chosen, part see-through, which input passes through and which
    /// never takes the activation.</summary>
    private sealed class Marker : Form
    {
        public Marker()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(OutlineEdge.R, OutlineEdge.G, OutlineEdge.B);
            Opacity = OutlineFill.A / 255.0 + 0.1;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams made = base.CreateParams;
                made.ExStyle |= Native.ExTransparent | Native.ExNoActivate | Native.ExToolWindow;
                return made;
            }
        }
    }
}
