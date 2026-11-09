using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
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

    private static string Nested(int deep, string open = "<a>", string close = "</a>") =>
        string.Concat(Enumerable.Repeat(open, deep)) + string.Concat(Enumerable.Repeat(close, deep));

    [Theory]
    [InlineData("<a>", "</a>")]
    [InlineData("<a.b>", "</a.b>")]
    [InlineData("<a DataContext=\"{Binding}\">", "</a>")]
    [InlineData("<Style Selector=\"^:a\">", "</Style>")]
    public void Elements_nested_past_what_a_tree_holds_read_as_far_as_it_holds_and_end_nothing(string open, string close)
    {
        string text = Nested(100_000, open, close);
        SmallStack.Run(() =>
        {
            foreach (XamlDialect dialect in (XamlDialect[])[AvaloniaDialect.Instance, WpfDialect.Instance])
            {
                XamlFile file = XamlFiles.Read("Deep.axaml", text, dialect);
                XmlTree tree = file.Tree;
                Assert.Equal(XmlTree.DeepestNesting, tree.Elements.Count);
                Assert.Equal(XmlTree.DeepestNesting, tree.Root!.DescendantsAndSelf().Count());
                int stopped = open.Length * XmlTree.DeepestNesting;
                Assert.Equal(TextRange.FromBounds(stopped, stopped + open.IndexOfAny([' ', '>'])), tree.Stopped);
                XmlElement last = tree.Elements[^1];
                Assert.Null(file.ObjectOf(last));
                Assert.Null(file.DataTypeAt(last));
                Assert.Null(file.StyleTargetAt(last));
            }
        });
    }

    [Fact]
    public void Where_reading_stopped_is_a_note_and_nothing_past_it_is_called_wrong()
    {
        XamlFile file = XamlFiles.Read("Deep.axaml", Nested(100_000), AvaloniaDialect.Instance);
        Assert.Empty(file.Tree.Problems);
        XamlDiagnostic note = Assert.Single(XamlDiagnostics.Of(file));
        Assert.Equal((XamlSeverity.Note, XmlTree.TooDeep, file.Tree.Stopped), (note.Severity, note.Message, (TextRange?)note.Range));
    }

    [Fact]
    public void A_fault_before_reading_stopped_stands_beside_its_note()
    {
        XamlFile file = XamlFiles.Read("Deep.axaml", "<b c>" + Nested(100_000), AvaloniaDialect.Instance);
        Assert.Equal([XamlSeverity.Error, XamlSeverity.Note], XamlDiagnostics.Of(file).Select(d => d.Severity));
    }

    [Fact]
    public void An_editorconfigs_max_line_length_off_with_a_comment_after_it_turns_xaml_wrapping_off()
    {
        string text = $"<Grid xmlns=\"https://github.com/avaloniaui\" Tag=\"{new string('t', 60)}\" Name=\"{new string('n', 60)}\" />";
        XamlFormatSettings off = new XamlFormatSettings().Over(key => key == "max_line_length" ? "off  # no wrap" : null);

        Assert.True(off.RightMargin == 0, $"max_line_length = off  # no wrap left XAML's right margin at {off.RightMargin} columns");
        Assert.Contains(XamlFormatter.Format(text, new XamlFormatSettings(), new IndentOptions(), "\n").Edits, e => e.Text.Contains('\n'));
        Assert.DoesNotContain(XamlFormatter.Format(text, off, new IndentOptions(), "\n").Edits, e => e.Text.Contains('\n'));
        XamlFormatSettings read = new XamlFormatSettings().Over(key => key switch
        {
            "xml_space_before_self_closing" => "false # tight",
            "xml_max_blank_lines_between_tags" => "1;one",
            "xml_attribute_style" => "on_different_lines  # one each",
            _ => null,
        });
        Assert.Equal(new XamlFormatSettings { SpaceBeforeSelfClosing = false, MaxBlankLines = 1, AttributeStyle = XamlAttributeStyle.OnDifferentLines }, read);
    }

    [Fact]
    public void A_file_read_in_part_is_neither_laid_out_nor_renamed_in()
    {
        string text = Nested(100_000);
        Assert.NotNull(XamlFormatter.Format(text, new XamlFormatSettings(), new IndentOptions(), "\n").Problem);
        XamlFile file = XamlFiles.Read("Deep.axaml", text, AvaloniaDialect.Instance);
        Assert.NotNull(XamlRefactorings.ElementNameProblem(file, "a", "b"));
    }

    /// <summary>How much stack a level of layout takes is the platform's: on a stack a third of the thread pool's a tree as
    /// deep as it is read is laid out, or refused as too deep where that stack runs short, and never ends the process.</summary>
    [Fact]
    public void Elements_as_deep_as_a_tree_holds_read_whole_and_are_laid_out_or_refused_where_the_stack_runs_short()
    {
        string text = Nested(XmlTree.DeepestNesting);
        SmallStack.Run(() =>
        {
            XmlTree tree = XmlTree.Parse(text);
            Assert.Null(tree.Stopped);
            Assert.Empty(tree.Problems);
            Assert.Equal(XmlTree.DeepestNesting, tree.Elements.Count);
            XamlFormatted laid = XamlFormatter.Format(text, new XamlFormatSettings(), new IndentOptions(), "\n");
            Assert.True(laid.Problem is null ? laid.Edits.Count > 0 : laid.Problem == XamlFormatter.TooDeep, $"The layout came to {laid.Problem ?? "no edits"}");
        });
    }

    /// <summary>A stack too small for a tree as deep as it is read, as a platform whose levels take more would leave: the
    /// layout is refused saying so, and every other walk of the tree answers as it does past where reading stops, or walks
    /// with a stack of its own.</summary>
    [Theory]
    [InlineData("<a>", "</a>")]
    [InlineData("<a.b>", "</a.b>")]
    [InlineData("<a DataContext=\"{Binding}\">", "</a>")]
    [InlineData("<Style Selector=\"^:a\">", "</Style>")]
    public void Walks_of_a_tree_on_a_stack_too_small_for_it_refuse_or_answer_as_past_the_bound_and_end_nothing(string open, string close)
    {
        string text = Nested(XmlTree.DeepestNesting, open, close);
        SmallStack.Run(192 * 1024, () =>
        {
            Assert.Equal(XamlFormatter.TooDeep, XamlFormatter.Format(text, new XamlFormatSettings(), new IndentOptions(), "\n").Problem);
            foreach (XamlDialect dialect in (XamlDialect[])[AvaloniaDialect.Instance, WpfDialect.Instance])
            {
                XamlFile file = XamlFiles.Read("Deep.axaml", text, dialect);
                Assert.Equal(XmlTree.DeepestNesting, file.Tree.Root!.DescendantsAndSelf().Count());
                XmlElement last = file.Tree.Elements[^1];
                Assert.Null(file.ObjectOf(last));
                Assert.Null(file.DataTypeAt(last));
                Assert.Null(file.StyleTargetAt(last));
                Assert.DoesNotContain(XamlDiagnostics.Of(file), d => d.Severity == XamlSeverity.Error);
            }
        });
    }
}
