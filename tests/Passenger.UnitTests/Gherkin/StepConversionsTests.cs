using System.Globalization;
using Passenger.Workspace.Gherkin;
using Reqnroll;
using Reqnroll.Bindings;
using Reqnroll.Bindings.Reflection;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A step's argument converted to its definition's parameter as Reqnroll 3.3.4's converter converts it at run time,
/// held to that converter itself, run here in-process: random texts, types and binding cultures, each verdict compared with
/// what its StepArgumentTypeConverter.CanConvert answers (a transformation for Money matching <c>\$(\d+)</c>); and what is
/// refused said in words naming what is taken.</summary>
public sealed class StepConversionsTests
{
    private const int Seed = 3095;
    private const int Drawn = 4000;

    private static readonly StepTransformation ToMoney = new("\\$(\\d+)", null, "Shop.Money") { Parameters = ["System.String"], Method = "Transforms.ToMoney" };

    /// <summary>Each type a case may take: its name, as Reqnroll's converter is given it and as Passenger reads the parameter.</summary>
    private static readonly (string Name, Type Type)[] Types =
    [
        ("Int32", typeof(int)), ("Int64", typeof(long)), ("Int16", typeof(short)), ("Byte", typeof(byte)), ("SByte", typeof(sbyte)),
        ("UInt32", typeof(uint)), ("UInt64", typeof(ulong)), ("Single", typeof(float)), ("Double", typeof(double)), ("Decimal", typeof(decimal)),
        ("Boolean", typeof(bool)), ("Char", typeof(char)), ("DateTime", typeof(DateTime)), ("DateTimeOffset", typeof(DateTimeOffset)),
        ("TimeSpan", typeof(TimeSpan)), ("Guid", typeof(Guid)), ("String", typeof(string)), ("Uri", typeof(Uri)),
        ("Int32?", typeof(int?)), ("Double?", typeof(double?)), ("DateTime?", typeof(DateTime?)), ("Guid?", typeof(Guid?)), ("Boolean?", typeof(bool?)),
        ("Color", typeof(Color)), ("Color?", typeof(Color?)), ("Access", typeof(Access)), ("Small", typeof(Small)), ("Money", typeof(Money)),
        ("Basket", typeof(Basket)),
    ];

    private static readonly string[] Cultures = ["en-US", "de-DE", "fr-FR", ""];

    /// <summary>What a case's text is made of: numbers, dates, spans, names and words as a step writes them, and what only
    /// looks like one.</summary>
    private static readonly string[] Pieces =
    [
        "0", "5", "12", "50", "255", "256", "300", "-1", "-7", "+7", "1000", "99999999999", "2147483648", "1,000", "1.000", "1,5", "1.5",
        "1,000.5", "1.000,5", "0x1F", "#FF", "1e3", "7-", "1.02:03:04", "12:30", "01/15/2024", "15/01/2024", "15.01.2024", "2024-01-15",
        "Jan 32 2024", "1.2.3", "Red", "red", "Dark Blue", "dark blue", "DarkBlue", "Purple", "Read, Write", "Red,", "A", "a", "B",
        "True", "false", "yes", "1", "two", "$12", "$", "x", "ab", "", " ", "not-a-guid", "6F9619FF-8B86-D011-B42D-00CF4FC964FF",
        "http://x", "NaN", "Infinity",
    ];

    private static readonly string[] Joints = ["", " ", ",", "."];

    /// <summary>Verdicts Passenger gives otherwise than Reqnroll's converter on purpose, each with why: none.</summary>
    private static readonly (string Type, string Text, string Why)[] Differences = [];

    [Fact]
    public void An_argument_is_taken_as_reqnrolls_own_converter_takes_it_at_run_time()
    {
        Random random = new(Seed);
        StepArgumentTypeConverter reqnroll = Converter();
        List<string> differ = [];
        int refused = 0;
        for (int i = 0; i < Drawn && differ.Count < 3; i++)
        {
            (string name, Type type) = Types[random.Next(Types.Length)];
            CultureInfo culture = CultureInfo.GetCultureInfo(Cultures[random.Next(Cultures.Length)]);
            string text = Text(random);
            refused += reqnroll.CanConvert(text, new RuntimeBindingType(type), culture) ? 0 : 1;
            if (Differs(reqnroll, name, type, text, culture) is { } found && !Differences.Any(d => d.Type == name && d.Text == text))
            {
                differ.Add(Shrunk(reqnroll, name, type, found.Text, culture));
            }
        }
        Assert.True(differ.Count == 0, $"Seed {Seed}: Passenger takes a step's argument otherwise than Reqnroll's converter:\n{string.Join("\n", differ)}");
        Assert.True(refused > Drawn / 5 && refused < Drawn * 4 / 5, $"Reqnroll's converter refused {refused} of {Drawn} cases, too few or too many for the cases to tell its rules apart");
    }

    /// <summary>What is refused is said in words naming what the parameter takes, and a transformation's parameters say
    /// whether it takes text at all: words and knowledge of Passenger's own, which Reqnroll's converter has none of.</summary>
    [Fact]
    public void A_refusal_says_what_the_parameter_takes_and_a_type_nothing_is_known_of_takes_any_text() => Cases.All(
        ("a refusal says what the parameter takes", () => Cases.Each(
        [
            ("en-US", "Double?", "a number (as en-US writes one, like 1234.5), or nothing"),
            ("de-DE", "DateTime", "a date (as de-DE writes one, like 31.01.2024)"),
            ("en-US", "Color", "one of Color's names (Red, Green or DarkBlue, in any letter case, spaces left out) or a number"),
            ("en-US", "Money", "text matching ^\\$(\\d+)$ (made by Transforms.ToMoney)"),
        ], row => Assert.Equal(row.Item3, StepConversions.Refusal("~", ParameterOf(row.Item2), [ToMoney], CultureInfo.GetCultureInfo(row.Item1))))),
        ("a type of the solution's own no transformation makes takes no text and one nothing is known of takes any", () =>
        {
            Assert.Equal("a Basket, which only a [StepArgumentTransformation] makes from text, and none makes one",
                StepConversions.Refusal("full", ParameterOf("Basket"), [ToMoney], CultureInfo.InvariantCulture));
            Assert.Null(StepConversions.Refusal("full", new StepParameter("anything", "Shop.Anything"), [ToMoney], CultureInfo.InvariantCulture));
        }),
        ("a transformation with no expression takes any text but one taking a table takes none", () =>
        {
            StepTransformation anyText = new(null, null, "Shop.Money") { Parameters = ["System.String"] };
            StepTransformation fromTable = new(null, null, "Shop.Money") { Parameters = ["Reqnroll.DataTable"] };
            Assert.Null(StepConversions.Refusal("twelve", ParameterOf("Money"), [anyText], CultureInfo.InvariantCulture));
            Assert.NotNull(StepConversions.Refusal("twelve", ParameterOf("Money"), [fromTable], CultureInfo.InvariantCulture));
        }));

    /// <summary>A case's text: one piece, or two joined, at times with white space around.</summary>
    private static string Text(Random random)
    {
        string text = Pieces[random.Next(Pieces.Length)];
        if (random.Next(10) < 3)
        {
            text += Joints[random.Next(Joints.Length)] + Pieces[random.Next(Pieces.Length)];
        }
        return random.Next(10) == 0 ? $" {text} " : text;
    }

    /// <summary>The case as both read it, where they differ: the text and each verdict.</summary>
    private static (string Text, bool Passenger, bool Reqnroll)? Differs(StepArgumentTypeConverter reqnroll, string name, Type type, string text, CultureInfo culture)
    {
        bool passenger = StepConversions.Refusal(text, ParameterOf(name), [ToMoney], culture) is null;
        bool reference = reqnroll.CanConvert(text, new RuntimeBindingType(type), culture);
        return passenger == reference ? null : (text, passenger, reference);
    }

    /// <summary>A differing case cut to the shortest text that still differs, said with both verdicts.</summary>
    private static string Shrunk(StepArgumentTypeConverter reqnroll, string name, Type type, string text, CultureInfo culture)
    {
        bool cut = true;
        while (cut)
        {
            cut = false;
            for (int at = 0; at < text.Length; at++)
            {
                string shorter = text.Remove(at, 1);
                if (Differs(reqnroll, name, type, shorter, culture) is not null)
                {
                    (text, cut) = (shorter, true);
                    break;
                }
            }
        }
        (string _, bool passenger, bool reference) = Differs(reqnroll, name, type, text, culture)!.Value;
        return $"\"{text}\" as {name} in {(culture.Name.Length > 0 ? culture.Name : "the invariant culture")}: Passenger {(passenger ? "takes" : "refuses")} it, Reqnroll {(reference ? "takes" : "refuses")} it";
    }

    /// <summary>Reqnroll's converter over a registry holding the one transformation, ToMoney.</summary>
    private static StepArgumentTypeConverter Converter()
    {
        BindingRegistry registry = new();
        registry.RegisterStepArgumentTransformationBinding(new StepArgumentTransformationBinding("\\$(\\d+)",
            new RuntimeBindingMethod(typeof(Transforms).GetMethod(nameof(Transforms.ToMoney))!), null, 0));
        return new StepArgumentTypeConverter(null!, registry, null!, null!);
    }

    /// <summary>A parameter of a type as the cases name it: a .NET type by its short name, an enum of the tests', a nullable
    /// one with a <c>?</c>.</summary>
    private static StepParameter ParameterOf(string type)
    {
        StepParameter parameter = type.TrimEnd('?') switch
        {
            "Color" => new StepParameter("color", "Shop.Color", ["Red", "Green", "DarkBlue"]) { Underlying = "System.Int32" },
            "Access" => new StepParameter("access", "Shop.Access", ["Read", "Write"]) { Underlying = "System.Int32" },
            "Small" => new StepParameter("small", "Shop.Small", ["A", "B"]) { Underlying = "System.Byte" },
            "Money" => new StepParameter("price", "Shop.Money") { Unconverted = true },
            "Basket" => new StepParameter("basket", "Shop.Basket") { Unconverted = true },
            { } name => new StepParameter("value", "System." + name),
        };
        return parameter with { Nullable = type.EndsWith('?') };
    }

    public sealed class Money;

    public sealed class Basket;

    public static class Transforms
    {
        [StepArgumentTransformation("\\$(\\d+)")]
        public static Money ToMoney(string amount) => new();
    }

    public enum Color
    {
        Red,
        Green,
        DarkBlue,
    }

    [Flags]
    public enum Access
    {
        Read = 1,
        Write = 2,
    }

    public enum Small : byte
    {
        A,
        B,
    }
}
