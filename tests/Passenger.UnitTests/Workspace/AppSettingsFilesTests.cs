using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>An app's settings files by their names alone: which files are settings, the environment each is of, and those an
/// app reads in its environments, the base first.</summary>
public sealed class AppSettingsFilesTests
{
    /// <summary>An app's settings files are its appsettings.json and its environments', read base first.</summary>
    [Fact]
    public void An_apps_settings_files_are_named_and_read_as_the_app_reads_them() => Cases.All(
        (nameof(A_settings_file_is_appsettings_json_or_an_environments_and_names_its_environment),
            A_settings_file_is_appsettings_json_or_an_environments_and_names_its_environment),
        (nameof(An_app_reads_the_base_settings_then_each_environments), An_app_reads_the_base_settings_then_each_environments));

    private void A_settings_file_is_appsettings_json_or_an_environments_and_names_its_environment() => Cases.Each<(string Path, bool Settings, string? Environment)>(
    [
        ("appsettings.json", true, null),
        ("/shop/appsettings.Development.json", true, "Development"),
        ("appsettings.Development.local.json", true, "Development.local"),
        ("appsettings-old.json", false, null),
        ("appsettings.json.bak", false, null),
        ("launchSettings.json", false, null),
    ], row => A_settings_file_is_appsettings_json_or_an_environments_and_names_its_environment(row.Path, row.Settings, row.Environment));

    private void A_settings_file_is_appsettings_json_or_an_environments_and_names_its_environment(string path, bool settings, string? environment)
    {
        Assert.Equal(settings, AppSettingsFiles.Is(path));
        Assert.Equal(environment, AppSettingsFiles.EnvironmentOf(path));
    }

    private void An_app_reads_the_base_settings_then_each_environments()
    {
        Assert.Equal(["appsettings.json", "appsettings.Development.json", "appsettings.Staging.json"], AppSettingsFiles.Read(["Development", "Staging"]));
        Assert.Equal(["appsettings.json"], AppSettingsFiles.Read([]));
        Assert.Equal("appsettings.Production.json", AppSettingsFiles.Of("Production"));
    }
}
