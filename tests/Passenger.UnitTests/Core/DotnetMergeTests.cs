using System.Runtime.Versioning;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>An unpacked SDK moved into an installation as one change: what the installation has stays, what it lacks comes
/// whole, through a folder that is a link too, onto another volume too, the sdk folder last; and whatever stops it or
/// follows it, taking it back puts the installation as it was, so no half of an SDK is left to read as one. Windows refuses
/// a move or a removal for a file another program holds, which is waited out, said, and named where it outlasts the wait,
/// and a folder's move for any file open under it, so such a folder comes in entry by entry, counting only once whole.</summary>
public sealed class DotnetMergeTests : IDisposable
{
    private const string Sdk = "10.0.403";
    private const string Runtime = "10.0.14";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
    private readonly List<string> shut = [];
    private readonly List<FileStream> held = [];
    private readonly List<string> stages = [];
    private readonly DotnetMerge merge;

    private string Unpacked => Path.Join(root, "unpacked");

    private string Installation => Path.Join(root, "dotnet");

    private string SdkFolder => Path.Join("sdk", Sdk);

    private string RuntimeFolder => Path.Join("shared", "Microsoft.NETCore.App", Runtime);

    public DotnetMergeTests()
    {
        merge = new DotnetMerge(Sdk, stages.Add, TimeSpan.FromSeconds(1));
        Write(Unpacked, "dotnet", "new host\n");
        Write(Unpacked, "LICENSE.txt", "licence\n");
        Write(Unpacked, Path.Join("host", "fxr", Runtime, "libhostfxr"), "fxr\n");
        Write(Unpacked, Path.Join(RuntimeFolder, "System.Private.CoreLib.dll"), "runtime\n");
        Write(Unpacked, Path.Join(SdkFolder, "dotnet.dll"), "cli\n");
        Write(Unpacked, Path.Join(SdkFolder, "dotnet.runtimeconfig.json"), $$"""{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "{{Runtime}}" } } }""");
    }

    public void Dispose()
    {
        held.ForEach(file => file.Dispose());
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
        Write(Installation, Path.Join("shared", "Microsoft.NETCore.App", "9.0.1", "System.Private.CoreLib.dll"), "older runtime\n");
        Refuse(Path.Join(Installation, "shared", "Microsoft.NETCore.App"), Path.Join(Unpacked, RuntimeFolder, "System.Private.CoreLib.dll"));
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
        Write(Installation, Path.Join("sdk", "9.0.319", "dotnet.dll"), "older cli\n");
        Refuse(Path.Join(Installation, "sdk"), Path.Join(Unpacked, SdkFolder, "dotnet.dll"));
        List<string> before = Listed(Installation);

        await Assert.ThrowsAnyAsync<IOException>(() => merge.IntoAsync(Unpacked, Installation, CancellationToken.None));

        Assert.True(File.Exists(Path.Join(Installation, RuntimeFolder, "System.Private.CoreLib.dll")), "The sdk folder did not come last, so this refusal took nothing back");
        Assert.Empty(await merge.TakeBackAsync());
        Assert.Equal(before, Listed(Installation));
    }

    /// <summary>What a virus scanner does to files just unpacked: the move waits, saying so, and goes on once let go.</summary>
    [Fact]
    public async Task A_file_held_in_what_moves_is_waited_for_saying_so_and_the_merge_lands_once_it_is_let_go()
    {
        WindowsAlone();
        FileStream file = Hold(Path.Join(Unpacked, RuntimeFolder, "System.Private.CoreLib.dll"));
        DotnetMerge waiting = new(Sdk, said =>
        {
            stages.Add(said);
            file.Dispose();
        });

        await waiting.IntoAsync(Unpacked, Installation, CancellationToken.None);

        Assert.Contains(stages, said => said.StartsWith("Waiting for Windows to let go of the .NET SDK's ", StringComparison.Ordinal));
        Assert.True(waiting.HeldSteps == 1 && waiting.Held > TimeSpan.Zero, $"The wait was not counted: {waiting.HeldSteps} steps, {waiting.Held}");
        Assert.Equal([Sdk], DotnetInstallation.Read(Installation).Sdks);
    }

    /// <summary>Taking back waits as the move does: what it reaches may be held by the same scanner.</summary>
    [Fact]
    public async Task A_file_held_as_taking_back_reaches_it_is_waited_for_saying_so_and_taken_out_once_it_is_let_go()
    {
        WindowsAlone();
        Write(Installation, Path.Join("shared", "Microsoft.NETCore.App", "9.0.1", "System.Private.CoreLib.dll"), "older runtime\n");
        List<string> before = Listed(Installation);
        FileStream? file = null;
        DotnetMerge waiting = new(Sdk, said =>
        {
            stages.Add(said);
            file?.Dispose();
        });
        await waiting.IntoAsync(Unpacked, Installation, CancellationToken.None);
        file = Hold(Path.Join(Installation, RuntimeFolder, "System.Private.CoreLib.dll"));

        Assert.Empty(await waiting.TakeBackAsync());

        Assert.Contains(stages, said => said.StartsWith("Waiting for Windows to let go of the .NET SDK's ", StringComparison.Ordinal));
        Assert.Equal(before, Listed(Installation));
    }

    [Fact]
    public async Task What_stays_held_past_the_wait_is_named_and_all_the_rest_is_taken_out_again()
    {
        WindowsAlone();
        Write(Installation, Path.Join("shared", "Microsoft.NETCore.App", "9.0.1", "System.Private.CoreLib.dll"), "older runtime\n");
        await merge.IntoAsync(Unpacked, Installation, CancellationToken.None);
        Hold(Path.Join(Installation, RuntimeFolder, "System.Private.CoreLib.dll"));

        IReadOnlyList<string> left = await merge.TakeBackAsync();

        Assert.Equal([Path.Join(Installation, RuntimeFolder)], left);
        Assert.False(Directory.Exists(Path.Join(Installation, "sdk")), "The SDK stayed behind the file that could not be taken out");
        Assert.False(File.Exists(Path.Join(Installation, "dotnet")), "The host stayed behind the file that could not be taken out");
    }

    /// <summary>A reader of the files just unpacked that shares their deletion, as a scanner or Passenger's own Local History
    /// does, holds up no file's move, only its folder's: the folders come in entry by entry with no wait.</summary>
    [Fact]
    public async Task Folders_whose_files_another_program_reads_come_in_entry_by_entry_with_no_wait_and_the_SDK_installs()
    {
        WindowsAlone();
        Read(Path.Join(Unpacked, RuntimeFolder, "System.Private.CoreLib.dll"));
        Read(Path.Join(Unpacked, SdkFolder, "dotnet.runtimeconfig.json"));

        await merge.IntoAsync(Unpacked, Installation, CancellationToken.None);

        Assert.True(merge.HeldSteps == 0 && stages.Count == 0, $"The merge waited for what reads the files: {string.Join(" | ", stages)}");
        Assert.True(merge.Piecemeal > 0, "The folders read were moved whole, so nothing held them");
        Assert.Equal("runtime\n", File.ReadAllText(Path.Join(Installation, RuntimeFolder, "System.Private.CoreLib.dll")));
        Assert.Equal([Sdk], DotnetInstallation.Read(Installation).Sdks);
        Assert.Empty(Directory.EnumerateFiles(Unpacked, "*", SearchOption.AllDirectories));
    }

    /// <summary>What makes a folder count to the dotnet host comes after all else of a folder brought in entry by entry, so
    /// until then the host passes over it and the SDK reads as none; each wait names who holds the file.</summary>
    [Fact]
    public async Task A_folder_brought_in_entry_by_entry_counts_only_once_all_of_it_is_there()
    {
        WindowsAlone();
        string deps = Write(Unpacked, Path.Join(RuntimeFolder, "Microsoft.NETCore.App.deps.json"), "{}\n");
        FileStream runtime = Hold(deps);
        FileStream cli = Hold(Path.Join(Unpacked, SdkFolder, "dotnet.dll"));
        List<string> found = [];
        DotnetMerge waiting = new(Sdk, said =>
        {
            stages.Add(said);
            bool sdk = said.Contains("dotnet.dll", StringComparison.Ordinal);
            string folder = Path.Join(Installation, sdk ? SdkFolder : RuntimeFolder);
            found.Add($"{(sdk ? "dotnet.dll" : "deps.json")}: {string.Join(", ", Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal))}; SDKs [{string.Join(", ", DotnetInstallation.Read(Installation).Sdks)}]");
            (sdk ? cli : runtime).Dispose();
        });

        await waiting.IntoAsync(Unpacked, Installation, CancellationToken.None);

        Assert.Equal(["deps.json: System.Private.CoreLib.dll; SDKs []", "dotnet.dll: dotnet.runtimeconfig.json; SDKs []"], found);
        Assert.All(stages, said => Assert.EndsWith($", which {FileHolders.Itself} holds", said, StringComparison.Ordinal));
        Assert.Equal([Sdk], DotnetInstallation.Read(Installation).Sdks);
    }

    /// <summary>The dotnet host takes the newest of host/fxr's folders as it stands, so the one holding its resolver waits
    /// to come whole, even for a reader a file's move would pass.</summary>
    [Fact]
    public async Task The_folder_of_the_hosts_resolver_waits_to_come_whole_and_never_stands_without_it()
    {
        WindowsAlone();
        string fxr = Path.Join("host", "fxr", Runtime);
        FileStream resolver = Read(Write(Unpacked, Path.Join(fxr, "hostfxr.dll"), "fxr\n"));
        List<bool> there = [];
        DotnetMerge waiting = new(Sdk, said =>
        {
            stages.Add(said);
            there.Add(Directory.Exists(Path.Join(Installation, fxr)));
            resolver.Dispose();
        });

        await waiting.IntoAsync(Unpacked, Installation, CancellationToken.None);

        Assert.Equal([false], there);
        Assert.StartsWith($"Waiting for Windows to let go of the .NET SDK's {Runtime}, which ", Assert.Single(stages), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(Installation, fxr, "hostfxr.dll")), "The host's resolver did not come");
    }

    [Theory]
    [InlineData("sdk/10.0.403/dotnet.dll", true)]
    [InlineData("shared/Microsoft.NETCore.App/10.0.14/Microsoft.NETCore.App.deps.json", true)]
    [InlineData("shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.App.deps.json", true)]
    [InlineData("shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll", false)]
    [InlineData("sdk/10.0.403/dotnet.deps.json", false)]
    [InlineData("sdk/10.0.403/dotnet.runtimeconfig.json", false)]
    public void What_makes_a_folder_count_to_the_host_is_told_from_the_rest(string entry, bool counts) =>
        Assert.Equal(counts, DotnetMerge.Counts(Path.Join("dotnet", entry)));

    /// <summary>The folder a link leads to lies on a volume of its own, so nothing of the SDK can be renamed there.</summary>
    [Fact]
    public async Task A_merge_through_a_link_onto_another_volume_lands_whole_and_is_taken_out_whole()
    {
        string elsewhere = AnotherVolume();
        try
        {
            Write(elsewhere, Path.Join("Microsoft.NETCore.App", "9.0.1", "kept.dll"), "kept\n");
            Directory.CreateDirectory(Installation);
            Link(() => Directory.CreateSymbolicLink(Path.Join(Installation, "shared"), elsewhere));
            List<string> before = Listed(elsewhere);

            await merge.IntoAsync(Unpacked, Installation, CancellationToken.None);

            Assert.Equal("runtime\n", File.ReadAllText(Path.Join(elsewhere, "Microsoft.NETCore.App", Runtime, "System.Private.CoreLib.dll")));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.Join(elsewhere, "Microsoft.NETCore.App")), entry => Path.GetFileName(entry).StartsWith('.'));
            Assert.Equal([Sdk], DotnetInstallation.Read(Installation).Sdks);
            Assert.Empty(await merge.TakeBackAsync());
            Assert.Equal(before, Listed(elsewhere));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
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

    /// <summary>The move of an entry of the SDK refused: on Windows, which has no folder mode to do it with, by a file under
    /// the entry held, else by the folder it goes into shut.</summary>
    private void Refuse(string into, string file)
    {
        if (OperatingSystem.IsWindows())
        {
            Hold(file);
        }
        else
        {
            Shut(into);
        }
    }

    /// <summary>A file held as a virus scanner holds it, shared with nothing, let go as the test ends if it has not been.</summary>
    private FileStream Hold(string file)
    {
        FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.None);
        held.Add(stream);
        return stream;
    }

    /// <summary>A file held as a reader of it holds it, sharing its writing and its deletion, as a scanner and Passenger's
    /// own reads do, let go as the test ends if it has not been.</summary>
    private FileStream Read(string file)
    {
        FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        held.Add(stream);
        return stream;
    }

    private static void WindowsAlone() => Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a move or a removal for a file another program holds");

    /// <summary>A new folder on a volume other than the test's, the test skipped where this system has none to hand.</summary>
    private string AnotherVolume()
    {
        const string Memory = "/dev/shm";
        Assert.SkipUnless(Directory.Exists(Memory), $"No volume to hand beside the test's: this system has no {Memory}");
        string elsewhere = Path.Join(Memory, $"passenger-unit-{Guid.NewGuid():N}");
        string probe = Directory.CreateDirectory(Path.Join(root, "probe")).FullName;
        try
        {
            Directory.Move(probe, elsewhere);
        }
        catch (IOException)
        {
            return Directory.CreateDirectory(elsewhere).FullName;
        }
        Directory.Delete(elsewhere);
        Assert.Skip($"{Memory} lies on the test's own volume");
        return elsewhere;
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
