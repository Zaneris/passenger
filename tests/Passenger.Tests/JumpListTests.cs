using System.Runtime.Versioning;
using System.Text;
using Passenger.App.Welcome;

namespace Passenger.Tests;

/// <summary>Windows' taskbar jump list as Passenger writes it, through the shell's own ICustomDestinationList, read back
/// from the file the shell keeps it in (CustomDestinations, one file per app ID). The test writes under an app ID of its
/// own, never Passenger's, and deletes that list as it ends.</summary>
[Collection(Beside.Name)]
public sealed class JumpListTests : IDisposable
{
    private readonly string folder = TestFolders.Create("jump-list-");

    private readonly string appId = "Passenger.Tests.JumpList." + Guid.NewGuid().ToString("N");

    public void Dispose() => TestFolders.Delete(folder);

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task The_recent_category_holds_a_link_to_Passenger_for_each_recent_path_by_its_title()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The taskbar's jump list is Windows'");
            return;
        }
        string shop = Directory.CreateDirectory(Path.Combine(folder, "Shop folder")).FullName;
        string solution = Path.Combine(folder, "Garden.slnx");
        File.WriteAllText(solution, "<Solution />");
        string exe = Environment.ProcessPath!;
        IReadOnlyList<(string Path, string Title)> entries = [(solution, "Garden.slnx"), (shop, "Shop folder")];
        try
        {
            IReadOnlyList<string> removed = await OnStaThread(() => JumpList.Write(exe, entries, appId));
            Assert.True(removed.Count == 0, $"A list never shown says the person removed {string.Join(", ", removed)} from it");
            string? written = null;
            Assert.True(Waiting.Until(() => (written = ListFile()) is not null, TimeSpan.FromSeconds(10)),
                $"The shell kept no jump list naming the test's app ID's entries in {CustomDestinations}");
            string text = Encoding.Unicode.GetString(File.ReadAllBytes(written!));
            foreach (string expected in new[] { "Recent", exe, "Garden.slnx", "Shop folder", RecentLists.ArgumentsOf(solution), RecentLists.ArgumentsOf(shop) })
            {
                Assert.True(text.Contains(expected, StringComparison.Ordinal),
                    $"The jump list the shell kept lacks \"{expected}\", so its Recent entry would not open the path in Passenger");
            }
        }
        finally
        {
            await OnStaThread(() =>
            {
                JumpList.Delete(appId);
                return 0;
            });
        }
        Assert.True(Waiting.Until(() => ListFile() is null, TimeSpan.FromSeconds(10)),
            $"The test's jump list is still kept in {CustomDestinations} after it was deleted");
    }

    private static string CustomDestinations =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent", "CustomDestinations");

    /// <summary>The shell's file of the test's list, known by a path only this test's entries name.</summary>
    private string? ListFile()
    {
        if (!Directory.Exists(CustomDestinations))
        {
            return null;
        }
        string marker = Path.GetFileName(folder);
        foreach (string file in Directory.EnumerateFiles(CustomDestinations, "*.customDestinations-ms"))
        {
            try
            {
                if (Encoding.Unicode.GetString(File.ReadAllBytes(file)).Contains(marker, StringComparison.Ordinal))
                {
                    return file;
                }
            }
            catch (IOException)
            {
                // The shell is writing it; the next look reads it whole.
            }
        }
        return null;
    }

    /// <summary>The work on a single-threaded apartment's thread, as the window's thread is where Passenger writes it.</summary>
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
