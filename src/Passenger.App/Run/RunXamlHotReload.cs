using Avalonia.Threading;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.App.Run;

/// <summary>The XAML half of a dotnet watch run. watch applies managed code; the inspector updates existing UI objects.
/// Source snapshots belong to that running build, and a replacement process must establish them again.</summary>
public sealed class RunXamlHotReload(EditorArea editors) : IDisposable
{
    private readonly Dictionary<OpenDocument, EventHandler<DocumentChangedEventArgs>> following = [];
    private readonly Dictionary<string, string> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XamlSourceUpdate> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XamlSourceUpdate> saved = new(StringComparer.Ordinal);
    private readonly HashSet<string> uncertain = new(StringComparer.Ordinal);
    private LiveApp? app;
    private Task<HotReloadOutcome>? applying;
    private bool queued;
    private bool saveAgain;
    private int? process;
    private int generation;
    private int saves;
    private int applyingSave;

    public bool Changed => pending.Count > 0 || saved.Count > 0;

    public HotReloadOutcome? LastApplied { get; private set; }

    public event EventHandler<HotReloadOutcome>? Applied;

    /// <summary>The window's restart decision for an edit that cannot be applied to existing objects.</summary>
    public Func<HotReloadOutcome, Task<bool>> RestartFor { get; set; } = _ => Task.FromResult(false);

    internal static bool IsXaml(string path) => Path.GetExtension(path).ToLowerInvariant() is ".axaml" or ".xaml";

    /// <summary>Records the files before watch starts its build. A file added later needs a new build, never guessed
    /// element anchors from another version of its source.</summary>
    public async Task StartAsync(LiveApp? next, IEnumerable<string> paths)
    {
        int started = ++generation;
        applying = null;
        queued = false;
        saveAgain = false;
        saves = 0;
        app = null;
        sources.Clear();
        pending.Clear();
        saved.Clear();
        uncertain.Clear();
        process = null;
        LastApplied = null;
        if (next is null)
        {
            return;
        }
        string[] files = [.. paths.Where(IsXaml).Distinct(StringComparer.Ordinal)];
        Dictionary<string, string> read = await Task.Run(() =>
        {
            Dictionary<string, string> texts = new(StringComparer.Ordinal);
            foreach (string file in files)
            {
                if (Read(file) is { } text)
                {
                    texts[file] = text;
                }
            }
            return texts;
        });
        if (generation != started || next.State == LiveAppState.Ended)
        {
            return;
        }
        foreach ((string file, string text) in read)
        {
            sources[file] = text;
        }
        app = next;
        next.StateChanged += (_, _) =>
        {
            if (app == next && next.State == LiveAppState.Connected && next.ProcessId != process)
            {
                process = next.ProcessId;
                // A save during a build may precede or follow the compiler's read. Refuse that uncertainty until an
                // explicit restart builds stable files. Unsaved edits survive reconnect against their saved baseline.
                foreach ((string file, string original) in sources)
                {
                    if (Read(file) != original)
                    {
                        uncertain.Add(file);
                    }
                }
                QueueSave();
            }
        };
        foreach (OpenDocument document in editors.Documents.Where(d => IsXaml(d.Path)))
        {
            Edited(document);
            string text = document.Text.Saved.ToString();
            if (sources.GetValueOrDefault(document.Path) is { } original && text != original)
            {
                saved[document.Path] = new XamlSourceUpdate(document.Path, original, text);
            }
        }
    }

    public void Attach(OpenDocument document)
    {
        if (!IsXaml(document.Path))
        {
            return;
        }
        EventHandler<DocumentChangedEventArgs> follow = (_, _) => Edited(document);
        following[document] = follow;
        document.Text.Changed += follow;
        document.Saved += Saved;
    }

    public void Detach(OpenDocument document)
    {
        if (following.Remove(document, out EventHandler<DocumentChangedEventArgs>? follow))
        {
            document.Text.Changed -= follow;
            document.Saved -= Saved;
        }
    }

    /// <summary>A closed session releases its documents, source snapshots and any result still crossing back to it.</summary>
    public void Dispose()
    {
        generation++;
        app = null;
        foreach (OpenDocument document in following.Keys.ToList())
        {
            Detach(document);
        }
        sources.Clear();
        pending.Clear();
        saved.Clear();
        uncertain.Clear();
    }

    private void Edited(OpenDocument document)
    {
        if (app is null || app.State == LiveAppState.Ended)
        {
            return;
        }
        string current = document.Text.Snapshot.ToString();
        string before = sources.GetValueOrDefault(document.Path) ?? document.Text.Saved.ToString();
        if (!sources.ContainsKey(document.Path))
        {
            uncertain.Add(document.Path);
        }
        if (current == before && applying is null)
        {
            pending.Remove(document.Path);
        }
        else
        {
            pending[document.Path] = new XamlSourceUpdate(document.Path, before, current);
        }
    }

    private void Saved(object? sender, EventArgs e)
    {
        if (sender is not OpenDocument document || app is null || app.State == LiveAppState.Ended)
        {
            return;
        }
        string text = document.Text.Saved.ToString();
        saves++;
        string before = sources.GetValueOrDefault(document.Path) ?? pending.GetValueOrDefault(document.Path)?.Before ?? text;
        if (text == before && applying is null)
        {
            saved.Remove(document.Path);
        }
        else
        {
            saved[document.Path] = new XamlSourceUpdate(document.Path, before, text);
        }
        QueueSave();
    }

    private void QueueSave()
    {
        if (queued || saved.Count == 0 || app?.State != LiveAppState.Connected)
        {
            return;
        }
        int requested = generation;
        queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != requested)
            {
                return;
            }
            queued = false;
            if (saved.Count > 0 && app?.State == LiveAppState.Connected)
            {
                if (applying is not null)
                {
                    saveAgain |= saves > applyingSave;
                }
                else
                {
                    _ = ApplyAndRestartAsync();
                }
            }
        });
    }

    public Task<HotReloadOutcome> ApplyAndRestartAsync()
    {
        if (applying is not null)
        {
            return applying;
        }
        applyingSave = saves;
        Task<HotReloadOutcome> started = ApplyOnceAsync();
        if (!started.IsCompleted)
        {
            applying = started;
        }
        return started;
    }

    private async Task<HotReloadOutcome> ApplyOnceAsync()
    {
        LiveApp? target = app;
        int started = generation;
        try
        {
            HotReloadOutcome outcome;
            using (BackgroundWork.Scope())
            {
                outcome = await ApplyCurrentAsync();
            }
            if (app != target || generation != started)
            {
                return outcome;
            }
            LastApplied = outcome;
            Applied?.Invoke(this, outcome);
            if (outcome.Result == HotReloadResult.RestartNeeded)
            {
                await RestartFor(outcome);
            }
            return outcome;
        }
        finally
        {
            if (generation == started)
            {
                applying = null;
                if (saveAgain)
                {
                    saveAgain = false;
                    QueueSave();
                }
            }
        }
    }

    private async Task<HotReloadOutcome> ApplyCurrentAsync()
    {
        if (!Changed)
        {
            return new HotReloadOutcome(HotReloadResult.NothingToApply, "No XAML changes to apply");
        }
        if (app is not { State: LiveAppState.Connected } target)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, "The running app's XAML inspector is not connected; retry when the app has started");
        }
        XamlSourceUpdate[] batch = [.. saved.Values];
        if (batch.Length == 0)
        {
            return new HotReloadOutcome(HotReloadResult.NothingToApply, "Save the XAML files to apply their changes to the running app");
        }
        List<XamlUpdateProblem> unknown = [.. batch.Where(p => uncertain.Contains(p.File)).Select(p => new XamlUpdateProblem(p.File, 1, 1,
            "The XAML source changed while watch rebuilt the app, or was not in its launch snapshot: restart to establish the compiled source", true))];
        if (unknown.Count > 0)
        {
            return HotReload.XamlFailure(new XamlUpdateResult(false, 0, unknown));
        }
        int? beforeProcess = target.ProcessId;
        try
        {
            XamlUpdateResult result = await target.ApplyXamlAsync(new XamlUpdate(batch));
            if (app != target || target.ProcessId != beforeProcess || target.State != LiveAppState.Connected)
            {
                return new HotReloadOutcome(HotReloadResult.Failed, "The app restarted during the XAML update; retry against the replacement process");
            }
            if (!result.Applied)
            {
                return HotReload.XamlFailure(result);
            }
            foreach (XamlSourceUpdate source in batch)
            {
                sources[source.File] = source.After;
                if (saved.GetValueOrDefault(source.File) == source)
                {
                    saved.Remove(source.File);
                }
                else if (saved.GetValueOrDefault(source.File) is { } newerSave)
                {
                    saved[source.File] = newerSave with { Before = source.After };
                }
                if (pending.GetValueOrDefault(source.File) == source)
                {
                    pending.Remove(source.File);
                }
                else if (pending.GetValueOrDefault(source.File) is { } edited)
                {
                    pending[source.File] = edited with { Before = source.After };
                }
            }
            return new HotReloadOutcome(result.Changes > 0 ? HotReloadResult.Applied : HotReloadResult.NothingToApply,
                $"XAML changes applied to {result.Changes} live {(result.Changes == 1 ? "property" : "properties")}: {string.Join(", ", batch.Select(p => Path.GetFileName(p.File)))}")
            {
                Files = [.. batch.Select(p => p.File)],
            };
        }
        catch (LiveAppException error)
        {
            return new HotReloadOutcome(HotReloadResult.Failed, $"The app did not confirm the XAML changes: {error.Message}");
        }
    }

    private static string? Read(string file)
    {
        try
        {
            return FileText.Load(file)?.Text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
