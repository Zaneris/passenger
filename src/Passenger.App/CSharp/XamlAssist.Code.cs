using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A XAML page's x:Code, answered by the language server as it answers C#: completion and Go to Declaration at a
/// place in a block, asked of Passenger's extension, which writes the blocks into the page's class as its compiler does.</summary>
internal sealed partial class XamlAssist
{
    /// <summary>What completion offers in the x:Code a caret is in, and where its text starts; null outside one, in a file
    /// with no x:Class, or with no server loaded.</summary>
    private async Task<(int Start, IReadOnlyList<(CompletionItem Item, XamlItem Offered)> Items, bool Head)?> CodeItemsAsync(XamlDocument xaml, int caret, CancellationToken cancel)
    {
        if (context.CSharp?.Session is not { IsLoaded: true } session || await xaml.FileAsync() is not { } file || XamlCodeBlocks.At(file, caret) is null
            || Asked(file) is not { } code)
        {
            return null;
        }
        Roslyn.Extension.MauiCompletions? found;
        try
        {
            found = await session.XamlCodeCompletionAsync(file.Path, file.Text, code, caret, cancel);
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
        return found is null ? null
            : (found.Start, [.. found.Items.Select(i => (new CompletionItem { Label = i.Label, Kind = KindOfTag(i.Kind), Detail = i.Detail, SortText = i.Sort },
                new XamlItem(i.Label, XamlItemKind.Member, i.Insert)))], false);
    }

    /// <summary>Where Go to Declaration goes from a place in x:Code: the symbol's declaration, in the folder's code or in the
    /// x:Code itself, else a library's type decompiled; with why not, where there is none. Null outside x:Code.</summary>
    private async Task<((string Path, TextPosition Position)? Place, string? Said)?> CodePlaceAsync(XamlDocument xaml, int offset)
    {
        if (await xaml.FileAsync() is not { } file || XamlCodeBlocks.At(file, offset) is null)
        {
            return null;
        }
        if (context.CSharp?.Session is not { IsLoaded: true } session || Asked(file) is not { } code)
        {
            return (null, Refusals.NoDeclaration);
        }
        Roslyn.Extension.NamedSymbol? symbol;
        try
        {
            symbol = await session.XamlCodeSymbolAsync(file.Path, file.Text, code, offset);
        }
        catch (LanguageServerFailed)
        {
            return (null, Refusals.NoDeclaration);
        }
        if (symbol is null)
        {
            return (null, Refusals.NoDeclaration);
        }
        if (symbol is { Path: { } path, At: { } at })
        {
            string canonical = PathNames.Canonical(path);
            return ((canonical, ServerPlaces.PositionOf(context.Editors, canonical, new LspPosition(at.Line, at.Character))), null);
        }
        if (LibraryTarget(file, symbol) is { } target)
        {
            return await XamlPlaceAsync(target, file);
        }
        return (null, $"{symbol.Shown} is declared outside the folder's code");
    }

    /// <summary>A file's x:Code as the extension is asked of it; null for none, or a file with no x:Class to put it in.</summary>
    internal static Roslyn.Extension.XamlCode? Asked(XamlFile file) =>
        file.ClassName is { } page && XamlCodeBlocks.In(file) is { Count: > 0 } blocks
            ? new Roslyn.Extension.XamlCode(page, [.. blocks.Select(b => new Roslyn.Extension.XamlCodeBlock(b.Code, b.WrittenOf))])
            : null;
}
