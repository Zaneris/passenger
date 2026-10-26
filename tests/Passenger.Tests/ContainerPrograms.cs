using System.Diagnostics;
using System.Globalization;
using System.Text;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What the container tests run in a container: a .NET 10 console program built here in Debug and copied into an
/// image of .NET's runtime, labelled as the run's, and Passenger's container tools published where a dev build finds them
/// (artifacts/container-tools), as <c>just container-tools</c> makes them: some seconds when they are up to date. Made by
/// the container classes' fixtures, which await each.</summary>
internal static class ContainerPrograms
{
    /// <summary>The engine's processor, as .NET names it (x64, arm64): Podman's own docker has no server apart in its version,
    /// and words it in its info.</summary>
    public static async Task<string> ProcessorAsync() =>
        (await Docker.SaidAsync("version", "--format", "{{.Server.Arch}}") ?? await Docker.SaidAsync("info", "--format", "{{.Host.Arch}}")) == "arm64" ? "arm64" : "x64";

    /// <summary>Each system's publish, once a run: a second, as another test's IDE copies the tools into a container, would
    /// rewrite them under it, and the IDE would then look for them elsewhere (a download from the run's dead address).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<ChildResult>>> Published = new(StringComparer.Ordinal);

    /// <summary>Publishes the tools for a system into artifacts/container-tools, as tools/container-tools.sh does, once a run,
    /// and one run of the checkout at a time (<see cref="Publishing"/>): another's, beside it, finds them up to date and
    /// writes nothing under a container that is reading them.</summary>
    public static async Task PublishAsync(string rid)
    {
        string tools = Path.Combine(Debugging.Helpers.Debuggees.Root, "artifacts", "container-tools");
        string output = Path.Combine(tools, rid);
        ChildResult published = await Published.GetOrAdd(rid, _ => new Lazy<Task<ChildResult>>(() => Waiting.Apart(async () =>
        {
            using FileStream held = await PublishingAsync(tools);
            return await Dotnet(["publish", Path.Combine(Debugging.Helpers.Debuggees.Root, "src", "Passenger.ContainerTools"),
                "-c", "Release", "-r", rid, "--self-contained", "-o", output, "-nologo", "-v:q"], Debugging.Helpers.Debuggees.Root);
        }), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        Assert.True(published.Succeeded, $"Passenger's container tools for {rid} did not publish: {published.Output}{published.Error}");
    }

    /// <summary>The checkout's lock on publishing the tools, held open by one process at a time (the system lets it go with
    /// a process that ends without letting go), waited for while another test run of the checkout publishes.</summary>
    private static async Task<FileStream> PublishingAsync(string tools)
    {
        Directory.CreateDirectory(tools);
        string path = Path.Combine(tools, ".publishing");
        FileStream? held = null;
        Assert.True(await Waiting.UntilAsync(() => (held = Open()) is not null, TimeSpan.FromMinutes(10), every: TimeSpan.FromMilliseconds(250)),
            $"Another test run of this checkout held {path} for 10 minutes, publishing Passenger's container tools");
        return held!;

        FileStream? Open()
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>A program's project in <paramref name="folder"/>, built in Debug into <paramref name="output"/>, its paths
    /// mapped to <paramref name="mapped"/> (the PDB naming them so) when given.</summary>
    public static async Task BuildAsync(string folder, string project, string program, string output, string? mapped = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Path.GetFileName(folder) + ".csproj"), project);
        File.WriteAllText(Path.Combine(folder, "Program.cs"), program);
        List<string> words = ["build", Path.Combine(folder, Path.GetFileName(folder) + ".csproj"), "-c", "Debug", "-o", output, "-nologo", "-v:q"];
        if (mapped is not null)
        {
            words.Add($"-p:PathMap={PathNames.Canonical(folder)}{Path.DirectorySeparatorChar}={mapped}");
        }
        ChildResult built = await Dotnet(words, folder);
        Assert.True(built.Succeeded, $"{Path.GetFileName(folder)} did not build: {built.Output}{built.Error}");
    }

    /// <summary>An image of a build on a runtime image, running <paramref name="assembly"/> with dotnet, with sources at
    /// /src/NAME when given; its tag, labelled as the run's.</summary>
    public static async Task<string> ImageAsync(string context, string variant, string from, string build, string assembly, string? sources = null, string? sourcesAt = null)
    {
        Directory.CreateDirectory(context);
        CopyFolder(build, Path.Combine(context, "app"));
        StringBuilder dockerfile = new($"FROM {from}\nCOPY app/ /app/\n");
        if (sources is not null && sourcesAt is not null)
        {
            Directory.CreateDirectory(Path.Combine(context, "src"));
            File.Copy(Path.Combine(sources, "Program.cs"), Path.Combine(context, "src", "Program.cs"));
            dockerfile.Append($"COPY src/ {sourcesAt}\n");
        }
        dockerfile.Append($"ENTRYPOINT [\"dotnet\", \"/app/{assembly}.dll\"]\n");
        File.WriteAllText(Path.Combine(context, "Dockerfile"), dockerfile.ToString());
        string tag = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{assembly.ToLowerInvariant()}:{variant}";
        await Docker.RunAsync(["build", "-q", .. await Docker.LoadAsync(), "--label", Docker.RunLabel, "-t", tag, context]);
        return tag;
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string into = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(into)!);
            File.Copy(file, into, overwrite: true);
        }
    }

    private static Task<ChildResult> Dotnet(IReadOnlyList<string> words, string folder)
    {
        ProcessStartInfo start = ChildProcesses.DotnetCommand(words);
        start.WorkingDirectory = folder;
        return ChildProcesses.RunAsync(start, null, TimeSpan.FromMinutes(10).Scaled(), CancellationToken.None);
    }
}
