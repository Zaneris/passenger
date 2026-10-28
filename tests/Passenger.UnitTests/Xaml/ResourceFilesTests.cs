using Passenger.Xaml.Localization;

namespace Passenger.UnitTests.Xaml;

/// <summary>A resource's strings read from a .resx and from an XLIFF file's text, what each language lacks, and a key's value
/// written in as the file already writes its entries, every other character kept.</summary>
public class ResourceFilesTests
{
    private const string Resx = """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
          <data name="Greeting" xml:space="preserve">
            <value>Hello &amp; welcome</value>
            <comment>On the first page</comment>
          </data>
          <data name="Raw"><value><![CDATA[<b>bold</b>]]></value></data>
          <data name="Icon" type="System.Drawing.Bitmap, System.Drawing" mimetype="application/x-microsoft.net.object.bytearray.base64"><value>AAAA</value></data>
          <data name="Empty" />
        </root>
        """;

    private const string Xliff = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2">
          <file datatype="xml" source-language="en" target-language="fr" original="../Strings.resx">
            <body>
              <trans-unit id="Bye">
                <source>Goodbye</source>
                <target state="needs-review-translation">Au revoir</target>
                <note />
              </trans-unit>
              <trans-unit id="Greeting">
                <source>Hello &amp; welcome</source>
                <target state="new">Hello &amp; welcome</target>
                <note />
              </trans-unit>
              <trans-unit id="Raw">
                <source>&lt;b&gt;bold&lt;/b&gt;</source>
                <note />
              </trans-unit>
            </body>
          </file>
        </xliff>
        """;

    [Fact]
    public void A_resx_reads_as_its_string_entries_their_values_and_comments_read_as_written_and_files_and_images_left_out()
    {
        ResourceFile file = ResourceFiles.Parse("Strings.resx", string.Empty, ResourceFormat.Resx, Resx);
        Assert.Equal(["Greeting", "Raw", "Empty"], file.Entries.Select(e => e.Name));
        ResourceEntry greeting = file.Entry("Greeting")!;
        Assert.Equal("Hello & welcome", greeting.Value);
        Assert.Equal("On the first page", greeting.Comment);
        Assert.Equal("Greeting", Resx[greeting.NameStart..greeting.NameEnd]);
        Assert.StartsWith("<data name=\"Greeting\"", Resx[greeting.Start..], StringComparison.Ordinal);
        Assert.EndsWith("</data>", Resx[..greeting.End], StringComparison.Ordinal);
        Assert.Equal("<b>bold</b>", file.Entry("Raw")!.Value);
        Assert.Equal(string.Empty, file.Entry("Empty")!.Value);
        Assert.True(file.Entry("Empty")!.Translated);
        Assert.Null(file.Entry("greeting"));
    }

    [Fact]
    public void An_XLIFF_file_reads_as_its_units_a_target_still_new_or_missing_being_no_translation()
    {
        ResourceFile file = ResourceFiles.Parse("xlf/Strings.fr.xlf", "fr", ResourceFormat.Xliff, Xliff);
        Assert.Equal(["Bye", "Greeting", "Raw"], file.Entries.Select(e => e.Name));
        Assert.Equal(("Au revoir", "needs-review-translation", true), (file.Entry("Bye")!.Value, file.Entry("Bye")!.State, file.Entry("Bye")!.Translated));
        Assert.False(file.Entry("Greeting")!.Translated);
        Assert.Null(file.Entry("Raw")!.Value);
        Assert.False(file.Entry("Raw")!.Translated);
    }

    /// <summary>German kept in a satellite .resx, French in XLIFF: a unit still new, or with no target, is there untranslated, and one
    /// that wants review is translated.</summary>
    [Fact]
    public void A_language_lacking_a_key_is_named_with_the_file_to_give_it_in_and_whether_it_is_there_untranslated()
    {
        ResourceSet set = new("Strings",
        [
            ResourceFiles.Parse("Strings.resx", string.Empty, ResourceFormat.Resx, Resx),
            ResourceFiles.Parse("Strings.de.resx", "de", ResourceFormat.Resx, """<root><data name="Greeting"><value>Hallo</value></data></root>"""),
            ResourceFiles.Parse("xlf/Strings.fr.xlf", "fr", ResourceFormat.Xliff, Xliff),
        ]);
        Assert.Equal(["de", "fr"], set.Cultures);
        Assert.Equal(["Bye", "Empty", "Greeting", "Raw"], set.Keys);
        Assert.Equal(["fr: Strings.fr.xlf, untranslated"], set.Lacking("Greeting").Select(Said));
        Assert.Equal(["de: Strings.de.resx, missing"], set.Lacking("Bye").Select(Said));
        Assert.Equal(["de: Strings.de.resx, missing", "fr: Strings.fr.xlf, untranslated"], set.Lacking("Raw").Select(Said));
        Assert.Equal(["neutral: none (Strings.resx)", "de: none (Strings.de.resx)", "fr: Au revoir (needs review)"], set.Languages("Bye"));
        Assert.Equal("'Raw' is not translated to French yet (Strings.fr.xlf)", set.Lacking("Raw").Last().Message);
    }

    [Fact]
    public void A_key_written_into_a_resx_follows_its_last_entrys_layout_without_its_comment_and_is_refused_once_there()
    {
        const string Text = """
            <root>
              <data name="A" xml:space="preserve">
                <value>a</value>
                <comment>note</comment>
              </data>
            </root>
            """;
        string written = ResourceFiles.WithEntry(Text, "B <x>", "b & c")!;
        Assert.Equal("""
            <root>
              <data name="A" xml:space="preserve">
                <value>a</value>
                <comment>note</comment>
              </data>
              <data name="B &lt;x>" xml:space="preserve">
                <value>b &amp; c</value>
              </data>
            </root>
            """, written);
        Assert.Null(ResourceFiles.WithEntry(written, "B <x>", "again"));
        Assert.Null(ResourceFiles.WithEntry("<notroot />", "Key", "value"));
    }

    [Fact]
    public void A_key_written_into_a_resx_whose_last_entry_is_an_image_is_written_as_a_string()
    {
        string written = ResourceFiles.WithEntry(Resx, "Bye", "See you")!;
        Assert.Equal("See you", ResourceFiles.Parse("Strings.resx", string.Empty, ResourceFormat.Resx, written).Entry("Bye")?.Value);
    }

    [Fact]
    public void A_key_written_into_a_resx_of_one_line_entries_is_written_on_one_line_too()
    {
        const string Text = "<root>\r\n  <data name=\"A\"><value>a</value></data>\r\n</root>";
        Assert.Equal("<root>\r\n  <data name=\"A\"><value>a</value></data>\r\n  <data name=\"B\"><value>b &lt; c</value></data>\r\n</root>",
            ResourceFiles.WithEntry(Text, "B", "b < c"));
        Assert.Equal("<root>\n  <data name=\"K\" xml:space=\"preserve\">\n    <value>v</value>\n  </data>\n</root>", ResourceFiles.WithEntry("<root>\n</root>", "K", "v"));
    }

    [Fact]
    public void A_translation_written_into_an_XLIFF_file_fills_its_unit_or_makes_one_in_order_of_the_ids()
    {
        string filled = ResourceFiles.WithTarget(Xliff, "Greeting", "Bonjour", "Hello & welcome", null)!;
        Assert.Contains("<target state=\"translated\">Bonjour</target>", filled, StringComparison.Ordinal);
        Assert.Equal(Xliff.Length + "Bonjour".Length - "Hello &amp; welcome".Length + "translated".Length - "new".Length, filled.Length);

        string targeted = ResourceFiles.WithTarget(Xliff, "Raw", "<b>gras</b>", "<b>bold</b>", null)!;
        Assert.Equal("<b>gras</b>", ResourceFiles.Parse("x.fr.xlf", "fr", ResourceFormat.Xliff, targeted).Entry("Raw")!.Value);

        string made = ResourceFiles.WithTarget(Xliff, "Cancel", "Annuler", "Cancel", "A button")!;
        ResourceFile file = ResourceFiles.Parse("x.fr.xlf", "fr", ResourceFormat.Xliff, made);
        Assert.Equal(["Bye", "Cancel", "Greeting", "Raw"], file.Entries.Select(e => e.Name));
        Assert.True(file.Entry("Cancel")!.Translated);
        Assert.Contains("<note>A button</note>", made, StringComparison.Ordinal);
        Assert.Null(ResourceFiles.WithTarget("<xliff />", "Cancel", "Annuler", "Cancel", null));
    }

    [Theory]
    [InlineData("Strings.fr", "fr")]
    [InlineData("Strings.zh-Hans", "zh-Hans")]
    [InlineData("Strings.pt-BR", "pt-BR")]
    [InlineData("Strings", "")]
    [InlineData("Strings.Designer", "")]
    [InlineData("Strings.notaculture", "")]
    public void A_culture_is_read_from_a_files_name_only_where_dotnet_knows_it(string stem, string culture)
    {
        Assert.Equal(culture, ResourceFiles.CultureOf(stem));
    }

    private static string Said(ResourceGap gap) => $"{gap.Culture}: {gap.File.Name}, {(gap.Untranslated ? "untranslated" : "missing")}";
}
