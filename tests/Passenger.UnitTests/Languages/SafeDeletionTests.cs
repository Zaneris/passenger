using Microsoft.CodeAnalysis.CSharp;
using Passenger.Languages.CSharp;

namespace Passenger.UnitTests.Languages;

/// <summary>Safe Delete of an enum member: it goes with the comma that parts it from the next, or with the one before it
/// for the last, its lines with it where it has them to itself, so what is left still parses as the enum less that
/// member.</summary>
public sealed class SafeDeletionTests
{
    private const string OneLine = "enum Color { Red, Green, Blue }\n";

    private const string Lines = "enum Color\n{\n    Red,\n    Green,\n    Blue\n}\n";

    [Theory]
    [InlineData(OneLine, "Green", "enum Color { Red, Blue }\n")]
    [InlineData(OneLine, "Red", "enum Color { Green, Blue }\n")]
    [InlineData(OneLine, "Blue", "enum Color { Red, Green }\n")]
    [InlineData(Lines, "Green", "enum Color\n{\n    Red,\n    Blue\n}\n")]
    [InlineData(Lines, "Blue", "enum Color\n{\n    Red,\n    Green\n}\n")]
    [InlineData("enum Color\n{\n    Red,\n    /// <summary>Grass.</summary>\n    Green = 4, // the lawn\n    Blue,\n}\n", "Green", "enum Color\n{\n    Red,\n    Blue,\n}\n")]
    [InlineData("enum Color\n{\n    Red,\n    Green, // the lawn\n    Blue\n}\n", "Blue", "enum Color\n{\n    Red,\n    Green, // the lawn\n}\n")]
    public void An_enum_member_goes_with_its_comma_and_the_enum_still_parses(string before, string member, string after)
    {
        Removal? removal = SafeDeletion.At(SafeDeletion.Parse(before, CSharpParseOptions.Default), before.IndexOf(member, StringComparison.Ordinal));
        Assert.NotNull(removal);
        Assert.Equal(("enum member", member), (removal.Kind, removal.Name));
        string left = before.Remove(removal.Range.Start, removal.Range.Length);
        Assert.True(after == left, $"Safe Delete of {member} left the enum so:\n{left}");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Assert.True(!CSharpSyntaxTree.ParseText(left, cancellationToken: cancel).GetDiagnostics(cancel).Any(), $"What Safe Delete of {member} left no longer parses:\n{left}");
    }

    private const string IfBlock = "enum Colour\n{\n    Red,\n#if DEBUG\n    Debug,\n#endif\n    Green\n}\n";

    private const string IfLast = "enum Colour\n{\n    Red,\n#if DEBUG\n    Debug\n#endif\n}\n";

    [Theory]
    [InlineData(IfBlock, true, "Green", "enum Colour\n{\n    Red,\n#if DEBUG\n    Debug,\n#endif\n}\n")]
    [InlineData(IfBlock, false, "Green", "enum Colour\n{\n    Red,\n#if DEBUG\n    Debug,\n#endif\n}\n")]
    [InlineData(IfLast, true, "Debug", "enum Colour\n{\n    Red,\n#if DEBUG\n#endif\n}\n")]
    public void The_last_member_after_a_directive_leaves_the_directives_and_the_comma_before_it(string before, bool debug, string member, string after)
    {
        CSharpParseOptions options = new(preprocessorSymbols: debug ? ["DEBUG"] : []);
        Removal? removal = SafeDeletion.At(SafeDeletion.Parse(before, options), before.IndexOf("    " + member, StringComparison.Ordinal) + 4);
        Assert.NotNull(removal);
        string left = before.Remove(removal.Range.Start, removal.Range.Length);
        Assert.True(after == left, $"Safe Delete of {member} with DEBUG {(debug ? "defined" : "undefined")} took the #if block's directives or members, so the build breaks or loses Debug:\n{left}");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Assert.True(!CSharpSyntaxTree.ParseText(left, options, cancellationToken: cancel).GetDiagnostics(cancel).Any(), $"What Safe Delete of {member} left no longer parses:\n{left}");
    }
}
