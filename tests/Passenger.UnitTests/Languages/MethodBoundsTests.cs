using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>A frame stopped at a place of a C# file sees the method holding it, with that method's lambdas and local
/// functions, and nothing else of the file: a hover evaluates a name there and nowhere else. Top-level statements are
/// one method, a property's accessors are each their own, and a primary constructor's parameters and base call are its
/// own text.</summary>
public sealed class MethodBoundsTests
{
    private const string Code = """
        using System;

        var total = Add(1, 2);
        Func<int, int> twice = x => x * total;
        Console.WriteLine(Local(total));

        int Local(int value) => value + total;

        sealed class Order(int start) : Base(start)
        {
            private int count = start;

            public Order() : this(0)
            {
                count = 1;
            }

            public int Count => count + 0;

            public string Name { get; set; } = "named";

            public int Twice
            {
                get => count * 2;
                set { count = value / 2; }
            }

            public void Add(int more)
            {
                Action add = () => count += more;
                void Again() => count -= more + 1;
                add();
            }

            public void Other(int more) => count -= more;
        }

        class Base(int first);
        """;

    [Fact]
    public void A_frame_sees_the_method_it_stopped_in_and_nothing_else_and_a_place_in_no_code_that_runs_is_in_none() => Cases.All(
        ("a frame sees the method it stopped in, its lambdas and local functions, and nothing else", () => Cases.Each(
        [
            ("x * total", "Local(total)", true),
            ("count = 1", "this(0)", true),
            ("count * 2", "value / 2", false),
            ("count += more", "Again()", true),
            ("count += more", "count -= more;", false),
            ("Base(start)", "int start", true),
        ], row =>
        {
            (string stopped, string hovered, bool sees) = row;
            ParsedText parsed = new(CSharpParsing.Parse(Code), TextSnapshot.From(Code));
            TextPosition stop = parsed.Text.GetPosition(Code.IndexOf(stopped, StringComparison.Ordinal));
            int at = Code.IndexOf(hovered, StringComparison.Ordinal);
            TextRange? method = MethodBounds.At(parsed, stop.Line, stop.Column);
            Assert.True(method is { } seen && (seen.Start <= at && at <= seen.End) == sees,
                $"A frame stopped at {stopped} would {(sees ? "not " : "")}evaluate {hovered} hovered, its method read as {(method is { } read ? Code[read.Start..read.End] : "none")}");
        })),
        ("a place in no code that runs is in no method", () =>
        {
            ParsedText parsed = new(CSharpParsing.Parse(Code), TextSnapshot.From(Code));
            TextPosition type = parsed.Text.GetPosition(Code.IndexOf("sealed class Order", StringComparison.Ordinal));
            Assert.Null(MethodBounds.At(parsed, type.Line, type.Column));
            Assert.Null(MethodBounds.At(parsed, parsed.Text.LineCount + 3, 0));
            Assert.Null(MethodBounds.At(parsed, -1, 0));
        }));
}
