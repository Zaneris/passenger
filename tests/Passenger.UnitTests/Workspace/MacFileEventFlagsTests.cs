using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What macOS's file events say by their flags: the stream's word that it dropped changes, whatever its path,
/// taken as a drop; the folder moved away as gone; each change below the folder in turn, a rename told apart by what stands
/// there now; and nothing of the folder itself, of a path outside it, or below a folder watched on its own.</summary>
public class MacFileEventFlagsTests
{
    private const string Folder = "/Users/someone/Shop";

    private static List<MacFileEvent> Read(string path, uint flags, bool recursive = true, bool there = true)
    {
        List<MacFileEvent> read = [];
        MacFileEventFlags.Read(path, flags, Folder, recursive, (_, _) => there, read);
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
        Assert.Empty(Read("/Users/someone", MacFileEventFlags.ItemModified));
        Assert.Empty(Read("/Users/someone/Shopping/Cart.cs", MacFileEventFlags.ItemModified));
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
}
