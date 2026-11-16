using System.Globalization;
using System.Text;

namespace Passenger.Core;

/// <summary>A property given on a dotnet or MSBuild command line, its name and value, the one way one is written there: its
/// value escaped so MSBuild reads back the text given, where a <c>;</c> or <c>,</c> would end it and begin another
/// property, a <c>%</c> begin an escape of its own and a <c>"</c> be dropped.</summary>
public static class PropertySwitch
{
    /// <summary>The characters MSBuild's command line reads otherwise than as a property value's text.</summary>
    private const string Special = "%;,\"";

    /// <summary>The switch giving a property to dotnet build, run, publish, restore and msbuild, or to dotnet watch
    /// (<paramref name="watched"/>), whose <c>-p</c> is its project's.</summary>
    public static string Of(string name, string value, bool watched = false) => $"{(watched ? "--property" : "-p")}:{name}={Escaped(value)}";

    /// <summary>A value as MSBuild's command line reads it back whole, each character it would read otherwise written as
    /// its %XX escape.</summary>
    public static string Escaped(string value)
    {
        if (value.AsSpan().IndexOfAny(Special) < 0)
        {
            return value;
        }
        StringBuilder escaped = new(value.Length + 8);
        foreach (char c in value)
        {
            if (Special.Contains(c, StringComparison.Ordinal))
            {
                escaped.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(c);
            }
        }
        return escaped.ToString();
    }
}
