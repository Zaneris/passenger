using System.Text.Json.Nodes;
using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>A theme source as a person writes it and settings keep it: a folder's full path, a GitHub repository by
/// <c>owner/name</c> or its address, an https address (http on this machine), each read where it should be and kept apart
/// from every other; and a theme chosen in 0.3 by name, moved to Passenger's themes repository.</summary>
public class ThemeSourceTests
{
    private static ThemeSource Source(string words) => ThemeSource.Of(words, out string? why) ?? throw new InvalidOperationException(why);

    private static string Refused(string words)
    {
        Assert.Null(ThemeSource.Of(words, out string? why));
        return why!;
    }

    [Fact]
    public void A_repository_is_owner_and_name_or_its_address_on_GitHub_read_through_its_raw_files_at_its_default_branch()
    {
        foreach (string words in (string[])["Zaneris/passenger-themes", " Zaneris/passenger-themes ", "https://github.com/Zaneris/passenger-themes", "https://github.com/Zaneris/passenger-themes.git", "https://github.com/Zaneris/passenger-themes/"])
        {
            Assert.Equal(ThemeSource.Repository, Source(words));
        }
        Assert.Equal("https://raw.githubusercontent.com/Zaneris/passenger-themes/HEAD/", ThemeSource.Repository.Place);
        Assert.Equal(ThemeSourceKind.GitHub, Source("a-b/c.d_e").Kind);
        Assert.Contains("no full path to a folder", Refused("-a/b"), StringComparison.Ordinal);
        Assert.Contains("no full path to a folder", Refused("a/.."), StringComparison.Ordinal);
        Assert.Contains("no full path to a folder", Refused("a/b/c"), StringComparison.Ordinal);
        Assert.Contains("no repository's own address on GitHub", Refused("https://github.com/Zaneris"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_is_https_or_http_on_this_machine_the_files_under_it_its_index_left_out()
    {
        Assert.Equal(new ThemeSource(ThemeSourceKind.Web, "https://example.org/themes/"), Source("https://example.org/themes"));
        Assert.Equal(new ThemeSource(ThemeSourceKind.Web, "https://example.org/themes/"), Source("https://example.org/themes/passenger-themes.json"));
        Assert.Equal("https://example.org/themes/", Source("https://example.org/themes/").Place);
        Assert.Equal(ThemeSourceKind.Web, Source("http://127.0.0.1:8080/t/").Kind);
        Assert.Contains("is not https", Refused("http://example.org/themes/"), StringComparison.Ordinal);
        Assert.Contains("carries a query", Refused("https://example.org/themes/?x=1"), StringComparison.Ordinal);
        Assert.Contains("carries a query", Refused("https://me:secret@example.org/themes/"), StringComparison.Ordinal);
        Assert.Contains("no http or https address", Refused("ftp://example.org/themes/"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_is_a_full_path_and_Passengers_own_two_sources_are_named_as_settings_keep_them()
    {
        string folder = OperatingSystem.IsWindows() ? @"C:\Themes\Mine" : "/themes/mine";
        Assert.Equal(new ThemeSource(ThemeSourceKind.Folder, folder), Source(folder + Path.DirectorySeparatorChar));
        Assert.Equal(folder, Source(folder).Place);
        Assert.Equal(ThemeSourceKind.Folder, Source("~/themes").Kind);
        Assert.Contains("names nothing", Refused("  "), StringComparison.Ordinal);
        Assert.Same(ThemeSource.BuiltIn, Source("built-in"));
        Assert.Same(ThemeSource.Added, Source("added"));
        Assert.Null(ThemeSource.Added.Place);
        Assert.Equal("Harbour (Zaneris/passenger-themes)", SourcedTheme.Repository("Harbour").Shown);
    }

    [Fact]
    public void Each_source_and_each_name_is_kept_in_a_folder_of_its_own_whose_name_a_file_system_takes()
    {
        ThemeSource[] all = [ThemeSource.Repository, Source("https://example.org/themes/"), Source("https://example.org/Themes/"), Source("zaneris/passenger-themes"),
            ThemeSource.Added, ThemeSource.BuiltIn, Source(OperatingSystem.IsWindows() ? @"C:\passenger-themes" : "/passenger-themes")];
        Assert.Equal(all.Length, all.Select(s => s.KeptFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.StartsWith("github-passenger-themes-", ThemeSource.Repository.KeptFolder, StringComparison.Ordinal);
        string[] names = ["Harbour", "harbour", "Dusk/Dawn", "..", "Ünïcode: ☃", new string('x', 64)];
        string[] kept = [.. names.Select(ThemeSource.KeptName)];
        Assert.Equal(names.Length, kept.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(kept.Concat(all.Select(s => s.KeptFolder)), name =>
        {
            Assert.True(name.Length <= 64 && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 && !name.StartsWith('.'), $"{name} is no folder's name every file system takes");
        });
    }

    [Fact]
    public void A_theme_and_a_code_colour_scheme_chosen_in_0_3_are_the_same_from_the_themes_repository()
    {
        JsonObject settings = new() { ["look"] = "harbour", ["colourScheme"] = "Rider", ["editorFontSize"] = 14 };
        ThemeSource.MoveFormerChoices(settings, "look", "colourScheme");
        Assert.Equal("""{"editorFontSize":14,"look":{"source":"Zaneris/passenger-themes","name":"Harbour"},"colourScheme":{"source":"Zaneris/passenger-themes","name":"Reef"}}""",
            settings.ToJsonString());

        JsonObject own = new() { ["look"] = "Passenger", ["colourScheme"] = "Theme" };
        ThemeSource.MoveFormerChoices(own, "look", "colourScheme");
        Assert.Equal("{}", own.ToJsonString());

        // A name no Passenger carried is none 0.3 kept, so it goes; a value already moved stays.
        JsonObject unknown = new() { ["look"] = "Mine", ["colourScheme"] = new JsonObject { ["source"] = "added", ["name"] = "Mine" } };
        ThemeSource.MoveFormerChoices(unknown, "look", "colourScheme");
        Assert.Equal("""{"colourScheme":{"source":"added","name":"Mine"}}""", unknown.ToJsonString());
    }
}
