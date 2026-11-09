using System.Reflection;
using System.Windows;

namespace Passenger.Inspector.Wpf;

/// <summary>An element's dependency properties as the Live Visual Tree lists them: those its type and its bases declare
/// (their public static XxxProperty fields, a property one took from another type under its own name), then each attached
/// property that has a value on it, named Owner.Name.</summary>
internal static class Properties
{
    private static readonly Dictionary<Type, List<DependencyProperty>> Declared = [];

    /// <summary>The element's properties and whether each is attached.</summary>
    public static List<(DependencyProperty Property, bool Attached)> Of(DependencyObject element)
    {
        List<(DependencyProperty, bool)> all = [];
        HashSet<DependencyProperty> listed = [];
        foreach (DependencyProperty property in DeclaredBy(element.GetType()))
        {
            if (listed.Add(property))
            {
                all.Add((property, false));
            }
        }
        LocalValueEnumerator local = element.GetLocalValueEnumerator();
        while (local.MoveNext())
        {
            DependencyProperty property = local.Current.Property;
            if (listed.Add(property))
            {
                all.Add((property, AttachedFrom(property, element)));
            }
        }
        return all;
    }

    /// <summary>A property as the window names it: its own name, an attached one's owner first.</summary>
    public static string NameOf(DependencyProperty property, bool attached) => Words.PropertyName(property.OwnerType, property.Name, attached);

    /// <summary>A property an element holds as the window lists it (<see cref="Of"/>), as its XAML writes it on the element.</summary>
    public static string NameOn(DependencyObject element, DependencyProperty property) =>
        NameOf(property, !DeclaredBy(element.GetType()).Contains(property) && AttachedFrom(property, element));

    /// <summary>Whether a property an element's type does not declare is another type's, attached to it.</summary>
    private static bool AttachedFrom(DependencyProperty property, DependencyObject element) => !property.OwnerType.IsAssignableFrom(element.GetType());

    /// <summary>An element's property by the name the Live Visual Tree lists it under, or null.</summary>
    public static DependencyProperty? Find(DependencyObject element, string name)
    {
        foreach ((DependencyProperty property, bool attached) in Of(element))
        {
            if (NameOf(property, attached) == name || (!attached && name.Contains('.', StringComparison.Ordinal) && $"{Words.TypeName(property.OwnerType)}.{property.Name}" == name))
            {
                return property;
            }
        }
        return null;
    }

    /// <summary>The dependency properties a type and its bases declare as public static fields, less the attached ones they
    /// declare for other elements (Grid.Row on a Grid): a property one took from another type (Control's FontSize, from
    /// TextElement) is its own, under its own name.</summary>
    private static List<DependencyProperty> DeclaredBy(Type type)
    {
        lock (Declared)
        {
            if (Declared.TryGetValue(type, out List<DependencyProperty>? known))
            {
                return known;
            }
            List<DependencyProperty> found = [];
            for (Type? at = type; at is not null && at != typeof(DependencyObject); at = at.BaseType)
            {
                foreach (FieldInfo field in at.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType == typeof(DependencyProperty) && field.GetValue(null) is DependencyProperty property && !IsAttachedOn(property, type))
                    {
                        found.Add(property);
                    }
                }
            }
            Declared[type] = found;
            return found;
        }
    }

    /// <summary>Whether a property is attached as the type sees it: it has no property of that name of its own.</summary>
    private static bool IsAttachedOn(DependencyProperty property, Type type)
    {
        try
        {
            return System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, type) is not { IsAttached: false };
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
