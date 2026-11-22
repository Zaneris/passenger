using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What opens a page in a browser program a person names: the program with its arguments, the page's address last;
/// on macOS an app bundle through the system's open, its arguments after --args.</summary>
public sealed class BrowserProgramsTests
{
    private static readonly Uri Page = new("https://localhost:7043/orders?id=4 2");

    /// <summary>A browser program or app bundle opens the page after its arguments.</summary>
    [Fact]
    public void A_browser_program_opens_the_page_after_its_arguments() => Cases.All(
        (nameof(A_program_opens_the_page_given_its_arguments_with_the_pages_address_after_them),
            A_program_opens_the_page_given_its_arguments_with_the_pages_address_after_them),
        (nameof(An_app_bundle_opens_the_page_through_the_systems_open_with_its_arguments_after_args_and_none_without),
            An_app_bundle_opens_the_page_through_the_systems_open_with_its_arguments_after_args_and_none_without));

    private void A_program_opens_the_page_given_its_arguments_with_the_pages_address_after_them()
    {
        string chrome = Path.Combine("/opt", "google", "chrome", "chrome");
        (string program, IReadOnlyList<string> arguments) = BrowserPrograms.Opening(chrome, ["--incognito", "--user-data-dir=/tmp/dev profile"], Page, bundle: false);
        Assert.Equal(chrome, program);
        Assert.Equal(["--incognito", "--user-data-dir=/tmp/dev profile", "https://localhost:7043/orders?id=4%202"], arguments);
        Assert.Equal(["https://localhost:7043/orders?id=4%202"], BrowserPrograms.Opening(chrome, [], Page, bundle: false).Arguments);
    }

    private void An_app_bundle_opens_the_page_through_the_systems_open_with_its_arguments_after_args_and_none_without()
    {
        string firefox = Path.Combine("/Applications", "Firefox.app");
        (string program, IReadOnlyList<string> arguments) = BrowserPrograms.Opening(firefox, ["--private-window"], Page, bundle: true);
        Assert.Equal(BrowserPrograms.Open, program);
        Assert.Equal(["-a", firefox, "https://localhost:7043/orders?id=4%202", "--args", "--private-window"], arguments);
        Assert.Equal(["-a", firefox, "https://localhost:7043/orders?id=4%202"], BrowserPrograms.Opening(firefox, [], Page, bundle: true).Arguments);
    }
}
