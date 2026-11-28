using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>A change made on the surface as the classic designer makes it, through the designers (a Toolbox item made by its
/// container's designer, properties set through their descriptors, components destroyed by the host), in one transaction;
/// and what it changed in the code, as Windows Forms' serializers write the surface before and after it, placed by the
/// file's own statements.</summary>
internal static class Editing
{
    /// <summary>Makes an edit; the components to choose after it, and a hook's handler as the code is to declare it.</summary>
    /// <param name="origin">Where the frame's corner is on the screen the surface's window lies on.</param>
    public static (List<string> Chosen, string? Handler) Apply(DesignEdit edit, IDesignerHost host, Control root, Point origin, int bar, Types types,
        IEventBindingService events, bool nullable)
    {
        using DesignerTransaction transaction = host.CreateTransaction(edit.Kind);
        string? handler = null;
        List<string> chosen;
        try
        {
            chosen = Made();
        }
        catch (Exception failed) when (failed is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException
            or TargetInvocationException or InvalidCastException)
        {
            throw new DesignerException($"Windows Forms' designers refused it: {(failed is TargetInvocationException { InnerException: { } inner } ? inner : failed).Message}");
        }
        transaction.Commit();
        return (chosen, handler);

        List<string> Made() => edit.Kind switch
        {
            DesignEditKinds.Add => Added(edit, host, root, origin, types),
            DesignEditKinds.Move => Moved(edit, host, root),
            DesignEditKinds.Resize => Resized(edit, host, root, origin, bar),
            DesignEditKinds.Delete => Deleted(edit, host, root),
            DesignEditKinds.Front or DesignEditKinds.Back => Ordered(edit, host, root),
            DesignEditKinds.Set => Each(edit, host, root, (component, member) => Inspecting.Set(component, member, edit.Text ?? string.Empty, host)),
            DesignEditKinds.Reset => Each(edit, host, root, Inspecting.Reset),
            DesignEditKinds.Hook => Each(edit, host, root, (component, member) => handler = Inspecting.Hook(component, member, edit.Text, events, types, nullable)),
            DesignEditKinds.Unhook => Each(edit, host, root, (component, member) => Inspecting.Unhook(component, member, events)),
            DesignEditKinds.Items => Each(edit, host, root, (component, member) => Inspecting.Lines(component, member, edit.Text ?? string.Empty, host)),
            DesignEditKinds.AddItem => ItemAdded(edit, host, root, types),
            DesignEditKinds.Order => Reordered(edit, host, root),
            DesignEditKinds.Picture => Each(edit, host, root, (component, member) => Inspecting.Picture(component, member, edit.Text ?? string.Empty)),
            _ => throw new DesignerException($"The designer makes no {edit.Kind}"),
        };
    }

    /// <summary>An item of a type made and added to a component's collection (a strip's items, a menu item's, a list's
    /// columns, a tab control's pages), as its collection editor makes one: through the host, its designer setting it up.</summary>
    private static List<string> ItemAdded(DesignEdit edit, IDesignerHost host, Control root, Types types)
    {
        IComponent owner = edit.Components is [var only] ? Components.Find(host, root, only) ?? throw new DesignerException($"The form has no component {only}")
            : throw new DesignerException("Items are added to one component at a time");
        string written = edit.Type ?? throw new DesignerException("No type of item was named");
        Type type = System.Type.GetType(written, throwOnError: false) ?? types.Find(written.Split(',')[0].Trim()) ?? throw new DesignerException($"{written.Split(',')[0]} is not found");
        PropertyDescriptor property = TypeDescriptor.GetProperties(owner)[edit.Member ?? string.Empty] ?? throw new DesignerException($"{owner.GetType().Name} has no {edit.Member}");
        if (property.GetValue(owner) is not System.Collections.IList items)
        {
            throw new DesignerException($"{edit.Member} takes no items");
        }
        IComponent made = host.CreateComponent(type);
        if (made is ToolStripItem item)
        {
            // A strip item's designer sets itself up for the strip's own Type Here, which no surface here shows: it is named instead.
            if (item is not ToolStripSeparator && string.IsNullOrEmpty(item.Text))
            {
                TypeDescriptor.GetProperties(item)["Text"]?.SetValue(item, Components.NameOf(item, root));
            }
        }
        else
        {
            (host.GetDesigner(made) as IComponentInitializer)?.InitializeNewComponent(new System.Collections.Hashtable());
        }
        IComponentChangeService? changes = host.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
        changes?.OnComponentChanging(owner, property);
        items.Add(made);
        changes?.OnComponentChanged(owner, property, null, null);
        return [Components.NameOf(made, root)];
    }

    /// <summary>An item moved up or down its list, by as many places as the edit's X, no further than its ends.</summary>
    private static List<string> Reordered(DesignEdit edit, IDesignerHost host, Control root)
    {
        IComponent item = edit.Components is [var only] ? Components.Find(host, root, only) ?? throw new DesignerException($"The form has no component {only}")
            : throw new DesignerException("One item is moved at a time");
        System.Collections.IList list = Components.ListOf(item) ?? throw new DesignerException($"{edit.Components[0]} is no item of a list");
        int from = list.IndexOf(item);
        int to = Math.Clamp(from + edit.X, 0, list.Count - 1);
        if (from != to)
        {
            IComponent owner = Components.Find(host, root, Components.OwnerOf(item, root) ?? string.Empty) ?? root;
            IComponentChangeService? changes = host.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            changes?.OnComponentChanging(owner, null);
            list.RemoveAt(from);
            list.Insert(to, item);
            changes?.OnComponentChanged(owner, null, null, null);
        }
        return [.. edit.Components];
    }

    /// <summary>A property's or an event's edit made to each component named.</summary>
    private static List<string> Each(DesignEdit edit, IDesignerHost host, Control root, Action<IComponent, string> made)
    {
        string member = edit.Member ?? throw new DesignerException("No property or event was named");
        foreach (string name in edit.Components)
        {
            made(Components.Find(host, root, name) ?? throw new DesignerException($"The form has no component {name}"), member);
        }
        return [.. edit.Components];
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
        return [.. made.Select(c => Components.NameOf(c, root))];
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
            if (Components.Find(host, root, name) is not { } component || component == root)
            {
                continue;
            }
            holder = Components.OwnerOf(component, root) ?? string.Empty;
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
        : Components.Find(host, root, name) as Control ?? throw new DesignerException($"The form has no control {name}");

    private static string Name(Control control, Control root) => Components.NameOf(control, root);

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
        HashSet<string> headed = [.. request.Statements.Where((s, i) => !gone.Contains(i)).Select(s => s.Section).OfType<string>()];
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
                anchor = (kept[i], request.Statements[kept[i]].Section);
                continue;
            }
            // The first of a section the file writes goes under its comment, before the statement after it there.
            int next = Enumerable.Range(i + 1, after.Lines.Count - i - 1).FirstOrDefault(j => kept[j] >= 0, -1);
            if (anchor.Section != line.Section && next >= 0 && request.Statements[kept[next]].Section == line.Section && line.Section is not null)
            {
                changes.Add(new DesignerChange(DesignerChangeKinds.Insert, kept[next]) { Text = line.Text, Before = true });
                continue;
            }
            changes.Add(new DesignerChange(DesignerChangeKinds.Insert, anchor.At) { Text = line.Text, Header = line.Section is { } section && headed.Add(section) ? section : null });
        }
        changes.AddRange(Fields(request, before, after));
        return changes;
    }

    /// <summary>What an edit changed in the form's .resx files, culture by culture: each element it wrote anew or otherwise, and
    /// each it no longer writes, as Windows Forms' resource writer wrote them before the edit and after it.</summary>
    public static List<DesignResourceChange> ResourceChanges(Serialized before, Serialized after)
    {
        List<DesignResourceChange> changes = [];
        foreach (string culture in before.Resources.Keys.Union(after.Resources.Keys, StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<(string Kind, string Name), string> was = Elements(before.Resources.GetValueOrDefault(culture));
            Dictionary<(string Kind, string Name), string> now = Elements(after.Resources.GetValueOrDefault(culture));
            changes.AddRange(now.Where(e => !was.TryGetValue(e.Key, out string? old) || old != e.Value).Select(e => new DesignResourceChange(e.Key.Kind, e.Key.Name, e.Value) { Culture = culture }));
            changes.AddRange(was.Keys.Where(k => !now.ContainsKey(k)).Select(k => new DesignResourceChange(k.Kind, k.Name, null) { Culture = culture }));
        }
        return changes;
    }

    /// <summary>A .resx's values, metadata and assembly aliases, by kind and name, as their elements' text, its lines broken
    /// with '\n', as written under the root.</summary>
    private static Dictionary<(string Kind, string Name), string> Elements(string? resx)
    {
        Dictionary<(string Kind, string Name), string> elements = [];
        foreach (XElement element in resx is null ? [] : XDocument.Parse(resx, LoadOptions.PreserveWhitespace).Root?.Elements() ?? [])
        {
            string kind = element.Name.LocalName;
            if (kind is "data" or "metadata" or "assembly" && element.Attribute(kind == "assembly" ? "alias" : "name")?.Value is { } name)
            {
                elements[(kind, name)] = element.ToString(SaveOptions.DisableFormatting).ReplaceLineEndings("\n");
            }
        }
        return elements;
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
