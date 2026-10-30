using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>Where a launch profile opens the browser: its launch URL from its first application URL, a host the program
/// listens on everywhere opened as this machine's.</summary>
public class LaunchProfileTests
{
    [Theory]
    [InlineData("http://*:5000", "swagger", "http://localhost:5000/swagger")]
    [InlineData("http://+:5000", "swagger", "http://localhost:5000/swagger")]
    [InlineData("https://0.0.0.0:7001;http://0.0.0.0:5000", "swagger/index.html", "https://localhost:7001/swagger/index.html")]
    [InlineData("http://[::]:5000", "", "http://localhost:5000")]
    [InlineData("http://*:5000", "http://*:5000/health", "http://localhost:5000/health")]
    [InlineData("http://localhost:5000", "swagger", "http://localhost:5000/swagger")]
    [InlineData("https://example.test:8443", "https://other.test/page", "https://other.test/page")]
    [InlineData(null, "swagger", "swagger")]
    public void A_wildcard_host_opens_as_localhost_on_its_port_and_scheme(string? applicationUrl, string launchUrl, string opened)
    {
        LaunchProfile profile = new("web", LaunchProfile.ProjectCommand, null, null, null, new Dictionary<string, string>(), applicationUrl, true, launchUrl);
        Assert.True(profile.BrowserUrl() == opened,
            $"A profile serving {applicationUrl ?? "nothing"} with launchUrl '{launchUrl}' opens the browser at '{profile.BrowserUrl()}', not {opened}, so the page it names never shows");
    }
}
