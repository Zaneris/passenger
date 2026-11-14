using Passenger.Xaml.Features;

namespace Passenger.UnitTests.Xaml;

/// <summary>The XAML an Avalonia previewer is sent to draw its root at a phone's or a tablet's size: the root's design width
/// and height given the size's where it writes them, by the designer's prefix whatever it is named, else added after the
/// root's name, every line where it was; a sample's root (Styles, a ResourceDictionary) left as it is.</summary>
public sealed class PreviewSizeTests
{
    private static readonly PreviewSize Phone = PreviewSize.Offered[0];

    [Fact]
    public void The_roots_design_width_and_height_take_the_sizes_where_the_root_writes_them()
    {
        const string View = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:design="http://schemas.microsoft.com/expression/blend/2008"
                         design:DesignWidth="800" design:DesignHeight='450'>
              <TextBlock Text="800" />
            </UserControl>
            """;
        string sized = Phone.Sized(View);
        Assert.Equal(View.Replace("design:DesignWidth=\"800\" design:DesignHeight='450'", "design:DesignWidth=\"412\" design:DesignHeight='915'", StringComparison.Ordinal), sized);
        Assert.Equal(View.Split('\n').Length, sized.Split('\n').Length);
    }

    [Fact]
    public void A_root_with_no_design_size_takes_one_after_its_name_and_an_avalonia_design_width_is_given_in_place()
    {
        const string View = "<Grid xmlns=\"https://github.com/avaloniaui\" Design.Width=\"300\">\n  <Button />\n</Grid>";
        Assert.Equal("<Grid Design.Height=\"915\" xmlns=\"https://github.com/avaloniaui\" Design.Width=\"412\">\n  <Button />\n</Grid>", Phone.Sized(View));
        Assert.Equal("<Border Design.Width=\"1280\" Design.Height=\"800\" />", PreviewSize.Offered[3].Sized("<Border />"));
    }

    [Fact]
    public void A_design_size_under_a_prefix_of_another_namespace_is_not_the_designers()
    {
        const string View = "<Panel xmlns=\"https://github.com/avaloniaui\" xmlns:d=\"using:Mine\" d:DesignWidth=\"5\" />";
        Assert.Equal("<Panel Design.Width=\"412\" Design.Height=\"915\" xmlns=\"https://github.com/avaloniaui\" xmlns:d=\"using:Mine\" d:DesignWidth=\"5\" />", Phone.Sized(View));
    }

    [Fact]
    public void A_samples_root_takes_no_size_and_is_sent_as_it_is()
    {
        const string Styles = "<Styles xmlns=\"https://github.com/avaloniaui\"><Design.PreviewWith><Border /></Design.PreviewWith></Styles>";
        Assert.False(PreviewSize.Fits(Styles));
        Assert.Equal(Styles, Phone.Sized(Styles));
        Assert.True(PreviewSize.Fits("<UserControl />"));
        Assert.False(PreviewSize.Fits(string.Empty));
        Assert.Equal("Tablet, turned (1280 × 800)", PreviewSize.Offered[3].Shown);
    }
}
