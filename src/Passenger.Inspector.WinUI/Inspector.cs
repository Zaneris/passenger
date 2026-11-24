using System.Globalization;
using System.Runtime.InteropServices;

namespace Passenger.Inspector.WinUI;

/// <summary>The inspector's work on a WinUI app's UI thread: its windows and their visual trees as WinUI's diagnostics tell
/// of them (<see cref="VisualTree"/>), an element looked at closely (its properties as the diagnostics read them, with what
/// gave each its value and where its XAML is), and for an agent its windows' pictures and input to them
/// (<see cref="Pictures"/>, <see cref="Driver"/>). A top level is a window's DesktopWindowXamlSource or a popup's root.
/// Select Element, property breakpoints, binding values and XAML Hot Reload are refused, saying so.</summary>
internal sealed partial class Inspector : Inspecting<Visual>
{
    private readonly Diagnostics diagnostics;
    private readonly VisualTree tree;
    private readonly UiQueue ui;
    private readonly Driver driver;

    public Inspector(Connection connection, Diagnostics diagnostics, VisualTree tree, UiQueue ui, string? project) : base(connection)
    {
        this.diagnostics = diagnostics;
        this.tree = tree;
        this.ui = ui;
        Project = project;
        driver = new Driver(this);
        tree.Changed += () => ui.Post(() => Changed(null));
    }

    /// <summary>The launched project's folder, which an ms-appx:/// path of the app's XAML is a file of.</summary>
    public string? Project { get; }

    public Diagnostics Diagnostics => diagnostics;

    public VisualTree Visuals => tree;

    /// <summary>Whether the app has a window yet.</summary>
    public bool HasWindow() => tree.Roots.Any(IsWindow);

    /// <summary>The app's top levels: its windows, then its popups' roots.</summary>
    public IReadOnlyList<Visual> TopLevels() => tree.Roots;

    /// <summary>Whether a top level is a window (its DesktopWindowXamlSource), not a popup's root.</summary>
    public static bool IsWindow(Visual top) => top.FullType.EndsWith(".DesktopWindowXamlSource", StringComparison.Ordinal);

    /// <summary>What a window shows, its content's root (the RootScrollViewer beside its PopupRoot); a top level that is no
    /// window shows itself.</summary>
    public Visual? ContentOf(Visual top) => !IsWindow(top) ? top
        : tree.ChildrenOf(top).FirstOrDefault(child => !child.FullType.EndsWith(".PopupRoot", StringComparison.Ordinal));

    /// <summary>The projected object of what a window shows, from which its XamlRoot is read; null where it shows nothing yet.</summary>
    public object? ContentObjectOf(Visual top) => ContentOf(top) is { } content ? ObjectOf(content) : null;

    /// <summary>The projected object an element is, as the app's code sees it; null for one gone.</summary>
    public object? ObjectOf(Visual visual)
    {
        nint inspectable = diagnostics.Inspectable(visual.Handle);
        try
        {
            return Projected.Of(inspectable);
        }
        finally
        {
            Projected.Release(inspectable);
        }
    }

    /// <summary>The element an object of the app's is, where WinUI's diagnostics have told of it.</summary>
    public Visual? VisualOf(object projected)
    {
        nint inspectable = Projected.InspectableOf(projected);
        try
        {
            return inspectable == 0 ? null : tree.Find(diagnostics.HandleOf(inspectable));
        }
        finally
        {
            Projected.Release(inspectable);
        }
    }

    /// <summary>The top level an element is in, itself when it is one.</summary>
    public static Visual? TopOf(Visual visual)
    {
        Visual at = visual;
        while (at.Parent is { } parent)
        {
            at = parent;
        }
        return at;
    }

    /// <summary>Where an element's XAML is, as the disk names it: an ms-appx:/// file of the project's; null for one WinUI's
    /// theme made, or of no file recorded.</summary>
    public SourcePlace? PlaceOf(Visual visual)
    {
        if (visual.File is not { } file || visual.Line <= 0 || visual.FromTheme)
        {
            return null;
        }
        const string App = "ms-appx:///";
        string? path = file.StartsWith(App, StringComparison.OrdinalIgnoreCase) && Project is { } folder
            ? Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(file[App.Length..]).Replace('/', Path.DirectorySeparatorChar)))
            : Path.IsPathRooted(file) ? file : null;
        return path is null ? null : new SourcePlace(path, visual.Line, Math.Max(1, visual.Column));
    }

    protected override AppTree Tree(Request request) => new([.. TopLevels().Select(top => ElementTrees.Made(top, 0, tree.ChildrenOf, (_, depth) => depth + 1,
        (Visual visual, int _, IReadOnlyList<Element> children) =>
        {
            Element made = new(IdOf(visual), visual.Type, visual.Name, [], visual.FromTheme, PlaceOf(visual) is not null, true, children);
            return request.Layout == true ? Shown.Laid(this, visual, made) : made;
        }))]);

    protected override List<AppWindow> Windows() => [.. TopLevels().Select(Shown.Window(this))];

    protected override ElementDetails DetailsOf(Visual element) => Details.Of(this, element);

    protected override void Pick(bool on) => throw new Refusal("Select Element is not available for a WinUI app: choose the element in the tree");

    protected override void Outline(Visual? element)
    {
        // A WinUI app is not drawn over: its chosen element is the tree's alone.
    }

    /// <summary>Never asked: a picture is drawn off the UI thread (<see cref="Pictures.TakeAsync"/>).</summary>
    protected override Picture Capture(Visual element, Request request) =>
        throw new InvalidOperationException("A WinUI app's picture is drawn off its UI thread");

    protected override InputDone Do(InputAction input) => driver.Do(input);

    protected override void Watch(int breakpoint, Visual element, string property, string? hitCondition) =>
        throw new Refusal("Property breakpoints are not available for a WinUI app");

    protected override void Unwatch(int breakpoint)
    {
    }

    protected override XamlValues ValuesOf(Request request) =>
        throw new Refusal("Binding values after a XAML file's lines are read from an Avalonia app's .axaml; a WinUI app's are not");

    /// <summary>The element WinUI made at each place asked of, by where its XAML recorded it: of several, the one chosen or
    /// inside it, else the first.</summary>
    protected override XamlValues MadeAt(XamlRead read)
    {
        List<(Visual Made, SourcePlace Place)> all = [.. TopLevels().SelectMany(top => ElementTrees.Below(top, tree.ChildrenOf).Prepend(top))
            .Select(v => (v, PlaceOf(v))).Where(p => p.Item2 is not null).Select(p => (p.v, p.Item2!))];
        string? inFile = XamlPlaces.FileOf(all.Select(a => a.Place.File), read.File);
        Visual? chosen = Find(read.Chosen);
        List<ElementValues> elements = [];
        foreach (XamlSpot spot in read.Elements)
        {
            List<Visual> instances = [.. all.Where(a => a.Place.File == inFile && a.Place.Line == spot.Line).Select(a => a.Made)];
            if (instances.Count > 0)
            {
                Visual instance = instances.FirstOrDefault(i => chosen is not null && Within(chosen, i)) ?? instances[0];
                elements.Add(new ElementValues(spot.Line, spot.Column, instance.ToString(), IdOf(instance), instances.Count, []));
            }
        }
        return new XamlValues(elements, null);
    }

    /// <summary>The one element of a type and name in the app's windows; null where none or several are.</summary>
    public Visual? Named(string fullType, string name)
    {
        Visual[] found = [.. TopLevels().SelectMany(top => ElementTrees.Below(top, tree.ChildrenOf).Prepend(top))
            .Where(v => v.Name == name && v.FullType == fullType).Take(2)];
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>Whether an element is another or inside it.</summary>
    public static bool Within(Visual element, Visual ancestor)
    {
        for (Visual? at = element; at is not null; at = at.Parent)
        {
            if (at == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    protected override List<AppWindow> SetOverlays(Request request) => throw new Refusal("Renderer debug overlays are Avalonia's: a WinUI app has none");

    protected override XamlReloaded Reload(XamlChange change) =>
        new([], "XAML Hot Reload is not available for a WinUI app: its XAML is compiled into the app, so a change takes a rebuild", []);

    protected override void Later(TimeSpan delay, Action action)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            ui.Post(action);
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    protected override bool LeftOut(Visual element) => false;

    protected override void Stopping() => Bindings.Stop();

    /// <summary>A number as the window writes it, whatever the app's culture.</summary>
    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetWindowText(nint window, char* text, int most);

    /// <summary>A window's title, as Windows has it.</summary>
    public static unsafe string TitleOf(nint window)
    {
        char* text = stackalloc char[512];
        int length = GetWindowText(window, text, 512);
        return new string(text, 0, Math.Max(0, length));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();
}
