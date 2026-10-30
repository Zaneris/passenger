using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>What a command that wrote or deleted a migration's files said of a context's listing (and of its model's check,
/// where it knows it), before any build has them: it stands for a later build whose reads are those before the command
/// (<see cref="Before"/>, as the project host gives them) but for those files and the folders holding them, each as the
/// command left it (<see cref="Left"/>), so that build's listing and check need no dotnet ef. <see cref="Built"/> is what
/// the build before the command made, which a build with the files changes, and <see cref="Others"/> what else dotnet ef
/// read then (the Migrations window's stamps of them).</summary>
internal sealed record EfTold(string Context, string Others, string Built, IReadOnlyList<Stamp> Before, IReadOnlyDictionary<string, Stamp> Left, bool? Changed)
{
    /// <summary>The longest the host is waited for to say what a build reads, past which the listing is read again.</summary>
    private static readonly TimeSpan Answering = TimeSpan.FromSeconds(10);

    /// <summary>What builds of the setting's startup project and project read now, as the project host evaluates them with
    /// the global properties the window's builds give; null when the host cannot say all of it.</summary>
    public static async Task<IReadOnlyList<Stamp>?> ReadsAsync(EfSetting setting, CancellationToken cancel)
    {
        List<Stamp> reads = [];
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bounded.CancelAfter(Waits.Of(Answering));
        foreach (ProjectModel each in setting.Project == setting.Startup ? [setting.Startup] : new[] { setting.Startup, setting.Project })
        {
            Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
            if (each == setting.Startup && setting.Framework is { } framework)
            {
                properties[MSBuildNames.TargetFramework] = framework;
            }
            try
            {
                ProjectHost host = await ProjectHosts.ForAsync(each.Folder, bounded.Token);
                BuildInputs inputs = await host.InputsAsync(each.Path, properties, bounded.Token);
                if (inputs.Unknown is not null)
                {
                    return null;
                }
                reads.AddRange(inputs.Reads);
            }
            catch (Exception unknown) when (unknown is ProjectHostFailed or InvalidOperationException or IOException
                || (unknown is OperationCanceledException && bounded.IsCancellationRequested))
            {
                return null;
            }
        }
        return reads;
    }

    /// <summary>The files a command wrote or deleted as it left them, with the model snapshots beside them (which remove
    /// writes without naming), and each folder above them up to the project's folder or one a read before it stamped; null
    /// where a folder of them cannot be read. On any thread.</summary>
    public static IReadOnlyDictionary<string, Stamp>? LeftOf(IReadOnlyList<string> files, string project, IReadOnlyList<Stamp> before)
    {
        HashSet<string> stamped = new(before.Where(s => s.Folder).Select(s => s.Path), PathNames.Comparer);
        Dictionary<string, Stamp> left = new(PathNames.Comparer);
        try
        {
            string[] snapshots = [.. files.Select(Path.GetDirectoryName).OfType<string>().Distinct(PathNames.Comparer).Where(Directory.Exists)
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*ModelSnapshot.cs"))];
            foreach (string file in files.Concat(snapshots).Select(PathNames.Canonical))
            {
                left[file] = Stamps.File(file);
                for (string? folder = Path.GetDirectoryName(file);
                    folder is not null && (PathNames.Same(folder, project) || PathNames.IsUnder(folder, project) || stamped.Contains(folder));
                    folder = Path.GetDirectoryName(folder))
                {
                    left[folder] = Stamps.Folder(folder);
                }
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return left;
    }

    /// <summary>Whether reads taken after a build stand for what the command said: the same as before it but for the files
    /// and folders it left, which are as it left them, each file it wrote among them. A file read as not there that the reads
    /// before it never stamped, or one they stamped as not there that is gone from them, reads nothing either way.</summary>
    public bool Stands(IReadOnlyList<Stamp> after)
    {
        HashSet<Stamp> others = [.. Before.Where(s => !Left.ContainsKey(s.Path))];
        HashSet<string> stamped = new(Before.Select(s => s.Path), PathNames.Comparer);
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (Stamp read in after)
        {
            if (Left.TryGetValue(read.Path, out Stamp? left) ? left != read : !others.Contains(read) && !(Absent(read) && !stamped.Contains(read.Path)))
            {
                return false;
            }
            seen.Add(read.Path);
        }
        return others.All(s => Absent(s) || seen.Contains(s.Path)) && Left.Values.Where(s => !s.Folder && s.Length >= 0).All(s => seen.Contains(s.Path));
    }

    private static bool Absent(Stamp stamp) => !stamp.Folder && stamp.Length < 0;
}
