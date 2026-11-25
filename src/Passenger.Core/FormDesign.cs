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
/// InitializeComponent's statements.</summary>
public sealed record FormDesignRequest
{
    public required int Sequence { get; init; }

    public required string Class { get; init; }

    public string? Base { get; init; }

    public IReadOnlyList<string> Usings { get; init; } = [];

    public IReadOnlyList<DesignerField> Fields { get; init; } = [];

    public IReadOnlyList<DesignerStatement> Statements { get; init; } = [];
}

/// <summary>The designer's answer to the newest request it read: the form as a PNG at 96 DPI with its title bar (a form's) and
/// each component's box in it (<see cref="DesignedBox"/>), what it could not make of statements (<see cref="DesignNote"/>);
/// or why it drew nothing.</summary>
public sealed record FormDesignReply
{
    public required string Kind { get; init; }

    public required int Sequence { get; init; }

    public byte[]? Png { get; init; }

    public IReadOnlyList<DesignedBox>? Components { get; init; }

    public IReadOnlyList<DesignNote>? Notes { get; init; }

    public string? Message { get; init; }
}

/// <summary>A component as drawn: its field (empty for the root), its type's name, the component holding it (none for the root
/// and for one in the tray), its box in the frame in pixels (empty for one in the tray, which draws nothing), and whether it
/// holds controls.</summary>
public sealed record DesignedBox(string Field, string Type, string? Parent, double X, double Y, double Width, double Height, bool Holds);

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
