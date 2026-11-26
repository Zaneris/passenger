using System.ComponentModel;
using System.ComponentModel.Design;
using System.Globalization;
using System.Reflection;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>A component's properties and events as Windows Forms' property grid shows them (each browsable one by name, its
/// value through its type converter in the person's culture, the designers' shadows in force), its properties set from
/// typed text and reset, its events hooked to handlers through the binding service; and a handler's declaration as the
/// form's code is to write it.</summary>
internal static class Inspecting
{
    /// <summary>A component's properties; <paramref name="written"/> says whether the form's code sets one, by its dotted
    /// name, which shows it set (an extender's is set where its provider's code says).</summary>
    public static List<DesignProperty> Properties(IComponent component, IDesignerHost host, Func<string, bool> written) =>
        [.. TypeDescriptor.GetProperties(component, [BrowsableAttribute.Yes]).Cast<PropertyDescriptor>().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => Listed(component, component, p, p.Name, host, written, depth: 0))];

    /// <summary>A property as the grid lists it, of <paramref name="owner"/> (the component, or a value whose part it is), its
    /// parts with it where its type lists them, down to a depth the grid opens.</summary>
    private static DesignProperty Listed(IComponent component, object owner, PropertyDescriptor property, string path, IDesignerHost host, Func<string, bool> written, int depth)
    {
        Context context = new(component, property, host, owner);
        TypeConverter converter = property.Converter;
        object? value = Read(property, owner);
        bool content = property.SerializationVisibility == DesignerSerializationVisibility.Content;
        bool typed = converter.CanConvertFrom(context, typeof(string)) && !content;
        IReadOnlyList<string>? choices = null;
        bool exclusive = false;
        if (typed && converter.GetStandardValuesSupported(context) && converter.GetStandardValues(context) is { } standard)
        {
            choices = [.. standard.Cast<object?>().Select(v => Written(converter, context, v)).Distinct()];
            exclusive = converter.GetStandardValuesExclusive(context);
        }
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        IReadOnlyList<DesignProperty>? parts = null;
        if (depth < 2 && value is not null && converter.GetPropertiesSupported(context) && converter.GetProperties(context, value) is { Count: > 0 } listed)
        {
            parts = [.. listed.Cast<PropertyDescriptor>().Where(p => p.IsBrowsable).OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => Listed(component, value, p, $"{path}.{p.Name}", host, written, depth + 1))];
        }
        bool extended = property.Attributes[typeof(ExtenderProvidedPropertyAttribute)] is ExtenderProvidedPropertyAttribute { Provider: not null };
        (IReadOnlyList<string>? lines, IReadOnlyList<string>? adds) = content && value is System.Collections.IList items ? Collected(property, items, owner) : (null, null);
        return new DesignProperty(path, property.Category, type.Name, Written(converter, context, value))
        {
            Set = extended || depth > 0 ? Serialized(property, owner) && (depth == 0 || written(path[..path.IndexOf('.')])) : written(path),
            ReadOnly = property.IsReadOnly || !typed,
            Choices = choices,
            Exclusive = exclusive,
            Description = property.Description is { Length: > 0 } said ? said : null,
            Parenthesized = property.Attributes[typeof(ParenthesizePropertyNameAttribute)] is ParenthesizePropertyNameAttribute { NeedParenthesis: true },
            Shown = property.DisplayName != property.Name ? property.DisplayName : null,
            Argb = value is System.Drawing.Color colour ? (uint)colour.ToArgb() : null,
            Flags = type.IsEnum && type.IsDefined(typeof(FlagsAttribute), inherit: false)
                ? [.. Enum.GetValues(type).Cast<object>().Where(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) is var bits and > 0 && (bits & (bits - 1)) == 0)
                    .Select(v => Enum.GetName(type, v)!).Distinct()]
                : null,
            Parts = parts,
            Lines = lines,
            Adds = adds,
            Picture = depth == 0 && !property.IsReadOnly && IsPicture(type),
        };
    }

    /// <summary>What a collection's editor makes of its items: a list of words' lines, or the types of component it adds; a
    /// strip's items those its designers offer where it stands (a menu's, a tool strip's, a status strip's), a grid's columns
    /// each kind of column, as their editors offer them only with a person's dialog open.</summary>
    private static (IReadOnlyList<string>? Lines, IReadOnlyList<string>? Adds) Collected(PropertyDescriptor property, System.Collections.IList items, object owner)
    {
        if (items is System.Windows.Forms.ToolStripItemCollection)
        {
            System.Windows.Forms.Design.ToolStripItemDesignerAvailability where = owner switch
            {
                System.Windows.Forms.MenuStrip => System.Windows.Forms.Design.ToolStripItemDesignerAvailability.MenuStrip,
                System.Windows.Forms.StatusStrip => System.Windows.Forms.Design.ToolStripItemDesignerAvailability.StatusStrip,
                System.Windows.Forms.ToolStripDropDownItem or System.Windows.Forms.ContextMenuStrip => System.Windows.Forms.Design.ToolStripItemDesignerAvailability.ContextMenuStrip,
                _ => System.Windows.Forms.Design.ToolStripItemDesignerAvailability.ToolStrip,
            };
            return (null, Kinds(typeof(System.Windows.Forms.ToolStripItem), t => TypeDescriptor.GetAttributes(t)[typeof(System.Windows.Forms.Design.ToolStripItemDesignerAvailabilityAttribute)]
                is System.Windows.Forms.Design.ToolStripItemDesignerAvailabilityAttribute offered && (offered.ItemAdditionVisibility & where) != 0));
        }
        if (items is System.Windows.Forms.DataGridViewColumnCollection)
        {
            return (null, Kinds(typeof(System.Windows.Forms.DataGridViewColumn), _ => true));
        }
        if (property.GetEditor(typeof(System.Drawing.Design.UITypeEditor)) is not CollectionEditor editor
            || typeof(CollectionEditor).GetMethod("CreateNewItemTypes", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(editor, null) is not Type[] made)
        {
            return (null, null);
        }
        if (made.All(t => t == typeof(string) || t == typeof(object)))
        {
            return ([.. items.Cast<object?>().Select(i => i?.ToString() ?? string.Empty)], null);
        }
        IReadOnlyList<string> adds = [.. made.Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract).Select(t => t.AssemblyQualifiedName ?? t.FullName!)];
        return (null, adds.Count > 0 ? adds : null);
    }

    /// <summary>Windows Forms' own types of a kind made by a constructor of no arguments that a test lets through, by their
    /// full names with their assembly's, in their names' order.</summary>
    private static List<string> Kinds(Type kind, Func<Type, bool> offered) =>
        [.. kind.Assembly.GetExportedTypes().Where(t => kind.IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null && offered(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t.AssemblyQualifiedName ?? t.FullName!)];

    /// <summary>A list of words given whole, a line an item, its empty last line left out.</summary>
    public static void Lines(IComponent component, string name, string text, IDesignerHost host)
    {
        PropertyDescriptor property = Property(component, name);
        if (property.GetValue(component) is not System.Collections.IList items)
        {
            throw new DesignerException($"{name} is no list");
        }
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        IComponentChangeService? changes = host.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
        changes?.OnComponentChanging(component, property);
        items.Clear();
        foreach (string line in lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines)
        {
            items.Add(line);
        }
        changes?.OnComponentChanged(component, property, null, null);
    }

    public static List<DesignEvent> Events(IComponent component, IEventBindingService events) =>
        [.. TypeDescriptor.GetEvents(component, [BrowsableAttribute.Yes]).Cast<EventDescriptor>().OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new DesignEvent(e.Name, e.Category)
            {
                Handler = events.GetEventProperty(e).GetValue(component) as string,
                Description = e.Description is { Length: > 0 } said ? said : null,
            })];

    /// <summary>A property given the value its text says, as its type converter reads it in the person's culture; a part of
    /// a value (its dotted name) by the value made again with it where its type makes values whole (a Point, a Font), else
    /// set on the value itself (a button's FlatAppearance).</summary>
    public static void Set(IComponent component, string name, string text, IDesignerHost host)
    {
        string[] path = name.Split('.');
        PropertyDescriptor property = Property(component, path[0]);
        if (path.Length == 1)
        {
            property.SetValue(component, Converted(component, component, property, text, host));
            return;
        }
        object value = property.GetValue(component) ?? throw new DesignerException($"{path[0]} has no value to set a part of");
        Context context = new(component, property, host, component);
        PropertyDescriptorCollection parts = property.Converter.GetProperties(context, value) ?? throw new DesignerException($"{path[0]} has no parts");
        PropertyDescriptor part = parts[path[1]] ?? throw new DesignerException($"{path[0]} has no part {path[1]}");
        object? given = Converted(component, value, part, text, host);
        if (property.Converter.GetCreateInstanceSupported(context))
        {
            Dictionary<string, object?> whole = parts.Cast<PropertyDescriptor>().ToDictionary(p => p.Name, p => p.Name == part.Name ? given : p.GetValue(value));
            property.SetValue(component, property.Converter.CreateInstance(context, whole));
            return;
        }
        IComponentChangeService? changes = host.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
        changes?.OnComponentChanging(component, property);
        part.SetValue(value, given);
        changes?.OnComponentChanged(component, property, null, null);
    }

    private static object? Converted(IComponent component, object owner, PropertyDescriptor property, string text, IDesignerHost host)
    {
        if (property.IsReadOnly)
        {
            throw new DesignerException($"{property.Name} takes no value");
        }
        try
        {
            return property.Converter.ConvertFromString(new Context(component, property, host, owner), CultureInfo.CurrentCulture, text);
        }
        catch (Exception unread) when (unread is FormatException or ArgumentException or NotSupportedException or InvalidCastException or OverflowException
            or InvalidOperationException)
        {
            throw new DesignerException($"'{text}' is no {property.PropertyType.Name} for {property.Name}: {unread.Message}");
        }
    }

    public static void Reset(IComponent component, string name)
    {
        PropertyDescriptor property = Property(component, name);
        if (!property.CanResetValue(component))
        {
            // A picture's default is none, though its type says no default.
            if (!property.IsReadOnly && IsPicture(property.PropertyType) && property.GetValue(component) is not null)
            {
                property.SetValue(component, null);
                return;
            }
            throw new DesignerException($"{name} has no default to put back");
        }
        property.ResetValue(component);
    }

    /// <summary>A picture's property given the picture a file holds, read whole into memory so the file is not held.</summary>
    public static void Picture(IComponent component, string name, string file)
    {
        PropertyDescriptor property = Property(component, name);
        if (property.IsReadOnly || !IsPicture(property.PropertyType))
        {
            throw new DesignerException($"{name} takes no picture");
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DesignerException($"{Path.GetFileName(file)} could not be read: {unread.Message}");
        }
        try
        {
            // GDI+ reads a bitmap from its stream as long as it lives, so the stream is the picture's own.
            object picture = typeof(System.Drawing.Icon).IsAssignableFrom(property.PropertyType) ? new System.Drawing.Icon(new MemoryStream(bytes)) : System.Drawing.Image.FromStream(new MemoryStream(bytes));
            property.SetValue(component, picture);
        }
        catch (Exception unread) when (unread is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            throw new DesignerException($"{Path.GetFileName(file)} holds no picture Windows Forms reads: {unread.Message}");
        }
    }

    private static bool IsPicture(Type type) => typeof(System.Drawing.Image).IsAssignableFrom(type) || typeof(System.Drawing.Icon).IsAssignableFrom(type);

    /// <summary>A handler hooked to an event, the one named or the one hooked already, else the binding service's name for
    /// it; its declaration as the form's code is to write it.</summary>
    public static string Hook(IComponent component, string name, string? handler, IEventBindingService events, Types types, bool nullable)
    {
        EventDescriptor raised = TypeDescriptor.GetEvents(component)[name] ?? throw new DesignerException($"{component.GetType().Name} has no event {name}");
        PropertyDescriptor hooked = events.GetEventProperty(raised);
        string method = handler ?? hooked.GetValue(component) as string ?? $"{(component.Site?.Name is { Length: > 0 } site ? site : component.GetType().Name)}_{raised.Name}";
        if (!Equals(hooked.GetValue(component), method))
        {
            hooked.SetValue(component, method);
        }
        return Declared(raised.EventType, method, types, nullable);
    }

    public static void Unhook(IComponent component, string name, IEventBindingService events)
    {
        EventDescriptor raised = TypeDescriptor.GetEvents(component)[name] ?? throw new DesignerException($"{component.GetType().Name} has no event {name}");
        events.GetEventProperty(raised).SetValue(component, null);
    }

    /// <summary>A handler's declaration for a delegate: private, void, its parameters' types as the file's scope names them,
    /// a reference type nullable where the delegate says so and the project annotates nullability.</summary>
    private static string Declared(Type handler, string method, Types types, bool nullable)
    {
        MethodInfo invoke = handler.GetMethod("Invoke") ?? throw new DesignerException($"{handler.Name} is no delegate");
        NullabilityInfoContext nullability = new();
        IEnumerable<string> parameters = invoke.GetParameters().Select(p =>
            $"{types.Spelled(p.ParameterType)}{(nullable && !p.ParameterType.IsValueType && nullability.Create(p).WriteState == NullabilityState.Nullable ? "?" : string.Empty)} {p.Name}");
        return $"private {(invoke.ReturnType == typeof(void) ? "void" : types.Spelled(invoke.ReturnType))} {method}({string.Join(", ", parameters)})";
    }

    private static PropertyDescriptor Property(IComponent component, string name) =>
        TypeDescriptor.GetProperties(component)[name] ?? throw new DesignerException($"{component.GetType().Name} has no property {name}");

    private static object? Read(PropertyDescriptor property, object component)
    {
        try
        {
            return property.GetValue(component);
        }
        catch (Exception unread) when (unread is TargetInvocationException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return $"({unread.Message})";
        }
    }

    private static bool Serialized(PropertyDescriptor property, object component)
    {
        try
        {
            return property.ShouldSerializeValue(component);
        }
        catch (Exception unread) when (unread is TargetInvocationException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Written(TypeConverter converter, ITypeDescriptorContext context, object? value)
    {
        try
        {
            return converter.ConvertToString(context, CultureInfo.CurrentCulture, value) ?? string.Empty;
        }
        catch (Exception unwritten) when (unwritten is NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return value?.ToString() ?? string.Empty;
        }
    }

    /// <summary>What a type converter asks of where a value stands: the component, the property, the object it is of (the
    /// component, or a value whose part it is), the surface's services.</summary>
    private sealed class Context(IComponent component, PropertyDescriptor property, IDesignerHost host, object owner) : ITypeDescriptorContext
    {
        public IContainer? Container => host.Container;

        public object? Instance => owner == component ? component : owner;

        public PropertyDescriptor? PropertyDescriptor => property;

        public object? GetService(Type serviceType) => host.GetService(serviceType);

        public void OnComponentChanged()
        {
        }

        public bool OnComponentChanging() => true;
    }
}
