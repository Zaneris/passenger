using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What macOS's file events say by their flags: the stream's word that it dropped changes, whatever its path,
/// taken as a drop; the folder moved away as gone; each change below the folder in turn, a rename told apart by what stands
/// there now; and nothing of the folder itself, of a path outside it, or below a folder watched on its own. A stream read
/// from above the folder, shared with others, says it gone as a folder holding it goes, reaches it with what it says beyond
/// the changes below it, and is shared only from a folder deep enough that not every program writes there.</summary>
public class MacFileEventFlagsTests
{
    private const string Home = "/Users/someone";
    private const string Folder = $"{Home}/Dev/Shop";

    private static List<MacFileEvent> Read(string path, uint flags, bool recursive = true, bool there = true, string from = Folder)
    {
        List<MacFileEvent> read = [];
        MacFileEventFlags.Read(path, flags, Folder, recursive, from, (_, _) => there, read);
        return read;
    }

    [Fact]
    public void A_streams_word_that_it_dropped_changes_is_a_drop_wherever_it_points()
    {
        // As a stream behind says it at its top, and the kernel's full queue at the volume's root.
        Assert.Equal([new MacFileEvent(Folder, MacFileEventKind.Dropped)], Read(Folder, MacFileEventFlags.MustScanSubDirs | MacFileEventFlags.UserDropped));
        Assert.Equal([new MacFileEvent("/", MacFileEventKind.Dropped)], Read("/", MacFileEventFlags.MustScanSubDirs | MacFileEventFlags.KernelDropped));
        Assert.Equal([new MacFileEvent($"{Folder}/src", MacFileEventKind.Dropped)], Read($"{Folder}/src", MacFileEventFlags.MustScanSubDirs));
        Assert.True(Read("/Volumes/Backup", MacFileEventFlags.Mount) is [{ Kind: MacFileEventKind.Dropped }],
            "A volume mounted was not taken for changes the stream cannot tell, so what it brought under the folder goes unseen");
    }

    [Fact]
    public void The_folder_moved_away_is_gone_and_nothing_of_itself_or_outside_it_is_a_change()
    {
        Assert.Equal([new MacFileEvent(Folder, MacFileEventKind.Gone)], Read(Folder, MacFileEventFlags.RootChanged));
        Assert.Empty(Read(Folder, MacFileEventFlags.ItemModified | MacFileEventFlags.ItemInodeMetaMod));
        Assert.Empty(Read(Home, MacFileEventFlags.ItemModified));
        Assert.Empty(Read($"{Home}/Dev/Shopping/Cart.cs", MacFileEventFlags.ItemModified));
    }

    [Fact]
    public void Each_change_below_the_folder_is_said_in_turn_and_a_rename_by_what_stands_there_now()
    {
        string cart = $"{Folder}/src/Cart.cs";
        Assert.Equal([new MacFileEvent(cart, MacFileEventKind.Changed)], Read(cart, MacFileEventFlags.ItemModified | MacFileEventFlags.ItemIsFile));
        Assert.Equal([new MacFileEvent(cart, MacFileEventKind.Changed), new MacFileEvent(cart, MacFileEventKind.Made), new MacFileEvent(cart, MacFileEventKind.Deleted)],
            Read(cart, MacFileEventFlags.ItemCreated | MacFileEventFlags.ItemModified | MacFileEventFlags.ItemRemoved | MacFileEventFlags.ItemIsFile));
        Assert.Equal([new MacFileEvent(cart, MacFileEventKind.MovedIn)], Read(cart, MacFileEventFlags.ItemRenamed | MacFileEventFlags.ItemIsFile, there: true));
        Assert.Equal([new MacFileEvent(cart, MacFileEventKind.Deleted)], Read(cart, MacFileEventFlags.ItemRenamed | MacFileEventFlags.ItemIsFile, there: false));
        Assert.Equal([new MacFileEvent(cart, MacFileEventKind.Made)], Read(cart, MacFileEventFlags.ItemCreated | MacFileEventFlags.ItemRenamed | MacFileEventFlags.ItemIsFile));
    }

    [Fact]
    public void A_folder_watched_on_its_own_hears_its_children_alone()
    {
        Assert.Equal([new MacFileEvent($"{Folder}/Cart.cs", MacFileEventKind.Changed)], Read($"{Folder}/Cart.cs", MacFileEventFlags.ItemModified, recursive: false));
        Assert.Empty(Read($"{Folder}/src/Cart.cs", MacFileEventFlags.ItemModified, recursive: false));
    }

    [Fact]
    public void Read_from_above_the_folder_it_is_gone_as_it_or_a_folder_holding_it_is_renamed_or_deleted_away()
    {
        const uint Removed = MacFileEventFlags.ItemRemoved | MacFileEventFlags.ItemIsDir;
        const uint Renamed = MacFileEventFlags.ItemRenamed | MacFileEventFlags.ItemIsDir;
        Assert.Equal([new MacFileEvent(Folder, MacFileEventKind.Gone)], Read(Folder, Removed, there: false, from: Home));
        Assert.Equal([new MacFileEvent(Folder, MacFileEventKind.Gone)], Read($"{Home}/Dev", Renamed, there: false, from: Home));
        Assert.True(Read(Folder, Renamed, there: true, from: Home).Count == 0, "A folder renamed onto the folder's name was taken for the folder gone");
        Assert.True(Read($"{Home}/Dev", MacFileEventFlags.ItemModified, there: false, from: Home).Count == 0, "A change to a folder holding the folder was taken for the folder gone");
        Assert.True(Read(Folder, MacFileEventFlags.ItemRemoved | MacFileEventFlags.ItemIsFile, there: false, from: Home).Count == 0,
            "A file of the folder's name was taken for the folder");
        Assert.True(Read(Home, Removed, there: false, from: Home).Count == 0, "The stream's own folder, which its own word says gone, was said gone again");
        Assert.True(Read(Folder, Removed, there: false).Count == 0, "A stream of the folder's own said it gone by its event as well as by its word");
    }

    [Fact]
    public void A_shared_stream_reaches_a_folder_with_a_drop_over_or_in_it_its_own_folder_moved_or_one_holding_it_gone()
    {
        Assert.True(MacFileEventFlags.Reaches("/", MacFileEventFlags.MustScanSubDirs | MacFileEventFlags.KernelDropped, Folder), "The kernel's drop at the root missed the folder");
        Assert.True(MacFileEventFlags.Reaches($"{Folder}/src", MacFileEventFlags.MustScanSubDirs, Folder), "A drop in the folder missed it");
        Assert.False(MacFileEventFlags.Reaches($"{Home}/Dev/Other", MacFileEventFlags.MustScanSubDirs, Folder), "A drop in another folder of the stream reached the folder");
        Assert.True(MacFileEventFlags.Reaches(Home, MacFileEventFlags.RootChanged, Folder), "The stream's own folder moved missed a folder below it");
        Assert.True(MacFileEventFlags.Reaches($"{Home}/Dev", MacFileEventFlags.ItemRemoved | MacFileEventFlags.ItemIsDir, Folder), "A folder holding the folder deleted missed it");
        Assert.False(MacFileEventFlags.Reaches($"{Home}/Dev/Other", MacFileEventFlags.ItemRemoved | MacFileEventFlags.ItemIsDir, Folder), "Another folder deleted reached the folder");
        Assert.False(MacFileEventFlags.Reaches($"{Folder}/Cart.cs", MacFileEventFlags.ItemModified | MacFileEventFlags.ItemIsFile, Folder),
            "A change below the folder, which the folders above its path hand over, was handed over twice");
    }

    [Fact]
    public void A_stream_is_shared_from_the_deepest_folder_both_are_in_and_never_from_a_home_its_children_or_a_volume()
    {
        Assert.Equal($"{Home}/.nuget/packages", MacFileEventFlags.Shared($"{Home}/.nuget/packages/avalonia/12.0.0/lib/net8.0", $"{Home}/.nuget/packages/xunit.v3/3.2.0/lib/net8.0", Home));
        Assert.Equal($"{Home}/Dev/Shop", MacFileEventFlags.Shared($"{Home}/Dev/Shop/App/obj", $"{Home}/Dev/Shop/Core/bin", Home));
        Assert.Equal("/usr/local/share/dotnet", MacFileEventFlags.Shared("/usr/local/share/dotnet/packs/Microsoft.NETCore.App.Ref", "/usr/local/share/dotnet/sdk/11.0.100", Home));
        Assert.Equal($"{Home}/Dev/Shop", MacFileEventFlags.Shared($"{Home}/Dev/Shop", $"{Home}/Dev/Shop/App/obj", Home));
        Assert.Null(MacFileEventFlags.Shared($"{Home}/.nuget/packages/avalonia/12.0.0/lib/net8.0", $"{Home}/.dotnet/packs", Home));
        Assert.Null(MacFileEventFlags.Shared($"{Home}/Library/Caches/a/b", $"{Home}/Library/Developer/c/d", Home));
        Assert.Null(MacFileEventFlags.Shared("/Volumes/Backup/a/b/c", "/Volumes/Backup/d/e/f", Home));
        Assert.Null(MacFileEventFlags.Shared($"{Home}/Dev/Shop/App/obj", $"{Home}/Dev/Shop/Core/bin", $"{Home}/Dev/Shop"));
        Assert.Null(MacFileEventFlags.Shared($"{Home}/Dev/Shop/App/obj", $"{Home}/Dev/Shop/Core/bin", $"{Home}/Dev/Shop/App"));
        Assert.Null(MacFileEventFlags.Shared("/Users/someone/a/b/c", "/Users/someonee/a/b/c", Home));
    }
}
