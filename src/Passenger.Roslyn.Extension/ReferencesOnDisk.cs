using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;

namespace Passenger.Roslyn.Extension;

/// <summary>The assemblies projects reference, each as the disk has it now. The server's own service gives back whatever it
/// first read of a path for as long as a project holds that, a read that failed included, so a reference to an assembly
/// not built yet could never be anything else. Here one of the server's services serves a path only while its file stays
/// as it was read; a file come, changed or gone is read anew, so hearing of the change takes the assembly in.</summary>
internal sealed class ReferencesOnDisk(IDocumentationProviderService documentation) : IMetadataService
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (FileMark Mark, IMetadataService Reader)> read = new(StringComparer.Ordinal);

    public PortableExecutableReference GetReference(string resolvedPath, MetadataReferenceProperties properties)
    {
        // Marked before it is read: a write between the two reads it again, never leaves the older bytes standing.
        FileMark now = FileMark.Of(resolvedPath);
        IMetadataService reader;
        lock (gate)
        {
            if (!read.TryGetValue(resolvedPath, out (FileMark Mark, IMetadataService Reader) kept) || kept.Mark != now)
            {
                kept = (now, new MetadataServiceFactory.MetadataService(documentation));
                read[resolvedPath] = kept;
            }
            reader = kept.Reader;
        }
        return reader.GetReference(resolvedPath, properties);
    }

    /// <summary>Whether a file read here is no longer as it was read.</summary>
    public bool Changed(string path)
    {
        FileMark now = FileMark.Of(path);
        lock (gate)
        {
            return read.TryGetValue(path, out (FileMark Mark, IMetadataService Reader) kept) && kept.Mark != now;
        }
    }

    /// <summary>What says a file is as it was: gone is the default.</summary>
    private readonly record struct FileMark(DateTime Written, long Length)
    {
        public static FileMark Of(string path)
        {
            FileInfo file = new(path);
            return file.Exists ? new FileMark(file.LastWriteTimeUtc, file.Length) : default;
        }
    }
}

[ExportWorkspaceServiceFactory(typeof(IMetadataService), ServiceLayer.Host)]
[Shared]
internal sealed class ReferencesOnDiskFactory : IWorkspaceServiceFactory
{
    [ImportingConstructor]
    public ReferencesOnDiskFactory()
    {
    }

    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices) =>
        new ReferencesOnDisk(workspaceServices.GetRequiredService<IDocumentationProviderService>());
}

/// <summary>The assemblies the solution's projects reference taken in now where the disk has them otherwise than they were
/// read: hearing of a change waits seconds for more to come, and what is asked the moment a build ends can't.</summary>
public sealed class ReferencesRead : IExtensionWorkspaceMessageHandler<ReferencesAsked, ReferencesTaken>
{
    public async Task<ReferencesTaken> ExecuteAsync(ReferencesAsked message, ExtensionMessageContext context, CancellationToken cancellationToken)
    {
        Solution solution = context.Solution;
        if (solution.Services.GetService<IMetadataService>() is not ReferencesOnDisk onDisk || solution.Workspace is not LanguageServerWorkspace { ProjectSystemProjectFactory: { } projects })
        {
            return new ReferencesTaken(0);
        }
        string[] changed = [.. solution.Projects.SelectMany(p => p.MetadataReferences).OfType<PortableExecutableReference>()
            .Select(r => r.FilePath).OfType<string>().Distinct(StringComparer.Ordinal).Where(onDisk.Changed)];
        foreach (string path in changed)
        {
            await projects.StartRefreshingMetadataReferencesForFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        // A file a refresh of the server's own has just read is no longer changed, and is in the solution once this gate opens.
        await projects.ApplyChangeToWorkspaceAsync(static _ => { }, cancellationToken).ConfigureAwait(false);
        return new ReferencesTaken(changed.Length);
    }
}
