namespace Passenger.Xaml.Metadata;

/// <summary>The names the MVVM Toolkit's generators give what they make of a member: the property of an
/// <c>[ObservableProperty]</c> field, the command and cancel command of a <c>[RelayCommand]</c> method; and the field or
/// method a new name for one of those asks for.</summary>
internal static class ToolkitNames
{
    private const string Command = "Command";
    private const string CancelCommand = "CancelCommand";
    private const string Async = "Async";
    private const string On = "On";

    /// <summary>A field's property: its name without a leading <c>_</c> or <c>m_</c>, its first letter upper case; null for
    /// a name that leaves nothing.</summary>
    public static string? PropertyOf(string field)
    {
        string bare = Bare(field);
        return bare.Length == 0 ? null : char.ToUpperInvariant(bare[0]) + bare[1..];
    }

    /// <summary>A method's command: SaveCommand from Save, OnSave or SaveAsync.</summary>
    public static string CommandOf(string method) => Named(method) + Command;

    /// <summary>The command <c>IncludeCancelCommand</c> makes of a method too: SaveCancelCommand from Save or SaveAsync.</summary>
    public static string CancelCommandOf(string method) => Named(method) + CancelCommand;

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

    /// <summary>The name a method takes for its command to be <paramref name="command"/>, its On and Async kept; null where
    /// no method's command is that name.</summary>
    public static string? MethodFor(string method, string command) => MethodFor(method, command, Command, CommandOf);

    /// <summary>The name a method takes for its cancel command to be <paramref name="command"/>, as <see cref="MethodFor(string, string)"/>.</summary>
    public static string? MethodForCancel(string method, string command) => MethodFor(method, command, CancelCommand, CancelCommandOf);

    private static string? MethodFor(string method, string command, string ending, Func<string, string> commandOf)
    {
        if (!command.EndsWith(ending, StringComparison.Ordinal) || command.Length == ending.Length)
        {
            return null;
        }
        string renamed = (HandlerNamed(method) ? On : string.Empty) + command[..^ending.Length] + (method.EndsWith(Async, StringComparison.Ordinal) ? Async : string.Empty);
        return commandOf(renamed) == command ? renamed : null;
    }

    private static string Named(string method)
    {
        string named = HandlerNamed(method) ? method[On.Length..] : method;
        return named.EndsWith(Async, StringComparison.Ordinal) ? named[..^Async.Length] : named;
    }

    /// <summary>Whether a method's On is a prefix its commands leave out: OnSave's, not Onboard's.</summary>
    private static bool HandlerNamed(string method) => method.Length > On.Length && method.StartsWith(On, StringComparison.Ordinal) && !char.IsLower(method[On.Length]);

    private static string Bare(string field) => field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
}
