using Passenger.App.Containers;
using Passenger.Text;

namespace Passenger.UnitTests.Containers;

/// <summary>Which FROM a finding on a Dockerfile's base image stands for, where several stages name one image.</summary>
public sealed class DockerfileImagesTests
{
    private const string Image = "mcr.microsoft.com/dotnet/aspnet:8.0";

    private static readonly string Two = $"FROM {Image} AS first\nRUN true\n\nFROM {Image} AS second\nCOPY . .\n";

    /// <summary>The stretch of a line of the text, as a finding on a FROM marks it.</summary>
    private static TextRange Line(TextSnapshot text, int line) => new(text.GetLine(line).Start, text.GetLineText(line).Length);

    [Fact]
    public void A_finding_on_the_second_of_two_identical_FROMs_stands_for_the_second()
    {
        TextSnapshot text = TextSnapshot.From(Two);
        BaseImage? at = DockerfileImages.Marked(text, Line(text, 3), Image);
        Assert.True(at?.Line == 3, $"The fix of the second stage's FROM would edit line {at?.Line}, leaving that stage's image as it was");
    }

    [Fact]
    public void A_finding_stands_for_its_own_FROM_once_the_stage_before_it_has_been_changed()
    {
        TextSnapshot text = TextSnapshot.From($"FROM mcr.microsoft.com/dotnet/aspnet:8.0.11 AS first\nRUN true\n\nFROM {Image} AS second\nCOPY . .\n");
        Assert.Equal(3, DockerfileImages.Marked(text, Line(text, 3), Image)?.Line);
    }

    [Fact]
    public void A_finding_whose_line_now_names_another_image_stands_for_none()
    {
        TextSnapshot text = TextSnapshot.From($"FROM {Image} AS first\nRUN true\n\nFROM alpine:3.20 AS second\n");
        Assert.Null(DockerfileImages.Marked(text, Line(text, 3), Image));
    }

    [Fact]
    public void A_stretch_that_grew_past_its_line_still_finds_its_FROM()
    {
        TextSnapshot text = TextSnapshot.From($"RUN true\n\nFROM {Image}\n");
        Assert.Equal(2, DockerfileImages.Marked(text, new TextRange(text.GetLine(1).Start, text.Length - text.GetLine(1).Start), Image)?.Line);
    }

    [Fact]
    public void Scratch_a_stage_of_the_file_and_an_ARGs_image_are_no_base_images()
    {
        TextSnapshot text = TextSnapshot.From($"FROM {Image} AS base\nFROM base AS next\nFROM scratch\nFROM $VERSION\n");
        Assert.Equal([Image], DockerfileImages.Of(text).Select(b => b.Image));
    }
}
