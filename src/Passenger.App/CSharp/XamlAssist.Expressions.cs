using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;

namespace Passenger.App.CSharp;

/// <summary>A XAML file's C# expressions (MAUI's), answered by the language server as it answers C#: completion, Go to
/// Declaration and Find Usages at a place in one, asked of Passenger's extension, which reads the expression as MAUI's
/// source generator writes it into the page's code.</summary>
internal sealed partial class XamlAssist
{
    /// <summary>What completion offers in the expression a caret is in, where its text starts, and whether the caret is in
    /// the name the expression starts with, which may still become a markup extension's; null outside one, or with no
    /// server loaded.</summary>
    private async Task<(int Start, IReadOnlyList<(CompletionItem Item, XamlItem Offered)> Items, bool Head)?> ExpressionItemsAsync(XamlDocument xaml, int caret,
        CancellationToken cancel)
    {
        if (context.CSharp?.Session is not { IsLoaded: true } session || await xaml.FileAsync() is not { } file
            || XamlExpressions.At(file, caret) is not ({ } expression, bool head))
        {
            return null;
        }
        Roslyn.Extension.MauiCompletions? found;
        try
        {
            found = await session.MauiCompletionAsync(file.Path, XamlService.Asked(expression), caret - expression.ValueStart, cancel);
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
        if (found is null)
        {
            return null;
        }
        return (expression.ValueStart + found.Start, [.. found.Items.Select(i => (new CompletionItem { Label = i.Label, Kind = KindOfTag(i.Kind), Detail = i.Detail, SortText = i.Sort },
            new XamlItem(i.Label, XamlItemKind.Member, i.Insert)))], head);
    }

    /// <summary>A completion item's kind by the tag Roslyn gives it.</summary>
    private static CompletionItemKind KindOfTag(string tag) => tag switch
    {
        "Property" => CompletionItemKind.Property,
        "Field" => CompletionItemKind.Field,
        "Method" or "ExtensionMethod" => CompletionItemKind.Method,
        "Event" => CompletionItemKind.Event,
        "Local" or "Parameter" or "RangeVariable" => CompletionItemKind.Variable,
        "Class" or "Record" or "Delegate" or "Module" => CompletionItemKind.Class,
        "Structure" or "RecordStruct" => CompletionItemKind.Struct,
        "Interface" => CompletionItemKind.Interface,
        "Enum" => CompletionItemKind.Enum,
        "EnumMember" => CompletionItemKind.EnumMember,
        "Constant" => CompletionItemKind.Constant,
        "Namespace" => CompletionItemKind.Module,
        "TypeParameter" => CompletionItemKind.TypeParameter,
        "Keyword" => CompletionItemKind.Keyword,
        _ => CompletionItemKind.Text,
    };

    /// <summary>The C# symbol a place in an expression names, and the file it is read from; null outside an expression.
    /// The symbol is null where the server names none.</summary>
    private async Task<(XamlFile File, Roslyn.Extension.NamedSymbol? Symbol)?> ExpressionSymbolAsync(XamlDocument xaml, int offset)
    {
        if (await xaml.FileAsync() is not { } file || XamlExpressions.At(file, offset) is not ({ } expression, _))
        {
            return null;
        }
        if (context.CSharp?.Session is not { IsLoaded: true } session)
        {
            return (file, null);
        }
        try
        {
            return (file, await session.MauiSymbolAsync(file.Path, XamlService.Asked(expression), offset - expression.ValueStart));
        }
        catch (LanguageServerFailed)
        {
            return (file, null);
        }
    }

    /// <summary>Where Go to Declaration goes from a place in an expression: the symbol's declaration in the folder's code,
    /// else a library's type decompiled; with why not, where there is none. Null outside an expression.</summary>
    private async Task<((string Path, TextPosition Position)? Place, string? Said)?> ExpressionPlaceAsync(XamlDocument xaml, int offset)
    {
        if (await ExpressionSymbolAsync(xaml, offset) is not ({ } file, var symbol))
        {
            return null;
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

    /// <summary>A library's type or member a symbol the server named is, found among the project's references by its
    /// documentation id; null for none.</summary>
    private static XamlTarget? LibraryTarget(XamlFile file, Roslyn.Extension.NamedSymbol symbol)
    {
        if (symbol.Id is not { Length: > 2 } id || id[1] != ':')
        {
            return null;
        }
        string named = id[2..];
        int parameters = named.IndexOf('(');
        if (parameters >= 0)
        {
            named = named[..parameters];
        }
        if (id[0] == 'T')
        {
            return file.Types.Find(named) is { } type ? new XamlTarget(XamlTargetKind.Type, type.Name) { Type = type } : null;
        }
        int dot = named.LastIndexOf('.');
        if (dot <= 0 || file.Types.Find(named[..dot]) is not { } owner)
        {
            return null;
        }
        string member = named[(dot + 1)..];
        int generic = member.IndexOf("``", StringComparison.Ordinal);
        member = generic >= 0 ? member[..generic] : member;
        return new XamlTarget(XamlTargetKind.Member, symbol.Name) { Type = owner, Member = owner.Members.FirstOrDefault(m => m.Name == member) };
    }

    /// <summary>Find Usages from a place in an expression: where the folder's C# and XAML use the symbol it names, as Find
    /// Usages finds them from its declaration. False outside an expression.</summary>
    private async Task<bool> ExpressionUsagesAsync(XamlDocument xaml, int offset, bool inPopup)
    {
        if (await ExpressionSymbolAsync(xaml, offset) is not (_, var symbol))
        {
            return false;
        }
        if (symbol is not { Path: { } path, At: { } at })
        {
            context.Say(symbol is null ? "No usages found" : $"{symbol.Shown} is declared outside the folder's code");
            return true;
        }
        IReadOnlyList<LspLocation> usages = await DeclaredUsesAsync(new SourcePlace(PathNames.Canonical(path), at.Line, at.Character));
        if (usages.Count == 0)
        {
            context.Say("No usages found");
            return true;
        }
        context.ShowUsages($"Usages of {symbol.Name}", usages, inPopup ? offset : null);
        return true;
    }
}
