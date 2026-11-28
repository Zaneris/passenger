using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using System.Windows.Forms.Design.Behavior;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>The surface's components as the design names and places them: each by its name, a part a container makes of its
/// own (a SplitContainer's Panel1) by its full name through its holder's ("splitContainer1.Panel1"); each under the
/// component holding it, a control under its parent, a strip's item under its strip or menu item, a list's column under its
/// list; those that draw nothing and belong to no other in the tray (none's). Shown ones have their box in the frame, the
/// rest (a tab page not chosen, a menu's items, a column) none.</summary>
internal static class Components
{
    /// <summary>A component's name as the design knows it: its full name where a container's own part is it.</summary>
    public static string NameOf(IComponent component, Control root) => component == root ? string.Empty
        : component.Site is INestedSite { FullName: { } full } ? full : component.Site?.Name ?? string.Empty;

    /// <summary>A component by its name as the design knows it, the root's empty; null where the form has none of it.</summary>
    public static IComponent? Find(IDesignerHost host, Control root, string name)
    {
        if (name.Length == 0)
        {
            return root;
        }
        if (host.Container.Components[name] is { } held)
        {
            return held;
        }
        return Controls(root).FirstOrDefault(c => c.Site is not null && NameOf(c, root) == name);
    }

    private static IEnumerable<Control> Controls(Control holder)
    {
        foreach (Control control in holder.Controls)
        {
            yield return control;
            foreach (Control inside in Controls(control))
            {
                yield return inside;
            }
        }
    }

    /// <summary>The component a component belongs to, by its name: a control's parent's, a strip item's strip's or menu item's,
    /// a column's list's; null for one in the tray, which belongs to none, and for the root.</summary>
    public static string? OwnerOf(IComponent component, Control root) => component switch
    {
        _ when component == root => null,
        Control { Parent: { } parent } => Named(parent, root),
        ToolStripItem { OwnerItem: { } item } => Named(item, root),
        ToolStripItem { Owner: { } strip } => Named(strip, root),
        ColumnHeader { ListView: { } list } => Named(list, root),
        DataGridViewColumn { DataGridView: { } grid } => Named(grid, root),
        _ => null,
    };

    /// <summary>The name of the nearest component a control or item is or lies in that the design names.</summary>
    private static string Named(IComponent component, Control root)
    {
        for (object? at = component; at is not null; at = at is Control control ? control.Parent : null)
        {
            if (at == root)
            {
                return string.Empty;
            }
            if (at is IComponent { Site: not null } sited)
            {
                return NameOf(sited, root);
            }
        }
        return string.Empty;
    }

    /// <summary>Every component's box: the root's, then what each control holds in turn (its controls, a strip's items and
    /// their items), then the rest of the container's (the tray's, a list's columns).</summary>
    public static List<DesignedBox> Boxes(IDesignerHost host, Control root, Point origin, int bar)
    {
        List<DesignedBox> boxes =
        [
            new DesignedBox(string.Empty, root.GetType().Name, null, 0, 0, root.ClientSize.Width, bar + root.ClientSize.Height, true)
            {
                DefaultEvent = DefaultEvent(root),
                Snaps = Snaps(root, host, root.Parent is { } frame ? Framed(frame.RectangleToScreen(root.Bounds), origin) : Rectangle.Empty),
            },
        ];
        HashSet<IComponent> placed = [root];
        Walk(root, host, root, origin, boxes, placed, shown: true);
        foreach (IComponent rest in host.Container.Components.Cast<IComponent>().Where(c => !placed.Contains(c)))
        {
            string? owner = OwnerOf(rest, root);
            if (owner is null && TypeDescriptor.GetAttributes(rest)[typeof(DesignTimeVisibleAttribute)] is DesignTimeVisibleAttribute { Visible: false })
            {
                continue;
            }
            // A tray component's box is drawn nowhere; its place says where it stands in the tray.
            Point tray = TypeDescriptor.GetProperties(rest)["TrayLocation"]?.GetValue(rest) is Point kept ? kept : Point.Empty;
            boxes.Add(Box(rest, root, owner, new Rectangle(tray, Size.Empty), false));
        }
        return boxes;
    }

    private static DesignedBox Box(IComponent component, Control root, string? owner, Rectangle at, bool holds) =>
        new(NameOf(component, root), component.GetType().Name, owner, at.X, at.Y, at.Width, at.Height, holds)
        {
            DefaultEvent = DefaultEvent(component),
            Place = ListOf(component) is { } list ? list.IndexOf(component) : null,
            Places = ListOf(component)?.Count ?? 0,
            Control = component is Control,
        };

    /// <summary>The list a component is an item of, in order (a strip's or a menu item's items, a list's or a grid's columns, a
    /// tab control's pages); none for one of no such list.</summary>
    public static System.Collections.IList? ListOf(IComponent component) => component switch
    {
        ToolStripItem { OwnerItem: ToolStripDropDownItem menu } => menu.DropDownItems,
        ToolStripItem { Owner: { } strip } => strip.Items,
        ColumnHeader { ListView: { } list } => list.Columns,
        DataGridViewColumn { DataGridView: { } grid } => grid.Columns,
        TabPage { Parent: TabControl tabs } => tabs.TabPages,
        _ => null,
    };

    /// <summary>The boxes of what a control holds: its controls (those not shown, a tab page not chosen, with none), a strip's
    /// items and the items under them.</summary>
    private static void Walk(Control holder, IDesignerHost host, Control root, Point origin, List<DesignedBox> boxes, HashSet<IComponent> placed, bool shown)
    {
        if (holder is ToolStrip strip)
        {
            Items(strip.Items, strip, host, root, origin, boxes, placed, shown);
        }
        foreach (Control control in holder.Controls)
        {
            bool drawn = shown && control.IsHandleCreated && Visible(control);
            if (control.Site is not null)
            {
                placed.Add(control);
                Rectangle at = drawn ? Framed(control.Parent!.RectangleToScreen(control.Bounds), origin) : Rectangle.Empty;
                boxes.Add(Box(control, root, Named(holder, root), at, host.GetDesigner(control) is ParentControlDesigner) with { Snaps = drawn ? Snaps(control, host, at) : null });
            }
            Walk(control, host, root, origin, boxes, placed, drawn);
        }
    }

    /// <summary>A strip's or a menu item's items, a box each (the designer's own, a strip's Type Here, sited in no container,
    /// left out): one the strip shows where it is, the rest, a menu's, with none.</summary>
    private static void Items(ToolStripItemCollection items, IComponent owner, IDesignerHost host, Control root, Point origin, List<DesignedBox> boxes, HashSet<IComponent> placed, bool shown)
    {
        foreach (ToolStripItem item in items.Cast<ToolStripItem>().Where(i => i.Site is not null))
        {
            placed.Add(item);
            // Available is the item's own choice: Visible says false for any item of a strip whose window is never shown.
            Rectangle at = shown && owner is ToolStrip strip && item.Available ? Framed(strip.RectangleToScreen(item.Bounds), origin) : Rectangle.Empty;
            boxes.Add(Box(item, root, Named(owner, root), at, false));
            if (item is ToolStripDropDownItem { HasDropDownItems: true } menu)
            {
                Items(menu.DropDownItems, menu, host, root, origin, boxes, placed, shown: false);
            }
        }
    }

    /// <summary>The lines a control's designer snaps by, each placed in the frame from the control's window, which Windows
    /// Forms measures them from; none where its designer gives none.</summary>
    private static List<DesignSnap>? Snaps(Control control, IDesignerHost host, Rectangle window)
    {
        if (host.GetDesigner(control) is not ControlDesigner designer)
        {
            return null;
        }
        try
        {
            return [.. designer.SnapLines.Cast<SnapLine>().Select(l => new DesignSnap(l.SnapLineType.ToString(), (l.IsVertical ? window.X : window.Y) + l.Offset, l.Filter))];
        }
        catch (Exception unread) when (unread is InvalidOperationException or NullReferenceException or ArgumentException)
        {
            // A designer that cannot say its lines leaves the control to be placed freely.
            Console.Error.WriteLine($"{control.Site?.Name}'s snap lines did not read: {unread.Message}");
            return null;
        }
    }

    /// <summary>Whether a control is shown as the design has it: the designer's shadowed Visible aside, its window's style.</summary>
    private static bool Visible(Control control) => Passenger.Inspector.WinForms.ControlDrawing.Shows(control);

    private static Rectangle Framed(Rectangle screen, Point origin) => new(screen.X - origin.X, screen.Y - origin.Y, screen.Width, screen.Height);

    /// <summary>The event a double click on a component hooks, as its type's <c>[DefaultEvent]</c> says.</summary>
    private static string? DefaultEvent(object component) => TypeDescriptor.GetDefaultEvent(component)?.Name;

    /// <summary>Shows the tab page a component lies in (a page itself, or what one holds), as a design shows the page of what is chosen.</summary>
    public static void Reveal(IComponent? component)
    {
        for (Control? at = component as Control; at is not null; at = at.Parent)
        {
            if (at is TabPage page && page.Parent is TabControl tabs && tabs.SelectedTab != page)
            {
                tabs.SelectedTab = page;
            }
        }
    }
}
