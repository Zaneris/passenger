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
        Resolution resolution = new(types);
        FormResources resources = new(request, resolution);
        services.AddService(typeof(IResourceService), resources);
        services.AddService(typeof(ITypeResolutionService), resolution);
        surface.BeginLoad(new Loading(based, loading => Localized(loading, request, resources)));
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
                Run(request.Statements[i], host, root, made, values, events, resources);
            }
            catch (Exception failed) when (failed is DesignerException or TargetInvocationException or ArgumentException or InvalidOperationException
                or NotSupportedException or MissingMethodException or FormatException or InvalidCastException or OverflowException or NullReferenceException)
            {
                notes.Add(new DesignNote((failed is TargetInvocationException { InnerException: { } inner } ? inner : failed).Message, i));
            }
        }
        Components.Reveal(request.Inspect is { } shownField ? Components.Find(host, root, shownField) : null);
        Show(root);
        Placed(resources, host, root, notes);
        int bar = Bar(root);
        Point origin = root.PointToScreen(Point.Empty);
        origin.Offset(0, -bar);
        List<DesignerChange>? changes = null;
        List<DesignResourceChange>? resourceChanges = null;
        List<DesignResourceFile>? resourcesMade = null;
        List<string>? picked = null;
        string? handler = null;
        if (request.Edit is { } edit)
        {
            CodeText text = new(request.Qualified, types);
            Serialized before = Serialized.Of(host, text, resources);
            (picked, handler) = Editing.Apply(edit, host, root, origin, bar, types, events, request.Nullable);
            Application.DoEvents();
            Serialized after = Serialized.Of(host, text, resources);
            changes = Editing.Changes(request, before, after);
            List<DesignResourceChange> kept = Editing.ResourceChanges(before, after);
            // A culture's .resx the form has none of yet is made whole; the rest are changed an element at a time.
            resourcesMade = [.. after.Resources.Where(r => kept.Any(c => c.Culture == r.Key) && (r.Key.Length == 0 ? request.Resources is null : !request.Cultures.ContainsKey(r.Key)))
                .Select(r => new DesignResourceFile(r.Key, r.Value))];
            resourceChanges = [.. kept.Where(c => !resourcesMade.Any(m => m.Culture == c.Culture))];
            origin = root.PointToScreen(Point.Empty);
            origin.Offset(0, -bar);
        }
        (byte[] png, List<DesignedBox> boxes) = Drawn(host, root, bar, origin);
        IComponent? inspected = request.Inspect is { } field ? Components.Find(host, root, field) : null;
        return new FormDesignReply
        {
            Kind = PreviewKinds.Frame,
            Sequence = request.Sequence,
            Png = png,
            Components = boxes,
            Notes = notes,
            Changes = changes,
            ResourceChanges = resourceChanges,
            ResourcesMade = resourcesMade,
            Chosen = picked,
            Handler = handler,
            Toolbox = request.Toolbox ? Toolbox.Listed(project, based) : null,
            Properties = inspected is null ? null : Inspecting.Properties(inspected, host, member => Written(request, resources, request.Inspect!, member)),
            Events = inspected is null ? null : Inspecting.Events(inspected, events),
        };
    }

    /// <summary>Whether the code the request was read from sets a component's member (its dotted name, a part of it set or
    /// called counting), or, for a localizable form, its .resx in the form's language does: what shows a property set in the
    /// Properties.</summary>
    private static bool Written(FormDesignRequest request, FormResources resources, string field, string member) =>
        request.Statements.Any(s => s is { Act: DesignerAct.Assign or DesignerAct.Call, Member: { } written } && s.Target == field
            && (written == member || written.StartsWith(member + ".", StringComparison.Ordinal)))
        || resources.Holds($"{(field.Length == 0 ? "$this" : field)}.{member}");

    /// <summary>How tall a form's title bar is drawn over its client area: none for a form without one or another control.</summary>
    private static int Bar(Control root) => root is Form { FormBorderStyle: not FormBorderStyle.None } ? SystemInformation.CaptionHeight : 0;

    /// <summary>The type drawn for a base type not found (the project not built yet): a form, said in a note.</summary>
    private static Type Missing(string written, List<DesignNote> notes)
    {
        notes.Add(new DesignNote($"{written} is not found until the project is built: drawn as a Form"));
        return typeof(Form);
    }

    /// <summary>The form's localizer made as its root loads, and the language it is designed in set then, which Windows Forms'
    /// resource serializers read as the language it was loaded in and write its values for.</summary>
    private static void Localized(IDesignerLoaderHost host, FormDesignRequest request, FormResources resources)
    {
        resources.Localizer = new CodeDomLocalizationProvider(host, CodeDomLocalizationModel.PropertyReflection);
        IComponent root = host.RootComponent;
        // The root's properties were listed as it was made: listed again, they take the localizer's Localizable and Language.
        TypeDescriptor.Refresh(root);
        if (TypeDescriptor.GetProperties(root)["Language"] is { } language && request.Language is { } spoken
            && language.Converter.ConvertFromString(spoken) is CultureInfo chosen && !chosen.Equals(CultureInfo.InvariantCulture))
        {
            resources.Language = chosen;
            language.SetValue(root, chosen);
        }
    }

    /// <summary>The surface's root made of its type, as the surface's own loader makes it, with what must be set while the host
    /// still loads.</summary>
    private sealed class Loading(Type based, Action<IDesignerLoaderHost> load) : DesignerLoader
    {
        public override void BeginLoad(IDesignerLoaderHost host)
        {
            List<Exception> errors = [];
            try
            {
                host.CreateComponent(based);
                load(host);
            }
            catch (Exception failed) when (failed is ArgumentException or InvalidOperationException or NotSupportedException or TargetInvocationException
                or MissingMethodException or FormatException)
            {
                // Said as the surface's load errors, which the reply names.
                errors.Add(failed);
            }
            host.EndLoad(based.FullName ?? based.Name, errors.Count == 0, errors);
        }

        public override void Dispose()
        {
        }
    }

    /// <summary>What only the designer reads of the form's .resx put back: each tray component's place in the tray, and
    /// whether the form is localizable.</summary>
    private static void Placed(FormResources resources, IDesignerHost host, Control root, List<DesignNote> notes)
    {
        foreach ((string name, object? value) in resources.Metadata())
        {
            if (name.EndsWith(".TrayLocation", StringComparison.Ordinal) && value is Point place && Components.Find(host, root, name[..^".TrayLocation".Length]) is { } held)
            {
                TypeDescriptor.GetProperties(held)["TrayLocation"]?.SetValue(held, place);
            }
            else if (name == "$this.Localizable" && value is true)
            {
                TypeDescriptor.GetProperties(root)["Localizable"]?.SetValue(root, true);
            }
        }
        if (resources.Unread is { } why)
        {
            notes.Add(new DesignNote(why));
        }
    }

    /// <summary>One statement run against what the surface holds.</summary>
    private static void Run(DesignerStatement statement, IDesignerHost host, Control root, Dictionary<string, object> made, Values values, Events events, FormResources resources)
    {
        switch (statement.Act)
        {
            case DesignerAct.Other when statement.Target.Length > 0 && statement.Value is { Kind: DesignerValueKinds.New, Type: { } manager }
                && typeof(System.Resources.ResourceManager).IsAssignableFrom(values.Type(manager)):
                // The form's resource manager reads the .resx as it stands, where the build's reads it as it was built.
                made[statement.Target] = resources.Manager();
                return;
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
                (object at, string last) = Reached(statement, root, made);
                if (TypeDescriptor.GetProperties(at)[last] is { } property)
                {
                    property.SetValue(at, values.Of(statement.Value!, property.PropertyType));
                    return;
                }
                PropertyInfo clr = at.GetType().GetProperty(last, BindingFlags.Public | BindingFlags.Instance) ?? throw new DesignerException($"{at.GetType().Name} has no {last}");
                clr.SetValue(at, values.Of(statement.Value!, clr.PropertyType));
                return;
            case DesignerAct.Call:
                (object on, string method) = Reached(statement, root, made);
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

    /// <summary>What a statement acts on and the member it names there: a component's, or the root's, or, for a path that
    /// starts at a local the code made (its resource manager), that local's.</summary>
    private static (object At, string Last) Reached(DesignerStatement statement, Control root, Dictionary<string, object> made) =>
        statement.Target.Length == 0 && statement.Member!.IndexOf('.') is var dot and > 0 && made.TryGetValue(statement.Member[..dot], out object? local)
            ? Walked(local, statement.Member[(dot + 1)..])
            : Walked(Owner(statement, root, made), statement.Member!);

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
        return (png.ToArray(), Components.Boxes(host, root, origin, bar));
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

    /// <summary>Types by their names for Windows Forms' serializers, which write what a type asks of its code (an
    /// ISupportInitialize's BeginInit and EndInit) only where its name is found: the project's and the framework's.</summary>
    private sealed class Resolution(Types types) : ITypeResolutionService
    {
        public Assembly? GetAssembly(AssemblyName name) => GetAssembly(name, false);

        public Assembly? GetAssembly(AssemblyName name, bool throwOnError)
        {
            try
            {
                return Assembly.Load(name);
            }
            catch (Exception missing) when (!throwOnError && missing is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return null;
            }
        }

        public string? GetPathOfAssembly(AssemblyName name) => GetAssembly(name)?.Location;

        public Type? GetType(string name) => GetType(name, false, false);

        public Type? GetType(string name, bool throwOnError) => GetType(name, throwOnError, false);

        public Type? GetType(string name, bool throwOnError, bool ignoreCase) =>
            Type.GetType(name, false, ignoreCase) ?? types.Find(name.Split(',')[0].Trim().Replace('+', '.'))
            ?? (throwOnError ? throw new TypeLoadException($"No type {name} is found") : null);

        public void ReferenceAssembly(AssemblyName name)
        {
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
