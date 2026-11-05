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
    public void An_entrys_value_comment_and_name_are_written_where_the_entry_has_them_or_made_as_its_lines_are_every_other_character_kept()
    {
        const string Text = """
            <root>
              <data name="A" xml:space="preserve">
                <value>a</value>
                <comment>note</comment>
              </data>
              <data name='B'>
                <value />
              </data>
              <data name="C"><value>c</value></data>
            </root>
            """;
        IReadOnlyList<ResourceEntry> entries = Read(Text);
        Assert.Equal(Text.Replace("<value>a</value>", "<value>x &lt; y</value>", StringComparison.Ordinal), ResourceFiles.WithValue(Text, entries[0], "x < y"));
        Assert.Equal(Text.Replace("<value />", "<value>b</value>", StringComparison.Ordinal), ResourceFiles.WithValue(Text, entries[1], "b"));
        Assert.Equal(Text.Replace("<comment>note</comment>", "<comment>said &amp; done</comment>", StringComparison.Ordinal), ResourceFiles.WithComment(Text, entries[0], "said & done"));
        Assert.Equal(Text.Replace("<value />", "<value />\n    <comment>new</comment>", StringComparison.Ordinal), ResourceFiles.WithComment(Text, entries[1], "new"));
        Assert.Equal(Text.Replace("\n    <comment>note</comment>", "", StringComparison.Ordinal), ResourceFiles.WithComment(Text, entries[0], ""));
        Assert.Equal(Text.Replace("name='B'", "name='B &apos;2&apos;'", StringComparison.Ordinal), ResourceFiles.Renamed(Text, entries[1], "B '2'"));
        Assert.Null(ResourceFiles.Renamed(Text, entries[1], "C"));
        Assert.Equal("c", Read(ResourceFiles.WithComment(Text, entries[2], "one line")!)[2].Value);
        Assert.Equal("one line", Read(ResourceFiles.WithComment(Text, entries[2], "one line")!)[2].Comment);

        string changed = ResourceFiles.WithValue(Text, entries[2], "changed")!;
        Assert.Null(ResourceFiles.WithValue(changed, entries[0] with { Start = entries[0].Start + 1 }, "gone"));
    }

    [Fact]
    public void Clearing_a_comment_takes_the_white_space_before_it_and_keeps_an_xml_comment_beside_it()
    {
        const string Text = "<root>\n  <data name=\"A\"><value>a</value><!-- translator note --><comment>old</comment></data>\n"
            + "  <data name=\"B\">\n    <value>b</value>\n    <!-- kept -->\n    <comment>old</comment>\n  </data>\n</root>\n";
        IReadOnlyList<ResourceEntry> entries = Read(Text);
        Assert.Equal(Text.Replace("<comment>old</comment></data>", "</data>", StringComparison.Ordinal), ResourceFiles.WithComment(Text, entries[0], ""));
        Assert.Equal(Text.Replace("\n    <comment>old</comment>\n  </data>", "\n  </data>", StringComparison.Ordinal), ResourceFiles.WithComment(Text, entries[1], ""));
    }

    [Fact]
    public void Entries_taken_out_of_a_resx_take_their_lines_with_them_and_one_sharing_its_line_only_itself()
    {
        const string Text = "<root>\r\n  <data name=\"A\"><value>a</value></data>\r\n  <data name=\"B\">\r\n    <value>b</value>\r\n  </data>\r\n"
            + "  <data name=\"C\"><value>c</value></data><data name=\"D\"><value>d</value></data>\r\n</root>";
        IReadOnlyList<ResourceEntry> entries = Read(Text);
        Assert.Equal("<root>\r\n  <data name=\"C\"><value>c</value></data><data name=\"D\"><value>d</value></data>\r\n</root>",
            ResourceFiles.Without(Text, [entries[1], entries[0]]));
        Assert.Equal("<root>\r\n  <data name=\"A\"><value>a</value></data>\r\n  <data name=\"B\">\r\n    <value>b</value>\r\n  </data>\r\n"
            + "  <data name=\"C\"><value>c</value></data>\r\n</root>", ResourceFiles.Without(Text, [entries[3]]));
        Assert.Null(ResourceFiles.Without(Text, [entries[0] with { Name = "Z" }]));
    }

    [Fact]
    public void An_XLIFF_units_source_and_note_are_read_beside_its_target()
    {
        ResourceEntry bye = ResourceFiles.Parse("Strings.fr.xlf", "fr", ResourceFormat.Xliff,
            "<xliff><file><body><trans-unit id=\"Bye\"><source>Good&amp;bye</source><target state=\"translated\">Au revoir</target><note>Said last</note></trans-unit></body></file></xliff>")
            .Entries[0];
        Assert.Equal(("Good&bye", "Au revoir", "Said last"), (bye.Source, bye.Value, bye.Comment));
    }

    private static IReadOnlyList<ResourceEntry> Read(string text) => ResourceFiles.Parse("Strings.resx", string.Empty, ResourceFormat.Resx, text).Entries;

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
    [InlineData("Apple", new[] { "Apple", "Bye", "Greeting" })]
    [InlineData("Cancel", new[] { "Bye", "Cancel", "Greeting" })]
    [InlineData("Zed", new[] { "Bye", "Greeting", "Zed" })]
    public void A_unit_written_into_a_one_line_XLIFF_file_goes_inside_its_body_in_order_of_the_ids(string key, string[] ids)
    {
        const string Text = "<?xml version=\"1.0\" encoding=\"utf-8\"?><xliff xmlns=\"urn:oasis:names:tc:xliff:document:1.2\" version=\"1.2\">"
            + "<file datatype=\"xml\" source-language=\"en\" target-language=\"fr\" original=\"../Strings.resx\"><body>"
            + "<trans-unit id=\"Bye\"><source>Goodbye</source><target state=\"translated\">Au revoir</target><note /></trans-unit>"
            + "<trans-unit id=\"Greeting\"><source>Hello</source><target state=\"new\">Hello</target><note /></trans-unit>"
            + "</body></file></xliff>";
        string made = ResourceFiles.WithTarget(Text, key, "Fait", "Done", null)!;
        System.Xml.Linq.XDocument read;
        try
        {
            read = System.Xml.Linq.XDocument.Parse(made);
        }
        catch (System.Xml.XmlException error)
        {
            throw new Xunit.Sdk.XunitException($"Adding '{key}' to a one-line XLIFF file left it no longer XML ({error.Message}), so the build refuses it:\n{made}");
        }
        System.Xml.Linq.XNamespace xliff = "urn:oasis:names:tc:xliff:document:1.2";
        Assert.Equal(ids, read.Root!.Element(xliff + "file")!.Element(xliff + "body")!.Elements(xliff + "trans-unit").Select(u => (string?)u.Attribute("id")));
        Assert.True(ResourceFiles.Parse("x.fr.xlf", "fr", ResourceFormat.Xliff, made).Entry(key) is { Value: "Fait", Translated: true }, made);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?><xliff ", made, StringComparison.Ordinal);
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
