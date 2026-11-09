using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.UnitTests.Editor;

/// <summary>How the font collection Passenger names its families through keeps the platform's fallback answers: a script's
/// letters under one answer, characters common to scripts by their page, and families named so its own fallback is asked.</summary>
public class FallbackFontsTests
{
    /// <summary>A script's letters share one kept answer and each script has its own, so a word asks the platform once and
    /// Hebrew never takes Arabic's face.</summary>
    [Fact]
    public void A_scripts_letters_share_a_bucket_and_scripts_differ()
    {
        Assert.Equal(FallbackFonts.Bucket('ا'), FallbackFonts.Bucket('م'));
        Assert.Equal(FallbackFonts.Bucket('ש'), FallbackFonts.Bucket('ם'));
        Assert.Equal(FallbackFonts.Bucket('न'), FallbackFonts.Bucket('स'));
        Assert.Equal(FallbackFonts.Bucket('日'), FallbackFonts.Bucket('語'));
        int[] buckets = [FallbackFonts.Bucket('ا'), FallbackFonts.Bucket('ש'), FallbackFonts.Bucket('न'), FallbackFonts.Bucket('ก'), FallbackFonts.Bucket('日'), FallbackFonts.Bucket('の')];
        Assert.True(buckets.Distinct().Count() == buckets.Length, $"Two scripts share a bucket ({string.Join(", ", buckets)}), so one would be drawn in the other's face");
    }

    /// <summary>Characters common to scripts (symbols, emoji, a script's punctuation) are kept by their page of 256, apart
    /// from every script's letters, so an emoji's face never draws an arrow or Japanese punctuation.</summary>
    [Fact]
    public void Characters_common_to_scripts_are_kept_by_their_page()
    {
        Assert.Equal(FallbackFonts.Bucket(0x1F680), FallbackFonts.Bucket(0x1F600));
        Assert.NotEqual(FallbackFonts.Bucket(0x1F680), FallbackFonts.Bucket(0x2192));
        Assert.NotEqual(FallbackFonts.Bucket(0x3002), FallbackFonts.Bucket(0x2192));
        Assert.NotEqual(FallbackFonts.Bucket(0x3002), FallbackFonts.Bucket('の'));
        Assert.True(FallbackFonts.Bucket(0x2192) < 0 && FallbackFonts.Bucket('ا') >= 0, "A common character's page could be taken for a script's bucket");
    }

    /// <summary>A family named through the collection is found in it, so the collection's own fallback is asked for what it
    /// lacks; a name its form can't hold stays a plain family.</summary>
    [Fact]
    public void A_family_is_named_through_the_collection_unless_its_name_cannot_be()
    {
        FontFamily named = FallbackFonts.Of("Cascadia Code");
        Assert.Equal("Cascadia Code", named.Name);
        Assert.Equal("fonts", named.Key?.Source.Scheme);
        FontFamily plain = FallbackFonts.Of("Odd, Name");
        Assert.True(plain.Key is not { Source.Scheme: "fonts" }, "A name with a comma was named through the collection, which reads it as two families");
    }
}
