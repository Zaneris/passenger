using System.Reflection;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Core;

namespace Passenger.Inspector;

/// <summary>The binding expression giving an element's property its values, on any Avalonia 12: an untyped one, or the
/// typed one Avalonia 12.2 makes of a compiled binding to one member of the DataContext, which is no untyped expression;
/// each read for the priority it gives values at and its path as its XAML writes it.</summary>
internal static class BindingExpressions
{
    /// <summary>The priority every expression keeps from Avalonia 12.2, read by name as the inspector is compiled against
    /// 12.0; null on an older Avalonia, whose expressions are all untyped.</summary>
    private static readonly PropertyInfo? PriorityOfAny = typeof(BindingExpressionBase).GetProperty(nameof(UntypedBindingExpressionBase.Priority), BindingFlags.Public | BindingFlags.Instance);

    /// <summary>The binding expression on a property of an element, with the priority it gives values at and its path as its
    /// XAML writes it; null where none is on it, or its priority could not be read.</summary>
    public static Found? Of(AvaloniaObject element, AvaloniaProperty property)
    {
        BindingExpressionBase? expression;
        try
        {
            expression = BindingOperations.GetBindingExpressionBase(element, property);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        if (expression is null)
        {
            return null;
        }
        BindingPriority? priority = expression is UntypedBindingExpressionBase untyped ? untyped.Priority : PriorityOfAny?.GetValue(expression) as BindingPriority?;
        return priority is { } at ? new Found(expression, at, (expression as IDescription)?.Description ?? string.Empty) : null;
    }

    /// <summary>A binding expression, the priority it gives values at and its path as its XAML writes it.</summary>
    public sealed record Found(BindingExpressionBase Expression, BindingPriority Priority, string Description);
}
