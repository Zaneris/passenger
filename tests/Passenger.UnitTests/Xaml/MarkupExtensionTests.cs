using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>A markup extension in an attribute's value read with its arguments, those nested in it included, each where it
/// stands in the file, and read as far as it goes while it is typed.</summary>
public class MarkupExtensionTests
{
    [Fact]
    public void A_binding_reads_as_its_named_and_positional_arguments_and_the_extension_nested_in_one()
    {
        const string Text = "<TextBlock Text=\"{Binding Name, Mode=TwoWay, Converter={StaticResource Upper}}\" />";
        int start = Text.IndexOf('{');
        MarkupExtension binding = MarkupExtension.Parse(Text, start, Text.LastIndexOf('"'))!;
        Assert.Equal("Binding", binding.Name);
        Assert.True(binding.IsClosed);
        Assert.Equal(Text.LastIndexOf('"'), binding.End);
        Assert.Equal("Name", binding.Argument(null)!.Value);
        Assert.Equal(0, binding.Argument(null)!.Index);
        Assert.Equal("TwoWay", binding.Argument("Mode")!.Value);
        Assert.Equal("Mode", Text[binding.Argument("Mode")!.KeyStart..binding.Argument("Mode")!.KeyEnd]);
        MarkupExtension nested = binding.Argument("Converter")!.Nested!;
        Assert.Equal("StaticResource", nested.Name);
        Assert.Equal("Upper", nested.Argument(null)!.Value);
        Assert.Equal("Upper", Text[nested.Argument(null)!.ValueStart..nested.Argument(null)!.ValueEnd]);
        Assert.Same(nested, binding.InnermostAt(Text.IndexOf("Upper", StringComparison.Ordinal)));
        Assert.Same(binding, binding.InnermostAt(Text.IndexOf("TwoWay", StringComparison.Ordinal)));
        Assert.Equal("Mode", binding.ArgumentAt(Text.IndexOf("TwoWay", StringComparison.Ordinal))!.Key);
    }

    [Fact]
    public void A_prefixed_name_reads_as_its_prefix_and_local_name_and_its_value_whole()
    {
        const string Text = "{x:Static local:Strings.Title}";
        MarkupExtension extension = MarkupExtension.Parse(Text, 0, Text.Length)!;
        Assert.Equal(("x", "Static"), (extension.Prefix, extension.LocalName));
        Assert.Equal("local:Strings.Title", Assert.Single(extension.Arguments).Value);
    }

    [Fact]
    public void A_quoted_and_an_escaped_value_read_as_the_text_they_hold()
    {
        const string Quoted = "{Binding StringFormat='{}{0:N2}, {1}'}";
        Assert.Equal("{}{0:N2}, {1}", MarkupExtension.Parse(Quoted, 0, Quoted.Length)!.Argument("StringFormat")!.Value);
        const string Escaped = "{Binding Path=A\\,B}";
        Assert.Equal("A,B", MarkupExtension.Parse(Escaped, 0, Escaped.Length)!.Argument("Path")!.Value);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("{}{literal braces}")]
    [InlineData("   ")]
    public void A_value_that_is_no_extension_reads_as_none(string text)
    {
        Assert.Null(MarkupExtension.Parse(text, 0, text.Length));
    }

    [Fact]
    public void An_extension_still_being_typed_reads_as_far_as_it_goes()
    {
        const string Text = "{Binding Path=Na";
        MarkupExtension extension = MarkupExtension.Parse(Text, 0, Text.Length)!;
        Assert.False(extension.IsClosed);
        Assert.Equal(Text.Length, extension.End);
        Assert.Equal("Na", extension.Argument("Path")!.Value);
        const string Nested = "{Binding Converter={StaticRes";
        MarkupExtension outer = MarkupExtension.Parse(Nested, 0, Nested.Length)!;
        Assert.Equal("StaticRes", outer.InnermostAt(Nested.Length).Name);
    }

    /// <summary>A value of extensions nested far past any written by hand reads to a depth short of the stack, the rest
    /// held unread, so a file pasted with one never ends the IDE.</summary>
    [Fact]
    public void Extensions_nested_a_hundred_thousand_deep_read_to_a_depth_short_of_the_stack()
    {
        string text = string.Concat(Enumerable.Repeat("{A B=", 100_000)) + new string('}', 100_000);
        MarkupExtension? at = MarkupExtension.Parse(text, 0, text.Length);
        int depth = 0;
        while (at is not null)
        {
            depth++;
            at = at.Arguments.FirstOrDefault()?.Nested;
        }
        Assert.InRange(depth, 2, 100);
    }
}
