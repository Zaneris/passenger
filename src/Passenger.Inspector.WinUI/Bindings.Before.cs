using System.Collections.Concurrent;
using System.Reflection;

namespace Passenger.Inspector.WinUI;

/// <summary>The app's bindings that failed before the inspector heard: WinUI's Application.Start makes the first window
/// (OnLaunched) before its UI thread takes any work, so a binding failing as that window binds is told to no listener. Each
/// binding of the app's own elements is read once the tree is known, its path followed through its source's members, and
/// one naming a member its source's type lacks is told as WinUI words it. A source that answers for its members itself
/// (ICustomPropertyProvider), and a path of indexes or attached properties, are passed over.</summary>
internal static partial class Bindings
{
    /// <summary>Each element type's dependency properties, as its projection names them (TextProperty).</summary>
    private static readonly ConcurrentDictionary<Type, (string Name, object Property)[]> Properties = new();

    private static IEnumerable<string> FailedBefore(Inspector inspecting)
    {
        foreach (Visual visual in inspecting.TopLevels().SelectMany(top => ElementTrees.Below(top, inspecting.Visuals.ChildrenOf)))
        {
            if (visual.FromTheme || inspecting.ObjectOf(visual) is not { } element || !Projected.Is(element, "Microsoft.UI.Xaml.FrameworkElement"))
            {
                continue;
            }
            foreach ((string name, object property) in PropertiesOf(element.GetType()))
            {
                if (Projected.Call(element, "GetBindingExpression", property) is not { } expression
                    || Projected.Get(expression, "DataItem") is not { } item
                    || (Projected.Get(expression, "ParentBinding") is { } binding && Projected.Get(binding, "Path") is { } path ? Projected.Get(path, "Path") as string : null) is not { Length: > 0 } written
                    || Lacking(item, written) is not ({ } member, { } lacking))
                {
                    continue;
                }
                string type = element.GetType().GetProperty(name)?.PropertyType.Name ?? "Object";
                yield return $"Error: BindingExpression path error: '{member}' property not found on '{lacking.FullName}'. BindingExpression: Path='{written}' "
                    + $"DataItem='{item.GetType().FullName}'; target element is '{element.GetType().FullName}' (Name='{visual.Name ?? "null"}'); "
                    + $"target property is '{name}' (type '{type}')";
            }
        }
    }

    /// <summary>The first member of a path its source lacks, and the type that lacks it; nothing where every step is there,
    /// a step is null, or the path is one the inspector does not follow.</summary>
    private static (string? Member, Type? Lacking) Lacking(object item, string path)
    {
        if (path.IndexOfAny(['[', '(']) >= 0)
        {
            return (null, null);
        }
        object? at = item;
        foreach (string step in path.Split('.'))
        {
            if (at is null || Projected.Is(at, "Microsoft.UI.Xaml.Data.ICustomPropertyProvider"))
            {
                return (null, null);
            }
            if (at.GetType().GetProperty(step, BindingFlags.Public | BindingFlags.Instance) is not { } member)
            {
                return (step, at.GetType());
            }
            at = member.GetIndexParameters().Length == 0 ? member.GetValue(at) : null;
        }
        return (null, null);
    }

    /// <summary>A type's dependency properties and their names without "Property", its bases' included.</summary>
    private static (string Name, object Property)[] PropertiesOf(Type type) => Properties.GetOrAdd(type, made =>
    [
        .. made.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(p => p.Name.EndsWith("Property", StringComparison.Ordinal) && p.PropertyType.FullName == "Microsoft.UI.Xaml.DependencyProperty")
            .Select(p => (p.Name[..^"Property".Length], p.GetValue(null)))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!)),
    ]);
}
