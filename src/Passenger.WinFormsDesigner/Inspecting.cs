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
    public static List<DesignProperty> Properties(IComponent component, IDesignerHost host)
    {
        List<DesignProperty> listed = [];
        foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component, [BrowsableAttribute.Yes]).Cast<PropertyDescriptor>().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            Context context = new(component, property, host);
            TypeConverter converter = property.Converter;
            object? value = Read(property, component);
            bool typed = converter.CanConvertFrom(context, typeof(string)) && property.SerializationVisibility != DesignerSerializationVisibility.Content;
            IReadOnlyList<string>? choices = null;
            bool exclusive = false;
            if (typed && converter.GetStandardValuesSupported(context) && converter.GetStandardValues(context) is { } standard)
            {
                choices = [.. standard.Cast<object?>().Select(v => Written(converter, context, v)).Distinct()];
                exclusive = converter.GetStandardValuesExclusive(context);
            }
            listed.Add(new DesignProperty(property.Name, property.Category, property.PropertyType.Name, Written(converter, context, value))
            {
                Set = Serialized(property, component),
                ReadOnly = property.IsReadOnly || !typed,
                Choices = choices,
                Exclusive = exclusive,
                Description = property.Description is { Length: > 0 } said ? said : null,
                Parenthesized = property.Attributes[typeof(ParenthesizePropertyNameAttribute)] is ParenthesizePropertyNameAttribute { NeedParenthesis: true },
            });
        }
        return listed;
    }

    public static List<DesignEvent> Events(IComponent component, IEventBindingService events) =>
        [.. TypeDescriptor.GetEvents(component, [BrowsableAttribute.Yes]).Cast<EventDescriptor>().OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new DesignEvent(e.Name, e.Category)
            {
                Handler = events.GetEventProperty(e).GetValue(component) as string,
                Description = e.Description is { Length: > 0 } said ? said : null,
            })];

    /// <summary>A property given the value its text says, as its type converter reads it in the person's culture.</summary>
    public static void Set(IComponent component, string name, string text, IDesignerHost host)
    {
        PropertyDescriptor property = Property(component, name);
        if (property.IsReadOnly)
        {
            throw new DesignerException($"{name} takes no value");
        }
        object? value;
        try
        {
            value = property.Converter.ConvertFromString(new Context(component, property, host), CultureInfo.CurrentCulture, text);
        }
        catch (Exception unread) when (unread is FormatException or ArgumentException or NotSupportedException or InvalidCastException or OverflowException
            or InvalidOperationException)
        {
            throw new DesignerException($"'{text}' is no {property.PropertyType.Name} for {name}: {unread.Message}");
        }
        property.SetValue(component, value);
    }

    public static void Reset(IComponent component, string name)
    {
        PropertyDescriptor property = Property(component, name);
        if (!property.CanResetValue(component))
        {
            throw new DesignerException($"{name} has no default to put back");
        }
        property.ResetValue(component);
    }

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

    /// <summary>What a type converter asks of where a value stands: the component, the property, the surface's services.</summary>
    private sealed class Context(IComponent component, PropertyDescriptor property, IDesignerHost host) : ITypeDescriptorContext
    {
        public IContainer? Container => host.Container;

        public object? Instance => component;

        public PropertyDescriptor? PropertyDescriptor => property;

        public object? GetService(Type serviceType) => host.GetService(serviceType);

        public void OnComponentChanged()
        {
        }

        public bool OnComponentChanging() => true;
    }
}
