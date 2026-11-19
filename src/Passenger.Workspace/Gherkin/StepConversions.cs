using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace.Gherkin;

/// <summary>Whether Reqnroll's step argument converter takes a step's text as a definition's parameter, ported from Reqnroll
/// 3's StepArgumentTypeConverter: a transformation making the parameter's type whose expression matches the whole text takes
/// it; a string or object takes any text, a Guid too (Reqnroll's reader pads or empties one); an enum is read as Enum.Parse
/// reads it with the text's spaces taken out, a nullable one as its type converter reads it; every other type of .NET's by
/// its own type converter, then Convert.ChangeType (a nullable one by the converter alone, empty text for none), in the
/// binding culture. A type of whose conversions nothing is known is taken as taking any text.</summary>
public static class StepConversions
{
    private const NumberStyles EnumNumber = NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingWhite;

    /// <summary>.NET's types Reqnroll converts by their type converters, with the words for what each takes.</summary>
    private static readonly Dictionary<string, (TypeConverter Converter, Type Type, string Noun, string? Detail)> Simple = new(StringComparer.Ordinal)
    {
        ["System.Byte"] = (new ByteConverter(), typeof(byte), "a whole number", Range(byte.MinValue, byte.MaxValue)),
        ["System.SByte"] = (new SByteConverter(), typeof(sbyte), "a whole number", Range(sbyte.MinValue, sbyte.MaxValue)),
        ["System.Int16"] = (new Int16Converter(), typeof(short), "a whole number", Range(short.MinValue, short.MaxValue)),
        ["System.UInt16"] = (new UInt16Converter(), typeof(ushort), "a whole number", Range(ushort.MinValue, ushort.MaxValue)),
        ["System.Int32"] = (new Int32Converter(), typeof(int), "a whole number", Range(int.MinValue, int.MaxValue)),
        ["System.UInt32"] = (new UInt32Converter(), typeof(uint), "a whole number", Range(uint.MinValue, uint.MaxValue)),
        ["System.Int64"] = (new Int64Converter(), typeof(long), "a whole number", Range(long.MinValue, long.MaxValue)),
        ["System.UInt64"] = (new UInt64Converter(), typeof(ulong), "a whole number", Range(ulong.MinValue, ulong.MaxValue)),
        ["System.Single"] = (new SingleConverter(), typeof(float), "a number", null),
        ["System.Double"] = (new DoubleConverter(), typeof(double), "a number", null),
        ["System.Decimal"] = (new DecimalConverter(), typeof(decimal), "a number", null),
        ["System.Boolean"] = (new BooleanConverter(), typeof(bool), "true or false", null),
        ["System.Char"] = (new CharConverter(), typeof(char), "one character", null),
        ["System.DateTime"] = (new DateTimeConverter(), typeof(DateTime), "a date", null),
        ["System.DateTimeOffset"] = (new DateTimeOffsetConverter(), typeof(DateTimeOffset), "a date", null),
        ["System.DateOnly"] = (new DateOnlyConverter(), typeof(DateOnly), "a date", null),
        ["System.TimeSpan"] = (new TimeSpanConverter(), typeof(TimeSpan), "a time span", "like 1.02:30:00"),
        ["System.TimeOnly"] = (new TimeOnlyConverter(), typeof(TimeOnly), "a time of day", null),
        ["System.Version"] = (new VersionConverter(), typeof(Version), "a version", "like 1.2.3"),
        ["System.Uri"] = (new UriTypeConverter(), typeof(Uri), "an address", null),
    };

    /// <summary>Why Reqnroll's converter refuses a text as a parameter, in words saying what it takes: null where it takes
    /// the text, or where nothing is known of what its type takes.</summary>
    public static string? Refusal(string text, StepParameter parameter, IReadOnlyList<StepTransformation> transformations, CultureInfo culture)
    {
        StepTransformation[] making = MakersOf(parameter, transformations);
        if (making.Any(t => Takes(t, text)) || Converts(text, parameter, culture) != false)
        {
            return null;
        }
        return Takes(parameter, making, culture);
    }

    /// <summary>The transformations that make a parameter's type, as Reqnroll finds them by their return type.</summary>
    public static StepTransformation[] MakersOf(StepParameter parameter, IReadOnlyList<StepTransformation> transformations)
    {
        string type = parameter.Nullable ? parameter.Type + "?" : parameter.Type;
        return [.. transformations.Where(t => t.ReturnType == type)];
    }

    /// <summary>Whether a transformation takes a text: one with an expression where it matches the whole text, one with
    /// none where it takes no table. An expression past its time limit throws a <see cref="RegexMatchTimeoutException"/>.</summary>
    public static bool Takes(StepTransformation transformation, string text)
    {
        if (transformation.Regex is not { } regex)
        {
            return transformation.Parameters.Count == 0 || !IsTable(transformation.Parameters[0]);
        }
        try
        {
            return TextSearch.IsWrittenMatch(text, StepExpressions.WholeText(regex), RegexOptions.CultureInvariant, TextSearch.Apart);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>Whether a type is Reqnroll's or SpecFlow's table.</summary>
    public static bool IsTable(string type) => type is "Reqnroll.Table" or "Reqnroll.DataTable" or "TechTalk.SpecFlow.Table";

    /// <summary>Whether Reqnroll's simple conversion (no transformation) takes a text as a parameter; null where nothing is
    /// known of what its type takes.</summary>
    public static bool? Converts(string text, StepParameter parameter, CultureInfo culture)
    {
        if (parameter.Unconverted)
        {
            return false;
        }
        if (parameter.Members is { } names)
        {
            return parameter.Nullable
                ? text.Length == 0 || text.Split(',').All(part => EnumParses(part, names, parameter.Underlying))
                : EnumParses(text.Replace(" ", "", StringComparison.Ordinal), names, parameter.Underlying);
        }
        if (parameter.Type is "System.String" or "System.Object" or "System.Guid")
        {
            return true;
        }
        if (!Simple.TryGetValue(parameter.Type, out (TypeConverter Converter, Type Type, string Noun, string? Detail) simple))
        {
            return null;
        }
        if (parameter.Nullable && text.Length == 0)
        {
            return true;
        }
        try
        {
            simple.Converter.ConvertFrom(null, culture, text);
            return true;
        }
        catch (Exception refused) when (refused is ArgumentException or FormatException or OverflowException or NotSupportedException or InvalidCastException)
        {
            // A nullable type's converter is its underlying type's, with no conversion after it.
        }
        if (parameter.Nullable)
        {
            return false;
        }
        try
        {
            Convert.ChangeType(text, simple.Type, culture);
            return true;
        }
        catch (Exception refused) when (refused is ArgumentException or FormatException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Enum.Parse of a text, letter case ignored, over an enum's names and integer type: a number of that type, else
    /// names joined by commas, each trimmed.</summary>
    private static bool EnumParses(string text, IReadOnlyList<string> names, string? underlying)
    {
        ReadOnlySpan<char> value = text.AsSpan().TrimStart();
        if (value.IsEmpty)
        {
            return false;
        }
        if ((char.IsAsciiDigit(value[0]) || value[0] is '-' or '+') && IntegerParses(value, underlying))
        {
            return true;
        }
        while (value.Length > 0)
        {
            int comma = value.IndexOf(',');
            if (comma == value.Length - 1)
            {
                return false;
            }
            ReadOnlySpan<char> name = (comma < 0 ? value : value[..comma]).Trim();
            value = comma < 0 ? default : value[(comma + 1)..];
            bool known = false;
            foreach (string each in names)
            {
                known |= name.Equals(each, StringComparison.OrdinalIgnoreCase);
            }
            if (!known)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IntegerParses(ReadOnlySpan<char> value, string? underlying) => underlying switch
    {
        "System.SByte" => sbyte.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
        "System.Byte" => byte.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
        "System.Int16" => short.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
        "System.UInt16" => ushort.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
        "System.UInt32" => uint.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
        "System.Int64" => long.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
        "System.UInt64" => ulong.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
        _ => int.TryParse(value, EnumNumber, CultureInfo.InvariantCulture, out _),
    };

    /// <summary>What a parameter takes, in words: the transformations making its type, else what its own conversion reads.</summary>
    private static string Takes(StepParameter parameter, StepTransformation[] making, CultureInfo culture)
    {
        string type = TypeNames.Short(parameter.Type);
        List<string> ways = [.. making.Select(t => $"text matching {StepExpressions.WholeText(t.Regex ?? ".*")}{(t.Method is { } method ? $" (made by {method})" : "")}")];
        if (parameter.Unconverted)
        {
            return ways.Count > 0 ? Plural.Either(ways) : $"a {type}, which only a [StepArgumentTransformation] makes from text, and none makes one";
        }
        string nothing = parameter.Nullable ? ", or nothing" : "";
        if (parameter.Members is { } names)
        {
            ways.Add($"one of {type}'s names ({Plural.Either(names)}, in any letter case{(parameter.Nullable ? "" : ", spaces left out")}) or a number{nothing}");
        }
        else if (Simple.TryGetValue(parameter.Type, out (TypeConverter Converter, Type Type, string Noun, string? Detail) simple))
        {
            string? detail = simple.Detail ?? parameter.Type switch
            {
                "System.Single" or "System.Double" or "System.Decimal" => $"as {CultureNamed(culture)} writes one, like {1234.5m.ToString(culture)}",
                "System.DateTime" or "System.DateTimeOffset" or "System.DateOnly" => $"as {CultureNamed(culture)} writes one, like {new DateTime(2024, 1, 31).ToString("d", culture)}",
                "System.TimeOnly" => $"like {new TimeOnly(14, 30).ToString("t", culture)}",
                _ => null,
            };
            ways.Add(detail is null ? simple.Noun + nothing : $"{simple.Noun} ({detail}){nothing}");
        }
        return Plural.Either(ways);
    }

    private static string CultureNamed(CultureInfo culture) => culture.Name.Length > 0 ? culture.Name : "the invariant culture";

    private static string Range<T>(T least, T most) where T : IFormattable =>
        $"from {least.ToString(null, CultureInfo.InvariantCulture)} to {most.ToString(null, CultureInfo.InvariantCulture)}";
}
