using Passenger.App.Aspire;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
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
            using CancellationTokenSource cancel = new();
            void WindowClosed(object? sender, EventArgs args) => cancel.Cancel();
            Closed += WindowClosed;
            string staging = Path.Combine(StateFolder, "aspire-staging", Guid.NewGuid().ToString("N"));
            IReadOnlyList<AspireOrchestration.Write> writes = [];
            try
            {
                string[] existing = [solution.Path, .. choice.Services.SelectMany(p => new[] { p.Path, Path.Combine(p.Folder, "Program.cs") }),
                    .. choice.AppHost is { } host ? (string[])[host.Path, .. AspireOrchestration.AppHostSources(host.Path)] : [],
                    .. choice.ServiceDefaults is { } defaults ? new[] { defaults.Path } : []];
                if (SaveBeforeWriting(existing) is { } unsaved)
                {
                    return status.Text = unsaved;
                }
                status.Text = "Preparing Aspire orchestration…";
                Task<IReadOnlyList<AspireOrchestration.Write>> preparing = AspireOrchestration.PrepareAsync(solution.Path, staging, choice, cancel.Token);
                BackgroundWork.Track(preparing);
                writes = await preparing;
                cancel.Token.ThrowIfCancellationRequested();
                if (Solution != solution)
                {
                    return status.Text = "The solution changed: Aspire orchestration was not added";
                }
                if (existing.Any(path => Editors.Documents.Any(d => PathNames.Same(d.Path, path) && d.IsModified)))
                {
                    throw new InvalidOperationException("A file was edited while orchestration was being prepared: save it and try again");
                }
                AspireOrchestration.CheckUnchanged(writes);
                using (CommandHistory.Recording recording = Editors.Commands.Record("Add Aspire Orchestration"))
                {
                    foreach (AspireOrchestration.Write write in writes)
                    {
                        recording.Watch(write.Path);
                    }
                    try
                    {
                        foreach (AspireOrchestration.Write write in writes.OrderBy(w => w.Before is null ? 0 : PathNames.Same(w.Path, solution.Path) ? 2 : 1))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(write.Path)!);
                            FileText.Save(write.Path, write.Text.Text, write.Text.Encoding, write.Text.HasBom, write.Text.CrBreaks);
                        }
                    }
                    finally
                    {
                        FollowWritten(writes.Select(w => w.Path));
                    }
                }
                ReloadSolution();
                return status.Text = writes.Count == 0 ? "Aspire orchestration is already configured" : "Added Aspire orchestration";
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
