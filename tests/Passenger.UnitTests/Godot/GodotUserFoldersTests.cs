using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>A Godot's environment takes the user folders it is given where it sets none of their variables itself: a run
/// configuration's own HOME stands, and an environment with none given is left as it is.</summary>
public class GodotUserFoldersTests
{
    private static readonly Dictionary<string, string> Folders = new(StringComparer.Ordinal) { ["HOME"] = "/run/godot", ["XDG_DATA_HOME"] = "/run/godot/.local/share" };

    [Fact]
    public void The_folders_fill_in_what_the_environment_does_not_set_and_its_own_stand()
    {
        Dictionary<string, string> own = new(StringComparer.Ordinal) { ["HOME"] = "/mine", ["GREETING"] = "hello" };

        IReadOnlyDictionary<string, string> merged = GodotUserFolders.Into(own, Folders);

        Assert.True(merged.Count == 3 && merged["HOME"] == "/mine" && merged["GREETING"] == "hello" && merged["XDG_DATA_HOME"] == "/run/godot/.local/share",
            $"A configuration's own HOME was to stand and the folders' data folder to come beside it: {string.Join(", ", merged.Select(v => $"{v.Key}={v.Value}"))}");
        Assert.True(own.Count == 2, "The configuration's own environment was changed in place");
    }

    [Fact]
    public void An_environment_given_no_folders_or_setting_them_all_is_left_as_it_is()
    {
        Dictionary<string, string> own = new(StringComparer.Ordinal) { ["GREETING"] = "hello" };
        Dictionary<string, string> all = new(Folders, StringComparer.Ordinal);

        Assert.Same(own, GodotUserFolders.Into(own, null));
        Assert.Same(all, GodotUserFolders.Into(all, Folders));
    }
}
