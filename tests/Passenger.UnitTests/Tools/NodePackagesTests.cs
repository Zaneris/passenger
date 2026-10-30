using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using Inputs = Passenger.NodePackages.Inputs;
using NodeInstall = Passenger.NodePackages.Packages;

namespace Passenger.UnitTests.Tools;

/// <summary>The build's install of npm packages from a lock file (tools/Passenger.NodePackages) beside another into the same
/// download cache, as two worktrees' builds, or a restore beside a publish, fill it: each download lands in the cache whole,
/// whichever install wrote it, and both installs succeed with every package in place.</summary>
public sealed class NodePackagesTests : IDisposable
{
    private const int Count = 12;

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Two_installs_filling_one_empty_cache_at_once_both_succeed_with_every_package_whole()
    {
        Dictionary<Uri, byte[]> tarballs = [];
        Dictionary<string, object> locked = new() { [""] = new { name = "two-installs", version = "1.0.0" } };
        for (int i = 0; i < Count; i++)
        {
            byte[] tarball = Tarball($"p{i}", i);
            Uri resolved = new($"https://registry.test/p{i}/-/p{i}-1.0.0.tgz");
            tarballs[resolved] = tarball;
            locked[$"node_modules/p{i}"] = new { version = "1.0.0", resolved = resolved.AbsoluteUri, integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)) };
        }
        string lockFile = Path.Join(root, "package-lock.json");
        File.WriteAllText(lockFile, JsonSerializer.Serialize(new { lockfileVersion = 3, packages = locked }));
        // Each download handed back off the asking thread, so the two installs' writes of the same tarball meet.
        Func<Uri, Task<byte[]>> download = async url =>
        {
            await Task.Yield();
            return tarballs[url];
        };

        for (int round = 0; round < 20; round++)
        {
            string cache = Path.Join(root, $"cache-{round}");
            Inputs[] installs = [.. new[] { "a", "b" }.Select(name => Read(lockFile, cache, Path.Join(root, $"{name}-{round}")))];
            Task[] running = [.. installs.Select(inputs => BackgroundWork.Run(() => NodeInstall.InstallAsync(inputs, download)))];
            Exception? failed = await Task.WhenAll(running).ContinueWith(all => all.Exception?.GetBaseException(), TaskScheduler.Default);
            Assert.True(failed is null, $"In round {round}, an install beside another into one empty cache failed, failing its build: {failed}");
            foreach (Inputs inputs in installs)
            {
                for (int i = 0; i < Count; i++)
                {
                    Assert.True(File.ReadAllText(Path.Join(inputs.Out, "node_modules", $"p{i}", "index.js")) == Body(i),
                        $"In round {round}, p{i} unpacked into {inputs.Out} is not the tarball's own file, so the build would carry a broken package");
                }
            }
            Assert.True(Directory.EnumerateFiles(cache, "*.partial").ToArray() is [], $"In round {round}, downloads left half-written files in the cache");
        }
    }

    private Inputs Read(string lockFile, string cache, string output)
    {
        string told = output + ".txt";
        File.WriteAllText(told, $"lock={lockFile}\ncache={cache}\nout={output}\nstamp={output}.stamp\n");
        return Inputs.Read(told);
    }

    /// <summary>A package's tarball as npm packs it, under <c>package/</c>, its file large enough that writing it takes a while.</summary>
    private static byte[] Tarball(string name, int index)
    {
        using MemoryStream packed = new();
        using (GZipStream zipped = new(packed, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(zipped, TarEntryFormat.Pax))
        {
            Add(writer, "package/package.json", $"{{\"name\":\"{name}\",\"version\":\"1.0.0\"}}");
            Add(writer, "package/index.js", Body(index));
        }
        return packed.ToArray();
    }

    private static void Add(TarWriter writer, string name, string text) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });

    /// <summary>A package's file, different for each, of random-looking lines that compress little.</summary>
    private static string Body(int index)
    {
        Random random = new(index);
        StringBuilder text = new();
        for (int line = 0; line < 4000; line++)
        {
            text.Append("// ").Append(random.NextInt64().ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }
}
