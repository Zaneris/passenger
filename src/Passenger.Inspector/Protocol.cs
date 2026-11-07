using System.Text.Json;
using System.Text.Json.Serialization;

// What Passenger and its inspector inside an Avalonia app (src/Passenger.Inspector) or a WPF app
// (src/Passenger.Inspector.Wpf) say to each other, compiled into all three: one JSON object a line over the socket
// Passenger names in the app's environment. The messages are the same for both (a WPF dependency property is a
// property, its value sources take the same words); what one framework has not (Avalonia's overlays, frames and binding
// values) the other's inspector refuses. Passenger asks (Request) and the app answers each by its id (Reply); the app
// also tells without being asked (a Reply with an Event): hello once its UI runs, treeChanged as controls come and go,
// picked when the person points at one, bindingFailed as a binding fails (from before hello), untold when it could not
// tell all it had. While the app is paused its binding values are read through the debugger's evaluation of
// BindingValues.Read. A XAML file's edit reaches what the app made from it through reload (XAML Hot Reload).
namespace Passenger.Inspector;

/// <summary>The names both ends agree on.</summary>
public static class Wire
{
    /// <summary>Bumped by a change either end must know of: a hello of another version is refused, saying so.</summary>
    public const int Version = 10;

    /// <summary>The app's environment: the socket's path, where it connects.</summary>
    [IdentifierHome]
    public const string SocketVariable = "PASSENGER_INSPECTOR";

    /// <summary>The app's environment: Avalonia's renderer debug overlays its windows show from the launch on, as
    /// <see cref="Request.Overlays"/> names them, joined by commas.</summary>
    [IdentifierHome]
    public const string OverlaysVariable = "PASSENGER_INSPECTOR_OVERLAYS";

    /// <summary>A WPF app's environment: the launched project's folder, where its XAML is when its build's symbols do not say.</summary>
    [IdentifierHome]
    public const string ProjectVariable = "PASSENGER_INSPECTOR_PROJECT";

    /// <summary>The app's environment: the XAML file Avalonia's designer previews, whose each load the inspector has record
    /// where in the file it made each element, for <see cref="Placed"/>.</summary>
    [IdentifierHome]
    public const string PreviewVariable = "PASSENGER_INSPECTOR_PREVIEW";

    /// <summary>The app's environment where nobody is to see it (a Passenger nobody sees launched it): its Avalonia draws on
    /// the headless platform, and where it would draw elsewhere it ends before its Main (<see cref="UnseenEnds"/>) or runs
    /// on, on a desktop of its own (<see cref="UnseenRuns"/>).</summary>
    [IdentifierHome]
    public const string UnseenVariable = "PASSENGER_INSPECTOR_UNSEEN";

    /// <summary><see cref="UnseenVariable"/>'s value where nothing hides an app's windows but its headless platform.</summary>
    public const string UnseenEnds = "ends";

    /// <summary><see cref="UnseenVariable"/>'s value where the app runs on a desktop of its own, which nobody sees.</summary>
    public const string UnseenRuns = "runs";

    /// <summary>The app's environment: a file the inspector traces its start and its answers to.</summary>
    [IdentifierHome]
    public const string TraceVariable = "PASSENGER_INSPECTOR_TRACE";

    public const string Tree = "tree";
    public const string Details = "details";
    public const string Pick = "pick";
    public const string Highlight = "highlight";
    public const string Windows = "windows";
    public const string Capture = "capture";
    public const string Input = "input";
    public const string Watch = "watch";
    public const string Values = "values";
    public const string Overlays = "overlays";
    public const string Reload = "reload";
    public const string Made = "made";
    public const string Placed = "placed";

    /// <summary>Avalonia's renderer debug overlays, by the names of its RendererDebugOverlays flags, in its order.</summary>
    public static readonly IReadOnlyList<string> OverlayNames = ["Fps", "DirtyRects", "LayoutTimeGraph", "RenderTimeGraph"];

    /// <summary>What the debugger evaluates for a XAML file's binding values while the app is paused, given the read (an
    /// <see cref="XamlRead"/>, as JSON) as a string: an object array, the <see cref="XamlValues"/> as JSON first, then each value
    /// shown, at the <see cref="ShownValue.Slot"/> that names it.</summary>
    public const string ReadValues = "global::Passenger.Inspector.BindingValues.Read";

    /// <summary>The method a watched property's change calls (PropertyBreaks.cs), where the debugger's function breakpoint
    /// for each property breakpoint is.</summary>
    public const string Changed = "Passenger.Inspector.Breaks.Changed";

    /// <summary>The method each failed binding calls in a debugged app (Bindings.cs), where the debugger's function breakpoint
    /// for the XAML Binding Errors breakpoint is; the WPF inspector's has the same name, so the one breakpoint serves both.</summary>
    public const string Failed = "Passenger.Inspector.Breaks.BindingFailed";

    public const string Hello = "hello";
    public const string TreeChanged = "treeChanged";
    public const string Picked = "picked";
    public const string PickEnded = "pickEnded";
    public const string BindingFailed = "bindingFailed";

    /// <summary>A property whose binding the last values read showed has changed since: what was read is out of date.</summary>
    public const string ValuesChanged = "valuesChanged";

    /// <summary>What the app could not tell so far (<see cref="Untold"/>), said again as it grows.</summary>
    public const string Untold = "untold";
}

/// <summary>What Passenger asks: the windows' tree, an element's details, Select Element on or off, an element outlined in
/// the app (none to take the outline away), the windows alone, a picture of a window (or of the element's part of it),
/// input to the app, a property breakpoint's watch on an element's property (On false to end it), a XAML file's text
/// applied, the elements the app made at places of a XAML file (Made, with <see cref="Xaml"/>), or where it shows each element
/// made from a XAML file (Placed).</summary>
public sealed record Request(int Id, string Method, long? Element = null, bool? On = null)
{
    /// <summary>With Watch: the property, by the name the element's details list it under (Owner.Name when attached).</summary>
    public string? Property { get; init; }

    /// <summary>With Watch: the property breakpoint's number, which the debugger's condition on its stop names.</summary>
    public int? Breakpoint { get; init; }

    /// <summary>With Watch: the breakpoint's hit count ("==2", ">3", "%2"), counted where the changes are seen.</summary>
    public string? HitCondition { get; init; }

    /// <summary>With Tree: each element's place in its window, its text, and whether it is disabled, focused or out of view.</summary>
    public bool? Layout { get; init; }

    /// <summary>With Tree: the values of these properties (Avalonia's, an attached one as Owner.Name, else the CLR's) on each
    /// element that has them.</summary>
    public IReadOnlyList<string>? Properties { get; init; }

    /// <summary>With Capture: pixels a unit; else the window's own scaling, less for a large picture.</summary>
    public double? Scale { get; init; }

    /// <summary>With Capture: the part of the window to draw, in its units; else the element's part, or all of it.</summary>
    public Box? Area { get; init; }

    public InputAction? Input { get; init; }

    /// <summary>With Values: the XAML file and its elements whose bindings to read; with Made, the elements to find, the one
    /// chosen telling apart the instances of one made more than once.</summary>
    public XamlRead? Xaml { get; init; }

    /// <summary>With Overlays: the renderer debug overlays every window of the app shows from now (<see cref="Wire.OverlayNames"/>),
    /// none to take them away; answered with the windows.</summary>
    public IReadOnlyList<string>? Overlays { get; init; }

    /// <summary>With Reload: the XAML file's text to apply to what the app made from it.</summary>
    public XamlChange? Change { get; init; }
}

/// <summary>A XAML file's text to apply to what the app made from it (XAML Hot Reload): the file's path as the disk names
/// it, its text now and as the app has it (the build's, or the text last applied), the assembly its build compiled it
/// into, the URI its build gave it (avares://Assembly/Folder/File.axaml, a pack URI in WPF), and whether a binding in it
/// compiles unless it says otherwise (Avalonia's project setting).</summary>
public sealed record XamlChange(string File, string Text, string? Before, string Assembly, string Uri)
{
    public bool? CompiledBindings { get; init; }
}

/// <summary>What a XAML file's text came to in the app: the objects that took it (Type#Name), each made again from it, or
/// why it could not apply (<see cref="Refused"/>: nothing changed), with the places in the file the app's XAML compiler
/// named. No objects and nothing refused: nothing open in the app was made from the file.</summary>
public sealed record XamlReloaded(IReadOnlyList<string> Objects, string? Refused, IReadOnlyList<XamlProblem> Problems);

/// <summary>Something the app's XAML compiler said of a XAML file, at its line and column (from one, as an XML reader counts
/// them; none when it named no place): an error, which keeps the text from applying, or a warning.</summary>
public sealed record XamlProblem(string Message, int? Line, int? Column, bool Error);

/// <summary>A XAML file's bindings to read with their values: the file as the IDE has it, its elements by where the app's build
/// recorded each, and the element chosen in the Live Visual Tree, which tells apart the instances of an element made more than
/// once (a template's, a user control's).</summary>
public sealed record XamlRead(string File, IReadOnlyList<XamlSpot> Elements, long? Chosen)
{
    /// <summary>Over the socket: these elements add to those the file's last read asked of (lines scrolled to), whose
    /// properties the app goes on watching, rather than taking their place.</summary>
    public bool? More { get; init; }
}

/// <summary>An element of a XAML file: where it starts as Avalonia records it (its name's line and column, from one), the class
/// a root element names (its x:Class, for a root Avalonia records no place of), and the properties its bindings set, by the
/// names their attributes have (an attached one as Owner.Name).</summary>
public sealed record XamlSpot(int Line, int Column, string? Class, IReadOnlyList<string> Properties);

/// <summary>What the app read of a XAML file's bindings: each element asked of that it made, or why none could be read
/// (<see cref="Problem"/>; <see cref="WrongThread"/> when asked on a thread other than its UI thread).</summary>
public sealed record XamlValues(IReadOnlyList<ElementValues> Elements, string? Problem)
{
    public bool? WrongThread { get; init; }
}

/// <summary>An element's bindings on the instance read (Type#Name, by the id the Live Visual Tree knows it by), or, made more
/// times than one and none of them nearest the element chosen, how many instances there are and no bindings.</summary>
public sealed record ElementValues(int Line, int Column, string? Control, long? Id, int Instances, IReadOnlyList<BoundValue> Bindings);

/// <summary>A binding on a property of an element's instance: the property's value now, the binding's path as Avalonia writes
/// it, the path walked from its source, each step with its value, and, for one that failed, why and at which step.
/// <see cref="Unbound"/> when no binding gives the property its value now (code set it since).</summary>
public sealed record BoundValue(string Property, string Path, ShownValue? Value, IReadOnlyList<PathStep> Steps, string? Failure, string? FailedAt)
{
    public bool? Unbound { get; init; }
}

/// <summary>A step of a binding's path: its source (DataContext, a named element, the templated parent) or a member read from
/// the step before, and its value, none when the walk never reached it.</summary>
public sealed record PathStep(string Name, ShownValue? Value);

/// <summary>A value as the app writes it (a string quoted, an object of no words of its own by its type in braces) and
/// its type; read through the debugger, also the slot of the evaluation's array that holds it (from one).</summary>
public sealed record ShownValue(string Text, string Type, int Slot);

/// <summary>Input to the app as a person gives it: into its window's own way in from the platform, so hit testing, routing,
/// pointer-over, focus, gestures, key bindings and commands all run as they would. Kind is one of <see cref="InputKinds"/>.
/// The pointer acts at the element's centre (or X,Y from its top left), or at X,Y in the window; typing and keys go to the
/// element, focused first, else to whatever has the focus. Keys are chords: an Avalonia Key's name and its
/// <see cref="Modifier"/>s.</summary>
public sealed record InputAction(string Kind)
{
    public long? Window { get; init; }

    public long? Element { get; init; }

    public double? X { get; init; }

    public double? Y { get; init; }

    /// <summary>left, right or middle.</summary>
    public string? Button { get; init; }

    /// <summary>Clicks in a row: two is a double click.</summary>
    public int? Count { get; init; }

    /// <summary>Held through a click or a scroll.</summary>
    public IReadOnlyList<Modifier>? Modifiers { get; init; }

    public string? Text { get; init; }

    /// <summary>With Type: the platform's Select All first, so the text replaces what the element holds.</summary>
    public bool? Replace { get; init; }

    public IReadOnlyList<Chord>? Keys { get; init; }

    /// <summary>With Scroll: notches of the wheel, right and down positive.</summary>
    public double? DeltaX { get; init; }

    public double? DeltaY { get; init; }
}

public static class InputKinds
{
    public const string Click = "click";
    public const string Type = "type";
    public const string Key = "key";
    public const string Scroll = "scroll";
}

/// <summary>A key and the modifiers held with it.</summary>
public sealed record Chord(string Key, IReadOnlyList<Modifier> Modifiers);

/// <summary>A modifier held with a key, a click or a scroll; Primary is the platform's command key (Cmd on macOS, Ctrl
/// elsewhere, and in a WPF app).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Modifier>))]
public enum Modifier
{
    Control,
    Shift,
    Alt,
    Meta,
    Primary,
}

/// <summary>What an input did: its window, where in it the pointer acted, the element there (the one that took it, or the
/// focused one for keys), what that element shows after (its text, a scroll viewer's offset), and what has the focus.</summary>
public sealed record InputDone(long Window, double? X, double? Y, long? Target, string? TargetName, string? Shows, string? Focused);

/// <summary>One of the app's top levels: a window, or a popup's (a menu, a tooltip, a drop-down) with the window it opened
/// from as its owner; its size in units and its pixels a unit.</summary>
public sealed record AppWindow(long Id, string Type, string? Name, string? Title, double Width, double Height, double Scaling, bool Active,
    bool Visible, long? Owner)
{
    /// <summary>The renderer debug overlays it shows (<see cref="Wire.OverlayNames"/>), read back from its renderer; null for none.</summary>
    public IReadOnlyList<string>? Overlays { get; init; }
}

/// <summary>A picture the app drew of one of its windows (its popups over it), or a part: a PNG, its size in pixels, the
/// pixels a unit, and the part drawn in the window's units.</summary>
public sealed record Picture(byte[] Png, int Width, int Height, double Scale, Box Area);

/// <summary>An answer to a request (its id, with what it asked for or why not), or something the app tells unasked.</summary>
public sealed record Reply
{
    public int? Id { get; init; }

    public string? Event { get; init; }

    public string? Error { get; init; }

    public Hello? Hello { get; init; }

    public AppTree? Tree { get; init; }

    public ElementDetails? Details { get; init; }

    /// <summary>The element pointed at, with Picked.</summary>
    public long? Element { get; init; }

    /// <summary>A binding that failed, with BindingFailed.</summary>
    public BindingFailure? Binding { get; init; }

    public IReadOnlyList<AppWindow>? Windows { get; init; }

    public Picture? Picture { get; init; }

    public InputDone? Done { get; init; }

    public XamlValues? Values { get; init; }

    /// <summary>What a XAML file's text came to, with Reload.</summary>
    public XamlReloaded? Reloaded { get; init; }

    /// <summary>Where the app shows each element of a XAML file it made, with Placed.</summary>
    public IReadOnlyList<PlacedBox>? Placed { get; init; }

    /// <summary>What the app could not tell, with Untold.</summary>
    public Untold? Untold { get; init; }
}

/// <summary>What the app could not tell since it started, in all: the failed bindings it counted but does not list (each
/// distinct failure past the most it lists, every time it failed), and the messages it left out while too many waited to
/// be written (none of them an answer, a binding failure or one of the events it merges: the inspector's own errors).</summary>
public sealed record Untold(int Bindings, int Messages);

/// <summary>The app's first word: its process, the Avalonia it runs (its assembly's version), and why the inspector cannot
/// serve it when it cannot (another major version of Avalonia, none at all).</summary>
public sealed record Hello(int Protocol, int ProcessId, string? Avalonia, string? Problem)
{
    /// <summary>From a WPF app's inspector: WPF and its version ("WPF 10.0.1"), where an Avalonia app says its Avalonia.</summary>
    public string? Framework { get; init; }

    /// <summary>Whether the app draws on Avalonia's headless platform, where nobody sees it.</summary>
    public bool Headless { get; init; }

    /// <summary>Whether the inspector started the app itself, through its BuildAvaloniaApp(), in place of its Main.</summary>
    public bool MainLeftOut { get; init; }
}

/// <summary>A binding that failed in the app, as Avalonia logged it (its Binding area, Warning or Error): the control it is
/// on (Type#Name) and the property it sets, its path and the part of the path that failed, Avalonia's message, the control's
/// XAML element when the app's build recorded it, and for a path naming what its source's type does not have, that type (as
/// Avalonia names it), the member and the type's members nearest it. Id names one failure for the run; it comes again as it
/// repeats, Count being how many times in all.</summary>
public sealed record BindingFailure(int Id, string Level, string Control, string Property, string Path, string? ErrorPoint, string Message,
    SourcePlace? Place, string? Type, string? Member, IReadOnlyList<string> Nearest, int Count)
{
    /// <summary>The type of the control's DataContext as it failed (no namespace), or null for none.</summary>
    public string? DataContext { get; init; }

    /// <summary>What it binds, the part of its path that failed when not the whole, and why: "TextBlock#Caption.Text to
    /// Person.Nmae at Nmae: Could not find a matching property accessor for 'Nmae' on 'App.Person'".</summary>
    [JsonIgnore]
    public string Description => $"{Control}.{Property} to {Path}{(ErrorPoint is { Length: > 0 } at && at != Path ? $" at {at}" : string.Empty)}: {Message}";
}

/// <summary>The app's top-level windows (popups among them), each with the visual tree under it, carried flat
/// (<see cref="AppTreeJson"/>).</summary>
[JsonConverter(typeof(AppTreeJson))]
public sealed record AppTree(IReadOnlyList<Element> Roots)
{
    /// <summary>The elements of <paramref name="level"/> and all they hold, each before what it holds, in tree order, with
    /// its depth (<paramref name="depth"/> for the level's own) and whether what it holds is walked: where it holds any and
    /// <paramref name="into"/>, asked as it is reached, says so (always by default). A walk with its own stack, so no tree
    /// is too deep for its thread.</summary>
    public static IEnumerable<(Element Element, int Depth, bool Open)> Walk(IReadOnlyList<Element> level, int depth = 0, Func<Element, int, bool>? into = null)
    {
        // Each level walked into, outermost first, with where the walk is in it.
        List<(IReadOnlyList<Element> Elements, int Next)> open = [(level, 0)];
        while (open.Count > 0)
        {
            (IReadOnlyList<Element> elements, int next) = open[^1];
            if (next >= elements.Count)
            {
                open.RemoveAt(open.Count - 1);
                continue;
            }
            open[^1] = (elements, next + 1);
            Element element = elements[next];
            int at = depth + open.Count - 1;
            bool walked = element.Children.Count > 0 && (into?.Invoke(element, at) ?? true);
            yield return (element, at, walked);
            if (walked)
            {
                open.Add((element.Children, 0));
            }
        }
    }
}

/// <summary>A visual in the tree: its id (the same for as long as it lives), type, name, style classes, whether a template
/// made it, whether the app's XAML has it (Avalonia recorded where), whether it shows, and its visual children, none read
/// past <see cref="DeepestNesting"/> levels (<see cref="Deeper"/>); asked with Layout, also where it is in its window, its
/// text, and whether it is disabled or focused, and with Properties their values.</summary>
public sealed record Element(long Id, string Type, string? Name, IReadOnlyList<string> Classes, bool FromTemplate, bool Sourced, bool Visible,
    IReadOnlyList<Element> Children)
{
    /// <summary>How deep an app's tree is read, its windows the first level: past any app's own layout, a TreeView nested
    /// fifty deep among it, and short of a tree whose every line an agent's answer could indent.</summary>
    public const int DeepestNesting = 256;

    /// <summary>True where it holds elements the tree does not carry, being <see cref="DeepestNesting"/> levels down.</summary>
    public bool? Deeper { get; init; }

    /// <summary>Its box in its window's units, transforms included.</summary>
    public Box? Bounds { get; init; }

    /// <summary>What it shows as text: a TextBlock's or TextBox's (a password's masked), a string content's or header's, a
    /// window's title.</summary>
    public string? Text { get; init; }

    /// <summary>True when it takes no input (IsEffectivelyEnabled false).</summary>
    public bool? Disabled { get; init; }

    /// <summary>True for the element with the keyboard focus.</summary>
    public bool? Focused { get; init; }

    /// <summary>True when what clips it (its window, a scroll viewer) shows none of it.</summary>
    public bool? OutOfView { get; init; }

    public IReadOnlyDictionary<string, string>? Values { get; init; }
}

/// <summary>An element shown that was made from a XAML file: where its name starts in the file, as the app's XAML compiler
/// counted (from one), and its box in its window's units.</summary>
public sealed record PlacedBox(int Line, int Column, Box Box);

/// <summary>A place in a XAML file, as Avalonia records it under a Debug build: the file's full path, the line and column of
/// the element's start, both from one.</summary>
public sealed record SourcePlace(string File, int Line, int Column);

/// <summary>A visual looked at closely: what it is, where its XAML is (or why there is none), its layout, every property with
/// its value and where the value came from, and the styles that apply to it and those that do not, with why.</summary>
public sealed record ElementDetails(
    long Id,
    string Type,
    string FullType,
    string? Name,
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> PseudoClasses,
    SourcePlace? Source,
    string? NoSource,
    string? TemplatedParent,
    LayoutInfo Layout,
    IReadOnlyList<PropertyValue> Properties,
    IReadOnlyList<StyleReport> Styles);

/// <summary>Where a visual is laid out: its bounds in its parent and in its window, its desired size, margin, border and
/// padding (none when it has no such property), and its alignment.</summary>
public sealed record LayoutInfo(Box Bounds, Box? InWindow, double DesiredWidth, double DesiredHeight, Edges Margin, Edges? Border,
    Edges? Padding, string HorizontalAlignment, string VerticalAlignment);

public sealed record Box(double X, double Y, double Width, double Height);

public sealed record Edges(double Left, double Top, double Right, double Bottom);

/// <summary>An Avalonia property's value on a visual and its source (<see cref="ValueSources"/>), with what that source is
/// (the style's selector, the ancestor it is inherited from) and where it is written when Avalonia knows.</summary>
public sealed record PropertyValue(string Name, string Owner, bool Attached, string Type, string Value, string Source, string? Detail,
    SourcePlace? Place);

/// <summary>The sources a value can have, in the words the window shows.</summary>
public static class ValueSources
{
    public const string Local = "Local";
    public const string Animation = "Animation";
    public const string Style = "Style";
    public const string Theme = "Theme";
    public const string Template = "Template";
    public const string Inherited = "Inherited";
    public const string Default = "Default";

    /// <summary>Set by the control itself (SetCurrentValue), over whatever else gave it a value.</summary>
    public const string Current = "Current";

    /// <summary>A direct property: a field of the control's, which code and bindings set and styles seldom do.</summary>
    public const string Direct = "Direct";
}

/// <summary>A style that could apply to a visual: its selector as Avalonia writes it, what kind it is (<see cref="StyleKinds"/>),
/// where it comes from (a file, a theme), where its selector is written, whether it applies and why not, and its setters.</summary>
public sealed record StyleReport(string Selector, string Kind, string Origin, SourcePlace? Place, bool Applies, string? WhyNot,
    IReadOnlyList<SetterReport> Setters);

public static class StyleKinds
{
    /// <summary>The control's control theme, or one it is based on.</summary>
    public const string Theme = "Theme";

    /// <summary>A style nested in the control's control theme.</summary>
    public const string ThemeStyle = "Theme style";

    /// <summary>A style nested in the control theme of the control whose template made this one.</summary>
    public const string TemplatedParentTheme = "Templated parent's theme";

    public const string Style = "Style";

    /// <summary>WPF's: a trigger of a style (the element's own, or its default one), applying while its conditions hold.</summary>
    public const string Trigger = "Trigger";

    /// <summary>WPF's: a trigger of a control template, the element's own or the one of the control whose template made it.</summary>
    public const string TemplateTrigger = "Template trigger";
}

/// <summary>A setter of a style: the property, the value it sets, where it is written, and what became of it
/// (<see cref="SetterStates"/>), with what overrides it.</summary>
public sealed record SetterReport(string Property, string Value, SourcePlace? Place, string State, string? By)
{
    /// <summary>Where what overrides it is written (another style's selector), which Passenger says after <see cref="By"/> at
    /// the editor's line: a place in words keeps the XAML compiler's, which a lone '\r' puts lines ahead.</summary>
    public SourcePlace? ByPlace { get; init; }
}

public static class SetterStates
{
    /// <summary>Its value is the property's.</summary>
    public const string Wins = "Wins";

    /// <summary>Something before it in Avalonia's order gives the property its value.</summary>
    public const string Overridden = "Overridden";

    /// <summary>Its style does not apply now.</summary>
    public const string Inactive = "Inactive";
}

/// <summary>An app's tree as the wire carries it, flat: its elements in tree order (<see cref="AppTree.Walk"/>), each
/// without what it holds, then how many each holds, read back into the tree with a stack of its own. So neither end's
/// System.Text.Json, which walks what it reads and writes on its thread's stack, goes deeper than an element, however deep
/// the tree.</summary>
internal sealed class AppTreeJson : JsonConverter<AppTree>
{
    private const string Elements = "elements";
    private const string Held = "held";

    public override void Write(Utf8JsonWriter writer, AppTree value, JsonSerializerOptions options)
    {
        List<int> held = [];
        writer.WriteStartObject();
        writer.WriteStartArray(Elements);
        foreach ((Element element, _, _) in AppTree.Walk(value.Roots))
        {
            JsonSerializer.Serialize(writer, element.Children.Count == 0 ? element : element with { Children = [] }, WireJson.Default.Element);
            held.Add(element.Children.Count);
        }
        writer.WriteEndArray();
        writer.WriteStartArray(Held);
        foreach (int count in held)
        {
            writer.WriteNumberValue(count);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public override AppTree Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        List<Element> elements = [];
        List<int> held = [];
        Expect(ref reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string? name = reader.GetString();
            reader.Read();
            if (name is Elements or Held)
            {
                Expect(ref reader, JsonTokenType.StartArray);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (name == Elements)
                    {
                        elements.Add(JsonSerializer.Deserialize(ref reader, WireJson.Default.Element) ?? throw new JsonException("An element of an app's tree was null"));
                    }
                    else
                    {
                        held.Add(reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int count) && count >= 0
                            ? count
                            : throw new JsonException("An app's tree said an element held what is no count"));
                    }
                }
            }
            else
            {
                reader.Skip();
            }
        }
        Expect(ref reader, JsonTokenType.EndObject);
        return new AppTree(Assembled(elements, held));
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (reader.TokenType != token)
        {
            throw new JsonException($"An app's tree had {reader.TokenType} where {token} belonged");
        }
    }

    /// <summary>The trees of elements in tree order, each followed by as many as it holds and all they hold.</summary>
    private static List<Element> Assembled(List<Element> elements, List<int> held)
    {
        if (held.Count != elements.Count)
        {
            throw new JsonException($"An app's tree said what {held.Count} of its {elements.Count} elements held");
        }
        List<Element> roots = [];
        // The elements whose children are still to come, outermost first, with how many are left.
        List<(Element Element, int Left, List<Element> Children)> open = [];
        for (int i = 0; i < elements.Count; i++)
        {
            open.Add((elements[i], held[i], []));
            while (open.Count > 0 && open[^1].Left == 0)
            {
                Element done = open[^1].Element with { Children = open[^1].Children };
                open.RemoveAt(open.Count - 1);
                if (open.Count == 0)
                {
                    roots.Add(done);
                    continue;
                }
                open[^1] = open[^1] with { Left = open[^1].Left - 1 };
                open[^1].Children.Add(done);
            }
        }
        return open.Count == 0 ? roots : throw new JsonException($"An app's tree ended with {open.Count} of its elements short of what they held");
    }
}

/// <summary>How the messages are written: camel case, nulls left out, generated for both ends (an app may turn reflection
/// off for System.Text.Json).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Reply))]
[JsonSerializable(typeof(Element))]
[JsonSerializable(typeof(BindingFailure))]
[JsonSerializable(typeof(XamlRead))]
[JsonSerializable(typeof(XamlValues))]
internal sealed partial class WireJson : JsonSerializerContext
{
}
