namespace Passenger.Xaml.Metadata;

/// <summary>The names the MVVM Toolkit's generators give what they make of a member: the property of an
/// <c>[ObservableProperty]</c> field, the command of a <c>[RelayCommand]</c> method.</summary>
internal static class ToolkitNames
{
    /// <summary>A field's property: its name without a leading <c>_</c> or <c>m_</c>, its first letter upper case; null for
    /// a name that leaves nothing.</summary>
    public static string? PropertyOf(string field)
    {
        string bare = field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
        return bare.Length == 0 ? null : char.ToUpperInvariant(bare[0]) + bare[1..];
    }

    /// <summary>A method's command: SaveCommand from Save or SaveAsync.</summary>
    public static string CommandOf(string method) =>
        (method.EndsWith("Async", StringComparison.Ordinal) ? method[..^"Async".Length] : method) + "Command";
}
