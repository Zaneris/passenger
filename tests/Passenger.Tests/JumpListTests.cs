using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Passenger.App;
using Passenger.App.Welcome;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Windows' taskbar jump list as Passenger sets it (<see cref="WindowsJumpList"/>), through the shell's own
/// ICustomDestinationList, read back from the file the shell keeps it in (CustomDestinations, one file per app ID). Each
/// test writes under an app ID of its own, never Passenger's, and deletes that list as it ends. The shell reads whether
/// Windows shows recently opened items at each write: a test changes that only on a CI runner, for its own write alone,
/// and skips on a person's machine whose setting is not as it needs.</summary>
[Collection(Beside.Name)]
public sealed class JumpListTests
{
    /// <summary>Where Windows keeps whether it shows recently opened items in Start, jump lists and File Explorer.</summary>
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string RecentItemsShown = "Start_TrackDocs";

    private readonly string appId = "Passenger.Tests.JumpList." + Guid.NewGuid().ToString("N");

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Each_recent_one_is_a_link_starting_Passenger_on_its_path_two_of_one_name_told_apart_by_their_folders_beside_New_Window()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The taskbar's jump list is Windows'");
        string folder = TestFolders.Create("jump-list-");
        string shop = Solution(folder, "shop");
        string notes = Directory.CreateDirectory(Path.Combine(folder, "Notes")).FullName;
        string garden = Solution(folder, "garden");
        string? deleted = null;
        try
        {
            (string? refused, IReadOnlyList<string> removed) = await SetAsync([(shop, "Api"), (notes, "Notes"), (garden, "Api")], recentItems: true);
            Assert.True(refused is null, $"The shell refused the jump list ({refused}), so the taskbar shows none of the recent ones");
            Assert.True(removed.Count == 0, $"A list never shown says the person removed {string.Join(", ", removed)} from it");
            string? kept = null;
            Assert.True(Waiting.Until(() => (kept = Kept(folder)) is not null, TimeSpan.FromSeconds(10)),
                $"The shell kept no jump list naming the test's recent ones in {CustomDestinations}, so the taskbar would list none of them");
            string[] expected =
            [
                MainMenu.RecentSolutions, "Api – " + Path.Combine("shop", "Api"), "Api – " + Path.Combine("garden", "Api"), "Notes",
                WindowsCommandLine.Of([shop]), WindowsCommandLine.Of([garden]), WindowsCommandLine.Of([notes]),
                RecentShortcuts.NewWindow.Title, RecentShortcuts.NewWindow.Tip, LaunchOptions.NewWindowFlag, Environment.ProcessPath!,
            ];
            string[] missing = [.. expected.Where(words => !kept!.Contains(words, StringComparison.Ordinal))];
            Assert.True(missing.Length == 0,
                $"The jump list the shell kept lacks [{string.Join(", ", missing)}], so the taskbar would show an Api it can't tell from the other, or a choice that opens nothing");
        }
        finally
        {
            deleted = await DeleteAsync();
        }
        Assert.True(deleted is null, deleted);
        Assert.True(Waiting.Until(() => Kept(folder) is null, TimeSpan.FromSeconds(10)), $"The test's jump list is still kept in {CustomDestinations} after it was deleted");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task With_recent_items_turned_off_in_Windows_the_list_keeps_New_Window_and_leaves_out_the_recent_ones_saying_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The taskbar's jump list is Windows'");
        Assert.SkipUnless(OnRunner, "The test turns Windows' recent items on and off, which it does only on a CI runner, never on a person's machine");
        string folder = TestFolders.Create("jump-list-");
        string shop = Solution(folder, "shop");
        string? deleted = null;
        try
        {
            (string? first, _) = await SetAsync([(shop, "Api")], recentItems: true);
            string? file = null;
            Assert.True(first is null && Waiting.Until(() => (file = KeptIn(folder)) is not null, TimeSpan.FromSeconds(10)),
                $"With recent items shown the shell kept no jump list naming the test's recent one in {CustomDestinations} ({first})");

            (string? refused, IReadOnlyList<string> removed) = await SetAsync([(shop, "Api")], recentItems: false);

            Assert.True(refused is null,
                $"With recent items turned off the jump list was refused ({refused}), so the taskbar keeps the list from before, recent ones and all, and the log says so at each change");
            Assert.True(removed.Count == 0, $"With recent items turned off the list says the person removed {string.Join(", ", removed)} from it");
            string? kept = null;
            Assert.True(Waiting.Until(() => (kept = Read(file!)) is { } text && !text.Contains(Path.GetFileName(folder), StringComparison.Ordinal), TimeSpan.FromSeconds(10)),
                $"With recent items turned off the jump list still names the recent one: {kept}");
            Assert.True(kept!.Contains(RecentShortcuts.NewWindow.Title, StringComparison.Ordinal) && kept.Contains(LaunchOptions.NewWindowFlag, StringComparison.Ordinal)
                && !kept.Contains(MainMenu.RecentSolutions, StringComparison.Ordinal),
                $"With recent items turned off the jump list lost New Window, or kept its Recent Solutions: {kept}");
        }
        finally
        {
            deleted = await DeleteAsync();
        }
        Assert.True(deleted is null, deleted);
    }

    private static string CustomDestinations =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent", "CustomDestinations");

    /// <summary>A solution named Api in a folder named Api under <paramref name="place"/>, as two clones of one repository
    /// hold it.</summary>
    private static string Solution(string folder, string place)
    {
        string solution = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, place, "Api")).FullName, "Api.slnx");
        File.WriteAllText(solution, "<Solution />\n");
        return solution;
    }

    /// <summary>The jump list set on a thread of a single-threaded apartment, as the window's is, with Windows' showing of
    /// recently opened items as asked, and the person's setting put back after: what the shell refused, and the paths it
    /// said the person removed.</summary>
    [SupportedOSPlatform("windows")]
    private Task<(string? Refused, IReadOnlyList<string> Removed)> SetAsync((string Path, string Name)[] newest, bool recentItems)
    {
        Assert.SkipWhen(RecentItemsShownNow() != recentItems && !OnRunner,
            $"Recent items are {(recentItems ? "turned off" : "shown")} in Windows, and a test changes that setting only on a CI runner, never on a person's machine");
        return OnStaThread(() => Set(newest, recentItems));
    }

    /// <summary>A GitHub runner, the one kind of machine whose Windows settings a test may change.</summary>
    private static bool OnRunner => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    [SupportedOSPlatform("windows")]
    private static bool RecentItemsShownNow()
    {
        using RegistryKey? advanced = Registry.CurrentUser.OpenSubKey(Advanced);
        return advanced?.GetValue(RecentItemsShown) is not int shown || shown != 0;
    }

    [SupportedOSPlatform("windows")]
    private (string? Refused, IReadOnlyList<string> Removed) Set((string Path, string Name)[] newest, bool recentItems)
    {
        List<string> removed = [];
        using RegistryKey advanced = Registry.CurrentUser.CreateSubKey(Advanced, writable: true);
        object? before = advanced.GetValue(RecentItemsShown);
        RegistryValueKind kind = before is null ? RegistryValueKind.DWord : advanced.GetValueKind(RecentItemsShown);
        advanced.SetValue(RecentItemsShown, recentItems ? 1 : 0, RegistryValueKind.DWord);
        try
        {
            string? refused = WindowsJumpList.Set(RecentShortcut.Recent(newest, _ => true), [RecentShortcuts.NewWindow], removed.AddRange, appId);
            return (refused, (IReadOnlyList<string>)removed);
        }
        finally
        {
            if (before is null)
            {
                advanced.DeleteValue(RecentItemsShown, throwOnMissingValue: false);
            }
            else
            {
                advanced.SetValue(RecentItemsShown, before, kind);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private Task<string?> DeleteAsync() => OnStaThread(() => WindowsJumpList.Delete(appId));

    /// <summary>The text of the shell's file of the test's list, known by the folder only this test's recent ones name.</summary>
    private static string? Kept(string folder) => KeptIn(folder) is { } file ? Read(file) : null;

    private static string? KeptIn(string folder)
    {
        if (!Directory.Exists(CustomDestinations))
        {
            return null;
        }
        string marker = Path.GetFileName(folder);
        return Directory.EnumerateFiles(CustomDestinations, "*.customDestinations-ms").FirstOrDefault(file => Read(file)?.Contains(marker, StringComparison.Ordinal) == true);
    }

    /// <summary>The words of a file of the shell's, its UTF-16 read from either byte, as a link's strings start at either;
    /// null while the shell writes it.</summary>
    private static string? Read(string file)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            return Encoding.Unicode.GetString(bytes) + "\n" + Encoding.Unicode.GetString(bytes, 1, Math.Max(0, bytes.Length - 1));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The work on a thread of a single-threaded apartment's, where the shell's objects are made and called.</summary>
    [SupportedOSPlatform("windows")]
    private static Task<T> OnStaThread<T>(Func<T> work)
    {
        TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "Sets the test's jump list",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
