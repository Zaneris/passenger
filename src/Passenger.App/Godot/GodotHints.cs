using System.Globalization;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>How the properties pane edits a running game's value: as text, a check box, a slider within a range, a choice
/// among names, a colour, axes that scrub, a resource of the project's (and its own properties), an array's items, or a
/// dictionary's keys and values.</summary>
public enum GodotEditing
{
    Text,
    Check,
    Range,
    Choice,
    Colour,
    Axes,
    Resource,
    Items,
    Entries,
}

/// <summary>A range a number is set within: its ends and step, whether a value typed may go past either end, whether the
/// slider moves along it exponentially, whether it is an angle kept in radians and shown in degrees, the unit after it, and
/// whether its slider shows (a hint may hide it, leaving the number to scrub).</summary>
public sealed record GodotRange(double Min, double Max, double Step, bool OrGreater = false, bool OrLess = false, bool Exponential = false,
    bool Degrees = false, string? Suffix = null, bool Tracked = true)
{
    /// <summary>A value kept within the ends a value typed may not pass.</summary>
    public double Within(double value) => Math.Clamp(value, OrLess ? double.MinValue : Min, OrGreater ? double.MaxValue : Max);

    /// <summary>A value within the range as the slider takes it, at its step from the range's start.</summary>
    public double Snapped(double value)
    {
        double clamped = Within(value);
        return Step > 0 ? Math.Round(Math.Round((clamped - Min) / Step) * Step + Min, 10) : clamped;
    }

    /// <summary>Where a value lies along the slider, from 0 at its start to 1 at its end.</summary>
    public double Fraction(double value)
    {
        if (Max <= Min)
        {
            return 0;
        }
        double at = Math.Clamp(value, Min, Max);
        return Exponential && Min > 0 ? Math.Log(at / Min) / Math.Log(Max / Min) : (at - Min) / (Max - Min);
    }

    /// <summary>The value at a place along the slider, snapped to the step.</summary>
    public double At(double fraction)
    {
        double f = Math.Clamp(fraction, 0, 1);
        double value = Exponential && Min > 0 ? Min * Math.Pow(Max / Min, f) : Min + ((Max - Min) * f);
        return Snapped(value);
    }
}

/// <summary>A name a value may take, and the value it stands for: a whole number for an int's enum, the name itself for a
/// string's.</summary>
public sealed record GodotChoice(string Name, object Value);

/// <summary>What the properties pane edits a property with, read from its type and the hint Godot sends with it.</summary>
public sealed record GodotHint(GodotEditing Editing)
{
    public GodotRange? Range { get; init; }

    public IReadOnlyList<GodotChoice> Choices { get; init; } = [];

    /// <summary>Whether a colour has its alpha to set.</summary>
    public bool Alpha { get; init; } = true;

    /// <summary>The axes' names (x, y, z, w), one with no name for a plain number.</summary>
    public IReadOnlyList<string> Axes { get; init; } = [];

    /// <summary>Whether the axes take whole numbers.</summary>
    public bool Integers { get; init; }

    /// <summary>The classes a resource may be (Texture2D, PackedScene), or the patterns a file's path matches (*.png).</summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>Whether a resource is a path the property holds as a string (a file hint), not a resource object.</summary>
    public bool PathString { get; init; }

    /// <summary>The type an array's items are (a dictionary's values), where the property says: Nil for any.</summary>
    public GodotType Element { get; init; }

    /// <summary>The type a dictionary's keys are, where the property says: Nil for any.</summary>
    public GodotType Key { get; init; }
}

/// <summary>Godot's property hints read as the editor a value takes (<see cref="Of"/>): a number's range, an enum's names, a
/// colour without alpha, a resource's classes, a file's patterns. The hints are Godot's <c>PropertyHint</c> numbers.</summary>
public static class GodotHints
{
    public const long Range = 1;
    public const long Enum = 2;
    public const long EnumSuggestion = 3;
    public const long File = 13;
    public const long ResourceType = 17;
    public const long ColorNoAlpha = 21;
    public const long TypeString = 23;
    public const long ArrayType = 31;
    public const long DictionaryType = 38;

    /// <summary>The editor a property's value takes: text for one no editor fits.</summary>
    public static GodotHint Of(GodotProperty property)
    {
        switch (property.Type)
        {
            case GodotType.Bool:
                return new GodotHint(GodotEditing.Check);
            case GodotType.Int when property.Hint == Enum:
                return new GodotHint(GodotEditing.Choice) { Choices = Choices(property.HintString, numbered: true) };
            case GodotType.String or GodotType.StringName when property.Hint is Enum or EnumSuggestion:
                return new GodotHint(GodotEditing.Choice) { Choices = Choices(property.HintString, numbered: false) };
            case GodotType.Int or GodotType.Float when property.Hint == Range && RangeOf(property.HintString, property.Type == GodotType.Int) is { Tracked: true } range:
                return new GodotHint(GodotEditing.Range) { Range = range };
            case GodotType.Int or GodotType.Float:
                return new GodotHint(GodotEditing.Axes) { Axes = [string.Empty], Integers = property.Type == GodotType.Int, Range = Stepped(property) };
            case GodotType.Color:
                return new GodotHint(GodotEditing.Colour) { Alpha = property.Hint != ColorNoAlpha };
            case GodotType.Vector2 or GodotType.Vector2I or GodotType.Vector3 or GodotType.Vector3I or GodotType.Vector4 or GodotType.Vector4I:
                int count = GodotVariant.Count(property.Type);
                return new GodotHint(GodotEditing.Axes)
                {
                    Axes = [.. new[] { "x", "y", "z", "w" }.Take(count)],
                    Integers = property.Type is GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I,
                    Range = Stepped(property),
                };
            case GodotType.Object when property.Hint == ResourceType:
                return new GodotHint(GodotEditing.Resource) { Classes = Split(property.HintString) };
            case GodotType.String when property.Hint == File:
                return new GodotHint(GodotEditing.Resource) { Classes = Split(property.HintString), PathString = true };
            case GodotType.Array:
                return new GodotHint(GodotEditing.Items) { Element = property.Hint is ArrayType or TypeString ? TypeNamed(property.HintString) : GodotType.Nil };
            case >= GodotType.PackedByteArray:
                return new GodotHint(GodotEditing.Items) { Element = GodotVariant.ElementOf(property.Type) };
            case GodotType.Dictionary when property.Hint == DictionaryType && property.HintString.Split(';') is [string key, string element]:
                return new GodotHint(GodotEditing.Entries) { Key = TypeNamed(key), Element = TypeNamed(element) };
            case GodotType.Dictionary:
                return new GodotHint(GodotEditing.Entries);
            default:
                return new GodotHint(GodotEditing.Text);
        }
    }

    /// <summary>A range hint's words ("0,100,1,or_greater,suffix:px"): its ends, its step (one for whole numbers, a
    /// thousandth for the rest, where none is given) and its flags; null where its ends are no numbers.</summary>
    public static GodotRange? RangeOf(string hint, bool integers)
    {
        string[] parts = [.. hint.Split(',').Select(p => p.Trim())];
        if (parts.Length < 2 || !Number(parts[0], out double min) || !Number(parts[1], out double max))
        {
            return null;
        }
        double step = parts.Length > 2 && Number(parts[2], out double given) ? given : integers ? 1 : 0.001;
        bool orGreater = false;
        bool orLess = false;
        bool exponential = false;
        bool degrees = false;
        bool slider = true;
        string? suffix = null;
        foreach (string flag in parts.Skip(2))
        {
            switch (flag)
            {
                case "or_greater":
                    orGreater = true;
                    break;
                case "or_less" or "or_lesser":
                    orLess = true;
                    break;
                case "exp":
                    exponential = true;
                    break;
                case "radians" or "radians_as_degrees":
                    degrees = true;
                    break;
                case "hide_slider":
                    slider = false;
                    break;
                default:
                    if (flag.StartsWith("suffix:", StringComparison.Ordinal))
                    {
                        suffix = flag["suffix:".Length..];
                    }
                    break;
            }
        }
        return new GodotRange(min, max, step, orGreater, orLess, exponential, degrees, suffix, slider);
    }

    /// <summary>An enum hint's names ("Walk,Run:5,Fly"): an int's numbered from zero, or after the number one gives; a
    /// string's the names themselves.</summary>
    public static IReadOnlyList<GodotChoice> Choices(string hint, bool numbered)
    {
        List<GodotChoice> choices = [];
        long next = 0;
        foreach (string part in hint.Split(','))
        {
            string name = part.Trim();
            if (name.Length == 0)
            {
                continue;
            }
            int colon = name.LastIndexOf(':');
            if (numbered && colon > 0 && long.TryParse(name[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                choices.Add(new GodotChoice(name[..colon].Trim(), value));
                next = value + 1;
            }
            else if (numbered)
            {
                choices.Add(new GodotChoice(name, next++));
            }
            else
            {
                choices.Add(new GodotChoice(name, name));
            }
        }
        return choices;
    }

    /// <summary>The step a number's or vector's axes scrub by: its range hint's, else a whole number's one, else a tenth.</summary>
    private static GodotRange? Stepped(GodotProperty property) =>
        property.Hint == Range && RangeOf(property.HintString, property.Type is GodotType.Int or GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I) is { } range
            ? range with { Tracked = false }
            : null;

    /// <summary>A container's element type as its hint names it: Godot's name for a built-in type ("int", "Vector2"), or its
    /// number before an element's own hint ("2:", "2/1:0,10"); a class's name, an object; Nil for none.</summary>
    public static GodotType TypeNamed(string hint)
    {
        string named = hint.Trim();
        int end = named.IndexOfAny(['/', ':']);
        if (int.TryParse(end < 0 ? named : named[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return System.Enum.IsDefined((GodotType)number) ? (GodotType)number : GodotType.Nil;
        }
        return named is "" or "Variant" ? GodotType.Nil : GodotTypes.Named(named) ?? GodotType.Object;
    }

    private static IReadOnlyList<string> Split(string hint) => [.. hint.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)];

    private static bool Number(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
