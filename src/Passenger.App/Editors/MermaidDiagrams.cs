using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Passenger.Core;

namespace Passenger.App.Editors;

/// <summary>Mermaid diagrams drawn as pictures, as Cursor draws a plan's: Mermaid's own command line (run with npx at a
/// pinned version, which downloads a headless browser the first time) draws each one to a PNG in Passenger's cache, named
/// by a hash of its text and theme, so a diagram drawn once is never drawn again. One drawing of a text at a time.</summary>
public static class MermaidDiagrams
{
    /// <summary>Mermaid's command line, at a version two weeks old or more when pinned.</summary>
    public const string Cli = "@mermaid-js/mermaid-cli@11.17.1";

    /// <summary>How long a drawing may take: the first one downloads the browser it draws in.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, Task<string>> Drawing = new(StringComparer.Ordinal);

    /// <summary>Where the pictures are kept.</summary>
    public static string Cache => Path.Combine(State.StateStore.DefaultRoot, "cache", "mermaid");

    /// <summary>Whether a diagram is drawn already, so it shows at once.</summary>
    public static bool Drawn(string source, bool dark) => File.Exists(PictureOf(source, dark));

    /// <summary>A diagram's picture, drawn first where it is not yet; fails with what Mermaid said where it cannot be.</summary>
    public static Task<string> DrawAsync(string source, bool dark)
    {
        string picture = PictureOf(source, dark);
        if (File.Exists(picture))
        {
            return Task.FromResult(picture);
        }
        return Drawing.GetOrAdd(picture, _ => DrawnAsync(source, dark, picture));
    }

    /// <summary>A diagram's picture decoded, off the window's thread.</summary>
    public static async Task<Bitmap> LoadAsync(string source, bool dark)
    {
        string picture = await DrawAsync(source, dark);
        return await BackgroundWork.Run(() => new Bitmap(picture));
    }

    private static async Task<string> DrawnAsync(string source, bool dark, string picture)
    {
        try
        {
            Directory.CreateDirectory(Cache);
            // npx is a .cmd script on Windows, which only cmd runs; the diagram goes in on standard input ("-i -").
            string[] arguments = ["-y", Cli, "-i", "-", "-o", picture, "-t", dark ? "dark" : "default", "-b", "transparent", "-s", "2"];
            ProcessStartInfo start = OperatingSystem.IsWindows()
                ? ChildProcesses.Command("cmd.exe", ["/d", "/c", "npx", .. arguments], Cache)
                : ChildProcesses.Command("npx", arguments, Cache);
            ChildResult result = await ChildProcesses.RunAsync(start, source, Limit);
            if (!result.Succeeded || !File.Exists(picture))
            {
                string said = (result.Error.Length > 0 ? result.Error : result.Output).Trim();
                throw new InvalidOperationException(said.Length > 0 ? said.Split('\n')[^1].Trim() : $"Mermaid ended with {result.ExitCode}");
            }
            return picture;
        }
        finally
        {
            Drawing.TryRemove(picture, out _);
        }
    }

    private static string PictureOf(string source, bool dark)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Cli}\n{(dark ? "dark" : "light")}\n{source.Trim()}")));
        return Path.Combine(Cache, $"{hash[..32]}.png");
    }
}
