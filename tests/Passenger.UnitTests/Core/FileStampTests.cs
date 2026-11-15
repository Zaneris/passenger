using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A file's stamp is the file a read of its path reads: a symbolic link's moves as the file at the end of its links
/// is written, the link's own staying as it was made; a link to nothing, links that go round and a file not there have
/// none.</summary>
public sealed class FileStampTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_links_stamp_moves_as_the_file_at_the_end_of_its_links_is_written()
    {
        string kept = Path.Join(root, "dotfiles", "vimrc");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "nnoremap Q dd\n");
        string middle = Path.Join(root, "profile", "vimrc");
        Directory.CreateDirectory(Path.GetDirectoryName(middle)!);
        Link(() => File.CreateSymbolicLink(middle, kept));
        string link = Path.Join(root, "vimrc");
        Link(() => File.CreateSymbolicLink(link, Path.Join("profile", "vimrc")));
        File.SetLastWriteTimeUtc(kept, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new FileStamp(14, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), FileStamp.Of(link));

        FileStamp before = FileStamp.Of(link)!.Value;
        File.WriteAllText(kept, "nnoremap Q dd\nnnoremap W dd\n");
        File.SetLastWriteTimeUtc(kept, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(FileStamp.Of(link) is { } after && after != before && after == FileStamp.Of(kept),
            $"The link's stamp ({FileStamp.Of(link)}) did not follow the file it names ({FileStamp.Of(kept)}), so a change written there is never looked at again");
        Assert.Equal(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), FileStamp.WrittenAt(link));
    }

    [Fact]
    public void A_link_to_nothing_links_that_go_round_and_a_file_not_there_have_no_stamp()
    {
        string dangling = Path.Join(root, "settings.json");
        Link(() => File.CreateSymbolicLink(dangling, Path.Join(root, "dotfiles", "settings.json")));
        string one = Path.Join(root, "one");
        string other = Path.Join(root, "other");
        Link(() => File.CreateSymbolicLink(one, other));
        Link(() => File.CreateSymbolicLink(other, one));

        Assert.Null(FileStamp.Of(dangling));
        Assert.Null(FileStamp.Of(one));
        Assert.Null(FileStamp.Of(Path.Join(root, "none")));
        Assert.Equal(DateTime.MinValue, FileStamp.WrittenAt(Path.Join(root, "none")));
    }

    private static void Link(Func<FileSystemInfo> make)
    {
        try
        {
            make();
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows lets this user make no symlink: {refused.Message}");
        }
    }
}
