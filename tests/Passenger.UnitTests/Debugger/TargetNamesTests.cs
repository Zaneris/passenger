using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>The type an assembly's debugger attribute names, read from the serialized name C# writes: its definition, and the
/// one construction it names, compared without assemblies, so an attribute for a box of numbers never reaches a box of words.</summary>
public sealed class TargetNamesTests
{
    private const string Definition = "Passenger.UnitTests.Debugger.TargetNamesTests+Box`1";

    private static TargetedAttribute Attribute(Type target, string shows)
    {
        (string definition, string? constructed) = TargetNames.Read(target.AssemblyQualifiedName!)!.Value;
        return new TargetedAttribute(definition, constructed, new DebuggerDisplay(shows, null, null), null);
    }

    [Fact]
    public void A_debugger_attributes_target_is_read_from_its_serialized_name_and_reaches_its_construction_and_no_other() => Cases.All(
        ("constructions of a generic type keep their arguments and share their definition", () =>
        {
            Assert.Equal((Definition, $"{Definition}[System.Int32]"), TargetNames.Read(typeof(Box<int>).AssemblyQualifiedName!));
            Assert.Equal((Definition, $"{Definition}[System.String]"), TargetNames.Read(typeof(Box<string>).AssemblyQualifiedName!));
        }),
        ("an argument is named whole through nesting arrays and pointers without its assembly", () =>
        {
            Assert.Equal($"{Definition}[{Definition}[System.Int32][]]", TargetNames.Read(typeof(Box<Box<int>[]>).AssemblyQualifiedName!)?.Constructed);
            Assert.Equal("Ns.Pair`2[System.Int32,Ns.Other]", TargetNames.Read("Ns.Pair`2[[System.Int32, System.Runtime, Version=10.0.0.0], [Ns.Other, Library]], Library")?.Constructed);
            Assert.Equal("Ns.Box`1[System.Int32*]", TargetNames.Read("Ns.Box`1[[System.Int32*, System.Runtime]]")?.Constructed);
        }),
        ("an array target is no definition of its element", () =>
        {
            Assert.Equal(("Ns.Thing[]", (string?)null), TargetNames.Read("Ns.Thing[], Library"));
        }),
        ("an attribute reaches its construction and no other", () =>
        {
            TargetedAttribute[] declared = [Attribute(typeof(Box<int>), "numbers"), Attribute(typeof(Box<string>), "words")];
            Assert.Equal("numbers", Assert.Single(TargetedAttribute.Fitting(declared, $"{Definition}[System.Int32]")).Display?.Value);
            Assert.Empty(TargetedAttribute.Fitting(declared, $"{Definition}[System.Double]"));
        }),
        ("an attribute for a definition reaches every construction and one for the construction comes after it", () =>
        {
            TargetedAttribute[] declared = [Attribute(typeof(Box<int>), "numbers"), Attribute(typeof(Box<>), "any"), Attribute(typeof(Box<string>), "words")];
            Assert.Equal(["any", "numbers"], TargetedAttribute.Fitting(declared, $"{Definition}[System.Int32]").Select(t => t.Display?.Value));
            Assert.Equal(["any"], TargetedAttribute.Fitting(declared, $"{Definition}[System.Double]").Select(t => t.Display?.Value));
        }));

    private sealed class Box<T>;
}
