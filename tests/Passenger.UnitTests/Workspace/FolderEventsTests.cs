using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What the system's file events say by their flags (FSEvents', into which Windows' are read): the watcher's word
/// that it dropped changes, whatever its path, taken as a drop, and that its watch failed as a failure; the folder moved away
/// as gone; each change below the folder in turn, a rename told apart by what stands there now; and nothing of the folder
/// itself, of a path outside it, or below a folder watched on its own. A watcher read from above the folder, shared with
/// others, says it gone as a folder holding it goes, reaches it with what it says beyond the changes below it, and is
/// shared only from a folder deep enough below its root that not every program writes there.</summary>
public class FolderEventsTests
{
    private const string Home = "/Users/someone";
    private const string Folder = $"{Home}/Dev/Shop";

    private static List<FolderEvent> Read(string path, uint flags, bool recursive = true, bool there = true, string from = Folder)
    {
        List<FolderEvent> read = [];
        FolderEvents.Read(path, flags, Folder, recursive, from, (_, _) => there, read);
        return read;
    }

    [Fact]
    public void A_streams_word_that_it_dropped_changes_is_a_drop_wherever_it_points()
    {
        // As a stream behind says it at its top, and the kernel's full queue at the volume's root.
        Assert.Equal([new FolderEvent(Folder, FolderEventKind.Dropped)], Read(Folder, FolderEvents.MustScanSubDirs | FolderEvents.UserDropped));
        Assert.Equal([new FolderEvent("/", FolderEventKind.Dropped)], Read("/", FolderEvents.MustScanSubDirs | FolderEvents.KernelDropped));
        Assert.Equal([new FolderEvent($"{Folder}/src", FolderEventKind.Dropped)], Read($"{Folder}/src", FolderEvents.MustScanSubDirs));
        Assert.True(Read("/Volumes/Backup", FolderEvents.Mount) is [{ Kind: FolderEventKind.Dropped }],
            "A volume mounted was not taken for changes the stream cannot tell, so what it brought under the folder goes unseen");
    }

    [Fact]
    public void The_folder_moved_away_is_gone_and_nothing_of_itself_or_outside_it_is_a_change()
    {
        Assert.Equal([new FolderEvent(Folder, FolderEventKind.Gone)], Read(Folder, FolderEvents.RootChanged));
        Assert.Empty(Read(Folder, FolderEvents.ItemModified | FolderEvents.ItemInodeMetaMod));
        Assert.Empty(Read(Home, FolderEvents.ItemModified));
        Assert.Empty(Read($"{Home}/Dev/Shopping/Cart.cs", FolderEvents.ItemModified));
    }

    [Fact]
    public void Each_change_below_the_folder_is_said_in_turn_and_a_rename_by_what_stands_there_now()
    {
        string cart = $"{Folder}/src/Cart.cs";
        Assert.Equal([new FolderEvent(cart, FolderEventKind.Changed)], Read(cart, FolderEvents.ItemModified | FolderEvents.ItemIsFile));
        Assert.Equal([new FolderEvent(cart, FolderEventKind.Changed), new FolderEvent(cart, FolderEventKind.Made), new FolderEvent(cart, FolderEventKind.Deleted)],
            Read(cart, FolderEvents.ItemCreated | FolderEvents.ItemModified | FolderEvents.ItemRemoved | FolderEvents.ItemIsFile));
        Assert.Equal([new FolderEvent(cart, FolderEventKind.MovedIn)], Read(cart, FolderEvents.ItemRenamed | FolderEvents.ItemIsFile, there: true));
        Assert.Equal([new FolderEvent(cart, FolderEventKind.Deleted)], Read(cart, FolderEvents.ItemRenamed | FolderEvents.ItemIsFile, there: false));
        Assert.Equal([new FolderEvent(cart, FolderEventKind.Made)], Read(cart, FolderEvents.ItemCreated | FolderEvents.ItemRenamed | FolderEvents.ItemIsFile));
    }

    [Fact]
    public void A_folder_watched_on_its_own_hears_its_children_alone()
    {
        Assert.Equal([new FolderEvent($"{Folder}/Cart.cs", FolderEventKind.Changed)], Read($"{Folder}/Cart.cs", FolderEvents.ItemModified, recursive: false));
        Assert.Empty(Read($"{Folder}/src/Cart.cs", FolderEvents.ItemModified, recursive: false));
    }

    [Fact]
    public void Read_from_above_the_folder_it_is_gone_as_it_or_a_folder_holding_it_is_renamed_or_deleted_away()
    {
        const uint Removed = FolderEvents.ItemRemoved | FolderEvents.ItemIsDir;
        const uint Renamed = FolderEvents.ItemRenamed | FolderEvents.ItemIsDir;
        Assert.Equal([new FolderEvent(Folder, FolderEventKind.Gone)], Read(Folder, Removed, there: false, from: Home));
        Assert.Equal([new FolderEvent(Folder, FolderEventKind.Gone)], Read($"{Home}/Dev", Renamed, there: false, from: Home));
        Assert.True(Read(Folder, Renamed, there: true, from: Home).Count == 0, "A folder renamed onto the folder's name was taken for the folder gone");
        Assert.True(Read($"{Home}/Dev", FolderEvents.ItemModified, there: false, from: Home).Count == 0, "A change to a folder holding the folder was taken for the folder gone");
        Assert.True(Read(Folder, FolderEvents.ItemRemoved | FolderEvents.ItemIsFile, there: false, from: Home).Count == 0,
            "A file of the folder's name was taken for the folder");
        Assert.True(Read(Home, Removed, there: false, from: Home).Count == 0, "The stream's own folder, which its own word says gone, was said gone again");
        Assert.True(Read(Folder, Removed, there: false).Count == 0, "A stream of the folder's own said it gone by its event as well as by its word");
    }

    [Fact]
    public void A_shared_stream_reaches_a_folder_with_a_drop_over_or_in_it_its_own_folder_moved_or_one_holding_it_gone()
    {
        Assert.True(FolderEvents.Reaches("/", FolderEvents.MustScanSubDirs | FolderEvents.KernelDropped, Folder), "The kernel's drop at the root missed the folder");
        Assert.True(FolderEvents.Reaches($"{Folder}/src", FolderEvents.MustScanSubDirs, Folder), "A drop in the folder missed it");
        Assert.False(FolderEvents.Reaches($"{Home}/Dev/Other", FolderEvents.MustScanSubDirs, Folder), "A drop in another folder of the stream reached the folder");
        Assert.True(FolderEvents.Reaches(Home, FolderEvents.RootChanged, Folder), "The stream's own folder moved missed a folder below it");
        Assert.True(FolderEvents.Reaches($"{Home}/Dev", FolderEvents.ItemRemoved | FolderEvents.ItemIsDir, Folder), "A folder holding the folder deleted missed it");
        Assert.False(FolderEvents.Reaches($"{Home}/Dev/Other", FolderEvents.ItemRemoved | FolderEvents.ItemIsDir, Folder), "Another folder deleted reached the folder");
        Assert.False(FolderEvents.Reaches($"{Folder}/Cart.cs", FolderEvents.ItemModified | FolderEvents.ItemIsFile, Folder),
            "A change below the folder, which the folders above its path hand over, was handed over twice");
    }

    [Fact]
    public void A_watch_failed_is_said_to_every_folder_its_watcher_reads_for_as_failed()
    {
        Assert.Equal([new FolderEvent(Home, FolderEventKind.Failed)], Read(Home, FolderEvents.Failed, from: Home));
        Assert.True(FolderEvents.Says(FolderEvents.Failed) && FolderEvents.Reaches(Home, FolderEvents.Failed, Folder),
            "A shared watcher's failure was handed only to the folders above its path, so a folder below it never heard its watch had failed");
        Assert.False(FolderEvents.Says(FolderEvents.ItemRemoved | FolderEvents.ItemIsDir), "A folder deleted was taken for the watcher's own word to every folder");
    }

    [Fact]
    public void Windows_folders_are_shared_from_the_deepest_folder_both_are_in_below_their_drives_and_shares()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows names a drive or a share at a path's root");
        const string WindowsHome = @"C:\Users\someone";
        Assert.Equal(@"C:\Users\someone\.nuget\packages",
            FolderEvents.Shared(@"C:\Users\someone\.nuget\packages\xunit\2.9.3\lib\net8.0", @"C:\Users\someone\.nuget\packages\nunit\4.3.2\lib\net8.0", WindowsHome));
        Assert.Equal(@"C:\Users\someone\source\repos", FolderEvents.Shared(@"C:\Users\someone\source\repos\Shop", @"C:\Users\someone\source\repos\Till", WindowsHome));
        Assert.Null(FolderEvents.Shared(@"C:\Users\someone\source\a\b", @"D:\Users\someone\source\a\b", WindowsHome));
        Assert.Null(FolderEvents.Shared(@"C:\Users\someone\AppData\Local\a", @"C:\Users\someone\AppData\Roaming\b", WindowsHome));
        Assert.Equal(@"\\server\share\a\b\c\d", FolderEvents.Shared(@"\\server\share\a\b\c\d\e", @"\\server\share\a\b\c\d\f", WindowsHome));
    }

    [Fact]
    public void A_stream_is_shared_from_the_deepest_folder_both_are_in_and_never_from_a_home_its_children_or_a_volume()
    {
        Assert.Equal($"{Home}/.nuget/packages", FolderEvents.Shared($"{Home}/.nuget/packages/avalonia/12.0.0/lib/net8.0", $"{Home}/.nuget/packages/xunit.v3/3.2.0/lib/net8.0", Home));
        Assert.Equal($"{Home}/Dev/Shop", FolderEvents.Shared($"{Home}/Dev/Shop/App/obj", $"{Home}/Dev/Shop/Core/bin", Home));
        Assert.Equal("/usr/local/share/dotnet", FolderEvents.Shared("/usr/local/share/dotnet/packs/Microsoft.NETCore.App.Ref", "/usr/local/share/dotnet/sdk/11.0.100", Home));
        Assert.Equal($"{Home}/Dev/Shop", FolderEvents.Shared($"{Home}/Dev/Shop", $"{Home}/Dev/Shop/App/obj", Home));
        Assert.Null(FolderEvents.Shared($"{Home}/.nuget/packages/avalonia/12.0.0/lib/net8.0", $"{Home}/.dotnet/packs", Home));
        Assert.Null(FolderEvents.Shared($"{Home}/Library/Caches/a/b", $"{Home}/Library/Developer/c/d", Home));
        Assert.Null(FolderEvents.Shared("/Volumes/Backup/a/b/c", "/Volumes/Backup/d/e/f", Home));
        Assert.Null(FolderEvents.Shared($"{Home}/Dev/Shop/App/obj", $"{Home}/Dev/Shop/Core/bin", $"{Home}/Dev/Shop"));
        Assert.Null(FolderEvents.Shared($"{Home}/Dev/Shop/App/obj", $"{Home}/Dev/Shop/Core/bin", $"{Home}/Dev/Shop/App"));
        Assert.Null(FolderEvents.Shared("/Users/someone/a/b/c", "/Users/someonee/a/b/c", Home));
    }
}
