namespace Passenger.Xaml.Metadata;

/// <summary>The names the MVVM Toolkit's generators give what they make of a member: the property of an
/// <c>[ObservableProperty]</c> field, the command of a <c>[RelayCommand]</c> method; and the field or method a new name for
/// one of those asks for.</summary>
internal static class ToolkitNames
{
    private const string Command = "Command";
    private const string Async = "Async";

    /// <summary>A field's property: its name without a leading <c>_</c> or <c>m_</c>, its first letter upper case; null for
    /// a name that leaves nothing.</summary>
    public static string? PropertyOf(string field)
    {
        string bare = Bare(field);
        return bare.Length == 0 ? null : char.ToUpperInvariant(bare[0]) + bare[1..];
    }

    /// <summary>A method's command: SaveCommand from Save or SaveAsync.</summary>
    public static string CommandOf(string method) =>
        (method.EndsWith(Async, StringComparison.Ordinal) ? method[..^Async.Length] : method) + Command;

    /// <summary>The name a field takes for its property to be <paramref name="property"/>, written as the field is (its
    /// <c>_</c> or <c>m_</c>, its first letter's case); null where no field's property is that name.</summary>
    public static string? FieldFor(string field, string property)
    {
        if (property.Length == 0)
        {
            return null;
        }
        string bare = Bare(field);
        string prefix = field[..^bare.Length];
        string renamed = prefix + (bare.Length > 0 && char.IsLower(bare[0]) ? char.ToLowerInvariant(property[0]) : property[0]) + property[1..];
        return PropertyOf(renamed) == property ? renamed : null;
    }

    /// <summary>The name a method takes for its command to be <paramref name="command"/>, an Async ending kept; null where
    /// no method's command is that name.</summary>
    public static string? MethodFor(string method, string command)
    {
        if (!command.EndsWith(Command, StringComparison.Ordinal) || command.Length == Command.Length)
        {
            return null;
        }
        string renamed = command[..^Command.Length] + (method.EndsWith(Async, StringComparison.Ordinal) ? Async : string.Empty);
        return CommandOf(renamed) == command ? renamed : null;
    }

    private static string Bare(string field) => field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
}
