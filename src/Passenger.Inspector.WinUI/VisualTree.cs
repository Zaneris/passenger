using System.Runtime.InteropServices;

namespace Passenger.Inspector.WinUI;

/// <summary>WinUI's IVisualTreeServiceCallback, which its XAML diagnostics tell of each element that comes or goes.</summary>
[ComImport]
[Guid("AA7A8931-80E4-4FEC-8F3B-553F87B4966E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVisualTreeServiceCallback
{
    void OnVisualTreeChange(ParentChildRelation relation, VisualElement element, int mutation);
}

/// <summary>An element of a WinUI app as its XAML diagnostics tell of it: the handle WinUI names it by, its type's full name,
/// its name, where its XAML made it (the file as its build named it, ms-appx:///MainWindow.xaml, and the line, from one;
/// none where nothing recorded it), and where it is in the tree.</summary>
internal sealed class Visual(ulong handle)
{
    public ulong Handle { get; } = handle;

    public string FullType { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? File { get; set; }

    public int Line { get; set; }

    public int Column { get; set; }

    public Visual? Parent { get; set; }

    public List<Visual> Children { get; } = [];

    /// <summary>Its type without its namespace, as the tree names it.</summary>
    public string Type => FullType[(FullType.LastIndexOf('.') + 1)..];

    /// <summary>Whether WinUI's own theme made it (a control's template), not the app's XAML.</summary>
    public bool FromTheme => File is { } file && (file.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || file.Contains("/Microsoft.UI.Xaml/", StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name is { Length: > 0 } ? $"{Type}#{Name}" : Type;
}

/// <summary>The app's elements as WinUI's XAML diagnostics tell of them, kept as they come and go, its top levels (each
/// window's DesktopWindowXamlSource, its popups' roots) the roots. Told on whatever thread WinUI tells on, read on the UI
/// thread: every change and read takes its lock.</summary>
[ComVisible(true)]
[Guid("3E5C8A71-0B2D-4F6E-A9C4-7D1B2E3F4A55")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class VisualTree : IVisualTreeServiceCallback
{
    private readonly object gate = new();
    private readonly Dictionary<ulong, Visual> byHandle = [];
    private readonly List<Visual> roots = [];

    /// <summary>Raised after an element came or went, on the thread WinUI told on.</summary>
    internal event Action? Changed;

    internal IReadOnlyList<Visual> Roots
    {
        get
        {
            lock (gate)
            {
                return [.. roots];
            }
        }
    }

    internal Visual? Find(ulong handle)
    {
        lock (gate)
        {
            return byHandle.GetValueOrDefault(handle);
        }
    }

    /// <summary>The children of an element, as they stand.</summary>
    internal IReadOnlyList<Visual> ChildrenOf(Visual visual)
    {
        lock (gate)
        {
            return [.. visual.Children];
        }
    }

    public void OnVisualTreeChange(ParentChildRelation relation, VisualElement element, int mutation)
    {
        try
        {
            lock (gate)
            {
                if (mutation == 0)
                {
                    Add(relation, element);
                }
                else
                {
                    Remove(relation.Child != 0 ? relation.Child : element.Handle);
                }
            }
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            // WinUI's own call: nothing the inspector meets may end the app.
            InspectorTrace.Step($"a change of the tree could not be taken: {error}");
        }
    }

    private void Add(ParentChildRelation relation, VisualElement element)
    {
        if (!byHandle.TryGetValue(element.Handle, out Visual? visual))
        {
            visual = new Visual(element.Handle);
            byHandle[element.Handle] = visual;
        }
        visual.FullType = Diagnostics.Text(element.Type) ?? visual.FullType;
        visual.Name = Diagnostics.Text(element.Name) is { Length: > 0 } name ? name : null;
        visual.File = Diagnostics.Text(element.SrcInfo.FileName) is { Length: > 0 } file ? file : null;
        visual.Line = (int)element.SrcInfo.LineNumber;
        visual.Column = (int)element.SrcInfo.ColumnNumber;
        Detach(visual);
        if (relation.Parent == 0)
        {
            roots.Add(visual);
            return;
        }
        if (!byHandle.TryGetValue(relation.Parent, out Visual? parent))
        {
            parent = new Visual(relation.Parent);
            byHandle[relation.Parent] = parent;
        }
        visual.Parent = parent;
        parent.Children.Insert(Math.Min((int)relation.ChildIndex, parent.Children.Count), visual);
    }

    private void Remove(ulong handle)
    {
        if (!byHandle.TryGetValue(handle, out Visual? visual))
        {
            return;
        }
        Detach(visual);
        List<Visual> gone = [visual];
        for (int i = 0; i < gone.Count; i++)
        {
            byHandle.Remove(gone[i].Handle);
            gone.AddRange(gone[i].Children);
        }
    }

    private void Detach(Visual visual)
    {
        if (visual.Parent is { } parent)
        {
            parent.Children.Remove(visual);
            visual.Parent = null;
        }
        roots.Remove(visual);
    }
}
