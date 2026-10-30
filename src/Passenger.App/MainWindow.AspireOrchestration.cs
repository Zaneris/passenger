using Passenger.App.Aspire;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
    /// <summary>What the status bar says once orchestration is added.</summary>
    private const string AspireAdded = "Added Aspire orchestration";

    /// <summary>The current orchestration dialog's result, retained after workspace loading changes the status bar.</summary>
    public Task<string?>? AspireOrchestrationResult { get; private set; }

    /// <summary>Chooses service projects and adds Aspire orchestration as one global undo command.</summary>
    public AspireOrchestrationDialog? ShowAddAspireOrchestration()
    {
        if (Solution is not { } solution)
        {
            status.Text = Refusals.NoSolution;
            return null;
        }
        AspireOrchestrationDialog dialog = new(solution.Name, session.Workspace?.Projects ?? []);
        dialog.InstallTemplates.Click += (_, _) =>
        {
            dialog.Close(null);
            if (Solution == solution)
            {
                ShowNewProject(null);
                status.Text = "Choose Aspire in New Project and install Aspire.ProjectTemplates, then Add Aspire Orchestration again";
            }
        };
        AspireOrchestrationResult = ApplyAsync();
        return dialog;

        async Task<string?> ApplyAsync()
        {
            AspireOrchestrationChoice? choice = await dialog.ShowDialog<AspireOrchestrationChoice?>(this);
            if (choice is null || Solution != solution)
            {
                return null;
            }
            return await AddAspireOrchestrationAsync(choice);
        }
    }

    /// <summary>Aspire orchestration of the services chosen, prepared away from the solution and then written whole: their
    /// AppHost (the one chosen, else one made) referencing and starting them, their ServiceDefaults, the solution and the
    /// solution filter open listing what was made. What the status bar then says; part of the command given
    /// (<paramref name="into"/>), else one of its own.</summary>
    public async Task<string?> AddAspireOrchestrationAsync(AspireOrchestrationChoice choice, CommandHistory.Recording? into = null)
    {
        if (Solution is not { } solution)
        {
            return status.Text = Refusals.NoSolution;
        }
        using CancellationTokenSource cancel = new();
        void WindowClosed(object? sender, EventArgs args) => cancel.Cancel();
        Closed += WindowClosed;
        string staging = Path.Combine(StateFolder, "aspire-staging", Guid.NewGuid().ToString("N"));
        string? filter = session.Workspace?.Filter?.Path;
        IReadOnlyList<AspireOrchestration.Write> writes = [];
        try
        {
            string[] existing = [solution.Path, .. filter is null ? [] : new[] { filter }, .. choice.Services.SelectMany(p => new[] { p.Path, Path.Combine(p.Folder, "Program.cs") }),
                .. choice.AppHost is { } host ? (string[])[host.Path, .. AspireOrchestration.AppHostSources(host.Path)] : [],
                .. choice.ServiceDefaults is { } defaults ? new[] { defaults.Path } : [],
                .. AspireOrchestration.PackagesProps(choice, session.Workspace?.Projects ?? [])];
            if (SaveBeforeWriting(existing) is { } unsaved)
            {
                return status.Text = unsaved;
            }
            status.Text = "Preparing Aspire orchestration…";
            Task<IReadOnlyList<AspireOrchestration.Write>> preparing = AspireOrchestration.PrepareAsync(solution.Path, filter, staging, choice, cancel.Token);
            BackgroundWork.Track(preparing);
            writes = await preparing;
            cancel.Token.ThrowIfCancellationRequested();
            // One read again meanwhile is the same solution: each file is written only as it was read (CheckUnchanged).
            if (Solution is not { } now || !PathNames.Same(now.Path, solution.Path))
            {
                return status.Text = "The solution changed: Aspire orchestration was not added";
            }
            if (existing.Concat(writes.Select(w => w.Path)).Any(path => Editors.Documents.Any(d => PathNames.Same(d.Path, path) && d.IsModified)))
            {
                throw new InvalidOperationException("A file was edited while orchestration was being prepared: save it and try again");
            }
            AspireOrchestration.CheckUnchanged(writes);
            bool Lists(string path) => PathNames.Same(path, solution.Path) || filter is not null && PathNames.Same(path, filter);
            // The solution is read again only once it and its projects hold what was written.
            if (await ChangeFileAsync("Add Aspire Orchestration", [.. writes.Select(w => w.Path)], () =>
            {
                foreach (AspireOrchestration.Write write in writes.OrderBy(w => w.Before is null ? 0 : Lists(w.Path) ? 2 : 1))
                {
                    FileText.SaveOver(write.Path, write.Text, write.Text.Text);
                }
                return null;
            }, into) is { } refused)
            {
                ReloadSolution();
                return status.Text = $"Could not add Aspire orchestration: {refused}" + (into is null ? string.Empty : $"; Undo {into.Name} takes back any change written");
            }
            ReloadSolution();
            return status.Text = writes.Count == 0 ? "Aspire orchestration is already configured" : AspireAdded;
        }
        catch (OperationCanceledException)
        {
            return status.Text = "Adding Aspire orchestration was canceled";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
        {
            FollowWritten(writes.Select(w => w.Path));
            ReloadSolution();
            return status.Text = $"Could not add Aspire orchestration: {error.Message}; Undo Add Aspire Orchestration takes back any change written";
        }
        finally
        {
            Closed -= WindowClosed;
            CleanAspireStaging(staging);
        }
    }

    private static void CleanAspireStaging(string staging)
    {
        try
        {
            if (!Directory.Exists(staging))
            {
                return;
            }
            foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
            foreach (string folder in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            {
                Directory.Delete(folder);
            }
            Directory.Delete(staging);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Staging lives under the window's state, never under the person's solution.
            IdeLog.Write(IdeLog.Ide, $"Add Aspire Orchestration could not remove its staging folder {staging}: {error.Message}");
        }
    }
}
