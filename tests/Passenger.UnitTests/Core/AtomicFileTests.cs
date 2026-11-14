using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A file that may be a symbolic link is written to the file it names: the link, made by a relative path as a
/// dotfiles manager makes one, stays the link it was, and the file at the end of its links takes the bytes. A write lands
/// over a file another program reads sharing its deletion; on Windows one sharing none refuses it, the file kept.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_link_written_through_its_target_stays_the_link_and_the_file_it_names_takes_the_bytes()
    {
        string kept = Write(Path.Join(root, "dotfiles", "settings.json"), "{}\n");
        string link = Path.Join(root, "home", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Link(() => File.CreateSymbolicLink(link, Path.Join("..", "dotfiles", "settings.json")));

        Assert.Equal(kept, AtomicFile.Target(link));
        AtomicFile.Write(AtomicFile.Target(link), "{ \"theme\": \"light\" }\n"u8);

        Assert.True(new FileInfo(link).LinkTarget == Path.Join("..", "dotfiles", "settings.json"),
            $"The link is no longer the one into the dotfiles folder (it links to {new FileInfo(link).LinkTarget ?? "nothing"}), so the person's later changes there never reach it");
        Assert.Equal("{ \"theme\": \"light\" }\n", File.ReadAllText(kept));
        string[] beside = Directory.GetFiles(Path.Join(root, "dotfiles"));
        Assert.True(beside.Length == 1, $"The write left {string.Join(", ", beside)} in the dotfiles folder, where only the file it wrote should be");
    }

    [Fact]
    public void A_link_to_a_link_is_written_to_the_file_at_the_end_of_both()
    {
        string kept = Write(Path.Join(root, "dotfiles", ".zshrc"), "alias ll='ls -l'\n");
        string middle = Path.Join(root, "profile", ".zshrc");
        Directory.CreateDirectory(Path.GetDirectoryName(middle)!);
        Link(() => File.CreateSymbolicLink(middle, kept));
        string link = Path.Join(root, ".zshrc");
        Link(() => File.CreateSymbolicLink(link, Path.Join("profile", ".zshrc")));

        Assert.Equal(kept, AtomicFile.Target(link));
        AtomicFile.Write(AtomicFile.Target(link), "export PATH\n"u8);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.NotNull(new FileInfo(middle).LinkTarget);
        Assert.Equal("export PATH\n", File.ReadAllText(kept));
    }

    [Fact]
    public void A_link_whose_file_is_not_there_yet_makes_that_file_and_stays_the_link()
    {
        Directory.CreateDirectory(Path.Join(root, "dotfiles"));
        string link = Path.Join(root, "settings.json");
        Link(() => File.CreateSymbolicLink(link, Path.Join("dotfiles", "settings.json")));

        AtomicFile.Write(AtomicFile.Target(link), "{}\n"u8);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("{}\n", File.ReadAllText(Path.Join(root, "dotfiles", "settings.json")));
    }

    [Fact]
    public void A_path_that_is_no_link_is_its_own_target_whether_or_not_it_is_there()
    {
        string file = Write(Path.Join(root, "settings.json"), "{}\n");
        Assert.Equal(file, AtomicFile.Target(file));
        string missing = Path.Join(root, "missing", "settings.json");
        Assert.Equal(missing, AtomicFile.Target(missing));
    }

    [Fact]
    public void A_write_lands_over_a_file_another_program_reads_sharing_its_deletion_which_reads_on_in_the_old_one()
    {
        string file = Write(Path.Join(root, "layout.json"), "old\n");
        using FileStream reading = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        AtomicFile.Write(file, "new\n"u8);

        Assert.Equal("new\n", File.ReadAllText(file));
        using StreamReader opened = new(reading);
        Assert.Equal("old\n", opened.ReadToEnd());
        string[] beside = Directory.GetFiles(root);
        Assert.True(beside.Length == 1, $"The write left {string.Join(", ", beside)} beside the file it wrote");
    }

    [Fact]
    public void On_Windows_a_write_over_a_file_another_program_reads_sharing_no_deletion_is_refused_and_the_file_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a move over a file another program has open");
        string file = Write(Path.Join(root, "layout.json"), "old\n");
        using FileStream reading = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Exception? refused = Record.Exception(() => AtomicFile.Write(file, "new\n"u8));

        Assert.True(refused is IOException or UnauthorizedAccessException, $"The write over the held file was not refused as one the writer tries again: {refused?.GetType().Name ?? "it went ahead"}");
        Assert.Equal("old\n", File.ReadAllText(file));
        string[] beside = Directory.GetFiles(root);
        Assert.True(beside.Length == 1, $"The refused write left {string.Join(", ", beside)} beside the file");
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
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
