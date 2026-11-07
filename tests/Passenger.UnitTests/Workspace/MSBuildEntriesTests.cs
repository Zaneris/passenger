using Passenger.App.MSBuild;
using Passenger.Text;

namespace Passenger.UnitTests.Workspace;

/// <summary>An entry of a list an MSBuild file's value holds, read from the text as typed: the semicolons between entries
/// are the list's, those closing XML's character references the XML's, and the entry's text is what the value says.</summary>
public class MSBuildEntriesTests
{
    private static (string Text, string Written) At(string value, int offset)
    {
        (string text, TextRange range) = MSBuildSyntax.EntryAt(value, 0, value.Length, offset);
        return (text, value.Substring(range.Start, range.Length));
    }

    [Fact]
    public void An_entry_holding_a_reference_is_one_entry_wherever_in_it_the_place_is()
    {
        const string Value = "a.txt;r&amp;d/notes.txt;b.txt";
        for (int at = Value.IndexOf('r', StringComparison.Ordinal); at <= Value.LastIndexOf(';'); at++)
        {
            Assert.Equal(("r&d/notes.txt", "r&amp;d/notes.txt"), At(Value, at));
        }
        Assert.Equal(("a.txt", "a.txt"), At(Value, 2));
        Assert.Equal(("b.txt", "b.txt"), At(Value, Value.Length));
    }

    [Theory]
    [InlineData("r&#38;d.txt", "r&d.txt")]
    [InlineData("r&#x26;d.txt", "r&d.txt")]
    [InlineData("&lt;r&gt;.txt", "<r>.txt")]
    public void An_entry_reads_every_reference_as_the_character_it_names(string written, string said)
    {
        Assert.Equal((said, written), At(written, written.Length));
    }

    [Fact]
    public void A_semicolon_after_an_ampersand_that_begins_no_reference_parts_two_entries()
    {
        Assert.Equal(("r&d", "r&d"), At("r&d;notes.txt", 1));
        Assert.Equal(("notes.txt", "notes.txt"), At("r&d;notes.txt", 6));
    }

    [Fact]
    public void A_semicolon_right_after_a_reference_parts_two_entries()
    {
        Assert.Equal(("r&", "r&amp;"), At("r&amp;;d", 1));
        Assert.Equal(("d", "d"), At("r&amp;;d", 8));
    }

    /// <summary>What an element of a project file is, read from its parents: the nearest that says, then down again, each
    /// level's from the one above; as deep as a tree is read, on a stack too small to walk it by recursing.</summary>
    [Fact]
    public void An_elements_role_comes_from_its_parents_however_deep()
    {
        Passenger.Xaml.Syntax.XmlTree tree = Passenger.Xaml.Syntax.XmlTree.Parse("<Project><PropertyGroup><Name /></PropertyGroup><ItemGroup><Compile><Link /></Compile></ItemGroup>"
            + "<Target><Exec><Output /><Other /></Exec><PropertyGroup /></Target><Choose><When><ItemGroup><Item><Meta><Deeper /></Meta></Item></ItemGroup></When></Choose></Project>");
        Assert.Equal(
            [MSBuildRole.Project, MSBuildRole.Structure, MSBuildRole.Property, MSBuildRole.Structure, MSBuildRole.Item, MSBuildRole.Metadata, MSBuildRole.Structure, MSBuildRole.Task,
                MSBuildRole.Structure, MSBuildRole.Unknown, MSBuildRole.Structure, MSBuildRole.Structure, MSBuildRole.Structure, MSBuildRole.Structure, MSBuildRole.Item,
                MSBuildRole.Metadata, MSBuildRole.Unknown],
            tree.Elements.Select(MSBuildSyntax.RoleOf));
        int deep = Passenger.Xaml.Syntax.XmlTree.DeepestNesting;
        Passenger.Xaml.Syntax.XmlTree nested = Passenger.Xaml.Syntax.XmlTree.Parse("<Project><Target>" + string.Concat(Enumerable.Repeat("<Task>", deep - 2))
            + string.Concat(Enumerable.Repeat("</Task>", deep - 2)) + "</Target></Project>");
        MSBuildRole last = MSBuildRole.Project;
        SmallStack.Run(64 * 1024, () => last = MSBuildSyntax.RoleOf(nested.Elements[^1]));
        Assert.Equal(MSBuildRole.Unknown, last);
    }
}
