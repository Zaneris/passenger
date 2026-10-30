using Passenger.Text;
using Passenger.Xaml.Localization;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Text;

/// <summary>Every reader of XML's values decodes them one way, and every writer escapes them one way: a XAML attribute, a
/// .resx value and a project's item read alike, and what is written reads back as it was.</summary>
public sealed class XmlEntitiesTests
{
    [Fact]
    public void A_value_with_a_reference_naming_no_character_reads_the_same_in_every_reader()
    {
        const string Raw = "Tom &amp; Jerry &bogus; &#x41;";
        const string Read = "Tom & Jerry &bogus; A";
        string xaml = XmlTree.Parse($"<a v=\"{Raw}\"/>").Root!.Attribute("v")!.Value;
        string? resx = ResourceFiles.Parse("Strings.resx", string.Empty, ResourceFormat.Resx,
            $"<root><data name=\"Key\"><value>{Raw}</value></data></root>").Entry("Key")!.Value;
        Assert.True(xaml == Read, $"A XAML attribute read \"{Raw}\" as \"{xaml}\": the references around the one naming nothing went unread");
        Assert.True(resx == Read, $"A .resx value read \"{Raw}\" as \"{resx}\": the references around the one naming nothing went unread");
        Assert.True(XmlEntities.DecodeText(Raw) == Read,
            $"An element's text in a project read \"{Raw}\" as \"{XmlEntities.DecodeText(Raw)}\", unlike the XAML and .resx readers");
    }

    [Fact]
    public void An_elements_text_reads_its_cdata_sections_as_they_stand_and_decodes_the_rest()
    {
        Assert.Equal("<b> & <i>", XmlEntities.DecodeText("<![CDATA[<b>]]> &amp; <![CDATA[<i>]]>"));
        Assert.Equal("a &amp; b", XmlEntities.DecodeText("<![CDATA[a &amp; b]]>"));
        Assert.Equal("open &amp;", XmlEntities.DecodeText("<![CDATA[open &amp;"));
    }

    [Fact]
    public void A_value_written_as_an_elements_text_escapes_what_would_end_it_and_reads_back_as_it_was()
    {
        const string Value = "a < b & c > \"d\" 'e' ]]>";
        string written = XmlEntities.Encode(Value);
        Assert.Equal("a &lt; b &amp; c &gt; \"d\" 'e' ]]&gt;", written);
        Assert.Equal(Value, XmlEntities.DecodeText(written));
        Assert.Equal(Value, System.Xml.Linq.XElement.Parse($"<v>{written}</v>").Value);
    }
}
