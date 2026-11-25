using System.Runtime.InteropServices;

namespace FormsTreeApp;

public partial class MainForm : Form
{
    private const int LeftDown = 0x0201;
    private const int LeftUp = 0x0202;

    private int clicks;

    public MainForm()
    {
        InitializeComponent();
        List.Items.AddRange([.. Enumerable.Range(1, 40).Select(i => (object)$"Item {i}")]);
    }

    private void OnCount(object? sender, EventArgs e)
    {
        clicks++;
        Counter.Text = clicks == 1 ? "Clicked once" : $"Clicked {clicks} times";
    }

    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            Hello.Text = $"Hello, {NameBox.Text}";
            e.SuppressKeyPress = true;
        }
    }

    private void OnHelp(object? sender, EventArgs e) => Hello.Text = "Help asked";

    /// <summary>A press and release at a point of the form's client area, posted to the window under it.</summary>
    public void PostClick(Point point)
    {
        Point screen = PointToScreen(point);
        IntPtr window = Handle;
        while (true)
        {
            Point inside = Control.FromHandle(window)?.PointToClient(screen) ?? screen;
            IntPtr child = ChildWindowFromPointEx(window, inside, 3);
            if (child == IntPtr.Zero || child == window)
            {
                break;
            }
            window = child;
        }
        Point local = Control.FromHandle(window)?.PointToClient(screen) ?? point;
        IntPtr where = (IntPtr)((local.Y << 16) | (local.X & 0xFFFF));
        PostMessage(window, LeftDown, 1, where);
        PostMessage(window, LeftUp, IntPtr.Zero, where);
        Console.WriteLine($"Posted a click at {local.X},{local.Y} in {Control.FromHandle(window)?.Name}");
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, Point at, uint skip);
}
