using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>C#'s and Razor's answers, through their language server and Passenger's extension in it (Aspire's resources,
/// the configuration the code reads, localized strings), and the language server's own for any file the ladder leaves (a
/// web language's whose server answers it); the last on the ladder, so it answers every command.</summary>
internal sealed partial class CSharpAssist(AssistContext context, PopupPlacement popups, CodeCompletion completion, ParameterInfo parameters,
    CodeNavigation navigation, CodeActions actions, XamlAssist xaml) : LanguageAssist
{
    /// <summary>A member's dot, and in a Razor file a tag's '&lt;' (the elements and components) and '@' (Razor's directives
    /// and the C#), list what may follow; so do a name's first letter and a space after a member's override. A call's
    /// bracket or comma shows its signature, and a Razor file's '>' and "&lt;/" close their tags.</summary>
    public override bool Typed(string text)
    {
        char last = text[^1];
        int caret = context.Caret;
        if (last == '.' || (context.InRazor && last is '<' or '@'))
        {
            IdeErrors.Observe(completion.Start(last.ToString()));
        }
        else if (last is '"' or '/' or '%' && !context.InRazor && InGodotPath(caret) || last == '"' && !context.InRazor && InGodotFile() && InGodotKind(caret))
        {
            IdeErrors.Observe(completion.Start(null));
        }
        else if (AssistContext.IsIdentifierChar(last) && !char.IsDigit(last) && context.WordStart(caret) == caret - 1)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        else if (last == ' ' && !context.InRazor && context.Syntax is { } syntax && CompletionTriggers.AfterOverride(syntax, caret))
        {
            IdeErrors.Observe(completion.Start(" "));
        }
        CloseRazorTag(text);
        parameters.AfterTyped(last);
        return true;
    }

    /// <summary>An AppHost's or a service's resource names where one is typed, alone; else a localizer's keys, or a Godot
    /// path's nodes or files, ahead of the server's items.</summary>
    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Current?.Lsp is not { } lsp)
        {
            return true;
        }
        await completion.ServerListAsync(lsp, trigger, incomplete, null,
            async (text, caret, cancel) => await LocalizedCompletionAsync(lsp, text, caret, cancel) is { } keys ? new CodeCompletion.OwnItems(keys.Start, keys.Items, keys.Tail)
                : await GodotCompletionAsync(lsp, text, caret, cancel),
            cancel => StartAspireCompletion(lsp, cancel));
        return true;
    }

    /// <summary>The server's description of the symbol, then a configuration key's values, a localized key's languages and
    /// what a Godot project's scenes say of a node path or an exported member; on a string naming what a Godot project
    /// declares (a GDScript member, a node, a res:// path's file), that in place of the server's description of a string.</summary>
    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        (string? godot, FilePreview? preview, bool ofString) = await GodotHoverAsync(document, offset, cancel);
        // What a string names is what it says; the server would describe System.String.
        if (preview is null && !(ofString && godot is not null) && context.ServerOf(document)?.Lsp is { } lsp)
        {
            await QuickInfo.ServerHoverAsync(lsp, card, offset, cancel);
        }
        if (await ConfigurationHoverAsync(document, offset, cancel) is { } configured)
        {
            cancel.ThrowIfCancellationRequested();
            card.AddText(configured);
        }
        if (await LocalizationHoverAsync(document, offset, cancel) is { } localized)
        {
            cancel.ThrowIfCancellationRequested();
            card.AddText(localized);
        }
        cancel.ThrowIfCancellationRequested();
        if (godot is not null)
        {
            card.AddText(godot);
        }
        if (preview is not null)
        {
            card.AddPreview(preview);
        }
        return true;
    }

    /// <summary>Go to Declaration from the file shown, where <see cref="DeclaredAsync"/> leads.</summary>
    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.Ask("Go to Declaration") is null || context.Editors.Active is not { } shown)
        {
            return true;
        }
        try
        {
            if (await DeclaredAsync(shown, offset) is { } declared && context.Editors.Active == shown)
            {
                navigation.Go(declared, offset);
                if (declared.Note is { } note)
                {
                    context.Say(note);
                }
            }
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Go to Declaration", failed);
        }
        return true;
    }

    /// <summary>Aspire's, a configuration read's, a localized key's, a JS interop call's and a Godot path's places first; then the server's, a Godot
    /// script's generated member going to the code's (<see cref="GodotGeneratedDeclaredAsync"/>), where a Dockerfile's
    /// FROM that names no stage goes to its image's registry page, a XAML element's generated field to its name, and any other
    /// place in a source generator's code, a type's generated parts among them, to its copy (<see cref="GeneratedCode"/>).</summary>
    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        if (context.ServerOf(document)?.Lsp is not { } lsp)
        {
            return null;
        }
        if ((await AspireDeclaredAsync(lsp, document, offset) ?? await ConfigurationDeclaredAsync(lsp, document, offset) ?? await LocalizationDeclaredAsync(lsp, document, offset)
            ?? await JsInteropDeclaredAsync(lsp, document, offset) ?? await GodotDeclaredAsync(lsp, document, offset)) is { } own)
        {
            return own;
        }
        IReadOnlyList<LspLocation> found = await lsp.DefinitionAsync(offset);
        if (await GodotGeneratedDeclaredAsync(lsp, document, offset, found) is { } script)
        {
            return script;
        }
        if (found.Count == 0 && context.WebOf(document) is { Kind: WebServerKind.Dockerfile } && WebAssist.FromImageAt(document.Text.Snapshot, offset) is ({ } image, { } page))
        {
            return new Declared([], $"The image {image}") { Page = page };
        }
        if (await xaml.NamedElementOfAsync(document, found, offset) is { } element && await xaml.XamlPlaceAsync(element, null) is ({ } place, _))
        {
            return new Declared(Declared.Served([place], ServerPlaces.Texts(context.Editors)), Refusals.NoDeclaration);
        }
        if (context.CSharp is not { } csharp)
        {
            return new Declared(found, Refusals.NoDeclaration);
        }
        // A project the folder has but C# does not load is declared in its own source, never its assembly's metadata stub.
        (found, bool onlyByName) = await UnloadedSources.OfAsync(found, csharp.UnloadedNow?.Invoke() ?? []);
        // One place of the person's is gone to straight, but from that place, where a generator's parts are all that's left to go to.
        IReadOnlyList<LspLocation> parts = found is [{ } only] && !(PathNames.Same(DocumentUris.ToPath(only.Uri), document.Path) && only.Range.Holds(lsp.PositionOf(offset)))
            ? [] : await lsp.GeneratedPartsAsync(offset);
        return new Declared(await csharp.Generated.CopiedAsync([.. found.Union(parts)]), Refusals.NoDeclaration) { Note = onlyByName ? Refusals.OverloadNotInSource : null };
    }

    public override async Task<bool> UsagesAsync(int offset, bool inPopup)
    {
        await navigation.ServerUsagesAsync(offset, inPopup);
        return true;
    }

    /// <summary>A Godot name of a kind in a string (<see cref="ShowKindRename"/>) or a node a GetNode path names
    /// (<see cref="ShowNodeRename"/>), an Aspire resource's name in a string (the AppHost's declaration and its services'
    /// reads), else the server's rename.</summary>
    public override bool Rename(int caret)
    {
        if (context.Editors.Active is { } shown && InGodotFile() && (InGodotPath(caret) || InString(shown.Text.Snapshot, caret)))
        {
            bool path = InGodotPath(caret);
            IdeErrors.Observe(popups.Carried("Rename", async () =>
            {
                if (!await ShowKindRename(shown, caret) && !(path && await ShowNodeRename(shown, caret)) && context.Editors.Active == shown)
                {
                    actions.ShowServerRename();
                }
            }, closeModal: false));
            return true;
        }
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
