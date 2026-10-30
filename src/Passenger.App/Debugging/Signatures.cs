using System.Globalization;
using System.Text;

namespace Passenger.App.Debugging;

/// <summary>A text standing for what breakpoints ask of the debugger, to tell a change worth sending: each field written
/// with its length before it, so two texts are the same only where every field is, whatever a condition, hit count or
/// message holds ('|', ';' or a field's own signature).</summary>
public static class Signatures
{
    /// <summary>The fields in order, null told apart from empty; a list is the signatures of its parts.</summary>
    public static string Of(params ReadOnlySpan<object?> fields)
    {
        StringBuilder text = new();
        foreach (object? field in fields)
        {
            if (field is null)
            {
                text.Append('~');
                continue;
            }
            string written = Convert.ToString(field, CultureInfo.InvariantCulture) ?? string.Empty;
            text.Append(written.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(written);
        }
        return text.ToString();
    }
}
