using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>Where a launch profile opens the browser: its launch URL from its first application URL, a host the program
/// listens on everywhere opened as this machine's; a settings file that is no JSON object gives no profiles.</summary>
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

    [Theory]
    [InlineData("[]")]
    [InlineData("[{ \"profiles\": {} }]")]
    [InlineData("\"profiles\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void A_settings_file_that_is_no_object_lists_no_profiles(string text)
    {
        IReadOnlyList<LaunchProfile> profiles = LaunchProfile.Parse(text);
        Assert.True(profiles.Count == 0, $"A launchSettings.json of {text} listed {profiles.Count} profiles, where Run Configurations, Run and an agent's workspace read none");
    }

    [Fact]
    public void A_profiles_values_of_another_kind_are_passed_over_and_a_port_is_read_from_its_digits_too()
    {
        const string Text = """
            {
              "profiles": {
                "web": {
                  "commandName": 5, "launchBrowser": "true", "applicationUrl": "http://localhost:5000", "httpPort": "8080", "sslPort": 70000,
                  "environmentVariables": ["A=1"], "composeProfile": "base", "useSSL": true
                },
                "worker": "Project"
              }
            }
            """;
        IReadOnlyList<LaunchProfile> profiles = LaunchProfile.Parse(Text);
        Assert.True(profiles is [{ Name: "web" }], $"A launchSettings.json with one profile and a string beside it listed {profiles.Count}, where Run Configurations list one");
        LaunchProfile web = profiles[0];
        Assert.True(web.Command is null && !web.LaunchBrowser && web.EnvironmentVariables.Count == 0 && web.ComposeProfiles.Count == 0,
            "A value written as another kind than dotnet run reads was taken as one, so the profile would run as it does nowhere else");
        Assert.True(web is { ApplicationUrl: "http://localhost:5000", UseSsl: true, HttpPort: 8080, SslPort: null },
            $"The profile's own values were lost beside the others (port {web.HttpPort}, SSL port {web.SslPort}), so its container would publish other ports");
    }
}
