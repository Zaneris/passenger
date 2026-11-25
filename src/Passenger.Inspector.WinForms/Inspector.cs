using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>The inspector's work on a Windows Forms app's UI thread: its top-level windows (its forms, and the drop-downs its
/// menus and tool strips have open) and what each holds, a control or item looked at closely, Select Element and the outline
/// over what is chosen, a word to Passenger as controls come and go, and for an agent the windows' pictures and input to
/// them (<see cref="Pictures"/>, <see cref="Driver"/>). Property breakpoints, binding values, overlays and XAML Hot Reload
/// are XAML's frameworks', refused saying so.</summary>
internal sealed class Inspector : Inspecting<Component>
{
    /// <summary>How often the windows the app shows are looked at while Passenger has the tree, as no event says a form opened.</summary>
    private static readonly TimeSpan TopsLook = TimeSpan.FromMilliseconds(500);

    private readonly Picker picker;
    private readonly Driver driver;

    /// <summary>The controls whose coming and going the inspector hears, each once.</summary>
    private readonly ConditionalWeakTable<Control, object> heard = [];
    private readonly System.Windows.Forms.Timer tops = new() { Interval = (int)TopsLook.TotalMilliseconds };
    private IReadOnlyList<Control> lastTops = [];

    public Inspector(Connection connection) : base(connection)
    {
        picker = new Picker(this, connection);
        driver = new Driver(this);
        tops.Tick += (_, _) =>
        {
            IReadOnlyList<Control> now = TopLevels();
            if (!now.SequenceEqual(lastTops))
            {
                lastTops = now;
                Changed(null);
            }
        };
        tops.Start();
    }

    /// <summary>The app's top-level windows on this thread, each once: its open forms (its main form first), then its other
    /// windows Windows Forms made that show (a drop-down, a context menu), the inspector's own outline left out.</summary>
    public IReadOnlyList<Control> TopLevels()
    {
        List<Control> found = [];
        foreach (Form form in Application.OpenForms)
        {
            if (form.IsHandleCreated && form.TopLevel && !picker.IsOwn(form))
            {
                found.Add(form);
            }
        }
        foreach (nint window in Native.ThreadWindows())
        {
            if (Control.FromHandle(window) is { Visible: true, Parent: null } control && control is Form or ToolStripDropDown && !found.Contains(control) && !picker.IsOwn(control))
            {
                found.Add(control);
            }
        }
        return found;
    }

    /// <summary>A top level's owner: a form's, a drop-down's item's window, a context menu's control's.</summary>
    public static Control? OwnerOf(Control top) => top switch
    {
        Form { Owner: { } owner } => owner,
        ToolStripDropDown dropDown when Shown.Parent(dropDown) is { } opened => Shown.TopOf(opened),
        _ => null,
    };

    /// <summary>What a component holds, the inspector's own left out, each control heard as its own controls come and go.</summary>
    public IReadOnlyList<Component> Children(Component component)
    {
        if (component is Control control && !heard.TryGetValue(control, out _))
        {
            heard.Add(control, true);
            control.ControlAdded += (_, e) => Changed(e.Control);
            control.ControlRemoved += (_, e) => Changed(e.Control);
            if (control is ToolStrip strip)
            {
                strip.ItemAdded += (_, e) => Changed(e.Item);
                strip.ItemRemoved += (_, e) => Changed(e.Item);
            }
        }
        return Shown.Children(component);
    }

    protected override AppTree Tree(Request request)
    {
        Look? look = request.Layout == true || request.Properties is { Count: > 0 } ? new Look(request.Layout == true, request.Properties ?? []) : null;
        IReadOnlyList<Control> shown = TopLevels();
        lastTops = shown;
        return new AppTree([.. shown.Select(top => ElementTrees.Made<Component, Rectangle>(top, Shown.ClientOf(top), Children,
            (component, clip) => component == top ? clip : Shown.BoxIn(component, top) is { } box ? Rectangle.Intersect(clip, box) : clip,
            (component, clip, children) => Node(component, top, clip, look, children)))]);
    }

    /// <summary>What an agent's tree carries beyond each component's identity: layout and text, and property values.</summary>
    private sealed record Look(bool Layout, IReadOnlyList<string> Properties);

    /// <summary>A component with what it holds made; with an agent's look, also where it is, what it shows and whether what
    /// clips it shows any of it.</summary>
    private Element Node(Component component, Control top, Rectangle clip, Look? look, IReadOnlyList<Element> children)
    {
        Element node = new(IdOf(component), Words.TypeName(component.GetType()), Shown.NameOf(component), [], false, Shown.DesignedOf(component) is not null,
            Shown.IsShown(component), children);
        if (look is null)
        {
            return node;
        }
        if (look.Layout)
        {
            Rectangle? box = Shown.BoxIn(component, top);
            node = node with
            {
                Bounds = box is { } at ? Shown.Boxed(at) : null,
                Text = Shown.TextOf(component),
                Disabled = Shown.IsEnabled(component) ? null : true,
                Focused = component is Control { Focused: true } ? true : null,
                OutOfView = box is { Width: > 0, Height: > 0 } && clip.IsEmpty ? true : null,
            };
        }
        return look.Properties.Count == 0 ? node : node with { Values = Shown.Values(component, look.Properties) };
    }

    protected override List<AppWindow> Windows() =>
        [.. TopLevels().Select(top => new AppWindow(IdOf(top), Words.TypeName(top.GetType()), Shown.NameOf(top), top is Form ? Native.TitleOf(top.Handle) : null,
            top.ClientSize.Width, top.ClientSize.Height, 1, Form.ActiveForm == top, top.Visible, OwnerOf(top) is { } owner ? IdOf(owner) : null))];

    protected override ElementDetails DetailsOf(Component element) => Details.Of(this, element);

    protected override void Pick(bool on) => picker.Choose(on);

    protected override void Outline(Component? element) => picker.Outline(element);

    protected override Picture Capture(Component element, Request request) => Pictures.Take(this, element, request);

    protected override InputDone Do(InputAction input) => driver.Do(input);

    /// <summary>What the last input came to once the app has taken what it posted: its answer again, read now.</summary>
    public InputDone? Took(InputDone? said) => said is null ? null : driver.Since(said);

    protected override void Watch(int breakpoint, Component element, string property, string? hitCondition) =>
        throw new Refusal("Property breakpoints are not available for a Windows Forms app");

    protected override void Unwatch(int breakpoint)
    {
    }

    protected override XamlValues ValuesOf(Request request) =>
        throw new Refusal("Binding values after a XAML file's lines are read from an Avalonia app's .axaml; a Windows Forms app has no XAML");

    protected override XamlValues MadeAt(XamlRead read) => new([], "A Windows Forms app has no XAML");

    protected override List<AppWindow> SetOverlays(Request request) => throw new Refusal("Renderer debug overlays are Avalonia's: a Windows Forms app has none");

    protected override XamlReloaded Reload(XamlChange change) => new([], "A Windows Forms app has no XAML to reload", []);

    protected override void Later(TimeSpan delay, Action action)
    {
        System.Windows.Forms.Timer timer = new() { Interval = Math.Max(1, (int)delay.TotalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    protected override bool LeftOut(Component element) => picker.IsOwn(element);

    protected override void Stopping()
    {
        tops.Stop();
        tops.Dispose();
        picker.Choose(false);
        picker.Outline(null);
    }

    /// <summary>A component as an agent's answer names it.</summary>
    public static string Named(Component component) => Words.Named(Shown.Called(component), null);
}
