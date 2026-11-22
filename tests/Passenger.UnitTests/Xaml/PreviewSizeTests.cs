using Passenger.Xaml.Features;

namespace Passenger.UnitTests.Xaml;

/// <summary>The XAML an Avalonia previewer is sent to draw its root at a phone's or a tablet's size: the root's design
/// width and height given the size's where it writes them, else added after the root's name, every line where it was; a
/// sample's root (Styles, a ResourceDictionary) left as it is.</summary>
public sealed class PreviewSizeTests
{
    private static readonly PreviewSize Phone = PreviewSize.Offered[0];

    [Fact]
    public void A_previewer_is_sent_the_root_at_a_devices_size_every_line_where_it_was() => Cases.All(
        ("the roots design width and height take the sizes where the root writes them", The_roots_design_width_and_height_take_the_sizes_where_the_root_writes_them),
        ("a root with no design size takes one after its name and an avalonia design width is given in place", A_root_with_no_design_size_takes_one_after_its_name_and_an_avalonia_design_width_is_given_in_place),
        ("a samples root takes no size and is sent as it is", A_samples_root_takes_no_size_and_is_sent_as_it_is));

    private void The_roots_design_width_and_height_take_the_sizes_where_the_root_writes_them()
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

    private void A_root_with_no_design_size_takes_one_after_its_name_and_an_avalonia_design_width_is_given_in_place()
    {
        const string View = "<Grid xmlns=\"https://github.com/avaloniaui\" Design.Width=\"300\">\n  <Button />\n</Grid>";
        Assert.Equal("<Grid Design.Height=\"915\" xmlns=\"https://github.com/avaloniaui\" Design.Width=\"412\">\n  <Button />\n</Grid>", Phone.Sized(View));
        Assert.Equal("<Border Design.Width=\"1280\" Design.Height=\"800\" />", PreviewSize.Offered[3].Sized("<Border />"));
    }

    private void A_samples_root_takes_no_size_and_is_sent_as_it_is()
    {
        const string Styles = "<Styles xmlns=\"https://github.com/avaloniaui\"><Design.PreviewWith><Border /></Design.PreviewWith></Styles>";
        Assert.False(PreviewSize.Fits(Styles));
        Assert.Equal(Styles, Phone.Sized(Styles));
        Assert.True(PreviewSize.Fits("<UserControl />"));
        Assert.False(PreviewSize.Fits(string.Empty));
        Assert.Equal("Tablet, turned (1280 × 800)", PreviewSize.Offered[3].Shown);
    }
}
