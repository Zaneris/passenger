using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>XAML's markup read as written, every name and value where it stands, and a file mid-edit read as far as it goes
/// with each fault said, as completion and the diagnostics read it.</summary>
public class XmlTreeTests
{
    private const string Window = """
        <?xml version="1.0"?>
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="App.Main" Title='A &amp; B'>
          <!-- the layout -->
          <Grid>
            <Grid.RowDefinitions><RowDefinition Height="Auto" /></Grid.RowDefinitions>
            <TextBlock Grid.Row="1">Hello &lt;world&gt;</TextBlock>
            <TextBlock><![CDATA[raw <text>]]></TextBlock>
          </Grid>
        </Window>
        """;

    [Fact]
    public void A_well_formed_file_reads_as_its_elements_in_order_each_name_and_value_where_it_is_written()
    {
        XmlTree tree = XmlTree.Parse(Window);
        Assert.Empty(tree.Problems);
        Assert.Equal(["Window", "Grid", "Grid.RowDefinitions", "RowDefinition", "TextBlock", "TextBlock"], tree.Elements.Select(e => e.Name));
        XmlElement window = Assert.Single(tree.Roots);
        Assert.Equal("App.Main", window.Attribute("x:Class")!.Value);
        Assert.Equal("x", window.Attribute("x:Class")!.Prefix);
        XmlAttribute title = window.Attribute("Title")!;
        Assert.Equal(("A &amp; B", "A & B", '\''), (title.RawValue, title.Value, title.Quote));
        Assert.Equal(title.RawValue, Window[title.ValueStart..title.ValueEnd]);
        Assert.Equal("Title", Window[title.NameStart..title.NameEnd]);

        XmlElement rows = tree.Elements[2];
        Assert.True(rows.IsPropertyElement);
        Assert.Equal(tree.Elements[1], rows.Parent);
        Assert.True(tree.Elements[3].IsSelfClosing);
        XmlElement text = tree.Elements[4];
        Assert.Equal("Hello &lt;world&gt;", Window[text.Content.Start..text.Content.End]);
        Assert.Equal("Hello &lt;world&gt;", Window[text.Texts[0].Start..text.Texts[0].End]);
        Assert.True(tree.Elements[5].Texts[0].IsCdata);
        Assert.Equal("raw <text>", Window[tree.Elements[5].Texts[0].Start..tree.Elements[5].Texts[0].End]);
        Assert.Equal("<!-- the layout -->", Window[tree.Comments[0].Start..tree.Comments[0].End]);
        Assert.Equal("Window", Window[window.EndTagNameStart!.Value..window.EndTagNameEnd!.Value]);
        Assert.Equal(Window.Length, window.End);
    }

    /// <summary>A file as it is while it is typed: a value left without its closing quote ends with its line, a start tag left
    /// open is taken as empty so what follows stays its sibling, and an element left open ends at its parent's end tag.</summary>
    [Fact]
    public void A_file_mid_edit_is_read_as_far_as_it_goes_each_fault_said_where_it_is()
    {
        const string Text = "<Window>\n  <Button Content=\"Hi\n  <TextBlock Text=x>\n</Window>";
        XmlTree tree = XmlTree.Parse(Text);
        Assert.Equal(
            ["The value of 'Content' has no closing quote", "The start tag of 'Button' is not closed", "The value of 'Text' must be in quotes", "'x' has no value",
                "Element 'TextBlock' is not closed"],
            tree.Problems.Select(p => p.Message));
        XmlElement window = tree.Root!;
        Assert.True(window.IsClosed);
        Assert.Equal(["Button", "TextBlock"], window.Children.Select(c => c.Name));
        XmlAttribute content = window.Children[0].Attribute("Content")!;
        Assert.Equal("Hi", content.Value);
        Assert.False(content.IsValueClosed);
        Assert.Equal("Content", Text[tree.Problems[0].Range.Start..tree.Problems[0].Range.End]);
    }

    [Theory]
    [InlineData("<a></b></a>", "End tag 'b' has no start tag")]
    [InlineData("<a/><b/>", "A XAML file has one root element")]
    [InlineData("<a/> text", "Text is outside the root element")]
    [InlineData("<a><!-- open", "Comment is not closed")]
    [InlineData("<a><![CDATA[ open", "CDATA section is not closed")]
    [InlineData("<a x=\"1\" x=\"2\"/>", "'x' is set twice")]
    [InlineData("<a>< /a>", "An element's name is missing")]
    [InlineData("<a></a", "The end tag of 'a' is not closed")]
    [InlineData("<a b=\"1\"\"/>", "Unexpected '\"' in the tag of 'a'")]
    [InlineData("<a><b></a>", "Element 'b' is not closed")]
    public void Each_kind_of_broken_markup_is_said_in_a_persons_words(string text, string problem)
    {
        Assert.Contains(problem, XmlTree.Parse(text).Problems.Select(p => p.Message));
    }

    [Theory]
    [InlineData("<Bu", XmlPlaceKind.StartTagName)]
    [InlineData("<Button Con", XmlPlaceKind.AttributeName)]
    [InlineData("<Button Content=\"x", XmlPlaceKind.AttributeValue)]
    [InlineData("<Button Content=\"x\" ", XmlPlaceKind.InStartTag)]
    [InlineData("<Button Content=\"x\">te", XmlPlaceKind.Content)]
    [InlineData("<Button Content=\"x\">text</Button", XmlPlaceKind.EndTagName)]
    [InlineData("<Button Content=\"x\">text<!-- no", XmlPlaceKind.Comment)]
    public void A_caret_at_the_end_of_what_is_typed_is_in_the_part_of_the_markup_it_types(string typed, XmlPlaceKind kind)
    {
        Assert.Equal(kind, XmlTree.Parse(typed).PlaceAt(typed.Length).Kind);
    }

    [Fact]
    public void The_element_at_a_place_is_the_innermost_holding_it_up_to_the_end_of_its_end_tag()
    {
        const string Text = "<a><b>x</b> <c/></a>";
        XmlTree tree = XmlTree.Parse(Text);
        Assert.Equal("b", tree.ElementAt(Text.IndexOf('x'))!.Name);
        Assert.Equal("b", tree.ElementAt(Text.IndexOf("</b>", StringComparison.Ordinal) + 1)!.Name);
        Assert.Equal("a", tree.ElementAt(Text.IndexOf("</b>", StringComparison.Ordinal) + 4)!.Name);
        Assert.Equal("c", tree.ElementAt(Text.IndexOf("<c", StringComparison.Ordinal) + 1)!.Name);
        Assert.Null(tree.ElementAt(Text.Length));
        Assert.Null(tree.ElementAt(0));
    }

    [Theory]
    [InlineData("&lt;&gt;&amp;&quot;&apos;", "<>&\"'")]
    [InlineData("&#65;&#x42;&#X43;", "ABC")]
    [InlineData("&#x1F600;", "\U0001F600")]
    [InlineData("&#xD83D;&#xDE00;", "&#xD83D;&#xDE00;")]
    [InlineData("&#0;&#x110000;", "&#0;&#x110000;")]
    [InlineData("a & b &unknown; &amp", "a & b &unknown; &amp")]
    public void Character_references_read_as_what_they_name_and_any_other_ampersand_as_written(string raw, string read)
    {
        Assert.Equal(read, XmlEntities.Decode(raw));
    }

    [Fact]
    public void A_value_written_into_a_file_escapes_what_would_end_it_and_reads_back_as_it_was()
    {
        const string Value = "a < b & \"c\" 'd'";
        Assert.Equal("a &lt; b &amp; &quot;c&quot; 'd'", XmlEntities.Encode(Value, '"'));
        Assert.Equal("a &lt; b &amp; \"c\" &apos;d&apos;", XmlEntities.Encode(Value, '\''));
        string text = $"<a v=\"{XmlEntities.Encode(Value, '"')}\" w='{XmlEntities.Encode(Value, '\'')}'/>";
        XmlElement a = XmlTree.Parse(text).Root!;
        Assert.Equal(Value, a.Attribute("v")!.Value);
        Assert.Equal(Value, a.Attribute("w")!.Value);
    }

    [Fact]
    public void A_comment_left_open_holds_every_place_to_the_end_of_the_text()
    {
        const string Text = "<a/><!-- open";
        XmlTree tree = XmlTree.Parse(Text);
        Assert.False(tree.InComment(4));
        Assert.True(tree.InComment(5));
        Assert.True(tree.InComment(Text.Length));
        Assert.False(XmlTree.Parse("<a/><!-- shut -->").InComment("<a/><!-- shut -->".Length));
        Assert.Equal(new TextRange(4, Text.Length - 4), tree.Comments[0]);
    }
}
