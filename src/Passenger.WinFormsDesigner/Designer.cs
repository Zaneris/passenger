using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using Passenger.Core;
using Passenger.Inspector.WinForms;

namespace Passenger.WinFormsDesigner;

/// <summary>A form or user control made from its designer code on Windows Forms' own design surface: its base type the root,
/// each component made through the surface's designer host (so it is in design mode, as its designer has it), and each
/// statement of InitializeComponent run in order against them (properties through their descriptors, so the designers'
/// shadowing holds; calls by reflection; hooks through the event binding service); then edited where asked (<see
/// cref="Editing"/>) and drawn, a form under a title bar like the system's, with each component's box. A statement that
/// fails is noted by its place and the rest made. The surface lives in a window never shown.</summary>
internal sealed class Designer(Assembly? project) : IDisposable
{
    private DesignSurface? surface;
    private Form? holder;

    public FormDesignReply Draw(FormDesignRequest request)
    {
        try
        {
            return Drawn(request);
        }
        catch (DesignerException refused) when (request.Edit is not null)
        {
            return new FormDesignReply { Kind = PreviewKinds.Error, Sequence = request.Sequence, Message = refused.Message };
        }
    }

    private FormDesignReply Drawn(FormDesignRequest request)
    {
        Close();
        List<DesignNote> notes = [];
        string name = request.Class[(request.Class.LastIndexOfAny(['.', '+']) + 1)..];
        int dot = request.Class.LastIndexOf('.');
        Types types = new(dot > 0 ? request.Class[..dot] : null, request.Usings, project);
        Type based = request.Base is { } written ? types.Find(written) ?? Missing(written, notes) : typeof(Form);
        if (!typeof(Control).IsAssignableFrom(based))
        {
            return new FormDesignReply { Kind = PreviewKinds.Nothing, Sequence = request.Sequence, Message = $"{name} derives from {based.Name}, no control: it has no surface to draw" };
        }
        surface = new DesignSurface();
        IServiceContainer services = (IServiceContainer)surface.GetService(typeof(IServiceContainer))!;
        services.AddService(typeof(INameCreationService), new Names());
        Events events = new(surface);
        services.AddService(typeof(IEventBindingService), events);
        services.AddService(typeof(IResourceService), new Resources());
        surface.BeginLoad(based);
        if (!surface.IsLoaded || surface.GetService(typeof(IDesignerHost)) is not IDesignerHost host || host.RootComponent is not Control root)
        {
            string why = string.Join("; ", surface.LoadErrors?.Cast<object>().Select(e => e is Exception error ? error.Message : e.ToString()) ?? []);
            return new FormDesignReply { Kind = PreviewKinds.Error, Sequence = request.Sequence, Message = $"{based.Name} did not load on the design surface: {why}" };
        }
        root.Site!.Name = name;
        Dictionary<string, object> made = new(StringComparer.Ordinal);
        Values values = new(types, field => field == "this" ? root : made.GetValueOrDefault(field) ?? (field == "components" ? host.Container : null));
        for (int i = 0; i < request.Statements.Count; i++)
        {
            try
            {
                Run(request.Statements[i], host, root, made, values, events);
            }
            catch (Exception failed) when (failed is DesignerException or TargetInvocationException or ArgumentException or InvalidOperationException
                or NotSupportedException or MissingMethodException or FormatException or InvalidCastException or OverflowException or NullReferenceException)
            {
                notes.Add(new DesignNote((failed is TargetInvocationException { InnerException: { } inner } ? inner : failed).Message, i));
            }
        }
        Show(root);
        int bar = Bar(root);
        Point origin = root.PointToScreen(Point.Empty);
        origin.Offset(0, -bar);
        List<DesignerChange>? changes = null;
        List<string>? chosen = null;
        string? handler = null;
        if (request.Edit is { } edit)
        {
            CodeText text = new(request.Qualified, types);
            Serialized before = Serialized.Of(host, text);
            (chosen, handler) = Editing.Apply(edit, host, root, origin, bar, types, events, request.Nullable);
            Application.DoEvents();
            changes = Editing.Changes(request, before, Serialized.Of(host, text));
            origin = root.PointToScreen(Point.Empty);
            origin.Offset(0, -bar);
        }
        (byte[] png, List<DesignedBox> boxes) = Drawn(host, root, bar, origin);
        IComponent? inspected = request.Inspect is { } field ? field.Length == 0 ? root : host.Container.Components[field] : null;
        return new FormDesignReply
        {
            Kind = PreviewKinds.Frame,
            Sequence = request.Sequence,
            Png = png,
            Components = boxes,
            Notes = notes,
            Changes = changes,
            Chosen = chosen,
            Handler = handler,
            Toolbox = request.Toolbox ? Toolbox.Listed(project, based) : null,
            Properties = inspected is null ? null : Inspecting.Properties(inspected, host, member => Written(request, request.Inspect!, member)),
            Events = inspected is null ? null : Inspecting.Events(inspected, events),
        };
    }

    /// <summary>Whether the code the request was read from sets a component's member (its dotted name, a part of it set or
    /// called counting): what shows a property set in the Properties.</summary>
    private static bool Written(FormDesignRequest request, string field, string member) =>
        request.Statements.Any(s => s is { Act: DesignerAct.Assign or DesignerAct.Call, Member: { } written } && s.Target == field
            && (written == member || written.StartsWith(member + ".", StringComparison.Ordinal)));

    /// <summary>How tall a form's title bar is drawn over its client area: none for a form without one or another control.</summary>
    private static int Bar(Control root) => root is Form { FormBorderStyle: not FormBorderStyle.None } ? SystemInformation.CaptionHeight : 0;

    /// <summary>The type drawn for a base type not found (the project not built yet): a form, said in a note.</summary>
    private static Type Missing(string written, List<DesignNote> notes)
    {
        notes.Add(new DesignNote($"{written} is not found until the project is built: drawn as a Form"));
        return typeof(Form);
    }

    /// <summary>One statement run against what the surface holds.</summary>
    private static void Run(DesignerStatement statement, IDesignerHost host, Control root, Dictionary<string, object> made, Values values, Events events)
    {
        switch (statement.Act)
        {
            case DesignerAct.Create when statement.Target == "components":
                // The surface's own container stands for the class's.
                return;
            case DesignerAct.Create when statement.Value is { Kind: DesignerValueKinds.New, Type: { } written } created:
                Type type = values.Type(written);
                made[statement.Target] = created.Arguments is null or { Count: 0 } or [{ Kind: DesignerValueKinds.Name, Name: "components" }]
                    && typeof(IComponent).IsAssignableFrom(type)
                    ? host.CreateComponent(type, statement.Target)
                    : Sited(values.Of(created) ?? throw new DesignerException($"{statement.Target} was made null"), host, statement.Target);
                return;
            case DesignerAct.Assign:
                object owner = Owner(statement, root, made);
                (object at, string last) = Walked(owner, statement.Member!);
                if (TypeDescriptor.GetProperties(at)[last] is { } property)
                {
                    property.SetValue(at, values.Of(statement.Value!, property.PropertyType));
                    return;
                }
                PropertyInfo clr = at.GetType().GetProperty(last, BindingFlags.Public | BindingFlags.Instance) ?? throw new DesignerException($"{at.GetType().Name} has no {last}");
                clr.SetValue(at, values.Of(statement.Value!, clr.PropertyType));
                return;
            case DesignerAct.Call:
                (object on, string method) = Walked(Owner(statement, root, made), statement.Member!);
                values.Call(on, method, statement.Arguments ?? []);
                return;
            case DesignerAct.Hook:
                // A handler runs in the app, never on the surface: the binding service keeps its name, which the code is written with.
                (object hooked, string name) = Walked(Owner(statement, root, made), statement.Member!);
                EventDescriptor raised = TypeDescriptor.GetEvents(hooked)[name] ?? throw new DesignerException($"{hooked.GetType().Name} has no event {name}");
                string handler = statement.Value switch
                {
                    { Kind: DesignerValueKinds.Name, Name: { } group } => group[(group.LastIndexOf('.') + 1)..],
                    { Kind: DesignerValueKinds.New, Arguments: [{ Kind: DesignerValueKinds.Name, Name: { } group }] } => group[(group.LastIndexOf('.') + 1)..],
                    _ => throw new DesignerException($"The designer hooks only a method of the class to {name}"),
                };
                ((IEventBindingService)events).GetEventProperty(raised).SetValue(hooked, handler);
                return;
            default:
                throw new DesignerException("The designer does not read this statement");
        }
    }

    /// <summary>A component made with arguments, put in the surface's container under its field's name.</summary>
    private static object Sited(object made, IDesignerHost host, string field)
    {
        if (made is IComponent component)
        {
            host.Container.Add(component, field);
        }
        return made;
    }

    private static object Owner(DesignerStatement statement, Control root, Dictionary<string, object> made) =>
        statement.Target.Length == 0 ? root : made.GetValueOrDefault(statement.Target) ?? throw new DesignerException($"{statement.Target} is used before it is made");

    /// <summary>An object's properties read along a dotted path but its last name, and that name.</summary>
    private static (object At, string Last) Walked(object owner, string path)
    {
        string[] names = path.Split('.');
        object at = owner;
        foreach (string name in names[..^1])
        {
            at = (TypeDescriptor.GetProperties(at)[name] is { } property ? property.GetValue(at) : at.GetType().GetProperty(name)?.GetValue(at))
                ?? throw new DesignerException($"{name} of {at.GetType().Name} is null");
        }
        return (at, names[^1]);
    }

    /// <summary>The surface's view in a window of its own that is never shown, off every screen, every control's window made
    /// and laid out, and what the designers queued run.</summary>
    private void Show(Control root)
    {
        Control view = (Control)surface!.View;
        holder = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(root.Width + 200, root.Height + 200),
        };
        view.Dock = DockStyle.Fill;
        holder.Controls.Add(view);
        _ = holder.Handle;
        view.CreateControl();
        root.CreateControl();
        Application.DoEvents();
    }

    /// <summary>The root drawn (a form's title bar over its client area) as a PNG, and each component's box in it, parents first,
    /// those in the tray last.</summary>
    private static (byte[] Png, List<DesignedBox> Boxes) Drawn(IDesignerHost host, Control root, int bar, Point origin)
    {
        Size client = root.ClientSize;
        using Bitmap picture = new(Math.Max(1, client.Width), Math.Max(1, bar + client.Height), PixelFormat.Format32bppArgb);
        using (Graphics drawing = Graphics.FromImage(picture))
        {
            if (bar > 0)
            {
                TitleBar(drawing, root.Text, client.Width, bar);
            }
            ControlDrawing.Client(drawing, root, new Point(0, bar));
        }
        using MemoryStream png = new();
        picture.Save(png, ImageFormat.Png);
        List<DesignedBox> boxes = [new DesignedBox(string.Empty, root.GetType().Name, null, 0, 0, client.Width, bar + client.Height, true) { DefaultEvent = DefaultEvent(root) }];
        Walk(root, host, origin, boxes);
        foreach (IComponent tray in host.Container.Components.Cast<IComponent>().Where(c => c is not Control && c is not ToolStripItem && c != root))
        {
            boxes.Add(new DesignedBox(tray.Site?.Name ?? string.Empty, tray.GetType().Name, null, 0, 0, 0, 0, false) { DefaultEvent = DefaultEvent(tray) });
        }
        return (png.ToArray(), boxes);
    }

    /// <summary>The boxes of what a control holds, in turn: its controls, a strip's items.</summary>
    private static void Walk(Control holder, IDesignerHost host, Point origin, List<DesignedBox> boxes)
    {
        if (holder is ToolStrip strip)
        {
            // The designer's own items (a strip's Type Here) are sited in no container: none of the form's.
            foreach (ToolStripItem item in strip.Items.Cast<ToolStripItem>().Where(i => i.Site is not null))
            {
                Point at = strip.PointToScreen(item.Bounds.Location);
                boxes.Add(new DesignedBox(item.Site!.Name ?? item.Name ?? string.Empty, item.GetType().Name, NameOf(strip, host), at.X - origin.X, at.Y - origin.Y, item.Width, item.Height, false)
                {
                    DefaultEvent = DefaultEvent(item),
                });
            }
        }
        foreach (Control control in holder.Controls)
        {
            if (!control.IsHandleCreated || control.Site is null && control.Controls.Count == 0)
            {
                continue;
            }
            if (control.Site is not null)
            {
                Point at = control.Parent!.PointToScreen(control.Location);
                boxes.Add(new DesignedBox(control.Site.Name ?? control.Name, control.GetType().Name, NameOf(holder, host), at.X - origin.X, at.Y - origin.Y,
                    control.Width, control.Height, host.GetDesigner(control) is ParentControlDesigner) { DefaultEvent = DefaultEvent(control) });
            }
            Walk(control, host, origin, boxes);
        }
    }

    /// <summary>The event a double click on a component hooks, as its type's <c>[DefaultEvent]</c> says.</summary>
    private static string? DefaultEvent(object component) => TypeDescriptor.GetDefaultEvent(component)?.Name;

    /// <summary>The field of the nearest component holding a control (empty for the root).</summary>
    private static string NameOf(Control control, IDesignerHost host)
    {
        for (Control? at = control; at is not null; at = at.Parent)
        {
            if (at == host.RootComponent)
            {
                return string.Empty;
            }
            if (at.Site?.Name is { } name)
            {
                return name;
            }
        }
        return string.Empty;
    }

    /// <summary>A title bar like the system's, drawn above the client area: it is no part of what the form holds.</summary>
    private static void TitleBar(Graphics drawing, string title, int width, int height)
    {
        using SolidBrush ground = new(SystemColors.ControlLightLight);
        drawing.FillRectangle(ground, 0, 0, width, height);
        using Pen edge = new(SystemColors.ControlLight);
        drawing.DrawLine(edge, 0, height - 1, width, height - 1);
        TextRenderer.DrawText(drawing, title, SystemFonts.CaptionFont ?? Control.DefaultFont, new Rectangle(10, 0, Math.Max(0, width - 20), height), SystemColors.ControlText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    private void Close()
    {
        holder?.Dispose();
        holder = null;
        surface?.Dispose();
        surface = null;
    }

    public void Dispose() => Close();

    /// <summary>The form's resources while its .resx is not read: none read, and what the serializers write of them (a tray
    /// component's place, a value code cannot hold) kept apart from the code, which is all an edit writes.</summary>
    private sealed class Resources : IResourceService
    {
        private readonly Dictionary<string, object?> written = new(StringComparer.Ordinal);

        public IResourceReader GetResourceReader(CultureInfo info) => new Read(written);

        public IResourceWriter GetResourceWriter(CultureInfo info) => new Written(written);

        private sealed class Read(Dictionary<string, object?> entries) : IResourceReader
        {
            public IDictionaryEnumerator GetEnumerator() => new Dictionary<string, object?>(entries).GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void Close()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class Written(Dictionary<string, object?> entries) : IResourceWriter
        {
            public void AddResource(string name, byte[]? value) => entries[name] = value;

            public void AddResource(string name, object? value) => entries[name] = value;

            public void AddResource(string name, string? value) => entries[name] = value;

            public void Generate()
            {
            }

            public void Close()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>The handlers hooked to components' events, by their methods' names: the code names them, the surface runs none.</summary>
    private sealed class Events(IServiceProvider services) : EventBindingService(services)
    {
        protected override string CreateUniqueMethodName(IComponent component, EventDescriptor e) => $"{component.Site?.Name}_{e.Name}";

        protected override ICollection GetCompatibleMethods(EventDescriptor e) => Array.Empty<string>();

        protected override bool ShowCode() => false;

        protected override bool ShowCode(int lineNumber) => false;

        protected override bool ShowCode(IComponent component, EventDescriptor e, string methodName) => false;
    }

    /// <summary>Names for what the surface makes unnamed, as the designer names a new component: its type's, camel cased, and
    /// the first number free.</summary>
    private sealed class Names : INameCreationService
    {
        public string CreateName(IContainer? container, Type dataType)
        {
            string stem = char.ToLowerInvariant(dataType.Name[0]) + dataType.Name[1..];
            for (int number = 1; ; number++)
            {
                if (container?.Components[stem + number] is null)
                {
                    return stem + number;
                }
            }
        }

        public bool IsValidName(string name) => name.Length > 0;

        public void ValidateName(string name)
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException("A component's name is not empty", nameof(name));
            }
        }
    }
}
