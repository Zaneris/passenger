using Passenger.App.Web;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>C#'s and Razor's answers, through their language server and Passenger's extension in it (Aspire's resources,
/// the configuration the code reads, localized strings), and the language server's own for any file the ladder leaves (a
/// web language's whose server answers it); the last on the ladder, so it answers every command.</summary>
internal sealed partial class CSharpAssist(AssistContext context, PopupPlacement popups, CodeCompletion completion, ParameterInfo parameters,
    CodeNavigation navigation, CodeActions actions, XamlAssist xaml) : LanguageAssist
{
    /// <summary>A member's dot, and in a Razor file a tag's '&lt;' (the elements and components) and '@' (Razor's directives
    /// and the C#), list what may follow; so does a name's first letter. A call's bracket or comma shows its signature.</summary>
    public override bool Typed(string text)
    {
        char last = text[^1];
        int caret = context.Caret;
        if (last == '.' || (context.InRazor && last is '<' or '@'))
        {
            IdeErrors.Observe(completion.Start(last.ToString()));
        }
        else if (AssistContext.IsIdentifierChar(last) && !char.IsDigit(last) && context.WordStart(caret) == caret - 1)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        parameters.AfterTyped(last);
        return true;
    }

    /// <summary>An AppHost's or a service's resource names where one is typed, alone; else a localizer's keys ahead of the
    /// server's items.</summary>
    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Current?.Lsp is not { } lsp)
        {
            return true;
        }
        await completion.ServerListAsync(lsp, trigger, incomplete, null,
            async (text, caret, cancel) => await LocalizedCompletionAsync(lsp, text, caret, cancel) is { } keys ? new CodeCompletion.OwnItems(keys.Start, keys.Items, keys.Tail) : null,
            cancel => StartAspireCompletion(lsp, cancel));
        return true;
    }

    /// <summary>The server's description of the symbol, then a configuration key's values and a localized key's languages.</summary>
    public override async Task<bool> HoverAsync(InfoCard card, int offset, CancellationToken cancel)
    {
        if (context.Current?.Lsp is { } lsp)
        {
            await QuickInfo.ServerHoverAsync(lsp, card, offset, cancel);
        }
        if (await ConfigurationHoverAsync(offset, cancel) is { } configured)
        {
            cancel.ThrowIfCancellationRequested();
            card.AddText(configured);
        }
        if (await LocalizationHoverAsync(offset, cancel) is { } localized)
        {
            cancel.ThrowIfCancellationRequested();
            card.AddText(localized);
        }
        return true;
    }

    /// <summary>Aspire's, a configuration read's and a localized key's places first; then the server's, where a Dockerfile's
    /// FROM that names no stage goes to its image's registry page and a XAML element's generated field to its name.</summary>
    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.Ask("Go to Declaration") is not { } lsp)
        {
            return true;
        }
        try
        {
            if (await AspireGoTo(lsp, offset) || await ConfigurationGoTo(lsp, offset) || await LocalizationGoTo(lsp, offset))
            {
                return true;
            }
            IReadOnlyList<LspLocation> found = await lsp.DefinitionAsync(offset);
            if (found.Count == 0 && context.WebCurrent is { Kind: WebServerKind.Dockerfile } && WebAssist.FromImageAt(context.Editor.Document.Snapshot, offset) is { } page)
            {
                context.Browse(page);
                return true;
            }
            if (await xaml.NamedElementOfAsync(found, offset) is { } element)
            {
                await xaml.Arrive(element);
                return true;
            }
            navigation.Go(found, offset, "Declarations", Refusals.NoDeclaration);
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Go to Declaration", failed);
        }
        return true;
    }

    public override async Task<bool> UsagesAsync(int offset)
    {
        await navigation.ServerUsagesAsync(offset);
        return true;
    }

    /// <summary>An Aspire resource's name in a string: the AppHost's declaration and its services' reads, else the server's rename.</summary>
    public override bool Rename(int caret)
    {
        if (AspireRoleOfShown != Aspire.AspireRole.None && context.Editors.Active is { } aspire && MayNameResource(caret))
        {
            IdeErrors.Observe(popups.Carried("Rename", async () =>
            {
                if (!await ShowAspireRename(aspire, caret) && context.Editors.Active == aspire)
                {
                    actions.ShowServerRename();
                }
            }, closeModal: false));
            return true;
        }
        actions.ShowServerRename();
        return true;
    }
}
