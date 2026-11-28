using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Passenger.Inspector.WinForms;

/// <summary>A Windows Forms app's tree as the inspector walks it, and what a person sees of each part: a component is a control
/// or a tool strip's item, a strip holding its items where another control holds its controls; where each is in its top level,
/// in the app's own units (pixels); the text it shows; and the class whose designer code makes it.</summary>
internal static class Shown
{
    /// <summary>What a component holds: a tool strip its items, an item hosting a control that control, any other control its
    /// controls.</summary>
    public static IReadOnlyList<Component> Children(Component component) => component switch
    {
        ToolStrip strip => [.. strip.Items.Cast<ToolStripItem>()],
        ToolStripControlHost { Control: { } hosted } => [hosted],
        Control control => [.. control.Controls.Cast<Control>()],
        _ => [],
    };

    /// <summary>What holds a component, as input and clipping see it: a control's parent, an item's strip, a drop-down's item
    /// it opened from or the control its context menu opened on; null at a top level that opened from nothing.</summary>
    public static Component? Parent(Component component) => component switch
    {
        ToolStripDropDown { OwnerItem: { } item } => item,
        ContextMenuStrip { SourceControl: { } source } => source,
        Control control => control.Parent,
        ToolStripItem item => (Component?)item.Owner ?? item.OwnerItem,
        _ => null,
    };

    /// <summary>The top-level window a component is in: a control's form or drop-down, an item's strip's.</summary>
    public static Control? TopOf(Component component) => component switch
    {
        Control control => control.TopLevelControl,
        ToolStripItem item => item.Owner?.TopLevelControl,
        _ => null,
    };

    /// <summary>Whether a component is another or inside it.</summary>
    public static bool Within(Component component, Component ancestor)
    {
        for (Component? at = component; at is not null; at = Parent(at))
        {
            if (at == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A component's name, as its designer gave it; null for none.</summary>
    public static string? NameOf(Component component) => (component switch
    {
        Control control => control.Name,
        ToolStripItem item => item.Name,
        _ => null,
    }) is { Length: > 0 } name ? name : null;

    /// <summary>A component as a sentence names it: its type, then #name, or the text it shows when it has no name.</summary>
    public static string Called(Component component) =>
        Words.Called(Words.Describe(component.GetType(), NameOf(component)), NameOf(component) is null ? TextOf(component) : null);

    /// <summary>Whether a person sees a component: a control shown with every control around it and its window, an item
    /// available on a strip that shows.</summary>
    public static bool IsShown(Component component) => component switch
    {
        Control control => control.Visible && control.IsHandleCreated,
        ToolStripItem item => item.Visible,
        _ => false,
    };

    public static bool IsEnabled(Component component) => component switch
    {
        Control control => control.Enabled,
        ToolStripItem item => item.Enabled,
        _ => false,
    };

    /// <summary>A top level's client area in its units, from its top left.</summary>
    public static Rectangle ClientOf(Control top) => new(Point.Empty, top.ClientSize);

    /// <summary>A component's box in its top level's client area; null where it has no window to place it by.</summary>
    public static Rectangle? BoxIn(Component component, Control top)
    {
        if (component == top)
        {
            return ClientOf(top);
        }
        if (!top.IsHandleCreated)
        {
            return null;
        }
        switch (component)
        {
            case Control { IsHandleCreated: true } control when TopOf(control) == top:
                Point corner = control.Parent is { IsHandleCreated: true } parent ? parent.PointToScreen(control.Location) : control.PointToScreen(Point.Empty);
                return new Rectangle(top.PointToClient(corner), control.Size);
            case ToolStripItem { Owner: { IsHandleCreated: true } strip } item when strip.TopLevelControl == top:
                return new Rectangle(top.PointToClient(strip.PointToScreen(item.Bounds.Location)), item.Bounds.Size);
            default:
                return null;
        }
    }

    /// <summary>A box as the protocol carries it.</summary>
    public static Box Boxed(Rectangle box) => Words.Boxed(box.X, box.Y, box.Width, box.Height);

    /// <summary>The text a component shows: a text box's (a password's masked), a choice's, a numeric box's, a window's title,
    /// any other control's or item's text where it has one; null for none.</summary>
    public static string? TextOf(Component component)
    {
        string? text = component switch
        {
            _ when HidesText(component) => Words.Dots(((Control)component).Text),
            TextBoxBase or ComboBox or UpDownBase => ((Control)component).Text,
            Control { Text.Length: > 0 } control => control.Text,
            ToolStripItem { Text.Length: > 0 } item => item.Text,
            _ => null,
        };
        return Words.Shows(text);
    }

    /// <summary>Whether a component is a password's box, whose text it shows as dots: a text box or masked text box with a
    /// password character of its own or the system's.</summary>
    private static bool HidesText(Component component) => component switch
    {
        TextBox box => box.UseSystemPasswordChar || box.PasswordChar != '\0',
        MaskedTextBox box => box.UseSystemPasswordChar || box.PasswordChar != '\0',
        _ => false,
    };

    /// <summary>The class whose designer code makes a component and its field there: the nearest form or user control of the
    /// app's own around it, which made it with its name; a form or user control of the app's own with none around it is its
    /// own code's root (an empty field). Null where none does (a control made in code, with no name).</summary>
    public static DesignedIn? DesignedOf(Component component)
    {
        for (Component? at = Parent(component); at is not null; at = Parent(at))
        {
            if (IsDesigned(at))
            {
                return NameOf(component) is { } name ? new DesignedIn(at.GetType().FullName!, name) : null;
            }
        }
        return IsDesigned(component) ? new DesignedIn(component.GetType().FullName!, string.Empty) : null;
    }

    /// <summary>Whether a component is a form or user control whose own code designs what it holds: one of the app's, not
    /// Windows Forms' own.</summary>
    private static bool IsDesigned(Component component) =>
        component is ContainerControl && component.GetType().Assembly != typeof(Control).Assembly && component.GetType().FullName is not null;

    /// <summary>The values of the properties named that a component has, by their names as the Properties grid lists them.</summary>
    public static Dictionary<string, string>? Values(Component component, IReadOnlyList<string> names) =>
        Words.Values(names, name => TypeDescriptor.GetProperties(component)[name] is { } property ? Format(Read(property, component), property.Converter) : null);

    /// <summary>A property's value, or what its getter threw; a password box's text as the box shows it, a dot for each
    /// character (<see cref="HidesText"/>).</summary>
    public static object? Read(PropertyDescriptor property, Component owner)
    {
        try
        {
            object? value = property.GetValue(owner);
            return !HidesText(owner) ? value : value switch
            {
                string text when property.Name is nameof(TextBoxBase.Text) or nameof(TextBoxBase.SelectedText) => Words.Dots(text),
                string[] lines when property.Name is nameof(TextBoxBase.Lines) => (string[])[.. lines.Select(Words.Dots)],
                _ => value,
            };
        }
        catch (Exception error)
        {
            // The app's own getter, whose failure is the value shown.
            return new Failed(error);
        }
    }

    /// <summary>A value as the window shows it: its converter's invariant words, text quoted.</summary>
    public static string Format(object? value, TypeConverter? converter) => value is Failed failed ? failed.Said : Words.Value(value, shown =>
    {
        try
        {
            // A converter with no words for its value (an expandable one's empty text) leaves it to say what it is.
            return converter?.CanConvertTo(typeof(string)) == true && converter.ConvertToString(null, CultureInfo.InvariantCulture, shown) is { Length: > 0 } said ? said : null;
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    });

    /// <summary>What a getter threw, and the words its value shows.</summary>
    private sealed record Failed(Exception Error)
    {
        public string Said => $"({Error.GetBaseException().GetType().Name}: {Error.GetBaseException().Message})";
    }
}
