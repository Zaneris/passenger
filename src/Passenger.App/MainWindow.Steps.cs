using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Workspace.Gherkin;

namespace Passenger.App;

/// <summary>The Reqnroll and SpecFlow projects' bindings (step definitions, hooks, step argument transformations), read from
/// their code by the language server's extension (<see cref="Handlers.StepBindings"/>), with no build: read once a .feature
/// file is open or a binding is asked for, and again a pause after any C# file changes (typed, or on disk) or a project's
/// reqnroll.json or specflow.json does, off the UI thread, one read at a time.</summary>
public partial class MainWindow
{
    private Debounce? stepsLater;
    private Task? stepsRead;
    private CSharpService? stepsFrom;
    private Bdd.BddSource? bdd;

    private void AddSteps()
    {
        stepsLater = new Debounce(TimeSpan.FromMilliseconds(400), () =>
        {
            IdeErrors.Observe(ReadStepsAsync());
            return Task.CompletedTask;
        });
        Closed += (_, _) => stepsLater.Stop();
        Editors.Opened += (_, document) =>
        {
            if (PathNames.HasExtension(document.Path, [".cs"]))
            {
                document.Text.Changed += (_, _) =>
                {
                    session.Steps.Stale();
                    WantSteps();
                };
            }
            else if (IsFeature(document.Path))
            {
                WantSteps(opening: true);
            }
        };
    }

    private static bool IsFeature(string path) => PathNames.HasExtension(path, [".feature"]);

    /// <summary>A read is due and someone looks: a .feature file open (<paramref name="opening"/> one now, which the open
    /// files may not list yet), or an agent's or a person's ask.</summary>
    private void WantSteps(bool opening = false)
    {
        if (ReadsSteps && session.Steps.Wants && (opening || Editors.Documents.Any(d => IsFeature(d.Path))))
        {
            stepsLater?.Restart();
        }
    }

    private bool ReadsSteps => session.CSharp is { IsReady: true, Session: not null };

    /// <summary>The language server is ready on the folder's projects (or a new one is), or loaded more: every project's
    /// bindings are read again.</summary>
    private void StepsServerChanged(CSharpService service, bool loaded = false)
    {
        if (service == session.CSharp && !session.IsEnded && service.IsReady && (loaded || stepsFrom != service))
        {
            stepsFrom = service;
            session.Steps.Forget();
            WantSteps();
        }
    }

    /// <summary>Files changed on disk: C# code, a project file or a Reqnroll configuration makes the bindings due.</summary>
    private void StepFilesChanged(FolderChanges changes)
    {
        if (changes.Projects || changes.Lost)
        {
            session.Steps.Forget();
        }
        else if (changes.Paths.Any(p => PathNames.HasExtension(p, [".cs"]) || Path.GetFileName(p) is ReqnrollConfig.ReqnrollFile or ReqnrollConfig.SpecFlowFile))
        {
            session.Steps.Stale();
        }
        WantSteps();
    }

    private Task ReadStepsAsync()
    {
        if (stepsRead is { IsCompleted: false } under)
        {
            return under;
        }
        stepsRead = ReadStepsCoreAsync();
        return stepsRead;
    }

    private async Task ReadStepsCoreAsync()
    {
        ProjectSession current = session;
        if (!current.Steps.Wants || current.IsEnded || current.CSharp is not { IsReady: true, Session: { } roslyn })
        {
            return;
        }
        int changes = current.Steps.Changes;
        string[] projects = [.. current.Projects.Where(p => p.Exists).Select(p => p.Path)];
        StepsFound? found = await BackgroundWork.Run(async () =>
        {
            try
            {
                return await roslyn.ExtensionAsync<StepsFound>(Handlers.StepBindings, new StepsRequest([], BindingAssemblies(projects))).ConfigureAwait(false);
            }
            catch (LanguageServerFailed)
            {
                return null;
            }
        });
        if (session != current || current.IsEnded || found is null)
        {
            return;
        }
        current.Steps.Took(found, changes);
        if (current.Steps.Wants)
        {
            stepsLater?.Restart();
        }
    }

    /// <summary>The bindings as read now, read again first where a read is due; the language server is the caller's to wait for.</summary>
    internal async Task<Bdd.StepCatalog> StepsAsync(CancellationToken cancel = default)
    {
        for (int turn = 0; turn < 4 && (session.Steps.Wants || stepsRead is { IsCompleted: false }); turn++)
        {
            await ReadStepsAsync().WaitAsync(cancel);
        }
        return session.Steps;
    }

    /// <summary>The binding assemblies each project's configuration names, by project file: read off the window's thread.</summary>
    private static Dictionary<string, IReadOnlyList<string>> BindingAssemblies(IEnumerable<string> projects)
    {
        Dictionary<string, IReadOnlyList<string>> named = new(PathNames.Comparer);
        foreach (string project in projects)
        {
            if (Path.GetDirectoryName(project) is { } folder && ReqnrollConfig.In(folder).BindingAssemblies is { Count: > 0 } assemblies)
            {
                named[project] = assemblies;
            }
        }
        return named;
    }
}
