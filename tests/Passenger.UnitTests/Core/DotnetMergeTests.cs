using System.Runtime.Versioning;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>An unpacked SDK moved into an installation as one change: what the installation has stays, what it lacks comes
/// whole, through a folder that is a link too, the sdk folder last; and whatever stops it or follows it, taking it back puts
/// the installation as it was, so no half of an SDK is left to read as one.</summary>
public sealed class DotnetMergeTests : IDisposable
{
    private const string Sdk = "10.0.403";
    private const string Runtime = "10.0.14";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
    private readonly DotnetMerge merge = new(Sdk, _ => { });
    private readonly List<string> shut = [];

    private string Unpacked => Path.Join(root, "unpacked");

    private string Installation => Path.Join(root, "dotnet");

    private string SdkFolder => Path.Join("sdk", Sdk);

    private string RuntimeFolder => Path.Join("shared", "Microsoft.NETCore.App", Runtime);

    public DotnetMergeTests()
    {
        Write(Unpacked, "dotnet", "new host\n");
        Write(Unpacked, "LICENSE.txt", "licence\n");
        Write(Unpacked, Path.Join("host", "fxr", Runtime, "libhostfxr"), "fxr\n");
        Write(Unpacked, Path.Join(RuntimeFolder, "System.Private.CoreLib.dll"), "runtime\n");
        Write(Unpacked, Path.Join(SdkFolder, "dotnet.dll"), "cli\n");
        Write(Unpacked, Path.Join(SdkFolder, "dotnet.runtimeconfig.json"), $$"""{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "{{Runtime}}" } } }""");
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string folder in shut)
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task What_the_installation_has_stays_as_it_is_and_what_it_lacks_comes_whole()
    {
        Write(Installation, "dotnet", "old host\n");
        Write(Installation, Path.Join("shared", "Microsoft.NETCore.App", "9.0.1", "System.Private.CoreLib.dll"), "older runtime\n");
        Write(Installation, Path.Join("shared", "Microsoft.NETCore.App", Runtime, "mine.txt"), "the person's\n");

        await merge.IntoAsync(Unpacked, Installation, CancellationToken.None);

        Assert.Equal("old host\n", File.ReadAllText(Path.Join(Installation, "dotnet")));
        Assert.Equal("the person's\n", File.ReadAllText(Path.Join(Installation, RuntimeFolder, "mine.txt")));
        Assert.Equal("runtime\n", File.ReadAllText(Path.Join(Installation, RuntimeFolder, "System.Private.CoreLib.dll")));
        Assert.Equal([Sdk], DotnetInstallation.Read(Installation).Sdks);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("sdk")]
    [InlineData("host/fxr")]
    public async Task A_folder_of_the_installation_that_is_a_link_is_merged_through_it_and_stays_a_link(string linked)
    {
        string link = Path.Join(Installation, linked);
        string elsewhere = Path.Join(root, "another-disk", Path.GetFileName(linked));
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Link(() => Directory.CreateSymbolicLink(link, elsewhere));

        await merge.IntoAsync(Unpacked, Installation, CancellationToken.None);

        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Equal(elsewhere, new DirectoryInfo(link).LinkTarget);
        Assert.Equal([Sdk], DotnetInstallation.Read(Installation).Sdks);
        Assert.True(File.Exists(Path.Join(Installation, RuntimeFolder, "System.Private.CoreLib.dll")), "The SDK came with no runtime to run on");
    }

    [Fact]
    public async Task Taking_back_a_merge_made_through_a_link_empties_what_it_filled_and_leaves_the_link()
    {
        string elsewhere = Path.Join(root, "another-disk", "shared");
        Write(elsewhere, Path.Join("Microsoft.NETCore.App", "9.0.1", "kept.dll"), "kept\n");
        Directory.CreateDirectory(Installation);
        Link(() => Directory.CreateSymbolicLink(Path.Join(Installation, "shared"), elsewhere));
        List<string> before = Listed(elsewhere);

        await merge.IntoAsync(Unpacked, Installation, CancellationToken.None);
        Assert.Empty(await merge.TakeBackAsync());

        Assert.Equal(before, Listed(elsewhere));
        Assert.Equal(["shared"], Directory.EnumerateFileSystemEntries(Installation).Select(Path.GetFileName));
    }

    [Fact]
    public void A_folder_from_another_volume_comes_under_a_name_of_its_own_and_lands_whole_its_programs_still_programs()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows' files have no mode to keep");
            return;
        }
        string from = Path.Join(Unpacked, RuntimeFolder);
        string program = Write(from, "createdump", "#!/bin/sh\n");
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(program, mode);
        string target = Path.Join(root, "another-disk", Runtime);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? coming = null;

        DotnetMerge.Carried(from, target, beside => coming = beside);

        Assert.NotEqual(target, coming);
        Assert.False(Directory.Exists(coming), $"What came over is still under its own name, {coming}");
        Assert.False(Directory.Exists(from), "The folder brought over is still where it was");
        Assert.Equal(mode, File.GetUnixFileMode(Path.Join(target, "createdump")));
        Assert.Equal("runtime\n", File.ReadAllText(Path.Join(target, "System.Private.CoreLib.dll")));
    }

    [Fact]
    public async Task A_runtime_refused_its_place_leaves_no_SDK_to_read_as_installed_and_taking_back_leaves_the_installation_as_it_was()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no folder mode to refuse the move with");
            return;
        }
        Write(Installation, Path.Join("shared", "Microsoft.NETCore.App", "9.0.1", "System.Private.CoreLib.dll"), "older runtime\n");
        Shut(Path.Join(Installation, "shared", "Microsoft.NETCore.App"));
        List<string> before = Listed(Installation);

        await Assert.ThrowsAnyAsync<IOException>(() => merge.IntoAsync(Unpacked, Installation, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Join(Installation, "sdk")), "The SDK came ahead of the runtime it runs on, to read as installed once the runtime was refused");
        Assert.Empty(DotnetInstallation.Read(Installation).Sdks);
        Assert.Empty(await merge.TakeBackAsync());
        Assert.Equal(before, Listed(Installation));
    }

    [Fact]
    public async Task A_move_refused_at_its_last_step_has_what_came_before_it_taken_out_again()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no folder mode to refuse the move with");
            return;
        }
        Write(Installation, Path.Join("sdk", "9.0.319", "dotnet.dll"), "older cli\n");
        Shut(Path.Join(Installation, "sdk"));
        List<string> before = Listed(Installation);

        await Assert.ThrowsAnyAsync<IOException>(() => merge.IntoAsync(Unpacked, Installation, CancellationToken.None));

        Assert.True(File.Exists(Path.Join(Installation, RuntimeFolder, "System.Private.CoreLib.dll")), "The sdk folder did not come last, so this refusal took nothing back");
        Assert.Empty(await merge.TakeBackAsync());
        Assert.Equal(before, Listed(Installation));
    }

    [Fact]
    public async Task A_merge_that_ended_is_taken_out_whole_where_what_follows_it_fails_and_a_folder_it_did_not_make_stays()
    {
        Write(Installation, Path.Join("sdk", "9.0.319", "dotnet.dll"), "older cli\n");
        List<string> before = Listed(Installation);

        await merge.IntoAsync(Unpacked, Installation, CancellationToken.None);
        Assert.Equal([Sdk, "9.0.319"], DotnetInstallation.Read(Installation).Sdks);
        Assert.Empty(await merge.TakeBackAsync());

        Assert.Equal(before, Listed(Installation));
    }

    [Theory]
    [InlineData("shared", "no folder, where the SDK has one")]
    [InlineData("dotnet", "a folder, where the SDK has a file")]
    public async Task What_stands_in_the_way_of_an_SDKs_entry_is_named_and_nothing_of_the_SDK_stays(string entry, string said)
    {
        if (entry == "shared")
        {
            Write(Installation, entry, "a file\n");
        }
        else
        {
            Directory.CreateDirectory(Path.Join(Installation, entry));
        }
        List<string> before = Listed(Installation);

        DotnetInstallFailed failed = await Assert.ThrowsAsync<DotnetInstallFailed>(() => merge.IntoAsync(Unpacked, Installation, CancellationToken.None));

        Assert.Contains(said, failed.Message, StringComparison.Ordinal);
        Assert.Contains(PathNames.Shown(Path.Join(Installation, entry), null), failed.Message, StringComparison.Ordinal);
        Assert.Empty(await merge.TakeBackAsync());
        Assert.Equal(before, Listed(Installation));
    }

    [Fact]
    public async Task A_link_to_nothing_where_the_SDK_has_a_folder_fails_and_leaves_the_link_alone()
    {
        Directory.CreateDirectory(Installation);
        string link = Path.Join(Installation, "shared");
        Link(() => Directory.CreateSymbolicLink(link, Path.Join(root, "unplugged")));

        await Assert.ThrowsAnyAsync<Exception>(() => merge.IntoAsync(Unpacked, Installation, CancellationToken.None));

        Assert.Empty(await merge.TakeBackAsync());
        Assert.Equal(["shared"], Directory.EnumerateFileSystemEntries(Installation).Select(Path.GetFileName));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task Stopped_it_moves_nothing_more()
    {
        using CancellationTokenSource stopped = new();
        await stopped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => merge.IntoAsync(Unpacked, Installation, stopped.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Installation));
    }

    /// <summary>Every entry under a folder, in order, with what each file says.</summary>
    private static List<string> Listed(string folder) =>
    [
        .. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(entry => $"{Path.GetRelativePath(folder, entry)}: {(File.Exists(entry) ? File.ReadAllText(entry) : "folder")}")
            .Order(StringComparer.Ordinal),
    ];

    private static string Write(string folder, string file, string text)
    {
        string path = Path.Join(folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A folder nothing may be moved into, opened again as the test ends.</summary>
    [UnsupportedOSPlatform("windows")]
    private void Shut(string folder)
    {
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        shut.Add(folder);
    }

    /// <summary>Makes a link, the test skipped where the system lets this user make none (Windows without Developer Mode).</summary>
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
