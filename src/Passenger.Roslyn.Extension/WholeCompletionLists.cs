using System.Composition;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Options;
using Roslyn.LanguageServer.Protocol;

namespace Passenger.Roslyn.Extension;

/// <summary>C# completion lists sent whole: the server cuts a list to its best items for what is typed and says it is
/// incomplete, so a client asks again at every key, the server working out the whole list each time; whole, it is asked
/// once for a name, and narrowed by Passenger as the name is typed. Set as the server is initialized, before any list is
/// asked for; a global option of the server's, no part of any solution.</summary>
internal sealed class WholeCompletionLists(IGlobalOptionService options) : ILspService, IOnInitialized
{
    /// <summary>The server's own size for a list it cuts no list at.</summary>
    public const int Uncut = -1;

    public Task OnInitializedAsync(ClientCapabilities clientCapabilities, RequestContext context, CancellationToken cancellationToken)
    {
        options.SetGlobalOption(LspOptionsStorage.MaxCompletionListSize, Uncut);
        return Task.CompletedTask;
    }
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(WholeCompletionLists), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class WholeCompletionListsFactory : ILspServiceFactory
{
    private readonly IGlobalOptionService options;

    [ImportingConstructor]
    public WholeCompletionListsFactory(IGlobalOptionService options)
    {
        this.options = options;
    }

    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new WholeCompletionLists(options);
}
