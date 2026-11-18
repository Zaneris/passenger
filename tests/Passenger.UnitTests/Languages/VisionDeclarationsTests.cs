using Microsoft.CodeAnalysis.CSharp;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>Which declarations of a C# file Code Vision hints after, and what it asks of each.</summary>
public sealed class VisionDeclarationsTests
{
    private const string Code = """
        namespace N;

        public interface IShape
        {
            double Area { get; }
            void Draw();
        }

        public abstract class Base : IShape
        {
            public abstract double Area { get; }
            public virtual void Draw() { }
            public void Plain() { }
            private int count;
            public Base() { }
        }

        public sealed class Leaf : Base
        {
            public override double Area => 1; public void SameLine() { }
        }
        """;

    private static IReadOnlyList<VisionDeclaration> Read(string code) =>
        VisionDeclarations.Of(new ParsedText(CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(code)));

    [Fact]
    public void A_type_and_each_member_is_read_with_what_may_inherit_from_it()
    {
        string read = string.Join(", ", Read(Code).Select(d => $"{d.Name} {d.Heritage}"));
        const string Expected = "IShape Implementations, Area Implementations, Draw Implementations, Base Inheritors, Area Implementations, Draw Overrides, "
            + "Plain None, count None, Base None, Leaf None, Area None";
        Assert.True(read == Expected, $"The declarations read as \"{read}\", not \"{Expected}\", so a hint asks for implementations of what has none or none of what has some");
    }

    [Fact]
    public void A_declaration_is_hinted_on_its_names_line_and_spans_its_own_lines()
    {
        VisionDeclaration shape = Read(Code)[0];
        Assert.True((shape.Line, shape.FirstLine, shape.LastLine) == (2, 2, 6),
            $"IShape was read at lines {shape.Line} to {shape.LastLine} (first {shape.FirstLine}), not 2 to 6, so its authors are those of other lines");
    }

    [Fact]
    public void A_second_declaration_on_a_line_has_no_hint_of_its_own()
    {
        Assert.True(Read(Code).All(d => d.Name != "SameLine"), "SameLine shares Area's line and was read too, so two hints would be drawn after the line");
    }

    [Fact]
    public void A_declaration_keeps_its_key_when_lines_are_added_above_it_and_overloads_have_each_their_own()
    {
        const string Overloads = "class C\n{\n    void M(int a) { }\n    void M(string a) { }\n}\n";
        IReadOnlyList<VisionDeclaration> before = Read(Overloads);
        IReadOnlyList<VisionDeclaration> after = Read("// a line added\n" + Overloads);
        Assert.True(before.Select(d => d.Key).SequenceEqual(after.Select(d => d.Key)) && before.Select(d => d.Key).Distinct().Count() == before.Count,
            "A declaration's key changed with a line added above it, or two overloads shared one, so its numbers would be shown for another");
    }
}
