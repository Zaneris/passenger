using Passenger.App.Templates;

namespace Passenger.UnitTests.Templates;

/// <summary>What New Solution says under a format list of .sln alone, true for the SDK read (#1368).</summary>
public sealed class SolutionFormatNoteTests
{
    [Fact]
    public void An_SDK_offering_no_slnx_is_said_to_and_one_offering_it_says_nothing() => Cases.All(
        ("an sdk before 9 0 200 is said to make no slnx", () => Cases.Each<string>(
        [
            "8.0.404",
        ], An_sdk_before_9_0_200_is_said_to_make_no_slnx)),
        ("a later sdk offering sln alone is said to offer no slnx here never to make none", () => Cases.Each<string>(
        [
            "9.0.200",
        ], A_later_sdk_offering_sln_alone_is_said_to_offer_no_slnx_here_never_to_make_none)),
        ("a list offering slnx says nothing", A_list_offering_slnx_says_nothing));

    private void An_sdk_before_9_0_200_is_said_to_make_no_slnx(string sdk)
    {
        Assert.Equal($"SDK {sdk} makes no .slnx: SDK 9.0.200 and later do", SolutionFormatNote.Of(sdk, [".sln"]));
    }

    private void A_later_sdk_offering_sln_alone_is_said_to_offer_no_slnx_here_never_to_make_none(string sdk)
    {
        string said = SolutionFormatNote.Of(sdk, [".sln"]);

        Assert.True(said == $"dotnet new sln of SDK {sdk} offers no .slnx here: its solution template takes no format",
            $"An SDK that makes .slnx would be said to make none, sending the person to an SDK they have: {said}");
    }

    private void A_list_offering_slnx_says_nothing()
    {
        Assert.Equal(string.Empty, SolutionFormatNote.Of("10.0.100", [".slnx", ".sln"]));
    }
}
