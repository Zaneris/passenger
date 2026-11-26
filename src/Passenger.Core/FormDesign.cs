using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

// What Passenger and its Windows Forms designer (src/Passenger.WinFormsDesigner) say to each other, and the designer code
// both read: the App compiles it with Core, the designer as a linked file. Passenger reads a form's InitializeComponent
// (DesignerCode) into its statements, each value as the code writes it, and sends them as a JSON line whenever they or
// the window's scale change; the designer answers the newest it has read: the form drawn as Windows Forms draws it, with
// each component's box, what it could not make of a statement, or why there is nothing to draw.
namespace Passenger.Core;

/// <summary>What a statement of designer code does to a component: makes it, sets a property of it, hooks a handler to one of
/// its events, or calls a method of it (adds a control to it, begins its layout); anything else the designer writes is other.</summary>
public enum DesignerAct
{
    Create,
    Assign,
    Hook,
    Call,
    Other,
}

/// <summary>A statement of a Windows Forms class's InitializeComponent: what it does, to which component (the class's field,
/// empty for the class's own root) and which of its members (a property's dotted path, an event, a method's), where it
/// starts in the text as an offset (the editor's lines, never Roslyn's, are counted from it), and its value (what a
/// creation makes, an assignment sets, a hook hooks) or a call's arguments, as the code writes them.</summary>
public sealed record DesignerStatement(DesignerAct Act, string Target, string? Member, int At)
{
    public DesignerValue? Value { get; init; }

    public IReadOnlyList<DesignerValue>? Arguments { get; init; }

    /// <summary>The component whose section comment it stands under (the last one above it); none before the first.</summary>
    public string? Section { get; init; }
}

/// <summary>A value of designer code as the code writes it, its names unresolved (the designer resolves them by the file's
/// usings): of a kind (<see cref="DesignerValueKinds"/>), with the type it names, a dotted name, a literal's text, and the
/// values it holds.</summary>
public sealed record DesignerValue(string Kind)
{
    /// <summary>A type as written: a new object's, an array's items', a cast's, a typeof's; a literal's C# type.</summary>
    public string? Type { get; init; }

    /// <summary>A dotted name as written (<c>DockStyle.Fill</c>, <c>button1</c>, <c>this</c>), or a call's method with what it
    /// is called on (<c>Color.FromArgb</c>).</summary>
    public string? Name { get; init; }

    /// <summary>A literal's value in invariant text.</summary>
    public string? Text { get; init; }

    public IReadOnlyList<DesignerValue>? Arguments { get; init; }
}

/// <summary>The kinds of <see cref="DesignerValue"/>.</summary>
public static class DesignerValueKinds
{
    public const string Literal = "literal";

    /// <summary>A component of the class, an enum's member, a static field or property: a dotted name.</summary>
    public const string Name = "name";

    public const string New = "new";

    /// <summary>A static method's result, or a method of a local (<c>resources.GetObject</c>).</summary>
    public const string Call = "call";

    public const string Array = "array";

    public const string Cast = "cast";

    /// <summary>Flags ored together.</summary>
    public const string Or = "or";

    public const string Negate = "negate";

    public const string TypeOf = "typeof";

    /// <summary>An expression the designer does not write (a lambda, an initializer), its text kept to say so.</summary>
    public const string Unread = "unread";
}

/// <summary>A field of the class: its name and its type as written.</summary>
public sealed record DesignerField(string Name, string Type);

/// <summary>A form or user control to draw: its class's full name, the type it derives from as written, the namespaces its
/// names are read in (its file's usings, its project's global ones, its own namespace's), its fields and its
/// InitializeComponent's statements; and a change to make of it (<see cref="Edit"/>), answered as the code it changes, written
/// as the file writes its code (<see cref="Qualified"/>), and whether to list the Toolbox's items with the frame.</summary>
public sealed record FormDesignRequest
{
    public required int Sequence { get; init; }

    public required string Class { get; init; }

    public string? Base { get; init; }

    public IReadOnlyList<string> Usings { get; init; } = [];

    public IReadOnlyList<DesignerField> Fields { get; init; } = [];

    public IReadOnlyList<DesignerStatement> Statements { get; init; } = [];

    public DesignEdit? Edit { get; init; }

    /// <summary>The file writes its code as .NET Framework's designer did (<c>this.</c>, full type names, CodeDOM's
    /// parentheses), rather than as .NET's does (short names in the file's scope, method groups).</summary>
    public bool Qualified { get; init; }

    public bool Toolbox { get; init; }

    /// <summary>The component whose properties and events to list with the frame (the root's empty); none for none.</summary>
    public string? Inspect { get; init; }

    /// <summary>The project's code annotates nullability, which a handler written for an event follows.</summary>
    public bool Nullable { get; init; }
}

/// <summary>A change to a design, as a gesture or a key makes it (<see cref="DesignEditKinds"/>): the components it is made to
/// (their fields, the root's empty), the type to add (its full name with its assembly's), the component to add into or move
/// into (the root's empty; none: where the point is for an add, where they are for a move), and its numbers: an add's point
/// in the frame, a move's distance, a resize's new box in the frame; a property's or an event's name (<see cref="Member"/>)
/// and the text it is given, a value as typed or a handler's name (none: the one the designer names).</summary>
public sealed record DesignEdit(string Kind)
{
    public string? Member { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<string> Components { get; init; } = [];

    public string? Type { get; init; }

    public string? Parent { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

/// <summary>The kinds of <see cref="DesignEdit"/>.</summary>
public static class DesignEditKinds
{
    /// <summary>A Toolbox item made at a point of the frame, or in the chosen container's place where it has none.</summary>
    public const string Add = "add";

    public const string Move = "move";

    public const string Resize = "resize";

    public const string Delete = "delete";

    public const string Front = "front";

    public const string Back = "back";

    /// <summary>A property given a value from its text, as its type converter reads it.</summary>
    public const string Set = "set";

    /// <summary>A property put back to its default, which the code then leaves unset.</summary>
    public const string Reset = "reset";

    /// <summary>A handler hooked to an event: the one named, or the designer's own name for it, kept where one is hooked.</summary>
    public const string Hook = "hook";

    public const string Unhook = "unhook";

    /// <summary>A list of words (a ListBox's Items) given as its text, a line an item.</summary>
    public const string Items = "items";

    /// <summary>An item of a type added to a component's collection (a strip's items, a list's columns), named by its member.</summary>
    public const string AddItem = "item";

    /// <summary>An item of a list moved up (X -1) or down (X 1) in it.</summary>
    public const string Order = "order";
}

/// <summary>A property of the component inspected, as Windows Forms' property grid shows it: its name, category and type,
/// its value as its type converter writes it, whether the code sets it, whether it takes no value typed, the values it
/// offers to choose from (only those where <see cref="Exclusive"/>), and what it is for.</summary>
public sealed record DesignProperty(string Name, string Category, string Type, string Text)
{
    public bool Set { get; init; }

    public bool ReadOnly { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    public bool Exclusive { get; init; }

    public string? Description { get; init; }

    /// <summary>Its name is shown in parentheses, as the component's own name, (Name), is: first, apart from the rest.</summary>
    public bool Parenthesized { get; init; }

    /// <summary>The name it is shown by where that is not its own: an extender's, "ToolTip on toolTip1".</summary>
    public string? Shown { get; init; }

    /// <summary>A colour's value, alpha first, for its swatch; none for a value of another type.</summary>
    public uint? Argb { get; init; }

    /// <summary>A flags enum's flags (Anchor's Top, Bottom, Left, Right), each turned on or off alone; none for any other.</summary>
    public IReadOnlyList<string>? Flags { get; init; }

    /// <summary>A list of words' items (a ListBox's Items), a line each, which the list is given whole; none for any other value.</summary>
    public IReadOnlyList<string>? Lines { get; init; }

    /// <summary>The types of item a collection of components takes (a strip's menu items and buttons, a list's columns), each
    /// its full name with its assembly's; none for a value of no such items.</summary>
    public IReadOnlyList<string>? Adds { get; init; }

    /// <summary>The parts of a value its type lists apart (a Point's X and Y, a Font's Bold, a button's FlatAppearance's
    /// BorderSize), each set by its dotted name; none for a value of no parts.</summary>
    public IReadOnlyList<DesignProperty>? Parts { get; init; }
}

/// <summary>An event of the component inspected: its name and category, the handler hooked to it (none where none is), and
/// what it is for.</summary>
public sealed record DesignEvent(string Name, string Category)
{
    public string? Handler { get; init; }

    public string? Description { get; init; }
}

/// <summary>A line of designer code a change writes, by the request's statements: a statement made after the one at
/// <c>At</c> (before the first where it is -1) or before it (<see cref="Before"/>), with its section's comment first where
/// the file has none for its component (<see cref="Header"/>); the one at <c>At</c> taken out or written again; a field declared after the request's
/// field at <c>At</c> (where the file declares the class's components, at the end of the class where it is -1), or taken out.
/// <see cref="Text"/> is the code as the file writes it, its lines broken with '\n' and indented from the first.</summary>
public sealed record DesignerChange(string Kind, int At)
{
    public string? Text { get; init; }

    public string? Header { get; init; }

    /// <summary>A statement made before the one at <c>At</c> (the first of its section the file writes) rather than after it.</summary>
    public bool Before { get; init; }
}

/// <summary>The kinds of <see cref="DesignerChange"/>.</summary>
public static class DesignerChangeKinds
{
    public const string Insert = "insert";

    public const string Remove = "remove";

    public const string Replace = "replace";

    public const string Declare = "declare";

    public const string Undeclare = "undeclare";
}

/// <summary>An item of the Toolbox: what it makes (its name, its type's full name with its assembly's), the group it is
/// listed under, its picture (a PNG, 16 pixels square, none where its type gives none) and what it is for.</summary>
public sealed record ToolboxEntry(string Name, string Type, string Group)
{
    public byte[]? Png { get; init; }

    public string? Description { get; init; }
}

/// <summary>What a statement of designer code is to the designer, whoever reads it: the same key for the same act on the
/// same member of the same component, whatever value it gives, so a change found by the designer's own code is placed
/// at the line the file writes it on. None for what no change is placed by.</summary>
public static class DesignerKeys
{
    public static string? Of(DesignerStatement statement) => statement.Act switch
    {
        DesignerAct.Create => $"new {statement.Target}",
        DesignerAct.Assign => $"set {statement.Target}.{statement.Member}",
        DesignerAct.Hook => $"hook {statement.Target}.{statement.Member} {Handler(statement.Value)}",
        DesignerAct.Call => $"call {statement.Target}.{statement.Member}({string.Join(',', (statement.Arguments ?? []).Where(a => a.Kind == DesignerValueKinds.Name).Select(a => Last(a.Name!)))})",
        DesignerAct.Other when statement.Target.Length > 0 => $"local {statement.Target}",
        _ => null,
    };

    /// <summary>The method a hook hooks: a method group's, or the one a delegate is made of.</summary>
    private static string? Handler(DesignerValue? value) => value switch
    {
        { Kind: DesignerValueKinds.Name, Name: { } name } => Last(name),
        { Kind: DesignerValueKinds.New, Arguments: [{ Kind: DesignerValueKinds.Name, Name: { } made }] } => Last(made),
        _ => null,
    };

    private static string Last(string dotted) => dotted[(dotted.LastIndexOf('.') + 1)..];
}

/// <summary>The designer's answer to the newest request it read: the form as a PNG at 96 DPI with its title bar (a form's) and
/// each component's box in it (<see cref="DesignedBox"/>), what it could not make of statements (<see cref="DesignNote"/>),
/// an edit's code and what to choose after it, the Toolbox's items and a component's properties and events where asked; or
/// why it drew nothing.</summary>
public sealed record FormDesignReply
{
    public required string Kind { get; init; }

    public required int Sequence { get; init; }

    public byte[]? Png { get; init; }

    public IReadOnlyList<DesignedBox>? Components { get; init; }

    public IReadOnlyList<DesignNote>? Notes { get; init; }

    public string? Message { get; init; }

    /// <summary>An edit's code (<see cref="DesignerChange"/>), none where it changed nothing the code says.</summary>
    public IReadOnlyList<DesignerChange>? Changes { get; init; }

    /// <summary>An edit's components to choose once it is made: those it made or moved, the holder of those it took out.</summary>
    public IReadOnlyList<string>? Chosen { get; init; }

    public IReadOnlyList<ToolboxEntry>? Toolbox { get; init; }

    /// <summary>The inspected component's properties and events (<see cref="FormDesignRequest.Inspect"/>).</summary>
    public IReadOnlyList<DesignProperty>? Properties { get; init; }

    public IReadOnlyList<DesignEvent>? Events { get; init; }

    /// <summary>A hook's handler as the form's code is to declare it (<c>private void Count_Click(object? sender, EventArgs e)</c>),
    /// its name the last before its parameters.</summary>
    public string? Handler { get; init; }
}

/// <summary>A component as drawn: its field (empty for the root), its type's name, the component holding it (none for the root
/// and for one in the tray), its box in the frame in pixels (empty for one in the tray, which draws nothing), and whether it
/// holds controls.</summary>
public sealed record DesignedBox(string Field, string Type, string? Parent, double X, double Y, double Width, double Height, bool Holds)
{
    /// <summary>The event a double click on it hooks, as its type says; none where it says none.</summary>
    public string? DefaultEvent { get; init; }

    /// <summary>Its place, from 0, in the list in order it is an item of (a menu's, a strip's, a list's columns, a tab control's
    /// pages), where it moves up or down; null for one of no such list.</summary>
    public int? Place { get; init; }

    /// <summary>How many items that list holds; 0 for one of no list.</summary>
    public int Places { get; init; }

    /// <summary>Whether it is a control, which stands in front of or behind the others its holder holds.</summary>
    public bool Control { get; init; }
}

/// <summary>What the designer could not make of a statement (by its place in the list sent) or of the form as a whole.</summary>
public sealed record DesignNote(string Message, int? Statement = null);

/// <summary>The designer's wire: its answers' type and how one is written.</summary>
public static class FormDesignWire
{
    public static JsonTypeInfo<FormDesignReply> Reply { get; } = PeerJson.Of<FormDesignReply>();

    /// <summary>An answer written as its line, whole, whichever thread answers.</summary>
    public static void Answer(TextWriter protocol, FormDesignReply reply)
    {
        string line = JsonSerializer.Serialize(reply, Reply);
        lock (protocol)
        {
            protocol.WriteLine(line);
        }
    }
}
