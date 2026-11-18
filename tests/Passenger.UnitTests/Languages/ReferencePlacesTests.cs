using Passenger.Languages.CSharp;

namespace Passenger.UnitTests.Languages;

/// <summary>The places a server names for a symbol's references, and which of them are its implementations' declarations.</summary>
public sealed class ReferencePlacesTests
{
    [Fact]
    public void Two_places_are_one_when_they_are_the_same_place_of_the_same_document_whatever_the_uris_case()
    {
        ReferencePlaces.Place declaration = new("file:///Work/Shapes.cs", 4, 11);
        Assert.True(ReferencePlaces.IsSame(new("file:///work/shapes.cs", 4, 11), declaration), "The same place, its URI cased otherwise, was taken for another");
        Assert.False(ReferencePlaces.IsSame(new("file:///Work/Shapes.cs", 4, 12), declaration), "A place a character on was taken for the same");
        Assert.False(ReferencePlaces.IsSame(new("file:///Work/Shapes.cs", 5, 11), declaration), "A place a line on was taken for the same");
        Assert.False(ReferencePlaces.IsSame(new("file:///Work/Other.cs", 4, 11), declaration), "The same place of another file was taken for the same");
    }

    [Fact]
    public void The_references_without_implementations_keep_the_declaration_and_the_calls_in_their_order()
    {
        ReferencePlaces.Place declaration = new("file:///Work/IShape.cs", 3, 8);
        ReferencePlaces.Place[] implementations = [new("file:///Work/Square.cs", 6, 15), new("file:///WORK/Disc.cs", 2, 15)];
        ReferencePlaces.Place[] references = [implementations[0], declaration, new("file:///Work/Caller.cs", 10, 20), new("file:///work/disc.cs", 2, 15), new("file:///Work/Caller.cs", 12, 20)];
        List<int> kept = ReferencePlaces.WithoutImplementations(references, implementations);
        Assert.True(kept.SequenceEqual([1, 2, 4]),
            $"The references kept were {string.Join(", ", kept)}, not the declaration and the two calls (1, 2, 4), so Show Usages lists what implements a member among where it is used");
        Assert.True(ReferencePlaces.WithoutImplementations(references, []).SequenceEqual([0, 1, 2, 3, 4]), "References were left out where no implementation was given");
    }
}
