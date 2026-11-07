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
    public void Constructions_of_a_generic_type_keep_their_arguments_and_share_their_definition()
    {
        Assert.Equal((Definition, $"{Definition}[System.Int32]"), TargetNames.Read(typeof(Box<int>).AssemblyQualifiedName!));
        Assert.Equal((Definition, $"{Definition}[System.String]"), TargetNames.Read(typeof(Box<string>).AssemblyQualifiedName!));
    }

    [Fact]
    public void A_definition_or_a_plain_type_names_no_construction()
    {
        Assert.Equal((Definition, (string?)null), TargetNames.Read(typeof(Box<>).AssemblyQualifiedName!));
        Assert.Equal(("Library.Gauge", (string?)null), TargetNames.Read("Library.Gauge, PassengerCasesProxies"));
        Assert.Equal(("Ns.Outer+Inner", (string?)null), TargetNames.Read(" Ns.Outer+Inner "));
    }

    [Fact]
    public void An_argument_is_named_whole_through_nesting_arrays_and_pointers_without_its_assembly()
    {
        Assert.Equal($"{Definition}[{Definition}[System.Int32][]]", TargetNames.Read(typeof(Box<Box<int>[]>).AssemblyQualifiedName!)?.Constructed);
        Assert.Equal($"{Definition}[System.Int32[,]]", TargetNames.Read(typeof(Box<int[,]>).AssemblyQualifiedName!)?.Constructed);
        Assert.Equal("Ns.Pair`2[System.Int32,Ns.Other]", TargetNames.Read("Ns.Pair`2[[System.Int32, System.Runtime, Version=10.0.0.0], [Ns.Other, Library]], Library")?.Constructed);
        Assert.Equal("Ns.Box`1[System.Int32]", TargetNames.Read("Ns.Box`1[System.Int32]")?.Constructed);
        Assert.Equal("Ns.Box`1[System.Int32*]", TargetNames.Read("Ns.Box`1[[System.Int32*, System.Runtime]]")?.Constructed);
    }

    [Fact]
    public void A_type_nested_in_a_construction_keeps_its_enclosing_arguments()
    {
        Assert.Equal(("Ns.Outer`1+Inner", "Ns.Outer`1+Inner[System.Int32]"), TargetNames.Read("Ns.Outer`1+Inner[[System.Int32, System.Runtime]], Library"));
    }

    /// <summary>An array of a type is another type, so an attribute for one reaches no value of its element's.</summary>
    [Fact]
    public void An_array_target_is_no_definition_of_its_element()
    {
        Assert.Equal(("Ns.Thing[]", (string?)null), TargetNames.Read("Ns.Thing[], Library"));
    }

    [Fact]
    public void Text_that_names_no_type_targets_nothing()
    {
        Assert.Null(TargetNames.Read("Ns.Box`1[["));
    }

    [Fact]
    public void An_attribute_reaches_its_construction_and_no_other()
    {
        TargetedAttribute[] declared = [Attribute(typeof(Box<int>), "numbers"), Attribute(typeof(Box<string>), "words")];
        Assert.Equal("numbers", Assert.Single(TargetedAttribute.Fitting(declared, $"{Definition}[System.Int32]")).Display?.Value);
        Assert.Equal("words", Assert.Single(TargetedAttribute.Fitting(declared, $"{Definition}[System.String]")).Display?.Value);
        Assert.Empty(TargetedAttribute.Fitting(declared, $"{Definition}[System.Double]"));
    }

    [Fact]
    public void An_attribute_for_a_definition_reaches_every_construction_and_one_for_the_construction_comes_after_it()
    {
        TargetedAttribute[] declared = [Attribute(typeof(Box<int>), "numbers"), Attribute(typeof(Box<>), "any"), Attribute(typeof(Box<string>), "words")];
        Assert.Equal(["any", "numbers"], TargetedAttribute.Fitting(declared, $"{Definition}[System.Int32]").Select(t => t.Display?.Value));
        Assert.Equal(["any"], TargetedAttribute.Fitting(declared, $"{Definition}[System.Double]").Select(t => t.Display?.Value));
    }

    private sealed class Box<T>;
}
