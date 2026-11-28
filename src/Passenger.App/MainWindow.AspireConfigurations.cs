using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App;

public partial class MainWindow
{
    private async Task WaitForAspireResourceReaderAsync(ProjectSession current, CancellationToken cancel)
    {
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel, current.Aspire.ClosingToken);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CSharpService? observed = null;
        current.CSharpChanged += FollowServer;
        try
        {
            FollowServer(null, EventArgs.Empty);
            await ready.Task.WaitAsync(Waits.Of(TimeSpan.FromSeconds(60)), waiting.Token);
            waiting.Token.ThrowIfCancellationRequested();
            if (session != current || current.IsEnded)
            {
                throw new RefusedException(Refusals.SolutionClosed);
            }
        }
        finally
        {
            current.CSharpChanged -= FollowServer;
            if (observed is not null)
            {
                observed.StateChanged -= CheckReady;
            }
        }

        void FollowServer(object? sender, EventArgs args)
        {
            if (observed is not null)
            {
                observed.StateChanged -= CheckReady;
            }
            observed = current.CSharp;
            if (observed is not null)
            {
                observed.StateChanged += CheckReady;
            }
            CheckReady(null, EventArgs.Empty);
        }

        void CheckReady(object? sender, EventArgs args)
        {
            if (session != current || current.IsEnded)
            {
                ready.TrySetException(new RefusedException(Refusals.SolutionClosed));
            }
            else if (observed is { IsReady: true, Session: not null })
            {
                ready.TrySetResult();
            }
        }
    }

    internal async Task<IReadOnlyList<AspireRunResource>> ReadAspireConfigurationResourcesAsync(RunConfiguration configuration, CancellationToken cancel)
    {
        if (session.CSharp?.Session is not { } roslyn)
        {
            throw new InvalidOperationException("The C# language server is not ready to read AppHost resources");
        }
        AspireRunResourcesResult result = await roslyn.ExtensionAsync<AspireRunResourcesResult>(Handlers.AspireRunResources,
            new AspireResourcesRequest(configuration.Project), cancel).ConfigureAwait(false);
        return result.Problem is { } problem ? throw new InvalidOperationException(problem) : result.Resources;
    }
}
