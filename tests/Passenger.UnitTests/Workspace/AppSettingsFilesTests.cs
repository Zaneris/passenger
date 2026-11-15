using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>An app's settings files by their names alone: which files are settings, the environment each is of, and those an
/// app reads in its environments, the base first.</summary>
public sealed class AppSettingsFilesTests
{
    [Theory]
    [InlineData("appsettings.json", true, null)]
    [InlineData("AppSettings.JSON", true, null)]
    [InlineData("/shop/appsettings.Development.json", true, "Development")]
    [InlineData("appsettings.Development.local.json", true, "Development.local")]
    [InlineData("appsettings-old.json", false, null)]
    [InlineData("appsettingsOld.json", false, null)]
    [InlineData("appsettings..json", false, null)]
    [InlineData("appsettings.json.bak", false, null)]
    [InlineData("launchSettings.json", false, null)]
    public void A_settings_file_is_appsettings_json_or_an_environments_and_names_its_environment(string path, bool settings, string? environment)
    {
        Assert.Equal(settings, AppSettingsFiles.Is(path));
        Assert.Equal(environment, AppSettingsFiles.EnvironmentOf(path));
    }

    [Fact]
    public void An_app_reads_the_base_settings_then_each_environments()
    {
        Assert.Equal(["appsettings.json", "appsettings.Development.json", "appsettings.Staging.json"], AppSettingsFiles.Read(["Development", "Staging"]));
        Assert.Equal(["appsettings.json"], AppSettingsFiles.Read([]));
        Assert.Equal("appsettings.Production.json", AppSettingsFiles.Of("Production"));
    }
}
