using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>A change made on the surface as the classic designer makes it, through the designers (a Toolbox item made by its
/// container's designer, properties set through their descriptors, components destroyed by the host), in one transaction;
/// and what it changed in the code, as Windows Forms' serializers write the surface before and after it, placed by the
/// file's own statements.</summary>
internal static class Editing
{
    /// <summary>Makes an edit; the components to choose after it.</summary>
    /// <param name="origin">Where the frame's corner is on the screen the surface's window lies on.</param>
    public static List<string> Apply(DesignEdit edit, IDesignerHost host, Control root, Point origin, int bar, Types types)
    {
        using DesignerTransaction transaction = host.CreateTransaction(edit.Kind);
        List<string> chosen = edit.Kind switch
        {
            DesignEditKinds.Add => Added(edit, host, root, origin, types),
            DesignEditKinds.Move => Moved(edit, host, root),
            DesignEditKinds.Resize => Resized(edit, host, root, origin, bar),
            DesignEditKinds.Delete => Deleted(edit, host, root),
            DesignEditKinds.Front or DesignEditKinds.Back => Ordered(edit, host, root),
            _ => throw new DesignerException($"The designer makes no {edit.Kind}"),
        };
        transaction.Commit();
        return chosen;
    }

    private static List<string> Added(DesignEdit edit, IDesignerHost host, Control root, Point origin, Types types)
    {
        string written = edit.Type ?? throw new DesignerException("No type to add was named");
        Type type = System.Type.GetType(written, throwOnError: false) ?? types.Find(written.Split(',')[0].Trim())
            ?? throw new DesignerException($"{written.Split(',')[0]} is not found (built the project yet?)");
        Point at = new(origin.X + edit.X, origin.Y + edit.Y);
        Control container = edit.Parent is { } parent ? Control(host, root, parent) : ContainerAt(host, root, root, at);
        if (host.GetDesigner(container) is not ParentControlDesigner designer)
        {
            throw new DesignerException($"{Name(container, root)} holds no controls");
        }
        // The container's designer makes it as a drop or a pick of the Toolbox does: placed, sized, its tab index and order set.
        MethodInfo create = typeof(ParentControlDesigner).GetMethod("CreateToolCore", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new DesignerException("Windows Forms' container designer makes no Toolbox items here");
        IComponent[] made = create.Invoke(designer, [Toolbox.ItemOf(type), at.X, at.Y, 0, 0, edit.Parent is null, false]) as IComponent[] ?? [];
        if (made.Length == 0)
        {
            throw new DesignerException($"{type.Name} was not made");
        }
        if (edit.Parent is null && made[0] is Control placed && placed.Parent is { } holder)
        {
            Set(placed, "Location", holder.PointToClient(at));
        }
        return [.. made.Select(c => c.Site?.Name ?? string.Empty)];
    }

    private static List<string> Moved(DesignEdit edit, IDesignerHost host, Control root)
    {
        Control? into = edit.Parent is { } parent ? Control(host, root, parent) : null;
        foreach (string name in edit.Components)
        {
            Control moved = Control(host, root, name);
            if (moved == root)
            {
                throw new DesignerException("The form itself does not move");
            }
            Point to = moved.Location + new Size(edit.X, edit.Y);
            if (into is not null && into != moved.Parent && !moved.Contains(into) && into != moved)
            {
                Point screen = moved.Parent!.PointToScreen(to);
                IComponentChangeService? changes = host.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
                PropertyDescriptor? held = TypeDescriptor.GetProperties(into)["Controls"];
                changes?.OnComponentChanging(into, held);
                into.Controls.Add(moved);
                into.Controls.SetChildIndex(moved, 0);
                changes?.OnComponentChanged(into, held, null, null);
                to = into.PointToClient(screen);
            }
            Set(moved, "Location", to);
        }
        return [.. edit.Components];
    }

    private static List<string> Resized(DesignEdit edit, IDesignerHost host, Control root, Point origin, int bar)
    {
        Control sized = Control(host, root, edit.Components is [var only] ? only : throw new DesignerException("One component is resized at a time"));
        if (sized == root)
        {
            Set(root, root is Form ? "ClientSize" : "Size", new Size(edit.Width, Math.Max(0, edit.Height - bar)));
            return [string.Empty];
        }
        Set(sized, "Location", sized.Parent!.PointToClient(new Point(origin.X + edit.X, origin.Y + edit.Y)));
        Set(sized, "Size", new Size(edit.Width, edit.Height));
        return [.. edit.Components];
    }

    private static List<string> Deleted(DesignEdit edit, IDesignerHost host, Control root)
    {
        string holder = string.Empty;
        foreach (string name in edit.Components)
        {
            if (host.Container.Components[name] is not { } component || component == root)
            {
                continue;
            }
            holder = component is Control { Parent: { } parent } ? Name(parent, root) : string.Empty;
            List<IComponent> gone = [];
            Within(component, gone);
            foreach (IComponent each in gone)
            {
                host.DestroyComponent(each);
            }
        }
        return [holder];
    }

    /// <summary>The components a control holds, deepest first, then itself.</summary>
    private static void Within(IComponent component, List<IComponent> gone)
    {
        if (component is Control control)
        {
            foreach (Control inside in control.Controls.Cast<Control>().ToList())
            {
                Within(inside, gone);
            }
        }
        if (component.Site is not null)
        {
            gone.Add(component);
        }
    }

    private static List<string> Ordered(DesignEdit edit, IDesignerHost host, Control root)
    {
        IComponentChangeService? changes = host.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
        foreach (string name in edit.Components)
        {
            Control ordered = Control(host, root, name);
            Control? parent = ordered.Parent;
            PropertyDescriptor? held = parent is null ? null : TypeDescriptor.GetProperties(parent)["Controls"];
            changes?.OnComponentChanging(parent!, held);
            if (edit.Kind == DesignEditKinds.Front)
            {
                ordered.BringToFront();
            }
            else
            {
                ordered.SendToBack();
            }
            changes?.OnComponentChanged(parent!, held, null, null);
        }
        return [.. edit.Components];
    }

    private static void Set(object component, string property, object value) =>
        (TypeDescriptor.GetProperties(component)[property] ?? throw new DesignerException($"{component.GetType().Name} has no {property}")).SetValue(component, value);

    private static Control Control(IDesignerHost host, Control root, string name) => name.Length == 0 ? root
        : host.Container.Components[name] as Control ?? throw new DesignerException($"The form has no control {name}");

    private static string Name(Control control, Control root) => control == root ? string.Empty : control.Site?.Name ?? control.Name;

    /// <summary>The deepest control holding controls under a point of the screen, the root where none other is.</summary>
    private static Control ContainerAt(IDesignerHost host, Control root, Control within, Point screen)
    {
        foreach (Control child in within.Controls)
        {
            if (child.Site is not null && child.RectangleToScreen(child.ClientRectangle).Contains(screen) && host.GetDesigner(child) is ParentControlDesigner)
            {
                return ContainerAt(host, root, child, screen);
            }
        }
        return within;
    }

    /// <summary>What an edit changed in the code, as lines of the request's file: what it took out of the code, what it moved
    /// in it (taken out and made again where it goes now), what it gave another value, and what it made, each made after the
    /// last statement before it that the file writes; and the fields it declared or took out.</summary>
    public static List<DesignerChange> Changes(FormDesignRequest request, Serialized before, Serialized after)
    {
        Dictionary<string, int> file = new(StringComparer.Ordinal);
        for (int i = 0; i < request.Statements.Count; i++)
        {
            if (DesignerKeys.Of(request.Statements[i]) is { } key)
            {
                file.TryAdd(key, i);
            }
        }
        Dictionary<string, (int At, SerializedLine Line)> was = new(StringComparer.Ordinal);
        for (int i = 0; i < before.Lines.Count; i++)
        {
            if (before.Lines[i].Key is { } key)
            {
                was.TryAdd(key, (i, before.Lines[i]));
            }
        }
        HashSet<string> now = [.. after.Lines.Select(l => l.Key).OfType<string>()];
        List<DesignerChange> changes = [];
        HashSet<int> gone = [];
        foreach (SerializedLine line in before.Lines)
        {
            if (line.Key is { } key && !now.Contains(key) && file.TryGetValue(key, out int at) && gone.Add(at))
            {
                changes.Add(new DesignerChange(DesignerChangeKinds.Remove, at));
            }
        }
        foreach (string key in Reordered([.. after.Lines.Select(l => l.Key).OfType<string>().Where(was.ContainsKey)], was))
        {
            if (file.TryGetValue(key, out int at) && gone.Add(at))
            {
                changes.Add(new DesignerChange(DesignerChangeKinds.Remove, at));
            }
        }
        int[] kept = [.. after.Lines.Select(l => l.Key is { } key && file.TryGetValue(key, out int at) && !gone.Contains(at) ? at : -1)];
        HashSet<string> headed = [.. after.Lines.Where((l, i) => kept[i] >= 0).Select(l => l.Section).OfType<string>()];
        (int At, string? Section) anchor = (-1, null);
        for (int i = 0; i < after.Lines.Count; i++)
        {
            SerializedLine line = after.Lines[i];
            if (line.Key is not { } key)
            {
                continue;
            }
            if (kept[i] >= 0)
            {
                if (!was.TryGetValue(key, out (int At, SerializedLine Line) old) || old.Line.Exact != line.Exact)
                {
                    changes.Add(new DesignerChange(DesignerChangeKinds.Replace, kept[i]) { Text = line.Text });
                }
                anchor = (kept[i], line.Section);
                continue;
            }
            // The first of a section the file writes goes under its comment, before the statement after it there.
            int next = Enumerable.Range(i + 1, after.Lines.Count - i - 1).FirstOrDefault(j => kept[j] >= 0, -1);
            if (anchor.Section != line.Section && next >= 0 && after.Lines[next].Section == line.Section && line.Section is not null)
            {
                changes.Add(new DesignerChange(DesignerChangeKinds.Insert, kept[next]) { Text = line.Text, Before = true });
                continue;
            }
            changes.Add(new DesignerChange(DesignerChangeKinds.Insert, anchor.At) { Text = line.Text, Header = line.Section is { } section && headed.Add(section) ? section : null });
        }
        changes.AddRange(Fields(request, before, after));
        return changes;
    }

    /// <summary>The fields an edit declared, each after the last component's field before it the file declares (none: where a
    /// class declaring no component's field takes one), and those it took out; the container's own field is neither.</summary>
    private static IEnumerable<DesignerChange> Fields(FormDesignRequest request, Serialized before, Serialized after)
    {
        const string Container = "components";
        Dictionary<string, int> declared = new(StringComparer.Ordinal);
        for (int i = 0; i < request.Fields.Count; i++)
        {
            declared.TryAdd(request.Fields[i].Name, i);
        }
        HashSet<string> kept = [.. after.Fields.Select(f => f.Name)];
        HashSet<int> gone = [];
        foreach (SerializedField field in before.Fields)
        {
            if (field.Name != Container && !kept.Contains(field.Name) && declared.TryGetValue(field.Name, out int at) && gone.Add(at))
            {
                yield return new DesignerChange(DesignerChangeKinds.Undeclare, at);
            }
        }
        int anchor = -1;
        foreach (SerializedField field in after.Fields.Where(f => f.Name != Container))
        {
            if (declared.TryGetValue(field.Name, out int at))
            {
                anchor = gone.Contains(at) ? anchor : at;
                continue;
            }
            yield return new DesignerChange(DesignerChangeKinds.Declare, anchor) { Text = field.Text };
        }
    }

    /// <summary>The statements of both whose order the edit changed: those of <paramref name="kept"/> (in their order after it)
    /// off the longest run that keeps their order before it.</summary>
    private static List<string> Reordered(List<string> kept, Dictionary<string, (int At, SerializedLine Line)> was)
    {
        int[] places = [.. kept.Select(k => was[k].At)];
        // The longest rising run by patience: each length's smallest last place, found by the framework's halving search.
        List<int> lasts = [];
        List<int> ends = [];
        int[] previous = new int[places.Length];
        for (int i = 0; i < places.Length; i++)
        {
            int found = lasts.BinarySearch(places[i]);
            int length = found < 0 ? ~found : found;
            previous[i] = length > 0 ? ends[length - 1] : -1;
            if (length == lasts.Count)
            {
                lasts.Add(places[i]);
                ends.Add(i);
            }
            else
            {
                lasts[length] = places[i];
                ends[length] = i;
            }
        }
        HashSet<int> inOrder = [];
        for (int at = ends.Count > 0 ? ends[^1] : -1; at >= 0; at = previous[at])
        {
            inOrder.Add(at);
        }
        return [.. kept.Where((_, i) => !inOrder.Contains(i))];
    }
}
