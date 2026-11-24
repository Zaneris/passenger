using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>A level of the dependency map (<see cref="DependenciesAsked"/>): the solution's projects and their references, or
/// a project's namespaces, a namespace's types or a type's members, each with what it depends on as the compiler binds it.</summary>
public sealed class Dependencies : IExtensionWorkspaceMessageHandler<DependenciesAsked, DependenciesFound>
{
    public Task<DependenciesFound> ExecuteAsync(DependenciesAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        DependencyMap.LevelAsync(context.Solution, message.Of, ChosenFrameworksHandler.Unchosen(context.Solution, message.Frameworks), message.Rules, cancellationToken);
}

/// <summary>The map's node of a place in a document (<see cref="DependencyPlace"/>), which the map opens on.</summary>
public sealed class DependencyAt : IExtensionDocumentMessageHandler<Caret, DependencyPlace>
{
    public async Task<DependencyPlace> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new DependencyPlace(await DependencyMap.PlaceAsync(document, WrittenPlaces.Offset(text, message.At), cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>The uses in a document that break a folder's layer rules (<see cref="LayersAsked"/>), each where it is written.</summary>
public sealed class LayerChecks : IExtensionDocumentMessageHandler<LayersAsked, LayersBroken>
{
    public async Task<LayersBroken> ExecuteAsync(LayersAsked message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        new(await DependencyMap.BrokenAsync(context.Document(), message.Rules, cancellationToken).ConfigureAwait(false));
}
