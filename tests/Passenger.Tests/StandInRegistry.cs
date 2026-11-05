using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Passenger.Tests;

/// <summary>An OCI registry on a loopback port of its own that serves one small base image, standin/base:1, for the SDK's
/// container publish to build on: a manifest, its config for this machine's processor on Linux, and one layer of a single
/// file, so no test pulls a real base image; given several processors, an index of a manifest for each. The SDK reaches
/// localhost's registries over HTTP, with no login. Its image name, for a project's ContainerBaseImage, is <see cref="BaseImage"/>.</summary>
public sealed class StandInRegistry : IDisposable, IAsyncDisposable
{
    private const string ManifestType = "application/vnd.oci.image.manifest.v1+json";
    private const string IndexType = "application/vnd.oci.image.index.v1+json";

    private readonly StandInHttp http;
    private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);
    private readonly byte[] manifest;
    private readonly string manifestType;
    private readonly Dictionary<string, byte[]> manifests = new(StringComparer.Ordinal);

    /// <param name="architectures">Docker's names of the processors it serves (amd64, arm64); none, this machine's.</param>
    public StandInRegistry(params string[] architectures)
    {
        byte[] layer = Layer(out byte[] tar);
        blobs[Digest(layer)] = layer;
        string[] served = architectures.Length > 0 ? architectures : [RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64"];
        foreach (string architecture in served)
        {
            byte[] config = JsonSerializer.SerializeToUtf8Bytes(new
            {
                architecture,
                os = "linux",
                config = new { Env = new[] { "PATH=/usr/local/bin:/usr/bin:/bin" }, WorkingDir = "/" },
                rootfs = new { type = "layers", diff_ids = new[] { Digest(tar) } },
                history = new[] { new { created_by = "stand-in" } },
            });
            blobs[Digest(config)] = config;
            byte[] one = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                mediaType = ManifestType,
                config = new { mediaType = "application/vnd.oci.image.config.v1+json", digest = Digest(config), size = config.Length },
                layers = new[] { new { mediaType = "application/vnd.oci.image.layer.v1.tar+gzip", digest = Digest(layer), size = layer.Length } },
            });
            manifests[architecture] = one;
        }
        if (served.Length == 1)
        {
            manifest = manifests[served[0]];
            manifestType = ManifestType;
        }
        else
        {
            manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                mediaType = IndexType,
                manifests = served.Select(a => new { mediaType = ManifestType, digest = Digest(manifests[a]), size = manifests[a].Length, platform = new { architecture = a, os = "linux" } }).ToArray(),
            });
            manifestType = IndexType;
        }
        http = new StandInHttp(AnswerAsync, keepAlive: true);
    }

    /// <summary>The base image as a project names it.</summary>
    public string BaseImage => $"localhost:{http.Port}/standin/base:1";

    /// <summary>The connections clients have made to it.</summary>
    public int Connections => http.Connections;

    /// <summary>The requests it has had, method and target.</summary>
    public IReadOnlyList<string> Requests => [.. requests];

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> requests = new();

    private async Task AnswerAsync(StandInHttp.Exchange exchange)
    {
        requests.Enqueue(exchange.Line);
        string path = exchange.Path.Split('?')[0];
        (byte[] Body, string Type)? found = path is "/v2/" or "/v2" ? ("{}"u8.ToArray(), "application/json")
            : path.EndsWith("/manifests/1", StringComparison.Ordinal) || path.EndsWith($"/manifests/{Digest(manifest)}", StringComparison.Ordinal) ? (manifest, manifestType)
            : manifests.Values.FirstOrDefault(one => path.EndsWith($"/manifests/{Digest(one)}", StringComparison.Ordinal)) is { } platform ? (platform, ManifestType)
            : blobs.FirstOrDefault(blob => path.EndsWith($"/blobs/{blob.Key}", StringComparison.Ordinal)) is { Value: { } bytes } ? (bytes, "application/octet-stream")
            : null;
        if (found is not { } answer)
        {
            await exchange.ReplyAsync(404, "application/json", "{\"errors\":[{\"code\":\"NAME_UNKNOWN\"}]}");
            return;
        }
        string headers = $"Docker-Content-Digest: {Digest(answer.Body)}\r\nDocker-Distribution-API-Version: registry/2.0\r\n";
        if (exchange.Method == "HEAD")
        {
            await exchange.HeadAsync(200, answer.Type, answer.Body.Length, headers);
        }
        else
        {
            await exchange.ReplyAsync(200, answer.Type, answer.Body, headers);
        }
    }

    /// <summary>A layer of one file, gzipped, and the tar it holds.</summary>
    private static byte[] Layer(out byte[] tar)
    {
        using MemoryStream archive = new();
        using (TarWriter writer = new(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, "etc/standin") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("stand-in base\n")), Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead };
            writer.WriteEntry(entry);
        }
        tar = archive.ToArray();
        using MemoryStream zipped = new();
        using (GZipStream gzip = new(zipped, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(tar);
        }
        return zipped.ToArray();
    }

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose() => http.Dispose();

    public ValueTask DisposeAsync() => http.DisposeAsync();
}
