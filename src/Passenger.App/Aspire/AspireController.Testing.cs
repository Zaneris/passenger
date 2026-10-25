using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

public sealed partial class AspireController
{
    /// <summary>Gives a test host its own DCP endpoint; Aspire.Hosting.Testing owns the AppHost itself.</summary>
    public async Task<AspireTestRun?> BeginTestAsync(ProjectModel testProject, bool debug, CancellationToken cancellation)
    {
        UiThread.Check();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!testProject.Packages.Any(package => package.Name.Equals("Aspire.Hosting.Testing", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        List<ProjectModel> hosts = [.. workspace.Projects.Where(project => project.IsAspireHost
            && testProject.ProjectReferences.Any(reference => PathNames.Same(reference, project.Path)))];
        if (hosts.Count != 1)
        {
            throw new InvalidOperationException($"{testProject.Name} must reference exactly one evaluated Aspire AppHost to run integration tests in Passenger.");
        }
        using CancellationTokenSource starting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
        starting.Token.ThrowIfCancellationRequested();
        if (workspace.IsEnded)
        {
            throw new InvalidOperationException(Refusals.SolutionClosed);
        }
        if (startingPaths.Contains(hosts[0].Path) || apps.Any(app => PathNames.Same(app.Configuration.Project, hosts[0].Path)))
        {
            throw new InvalidOperationException("Stop this AppHost before running another integration-test instance of it");
        }
        RunConfiguration configuration = new() { Name = $"Tests: {testProject.Name}", Project = hosts[0].Path, Aspire = true };
        AspireAppRun app = new(configuration, debug ? RunStart.Debug : RunStart.Run, Guid.NewGuid().ToString("N")) { IsTest = true };
        apps.Add(app);
        try
        {
            AspireExecutionServer execution = await StartEndpointAsync(app, starting.Token);
            if (app.Stopping || workspace.IsEnded || disposed || starting.IsCancellationRequested)
            {
                await execution.DisposeAsync();
                starting.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The solution was closed while starting integration tests");
            }
            Dictionary<string, string> environment = new(execution.Environment, StringComparer.Ordinal)
            {
                ["DEBUG_SESSION_RUN_MODE"] = debug ? "Debug" : "NoDebug",
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return new AspireTestRun(this, app, environment);
        }
        catch
        {
            await StopAppAsync(app);
            throw;
        }
    }
}

/// <summary>One test project's execution endpoint and resources, never the test-owned AppHost.</summary>
public sealed class AspireTestRun : IAsyncDisposable
{
    private readonly AspireController controller;
    private readonly AspireAppRun app;

    internal AspireTestRun(AspireController controller, AspireAppRun app, IReadOnlyDictionary<string, string> environment)
    {
        this.controller = controller;
        this.app = app;
        Environment = environment;
    }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public ValueTask DisposeAsync() => new(UiThread.InvokeAsync(async () =>
    {
        await controller.StopAppAsync(app);
        return true;
    }));
}
