using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>What Code Spyglass decides apart from the server and the window: its words, where a hint's name is, which places
/// are no use, and when numbers are read again.</summary>
public sealed class SpyglassReadingTests
{
    [Theory]
    [InlineData(0, "no usages")]
    [InlineData(1, "1 usage")]
    [InlineData(2, "2 usages")]
    public void A_count_of_usages_is_said_in_words(int count, string words) =>
        Assert.True(SpyglassReading.Usages(count) == words, $"{count} usages were said as \"{SpyglassReading.Usages(count)}\", not \"{words}\"");

    [Theory]
    [InlineData(Heritage.Implementations, 0, "no implementations")]
    [InlineData(Heritage.Implementations, 1, "1 implementation")]
    [InlineData(Heritage.Overrides, 0, "no overrides")]
    [InlineData(Heritage.Overrides, 1, "1 override")]
    [InlineData(Heritage.Overrides, 3, "3 overrides")]
    [InlineData(Heritage.Inheritors, 0, "no inheritors")]
    [InlineData(Heritage.Inheritors, 2, "2 inheritors")]
    public void What_inherits_is_said_by_what_the_declaration_is(Heritage heritage, int count, string words) =>
        Assert.True(SpyglassReading.Inheriting(heritage, count) == words, $"{count} of {heritage} were said as \"{SpyglassReading.Inheriting(heritage, count)}\", not \"{words}\"");

    [Fact]
    public void Two_places_are_one_when_they_are_the_same_place_of_the_same_document_whatever_the_uris_case()
    {
        SpyglassReading.Place declaration = new("file:///Work/Shapes.cs", 4, 11);
        Assert.True(SpyglassReading.IsSame(new("file:///work/shapes.cs", 4, 11), declaration), "The same place, its URI cased otherwise, was taken for another");
        Assert.False(SpyglassReading.IsSame(new("file:///Work/Shapes.cs", 4, 12), declaration), "A place a character on was taken for the same");
        Assert.False(SpyglassReading.IsSame(new("file:///Work/Shapes.cs", 5, 11), declaration), "A place a line on was taken for the same");
        Assert.False(SpyglassReading.IsSame(new("file:///Work/Other.cs", 4, 11), declaration), "The same place of another file was taken for the same");
    }

    [Fact]
    public void The_references_of_a_member_are_its_uses_less_its_own_declaration_and_those_of_what_implements_it()
    {
        SpyglassReading.Place declaration = new("file:///Work/IShape.cs", 3, 8);
        SpyglassReading.Place[] implementations = [.. Enumerable.Range(0, 18).Select(i => new SpyglassReading.Place($"file:///Work/Shape{i}.cs", 6, 15))];
        SpyglassReading.Place[] calls = [new("file:///Work/Caller.cs", 10, 20), new("file:///Work/Caller.cs", 12, 20), new("file:///Work/Other.cs", 6, 15)];
        SpyglassReading.Place[] references = [declaration, .. implementations, .. calls];
        List<int> uses = SpyglassReading.UsesAmong(references, declaration, implementations);
        Assert.True(uses.SequenceEqual([19, 20, 21]),
            $"The 22 references of an interface method (itself, 18 implementations and 3 calls) were read as {uses.Count} uses ({string.Join(", ", uses)}), not its 3 calls, "
            + "so a method shows its implementations as usages; the call at another file's place of an implementation's line and column is a use");
        Assert.True(SpyglassReading.UsesAmong(references, declaration, []).Count == references.Length - 1,
            "A declaration with no implementations given lost uses, or kept its own declaration among them");
        Assert.True(SpyglassReading.UsesAmong([], declaration, implementations).Count == 0, "No references were read as having uses");
    }

    [Fact]
    public void A_name_is_found_at_its_column_while_it_stands_there_else_as_the_first_whole_word_and_otherwise_nowhere()
    {
        TextSnapshot text = TextSnapshot.From("public Total Total => 1;\n    int MyTotal = Total;\n");
        // The second Total is the property's name; the type of the same name before it is not.
        Assert.True(SpyglassReading.NameOffset(text, 0, "Total", 13) == 13, "The name standing at its column was looked for elsewhere on the line");
        // Edited since: the column holds another word, and the first whole word of the line is taken, never one inside MyTotal.
        Assert.True(SpyglassReading.NameOffset(text, 1, "Total", 0) == text.GetLineStart(1) + "    int MyTotal = ".Length, "A word holding the name was taken for it, or the whole word was missed");
        Assert.True(SpyglassReading.NameOffset(text, 1, "Missing", 4) is null, "A name the line does not hold was found");
    }

    [Fact]
    public void A_name_on_a_line_that_is_gone_or_a_column_past_its_end_is_looked_for_without_failing()
    {
        TextSnapshot text = TextSnapshot.From("int x;\n");
        Assert.True(SpyglassReading.NameOffset(text, 9, "x", 4) is null && SpyglassReading.NameOffset(text, -1, "x", 4) is null, "A line past the text's was read");
        Assert.True(SpyglassReading.NameOffset(text, 0, "x", 400) == 4, "A column past the line's end stopped the search for the name by its word");
        Assert.True(SpyglassReading.NameOffset(text, 0, string.Empty, 0) is null, "An empty name was found");
    }

    [Fact]
    public void Numbers_never_read_or_of_a_kind_not_yet_read_are_read_and_those_read_stand_until_the_text_is_edited()
    {
        TimeSpan young = TimeSpan.FromSeconds(1);
        bool Needs(bool known, int? usages, int? inheritors, Heritage heritage, bool wantUsages = true, bool wantInheritors = true, int counted = 3, int version = 3, bool saved = false, TimeSpan? age = null) =>
            SpyglassReading.Needs(known, usages, inheritors, counted, heritage, wantUsages, wantInheritors, version, saved, age ?? young);
        Assert.True(Needs(false, null, null, Heritage.None), "A declaration with nothing read was not read");
        Assert.False(Needs(true, 2, null, Heritage.None), "Numbers read for the text as it stands were read again");
        Assert.True(Needs(true, null, null, Heritage.None), "A declaration read with its usages turned off was not read once they were on");
        Assert.False(Needs(true, null, null, Heritage.None, wantUsages: false), "Usages nobody shows were read");
        Assert.True(Needs(true, 2, null, Heritage.Overrides), "An override count turned on was not read");
        Assert.False(Needs(true, 2, null, Heritage.None), "Implementations of a declaration nothing can inherit from were read");
        Assert.False(Needs(true, 2, null, Heritage.Overrides, wantInheritors: false), "Implementations nobody shows were read");
    }

    [Fact]
    public void Numbers_of_a_text_since_edited_are_read_again_once_the_file_is_saved_or_they_have_grown_old_and_not_before()
    {
        bool Needs(bool saved, TimeSpan age) => SpyglassReading.Needs(true, 2, 1, 3, Heritage.Overrides, true, true, 4, saved, age);
        Assert.False(Needs(saved: false, TimeSpan.FromSeconds(1)), "Typing read the numbers again at once, a request of the server for each pause");
        Assert.True(Needs(saved: true, TimeSpan.FromSeconds(1)), "A save left the numbers of the text before it");
        Assert.True(Needs(saved: false, SpyglassReading.StaleAfter + TimeSpan.FromSeconds(1)), "Numbers older than the limit were kept through an edit");
        Assert.False(SpyglassReading.Needs(true, 2, 1, 4, Heritage.Overrides, true, true, 4, saved: true, SpyglassReading.StaleAfter * 9), "Numbers of the text as it stands were read again for being old");
    }
}
