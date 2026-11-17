using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Windows' own Recycle Bin as Delete, Undo and an agent's change reach it: a file goes and comes back as it was,
/// and one another program holds is refused at once in words, the shell showing nothing for nobody to answer.</summary>
public sealed class WindowsTrashTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("recycle-"));

    private readonly string? trash = Trash.Folder;

    public void Dispose()
    {
        Trash.Folder = trash;
        TestFolders.Delete(folder);
    }

    /// <summary>The system's own Trash from here on, which only Windows' is in a test.</summary>
    private static void UseRecycleBin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows has the Recycle Bin");
        Trash.Folder = null;
    }

    [Fact]
    public async Task A_file_goes_to_the_Recycle_Bin_and_is_put_back_as_it_was()
    {
        UseRecycleBin();
        string file = Path.Combine(folder, "Shop.cs");
        File.WriteAllText(file, "class Shop { }\n");

        Trashed went = await Waiting.Apart(() => Trash.Move(file), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The Recycle Bin kept Shop.cs nowhere Passenger could find");
        Assert.False(File.Exists(file), "Shop.cs is still where it was after it went to the Recycle Bin");
        Assert.True(Trash.Holds(went), $"The Recycle Bin does not hold Shop.cs where it was said to go, {went.At}");
        Trash.PutBack(went);
        Assert.Equal("class Shop { }\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_file_another_program_holds_is_refused_at_once_with_no_dialog_for_nobody_to_answer()
    {
        UseRecycleBin();
        string file = Path.Combine(folder, "Held.cs");
        File.WriteAllText(file, "class Held { }\n");
        using FileStream held = new(file, FileMode.Open, FileAccess.Read, FileShare.None);

        Task<Trashed?> moving = Waiting.Apart(() => Trash.Move(file), TestContext.Current.CancellationToken);
        Assert.True(await Waiting.Within(moving, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            "The Recycle Bin neither took nor refused a file another program holds: the shell waits on a dialog nobody can answer");
        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => moving);
        Assert.True(refused is IOException or UnauthorizedAccessException, $"The refusal was not one the window says: {refused}");
        Assert.True(File.Exists(file), "The file another program holds is gone, though the Recycle Bin refused it");
    }
}
