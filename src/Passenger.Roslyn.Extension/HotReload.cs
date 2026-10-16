using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Contracts.EditAndContinue;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

public sealed class StartHotReload : IExtensionWorkspaceMessageHandler<HotReloadStart, HotReloadStarted>
{
    public Task<HotReloadStarted> ExecuteAsync(HotReloadStart message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        HotReload.StartAsync(message, context.Solution, cancellationToken);
}

public sealed class EmitHotReload : IExtensionWorkspaceMessageHandler<HotReloadEmit, HotReloadEmitted>
{
    public Task<HotReloadEmitted> ExecuteAsync(HotReloadEmit message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        HotReload.EmitAsync(message, context.Solution, cancellationToken);
}

public sealed class CommitHotReload : IExtensionWorkspaceMessageHandler<HotReloadSessionMessage, HotReloadDone>
{
    public Task<HotReloadDone> ExecuteAsync(HotReloadSessionMessage message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        Task.FromResult(HotReload.Commit(message.Session));
}

public sealed class DiscardHotReload : IExtensionWorkspaceMessageHandler<HotReloadSessionMessage, HotReloadDone>
{
    public Task<HotReloadDone> ExecuteAsync(HotReloadSessionMessage message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        Task.FromResult(HotReload.Discard(message.Session));
}

public sealed class EndHotReload : IExtensionWorkspaceMessageHandler<HotReloadSessionMessage, HotReloadDone>
{
    public Task<HotReloadDone> ExecuteAsync(HotReloadSessionMessage message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        Task.FromResult(HotReload.End(message.Session));
}

/// <summary>Edit and Continue through the server's own service (Roslyn's EditAndContinueService, the one dotnet watch and
/// Visual Studio drive), over the solution the server has open: a session per debugging session, started at its first
/// edit, each emit comparing the solution now with what was built (an edited file's build text found by its PDB checksum,
/// <see cref="Originals"/>) and applied since. The debugger is Passenger's: the runtime's capabilities, the modules loaded
/// and the active statements are as Passenger's last message said them (<see cref="Debugger"/>).</summary>
internal static class HotReload
{
    private static readonly ConcurrentDictionary<int, Session> sessions = new();

    public static async Task<HotReloadStarted> StartAsync(HotReloadStart message, Solution solution, CancellationToken cancel)
    {
        if (solution.Services.GetService<IEditAndContinueWorkspaceService>() is not { } workspace)
        {
            return new HotReloadStarted(0, "the C# language server has no Edit and Continue service");
        }
        IEditAndContinueService service = workspace.Service;
        // The files edited before the session began are taken back to their texts before (the build's), its baseline.
        foreach (HotReloadOriginal original in message.Originals)
        {
            ImmutableArray<DocumentId> ids = solution.GetDocumentIdsWithFilePath(original.Path);
            if (!ids.IsEmpty && solution.GetDocument(ids[0]) is { } document)
            {
                SourceText now = await document.GetTextAsync(cancel).ConfigureAwait(false);
                solution = solution.WithDocumentText(ids, SourceText.From(original.Text, now.Encoding, now.ChecksumAlgorithm));
            }
        }
        // Every document's text read now, as dotnet watch's start does: the session compares later snapshots with these.
        await EditAndContinueService.HydrateDocumentsAsync(solution, cancel).ConfigureAwait(false);
        Debugger debugger = new([.. message.Capabilities]);
        Originals originals = new();
        originals.Take(message.Originals);
        DebuggingSessionId id = service.StartDebuggingSession(solution, debugger, originals, reportDiagnostics: false);
        sessions[id.Ordinal] = new Session(service, id, debugger, originals) { Committed = solution };
        return new HotReloadStarted(id.Ordinal, null);
    }

    public static async Task<HotReloadEmitted> EmitAsync(HotReloadEmit message, Solution solution, CancellationToken cancel)
    {
        if (!sessions.TryGetValue(message.Session, out Session? session))
        {
            return Failed($"there is no Hot Reload session {message.Session}");
        }
        lock (session)
        {
            if (session.RestartRequired is { } refused)
            {
                return refused;
            }
            if (session.Pending is not null)
            {
                return Failed("the last update has not been committed or discarded");
            }
        }
        session.Debugger.Take(message);
        session.Originals.Take(message.Originals);
        // A new edit session, which reads the active statements again: paused, Edit and Continue; running, Hot Reload.
        session.Service.BreakStateOrCapabilitiesChanged(session.Id, message.Paused);
        EmitSolutionUpdateResults results = await session.Service.EmitSolutionUpdateAsync(session.Id, solution,
            ImmutableDictionary<ProjectId, RunningProjectOptions>.Empty, NoTrackedSpans, cancel).ConfigureAwait(false);

        ImmutableArray<Diagnostic> diagnostics = [.. results.GetAllDiagnostics()];
        List<HotReloadProblem> problems = [.. diagnostics.Select(d => Problem(d, results.Solution))];
        IReadOnlyList<string> changed = Changed(session.Committed, results.Solution ?? solution);
        // None can mean a stale/unbuilt project was excluded, rather than an insignificant edit. Roslyn has already
        // advanced its snapshot in that case: keep our pending documents and built lines, and require a restart.
        // A ready batch can also exclude a stale project: do not silently apply only the other projects' changes.
        bool noEffect = diagnostics.Any(d => d.IsNoEffectDiagnostic())
            || results.ModuleUpdates.Status == ModuleUpdateStatus.None && problems.Any(p => p.RestartRequired);
        if (results.ModuleUpdates.Status == ModuleUpdateStatus.Blocked || problems.Any(p => p.Error) || noEffect)
        {
            if (results.ModuleUpdates.Status == ModuleUpdateStatus.Ready)
            {
                session.Service.DiscardSolutionUpdate(session.Id);
            }
            HotReloadEmitted refused = new(null, "blocked", [], problems, changed);
            if (results.ModuleUpdates.Status == ModuleUpdateStatus.None && noEffect)
            {
                // Do not reset the EnC session: prior generations and active-statement remaps belong to it. A later
                // emit against Roslyn's advanced snapshot could otherwise falsely acknowledge the same pending edits.
                lock (session)
                {
                    session.RestartRequired = refused;
                }
            }
            return refused;
        }
        if (results.ModuleUpdates.Status == ModuleUpdateStatus.None)
        {
            // Only insignificant changes, with no diagnostic saying an edit was left behind, are acknowledged.
            session.Committed = results.Solution ?? solution;
            return new HotReloadEmitted(null, "none", [], problems, changed);
        }
        lock (session)
        {
            session.Pending = results.Solution ?? solution;
        }
        return new HotReloadEmitted(null, "ready", [.. results.ModuleUpdates.Updates.Select(Update)], problems, changed);
    }

    public static HotReloadDone Commit(int id)
    {
        if (!sessions.TryGetValue(id, out Session? session))
        {
            return new HotReloadDone($"there is no Hot Reload session {id}");
        }
        lock (session)
        {
            if (session.Pending is not { } pending)
            {
                return new HotReloadDone("there is no update to commit");
            }
            session.Service.CommitSolutionUpdate(session.Id);
            session.Committed = pending;
            session.Pending = null;
        }
        return new HotReloadDone(null);
    }

    public static HotReloadDone Discard(int id)
    {
        if (!sessions.TryGetValue(id, out Session? session))
        {
            return new HotReloadDone($"there is no Hot Reload session {id}");
        }
        lock (session)
        {
            if (session.Pending is null)
            {
                return new HotReloadDone("there is no update to discard");
            }
            session.Service.DiscardSolutionUpdate(session.Id);
            session.Pending = null;
        }
        return new HotReloadDone(null);
    }

    public static HotReloadDone End(int id)
    {
        if (!sessions.TryRemove(id, out Session? session))
        {
            return new HotReloadDone($"there is no Hot Reload session {id}");
        }
        lock (session)
        {
            if (session.Pending is not null)
            {
                session.Service.DiscardSolutionUpdate(session.Id);
                session.Pending = null;
            }
            session.Service.EndDebuggingSession(session.Id);
        }
        return new HotReloadDone(null);
    }

    private static HotReloadEmitted Failed(string problem) => new(problem, "blocked", [], [], []);

    /// <summary>No editor tracks the active statements' spans as they are edited: the service maps them itself.</summary>
    private static ValueTask<ImmutableArray<ActiveStatementSpan>> NoTrackedSpans(DocumentId? document, string path, CancellationToken cancel) =>
        ValueTask.FromResult(ImmutableArray<ActiveStatementSpan>.Empty);

    /// <summary>The files whose text differs between two snapshots, or which one has and the other not.</summary>
    private static IReadOnlyList<string> Changed(Solution before, Solution after)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        SolutionChanges changes = after.GetChanges(before);
        foreach (Microsoft.CodeAnalysis.ProjectChanges project in changes.GetProjectChanges())
        {
            foreach (DocumentId id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(project.GetAddedDocuments()))
            {
                if (after.GetDocument(id)?.FilePath is { } path)
                {
                    paths.Add(path);
                }
            }
            foreach (DocumentId id in project.GetRemovedDocuments())
            {
                if (before.GetDocument(id)?.FilePath is { } path)
                {
                    paths.Add(path);
                }
            }
        }
        foreach (Project added in changes.GetAddedProjects())
        {
            paths.UnionWith(added.Documents.Select(d => d.FilePath).OfType<string>());
        }
        return [.. paths.Order(StringComparer.Ordinal)];
    }

    private static HotReloadProblem Problem(Diagnostic diagnostic, Solution? solution)
    {
        FileLinePositionSpan place = diagnostic.Location.GetMappedLineSpan();
        bool placed = diagnostic.Location != Location.None && place.IsValid && !string.IsNullOrEmpty(place.Path);
        string? project = diagnostic.Location.SourceTree is { } tree && solution?.GetDocument(tree) is { } document ? document.Project.Name : null;
        return new HotReloadProblem(
            diagnostic.Id,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            diagnostic.Severity == DiagnosticSeverity.Error,
            EditAndContinueDiagnosticDescriptors.IsEncDiagnostic(diagnostic.Id),
            placed ? place.Path : null,
            placed ? place.StartLinePosition.Line : 0,
            placed ? place.StartLinePosition.Character : 0,
            placed ? place.EndLinePosition.Line : 0,
            placed ? place.EndLinePosition.Character : 0,
            project);
    }

    private static HotReloadModuleUpdate Update(ManagedHotReloadUpdate update) => new(
        update.Module,
        update.ModuleName,
        [.. update.MetadataDelta],
        [.. update.ILDelta],
        [.. update.PdbDelta],
        [.. update.UpdatedMethods],
        [.. update.UpdatedTypes],
        [.. update.SequencePoints.Select(s => new HotReloadLines(s.FileName, [.. s.LineUpdates.Select(l => new HotReloadLineUpdate(l.OldLine, l.NewLine))]))],
        [.. update.ActiveStatements.Select(a => new HotReloadStatementUpdate(a.Method.Token, a.Method.Version, a.ILOffset,
            a.NewSpan.StartLine, a.NewSpan.StartColumn, a.NewSpan.EndLine, a.NewSpan.EndColumn))],
        [.. update.RequiredCapabilities]);

    private sealed class Session(IEditAndContinueService service, DebuggingSessionId id, Debugger debugger, Originals originals)
    {
        public IEditAndContinueService Service { get; } = service;
        public DebuggingSessionId Id { get; } = id;
        public Debugger Debugger { get; } = debugger;
        public Originals Originals { get; } = originals;

        /// <summary>The solution as last committed (as built, at first): what the files changed since are measured from.</summary>
        public Solution Committed { get; set; } = null!;

        /// <summary>The solution of the update emitted and not yet committed or discarded.</summary>
        public Solution? Pending { get; set; }

        /// <summary>A no-effect batch Roslyn already acknowledged internally: only rebuilding/restarting can recover.</summary>
        public HotReloadEmitted? RestartRequired { get; set; }
    }

    /// <summary>The files' texts before the session's first edit to each, as Passenger kept them: the build's, which Roslyn
    /// asks for once a file's text in the solution no longer matches its checksum in the build's PDB. Given only when the
    /// text's bytes (UTF-8, with or without its byte order mark) hash to that checksum.</summary>
    private sealed class Originals : IPdbMatchingSourceTextProvider
    {
        private readonly ConcurrentDictionary<string, string> texts = new(StringComparer.OrdinalIgnoreCase);

        public void Take(IEnumerable<HotReloadOriginal> originals)
        {
            foreach (HotReloadOriginal original in originals)
            {
                texts.TryAdd(original.Path, original.Text);
            }
        }

        public ValueTask<string?> TryGetMatchingSourceTextAsync(string filePath, ImmutableArray<byte> requiredChecksum, SourceHashAlgorithm checksumAlgorithm, CancellationToken cancellationToken)
        {
            if (!texts.TryGetValue(filePath, out string? text))
            {
                return ValueTask.FromResult<string?>(null);
            }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] marked = [.. System.Text.Encoding.UTF8.Preamble, .. bytes];
            return ValueTask.FromResult(Hash(bytes, checksumAlgorithm).SequenceEqual(requiredChecksum) || Hash(marked, checksumAlgorithm).SequenceEqual(requiredChecksum)
                ? text
                : null);
        }

        private static byte[] Hash(byte[] bytes, SourceHashAlgorithm algorithm) => algorithm switch
        {
            SourceHashAlgorithm.Sha1 => System.Security.Cryptography.SHA1.HashData(bytes),
            _ => System.Security.Cryptography.SHA256.HashData(bytes),
        };
    }

    /// <summary>Passenger's debugger as the service asks of it, answered from what Passenger's last emit said.</summary>
    private sealed class Debugger(ImmutableArray<string> capabilities) : IManagedHotReloadService
    {
        private volatile HotReloadEmit? said;

        public void Take(HotReloadEmit message) => said = message;

        public ValueTask<ImmutableArray<ManagedActiveStatementDebugInfo>> GetActiveStatementsAsync(CancellationToken cancellation)
        {
            HotReloadEmit? message = said;
            if (message is null || !message.Paused)
            {
                return ValueTask.FromResult(ImmutableArray<ManagedActiveStatementDebugInfo>.Empty);
            }
            return ValueTask.FromResult<ImmutableArray<ManagedActiveStatementDebugInfo>>([.. message.ActiveStatements.Select(s => new ManagedActiveStatementDebugInfo(
                new ManagedInstructionId(new ManagedMethodId(s.Module, s.Token, s.Version), s.ILOffset),
                s.Document,
                s.Document is null ? default : new SourceSpan(s.StartLine, s.StartColumn, s.EndLine, s.EndColumn),
                (ActiveStatementFlags)s.Flags))]);
        }

        /// <summary>A module the program can take edits to is available; one it loaded without Edit and Continue says why;
        /// one not loaded yet takes its update as it loads.</summary>
        public ValueTask<ManagedHotReloadAvailability> GetAvailabilityAsync(Guid module, CancellationToken cancellation)
        {
            HotReloadEmit? message = said;
            return ValueTask.FromResult(message?.Modules.Contains(module) == true
                ? new ManagedHotReloadAvailability(ManagedHotReloadAvailabilityStatus.Available)
                : message?.Locked.FirstOrDefault(l => l.Module == module) is { } locked
                    ? new ManagedHotReloadAvailability(ManagedHotReloadAvailabilityStatus.NotAllowedForModule, locked.Why)
                    : new ManagedHotReloadAvailability(ManagedHotReloadAvailabilityStatus.ModuleNotLoaded));
        }

        // The runtime's, as Passenger's last message said them; those the session started with until one has.
        public ValueTask<ImmutableArray<string>> GetCapabilitiesAsync(CancellationToken cancellation) =>
            ValueTask.FromResult(said is { Capabilities.Count: > 0 } message ? [.. message.Capabilities] : capabilities);

        public ValueTask PrepareModuleForUpdateAsync(Guid module, CancellationToken cancellation) => ValueTask.CompletedTask;
    }
}
