using System.Diagnostics;
using System.Formats.Tar;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>A file or folder inside a container, at its depth in the Files tab.</summary>
public sealed record ContainerFile(string Path, string Name, bool IsFolder, int Depth);

/// <summary>A container's files, browsed lazily: a folder's entries listed as it is opened (<c>docker exec … ls</c>), and a
/// file opened read-only from a copy (<c>docker cp …:path -</c>) under the state's <c>cache/containers</c>, which every
/// editor takes as read-only (<see cref="IsCopy"/>).</summary>
public sealed class ContainerFilesPane : DockPanel
{
    /// <summary>The largest file a copy is made of.</summary>
    private const long Largest = 16 * 1024 * 1024;

    private readonly Func<ContainerEngine> engine;
    private readonly Func<string> stateFolder;
    private readonly ListBox list;
    private readonly TextBlock said = new() { Classes = { "emptyText" } };
    private readonly Dictionary<string, IReadOnlyList<ContainerFile>> listed = new(StringComparer.Ordinal);
    private readonly HashSet<string> open = new(StringComparer.Ordinal) { "/" };
    private ContainerInfo? container;

    public ContainerFilesPane(Func<ContainerEngine> engine, Func<string> stateFolder)
    {
        this.engine = engine;
        this.stateFolder = stateFolder;
        list = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<ContainerFile>((file, _) => file is null ? new Panel() : Row(file), supportsRecycling: false),
        };
        list.DoubleTapped += (_, _) => IdeErrors.Observe(ActivateAsync(list.SelectedItem as ContainerFile));
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                IdeErrors.Observe(ActivateAsync(list.SelectedItem as ContainerFile));
                e.Handled = true;
            }
        };
        Children.Add(new Panel { Children = { list, said } });
    }

    /// <summary>A file copied out of a container, to open read-only.</summary>
    public event EventHandler<string>? Opened;

    /// <summary>Something to say of a file that could not be opened.</summary>
    public event EventHandler<string>? Problem;

    /// <summary>The entries listed, folders opened among them.</summary>
    public IReadOnlyList<ContainerFile> Entries => list.ItemsSource as IReadOnlyList<ContainerFile> ?? [];

    /// <summary>Whether a path is a container file's copy, which the editor shows read-only.</summary>
    public static bool IsCopy(string path) =>
        path.Contains($"{System.IO.Path.DirectorySeparatorChar}cache{System.IO.Path.DirectorySeparatorChar}containers{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>Lists a container's root, or nothing; the same container again keeps what was opened.</summary>
    public void Show(ContainerRow? row)
    {
        UiThread.Check();
        ContainerInfo? next = row?.Container;
        if (next?.Id == container?.Id)
        {
            container = next;
            return;
        }
        container = next;
        listed.Clear();
        open.Clear();
        open.Add("/");
        list.ItemsSource = null;
        said.IsVisible = next is not null;
        said.Text = "Listing /…";
        if (next is not null)
        {
            IdeErrors.Observe(ListAsync("/"));
        }
    }

    /// <summary>Opens a folder (listing it the first time) or closes it, or opens a file read-only.</summary>
    public async Task ActivateAsync(ContainerFile? file)
    {
        if (file is null)
        {
            return;
        }
        if (!file.IsFolder)
        {
            await OpenAsync(file.Path);
            return;
        }
        if (!open.Remove(file.Path))
        {
            open.Add(file.Path);
            if (!listed.ContainsKey(file.Path))
            {
                await ListAsync(file.Path);
                return;
            }
        }
        Fill();
    }

    private async Task ListAsync(string folder)
    {
        if (container is not { } listing)
        {
            return;
        }
        ContainerEngine docker = engine();
        (IReadOnlyList<ContainerFile>? found, string? problem) = await BackgroundWork.Run(async () =>
        {
            try
            {
                ChildResult result = await docker.RunAsync(["exec", listing.Id, "ls", "-1ApL", folder], ContainerEngine.AskLimit);
                if (!result.Succeeded)
                {
                    string error = ContainerEngine.Said(result);
                    return (null, error.Contains("is not running", StringComparison.Ordinal) ? $"Start {listing.Name} to browse its files"
                        : error.Contains("executable file not found", StringComparison.Ordinal) ? $"{listing.Image} has no ls to list its files" : error);
                }
                int depth = folder == "/" ? 0 : folder.Count(c => c == '/');
                return ((IReadOnlyList<ContainerFile>?)[.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(IsName)
                    .Select(name => new ContainerFile($"{folder.TrimEnd('/')}/{name.TrimEnd('/')}", name.TrimEnd('/'), name.EndsWith('/'), depth))
                    .OrderBy(f => !f.IsFolder).ThenBy(f => f.Name, StringComparer.Ordinal)], (string?)null);
            }
            catch (Exception failed) when (failed is TimeoutException or FileNotFoundException or System.ComponentModel.Win32Exception or IOException
                or InvalidOperationException)
            {
                return (null, failed.Message);
            }
        });
        if (container?.Id != listing.Id)
        {
            return;
        }
        if (found is null)
        {
            open.Remove(folder);
            if (folder == "/")
            {
                said.Text = problem;
                said.IsVisible = true;
            }
            else
            {
                Problem?.Invoke(this, problem!);
            }
            return;
        }
        listed[folder] = found;
        Fill();
    }

    private void Fill()
    {
        object? chosen = list.SelectedItem;
        List<ContainerFile> rows = [];
        Add("/");
        list.ItemsSource = rows;
        list.SelectedItem = chosen is ContainerFile was ? rows.FirstOrDefault(r => r.Path == was.Path) : null;
        said.IsVisible = rows.Count == 0;
        said.Text = "The folder is empty";

        void Add(string folder)
        {
            foreach (ContainerFile file in listed.GetValueOrDefault(folder) ?? [])
            {
                rows.Add(file);
                if (file.IsFolder && open.Contains(file.Path))
                {
                    Add(file.Path);
                }
            }
        }
    }

    /// <summary>Copies a file out of the container under the state's cache and opens it read-only: what the Files tab and a
    /// test open. Null once opened, else why not.</summary>
    public async Task<string?> OpenAsync(string path)
    {
        if (container is not { } from)
        {
            return "No container is chosen";
        }
        ContainerEngine docker = engine();
        string cache = System.IO.Path.Combine(stateFolder(), "cache", "containers");
        string? copy = CopyPlace(cache, from.Name, path);
        string? problem = copy is null ? $"{path} is no file of the container's that Passenger copies out"
            : await BackgroundWork.Run(() => CopyAsync(docker, from.Id, path, copy, cache));
        if (problem is not null)
        {
            Problem?.Invoke(this, problem);
            return problem;
        }
        Opened?.Invoke(this, copy!);
        return null;
    }

    /// <summary>Whether a line of <c>ls -1Ap</c> is a name in the folder listed: never one with a slash but a folder's last
    /// one, nor the folder itself or the one above it; a name that is not is a listing the container made up.</summary>
    private static bool IsName(string listed)
    {
        string name = listed.EndsWith('/') ? listed[..^1] : listed;
        return name.Length > 0 && name is not ("." or "..") && !name.Contains('/', StringComparison.Ordinal) && !name.Contains('\0', StringComparison.Ordinal);
    }

    /// <summary>Where a container's file is copied: under the container's own folder of the cache, each of its path's names
    /// made a file name here, or null for a path that names no file under it (an empty one, a <c>..</c>).</summary>
    private static string? CopyPlace(string cache, string container, string path)
    {
        string[] names = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0 || names.Any(n => n is "." or ".."))
        {
            return null;
        }
        string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(cache, Safe(container)));
        string copy = System.IO.Path.GetFullPath(System.IO.Path.Combine([folder, .. names.Select(Safe)]));
        return PathNames.IsUnder(copy, folder) ? copy : null;
    }

    /// <summary>Copies a file out of the container into <paramref name="copy"/>: the tar <c>docker cp</c> writes read as it
    /// comes, a file over <see cref="Largest"/> refused at its header with the copy ended there, and the file written beside
    /// its place, then renamed over it, only once its folder is found under <paramref name="cache"/> with no link leading out.
    /// Null once copied, else why not.</summary>
    private static async Task<string?> CopyAsync(ContainerEngine docker, string id, string path, string copy, string cache)
    {
        Process process;
        try
        {
            // -L: the file ls -L listed, which a link names, rather than the link; Podman's own cp follows it unasked, and has no -L.
            ProcessStartInfo start = docker.Command(docker.Known is { PodmanCli: true } ? ["cp", $"{id}:{path}", "-"] : ["cp", "-L", $"{id}:{path}", "-"]);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception failed) when (failed is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return failed.Message;
        }
        using (process)
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using CancellationTokenSource limit = new(ContainerEngine.AskLimit);
            try
            {
                string? written = await ReadAsync(process.StandardOutput.BaseStream, path, copy, cache, limit.Token);
                if (written is null or NotAFile)
                {
                    // The archive's end, which docker cp waits to write before it ends.
                    await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, limit.Token);
                    await process.WaitForExitAsync(limit.Token);
                    return process.ExitCode != 0 ? Said(await errors) : written is null ? null : $"{path} is not a file";
                }
                // Refused at its header: nothing more of it is read.
                ChildProcesses.Kill(process);
                return written;
            }
            catch (OperationCanceledException)
            {
                ChildProcesses.Kill(process);
                return $"Copying {path} out of the container took too long";
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
                or ArgumentException or InvalidOperationException)
            {
                ChildProcesses.Kill(process);
                // A copy that ended inside the archive says why in its errors.
                return process.HasExited && process.ExitCode != 0 ? Said(await errors) : $"{path} could not be copied out of the container: {failed.Message}";
            }
        }

        static string Said(string errors) => errors.Trim() is { Length: > 0 } said ? ContainerSecrets.InSaid(said) : "docker cp failed";
    }

    private const string NotAFile = "not a file";

    /// <summary>The first entry of the archive written to the copy's place: null once written, else why not.</summary>
    private static async Task<string?> ReadAsync(Stream archive, string path, string copy, string cache, CancellationToken cancel)
    {
        using TarReader reader = new(archive, leaveOpen: true);
        if (await reader.GetNextEntryAsync(copyData: false, cancel) is not { } entry)
        {
            return NotAFile;
        }
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            return entry.EntryType == TarEntryType.Directory ? $"{path} is a folder" : $"{path} is not a file";
        }
        if (entry.Length > Largest)
        {
            return $"{path} is larger than 16 MB";
        }
        string folder = System.IO.Path.GetDirectoryName(copy)!;
        Directory.CreateDirectory(folder);
        // Only Passenger writes there; a link that would lead the copy out of the cache is refused all the same.
        if (!PathNames.IsUnder(PathNames.Canonical(folder), PathNames.Canonical(cache)))
        {
            return $"{path} could not be copied: its place in Passenger's cache leads out of it";
        }
        string partial = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(copy)}.{Guid.NewGuid():N}");
        try
        {
            // The state's own copy, never a workspace file: FileText's writes are for what a person edits.
            await using (FileStream written = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (entry.DataStream is { } data)
                {
                    await data.CopyToAsync(written, cancel);
                }
            }
            // A rename puts the file where a link may stand, never through it.
            File.Move(partial, copy, overwrite: true);
            return null;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>A name as a file name here: what this system refuses in one replaced, and one of dots and spaces alone, which
    /// Windows would read as a folder above, given a letter first.</summary>
    private static string Safe(string name)
    {
        string safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '\\' or '/' ? '_' : c));
        return safe.TrimEnd('.', ' ').Length == 0 ? $"_{safe}" : safe;
    }

    private Control Row(ContainerFile file)
    {
        PathIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(file.IsFolder ? open.Contains(file.Path) ? IconKeys.FolderOpen : IconKeys.Explorer : IconKeys.DocumentText));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable(TokenKeys.TextMuted));
        TextBlock name = new() { Text = file.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, file.Path);
        return new StackPanel { Orientation = Orientation.Horizontal, Height = ListRow.Height, Margin = new Thickness(8 + (file.Depth * 16), 0, 8, 0), Children = { icon, name } };
    }
}
