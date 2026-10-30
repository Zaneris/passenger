using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Passenger.NodePackages;

/// <summary>Puts every package a package-lock.json pins into a folder's node_modules, as <c>npm ci --ignore-scripts</c>
/// lays them out, with no Node.js: each tarball is downloaded from the registry the lock file names (or taken from the
/// cache it was downloaded to before), checked against the lock file's sha512 <c>integrity</c>, and unpacked. What the
/// build hands over is a file of <c>name=value</c> lines. A tarball whose hash differs, a package only for some systems,
/// or a file to leave out that no package carries any longer fails the build, naming it.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Passenger.NodePackages : error PNP0001: takes one argument, the file of what to do");
            return 1;
        }
        try
        {
            Inputs inputs = Inputs.Read(args[0]);
            await Packages.InstallAsync(inputs);
            return 0;
        }
        catch (PackageException e)
        {
            // MSBuild's own form, so the build fails with the line as an error.
            Console.Error.WriteLine($"Passenger.NodePackages : error PNP0002: {e.Message}");
            return 1;
        }
    }
}

/// <summary>Why the packages could not be put in place.</summary>
public sealed class PackageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What the build hands over: the lock file, where downloaded tarballs are kept, the folder the packages go in,
/// the stamp written once they are all there, and the files left out of them (globs from that folder).</summary>
public sealed class Inputs
{
    public string Lock { get; private set; } = "";
    public string Cache { get; private set; } = "";
    public string Out { get; private set; } = "";
    public string Stamp { get; private set; } = "";
    public List<string> LeftOut { get; } = [];

    /// <summary>Files moved where a package looks for them rather than where npm lays them (a glob, and the folder they go
    /// to, both from the folder the packages go in).</summary>
    public List<(string Glob, string To)> Moved { get; } = [];

    public static Inputs Read(string path)
    {
        Inputs inputs = new();
        foreach (string line in File.ReadAllLines(path).Where(l => l.Length > 0))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                throw new PackageException($"{path}: '{line}' is not name=value");
            }
            string value = line[(equals + 1)..].Trim();
            switch (line[..equals].Trim())
            {
                case "lock":
                    inputs.Lock = value;
                    break;
                case "cache":
                    inputs.Cache = value;
                    break;
                case "out":
                    inputs.Out = value;
                    break;
                case "stamp":
                    inputs.Stamp = value;
                    break;
                case "leaveOut":
                    inputs.LeftOut.Add(value.Replace('\\', '/'));
                    break;
                case "move":
                    string[] parts = value.Replace('\\', '/').Split('|', 2);
                    if (parts.Length != 2)
                    {
                        throw new PackageException($"{path}: move takes a glob and the folder its files go to, as glob|folder, not '{value}'");
                    }
                    inputs.Moved.Add((parts[0], parts[1]));
                    break;
                default:
                    throw new PackageException($"{path}: '{line[..equals]}' is not something this tool reads");
            }
        }
        if (inputs.Lock.Length == 0 || inputs.Cache.Length == 0 || inputs.Out.Length == 0 || inputs.Stamp.Length == 0)
        {
            throw new PackageException($"{path} needs lock, cache, out and stamp");
        }
        return inputs;
    }
}

/// <summary>One package as the lock file pins it: where it goes under the folder, its version, its tarball and the hash
/// that tarball must have.</summary>
public sealed record Locked(string Place, string Version, Uri Tarball, byte[] Sha512)
{
    /// <summary>Its tarball's name in the cache, from its hash, so a tarball cached is the one the lock file names.</summary>
    public string CacheName => $"{Path.GetFileName(Place)}-{Version}-{Convert.ToHexStringLower(Sha512)[..32]}.tgz";
}

public static class Packages
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false, ActivityHeadersPropagator = null })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public static Task InstallAsync(Inputs inputs) => InstallAsync(inputs, Http.GetByteArrayAsync);

    /// <summary>Puts the packages in place as <see cref="InstallAsync(Inputs)"/> does, each tarball not cached fetched by
    /// <paramref name="download"/>.</summary>
    public static async Task InstallAsync(Inputs inputs, Func<Uri, Task<byte[]>> download)
    {
        byte[] lockBytes = await File.ReadAllBytesAsync(inputs.Lock);
        IReadOnlyList<Locked> packages = Read(lockBytes, inputs.Lock);
        Directory.CreateDirectory(inputs.Cache);
        string staging = inputs.Out.TrimEnd('/', '\\') + ".new";
        // Two builds at once (a solution's and a publish's) take turns, the second finding the packages in place.
        await using FileStream turn = await TakeTurnAsync(inputs.Out.TrimEnd('/', '\\') + ".lock");
        string stamp = Stamp(lockBytes, inputs);
        if (File.Exists(inputs.Stamp) && await File.ReadAllTextAsync(inputs.Stamp) == stamp && Directory.Exists(inputs.Out))
        {
            return;
        }
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
        Directory.CreateDirectory(staging);
        // Unpacked in the lock file's order, so a package's folder is there before one nested in it is unpacked.
        using SemaphoreSlim downloads = new(6);
        byte[][] tarballs = await Task.WhenAll(packages.Select(async package =>
        {
            await downloads.WaitAsync();
            try
            {
                return await TarballAsync(package, inputs.Cache, download);
            }
            finally
            {
                downloads.Release();
            }
        }));
        for (int i = 0; i < packages.Count; i++)
        {
            await UnpackAsync(tarballs[i], Path.Combine(staging, packages[i].Place), packages[i]);
        }
        LeaveOut(staging, inputs.LeftOut);
        Move(staging, inputs.Moved);
        if (File.Exists(inputs.Stamp))
        {
            File.Delete(inputs.Stamp);
        }
        if (Directory.Exists(inputs.Out))
        {
            Directory.Delete(inputs.Out, recursive: true);
        }
        Directory.Move(staging, inputs.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(inputs.Stamp))!);
        await File.WriteAllTextAsync(inputs.Stamp, stamp);
        Console.WriteLine($"Passenger.NodePackages: {packages.Count} packages from {Path.GetFileName(inputs.Lock)}, each checked against its sha512, in {inputs.Out}");
    }

    /// <summary>The packages a lock file (npm's lockfileVersion 2 or 3) pins, in its order: every one under node_modules/
    /// but a development one or a link.</summary>
    public static IReadOnlyList<Locked> Read(byte[] lockFile, string path)
    {
        using JsonDocument document = JsonDocument.Parse(lockFile);
        if (!document.RootElement.TryGetProperty("packages", out JsonElement entries) || entries.ValueKind != JsonValueKind.Object)
        {
            throw new PackageException($"{path} has no \"packages\": it is not a lock file of npm 7 or later (lockfileVersion 2 or 3)");
        }
        List<Locked> packages = [];
        foreach (JsonProperty entry in entries.EnumerateObject())
        {
            if (entry.Name.Length == 0)
            {
                continue;
            }
            JsonElement value = entry.Value;
            if (Flag(value, "dev") || Flag(value, "link"))
            {
                continue;
            }
            if (!entry.Name.StartsWith("node_modules/", StringComparison.Ordinal) || entry.Name.Split('/').Any(part => part is "" or "." or ".."))
            {
                throw new PackageException($"{path}: '{entry.Name}' is not a place under node_modules/");
            }
            if (value.TryGetProperty("os", out _) || value.TryGetProperty("cpu", out _))
            {
                throw new PackageException($"{path}: {entry.Name} is only for some systems, and Passenger carries one copy of these packages for every system");
            }
            string version = Text(value, "version") ?? throw new PackageException($"{path}: {entry.Name} has no version");
            string resolved = Text(value, "resolved") ?? throw new PackageException($"{path}: {entry.Name} has no resolved tarball");
            string integrity = Text(value, "integrity") ?? throw new PackageException($"{path}: {entry.Name} has no integrity");
            if (!Uri.TryCreate(resolved, UriKind.Absolute, out Uri? tarball) || tarball.Scheme != Uri.UriSchemeHttps)
            {
                throw new PackageException($"{path}: {entry.Name}'s tarball {resolved} is not an https URL");
            }
            string? sha512 = integrity.Split(' ').FirstOrDefault(h => h.StartsWith("sha512-", StringComparison.Ordinal));
            if (sha512 is null)
            {
                throw new PackageException($"{path}: {entry.Name} has no sha512 integrity ('{integrity}'), the only hash this tool takes");
            }
            packages.Add(new Locked(entry.Name, version, tarball, Convert.FromBase64String(sha512["sha512-".Length..])));
        }
        return packages;
    }

    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    /// <summary>What the folder was made from: the lock file's hash and the files left out and moved, which a build compares.</summary>
    private static string Stamp(byte[] lockFile, Inputs inputs) =>
        $"{Convert.ToHexStringLower(SHA256.HashData(lockFile))}\n{string.Join('\n', inputs.LeftOut)}\n{string.Join('\n', inputs.Moved.Select(m => $"{m.Glob}|{m.To}"))}\n";

    private static async Task<FileStream> TakeTurnAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        DateTime until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    /// <summary>The package's tarball as the lock file pins it: the cache's, or downloaded into the cache when it is not there
    /// or not the one the lock file names.</summary>
    private static async Task<byte[]> TarballAsync(Locked package, string cache, Func<Uri, Task<byte[]>> download)
    {
        string path = Path.Combine(cache, package.CacheName);
        if (await CachedAsync(path) is { } cached && Matches(cached, package.Sha512))
        {
            return cached;
        }
        byte[] downloaded = [];
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                downloaded = await download(package.Tarball);
                break;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                throw new PackageException($"{package.Place} {package.Version} could not be downloaded from {package.Tarball}: {error.Message}", error);
            }
        }
        if (!Matches(downloaded, package.Sha512))
        {
            throw new PackageException($"{package.Place} {package.Version} from {package.Tarball} is not the tarball the lock file pins: its sha512 differs");
        }
        // A file of this download's own, so installs filling one cache at once never write or move each other's.
        string partial = Path.Combine(cache, $"{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllBytesAsync(partial, downloaded);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            // Another install put the same tarball there first, which it may be reading or replacing still (Windows refuses the move then).
            if (await CachedAsync(path) is not { } other || !Matches(other, package.Sha512))
            {
                throw;
            }
        }
        finally
        {
            File.Delete(partial);
        }
        return downloaded;
    }

    /// <summary>A tarball in the cache, read whole as it stands, or null where the cache has none. Windows refuses a read while
    /// another install's move replaces the file, so a refused read is tried again until the move is done.</summary>
    private static async Task<byte[]?> CachedAsync(string path)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return await File.ReadAllBytesAsync(path);
            }
            catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                throw new PackageException($"{path}, a tarball in the cache, could not be read: {refused.Message}", refused);
            }
        }
    }

    private static bool Matches(byte[] tarball, byte[] sha512) => CryptographicOperations.FixedTimeEquals(SHA512.HashData(tarball), sha512);

    /// <summary>A tarball's files under a package's folder, without the one folder npm packs them in (<c>package/</c>).</summary>
    private static async Task UnpackAsync(byte[] tarball, string folder, Locked package)
    {
        string root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        await using MemoryStream file = new(tarball, writable: false);
        await using GZipStream unzipped = new(file, CompressionMode.Decompress);
        using TarReader reader = new(unzipped);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            int slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
            string inside = slash < 0 ? string.Empty : entry.Name[(slash + 1)..];
            if (inside.Length == 0)
            {
                continue;
            }
            string target = Path.GetFullPath(Path.Combine(root, inside));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new PackageException($"{package.Place} {package.Version}'s tarball has {entry.Name}, which is outside its folder");
            }
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (FileStream written = File.Create(target))
                    {
                        if (entry.DataStream is { } data)
                        {
                            await data.CopyToAsync(written);
                        }
                    }
                    break;
                default:
                    throw new PackageException($"{package.Place} {package.Version}'s tarball has {entry.Name}, a {entry.EntryType}, which npm would not unpack either");
            }
        }
    }

    /// <summary>Deletes the files the globs name (from the folder, '/' between parts, '*' within a part, '**' across them);
    /// a glob that names nothing is stale and fails.</summary>
    private static void LeaveOut(string folder, IReadOnlyList<string> globs)
    {
        foreach (string glob in globs)
        {
            foreach (string file in Matching(folder, glob, "left out"))
            {
                File.Delete(Path.Combine(folder, file));
            }
        }
    }

    /// <summary>Moves the files the globs name into the folders given (from the folder): where a package reads them, its
    /// own path to them wrong for npm's layout. A glob that names nothing is stale and fails.</summary>
    private static void Move(string folder, IReadOnlyList<(string Glob, string To)> moves)
    {
        foreach ((string glob, string to) in moves)
        {
            string target = Path.Combine(folder, to);
            Directory.CreateDirectory(target);
            foreach (string file in Matching(folder, glob, "moved"))
            {
                File.Move(Path.Combine(folder, file), Path.Combine(target, Path.GetFileName(file)));
            }
        }
    }

    /// <summary>The files under the folder a glob names ('/' between parts, '*' within a part, '**' across them), from it.</summary>
    private static string[] Matching(string folder, string glob, string what)
    {
        Regex pattern = new("^" + Regex.Escape(glob).Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$");
        string[] matched = [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => pattern.IsMatch(f))];
        if (matched.Length == 0)
        {
            throw new PackageException($"no package carries {glob}, which is {what}: the packages have changed, so check what is {what} again");
        }
        return matched;
    }
}
