using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Passenger.Xaml.Metadata;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>What the MVVM Toolkit's generators make of the field or method at a caret, which no rename of its own name
/// reaches: an <c>[ObservableProperty]</c> field's property and the code's own OnXChanging and OnXChanged, a
/// <c>[RelayCommand]</c> method's command and, where it asks for one, its cancel command; each use of them in the solution's
/// own files, with the name it takes once the field or method has the name asked (<see cref="ToolkitAsked.Name"/>).</summary>
public sealed class ToolkitRenames : IExtensionDocumentMessageHandler<ToolkitAsked, ToolkitFound>
{
    private const string ObservableProperty = "CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute";
    private const string RelayCommand = "CommunityToolkit.Mvvm.Input.RelayCommandAttribute";

    public async Task<ToolkitFound> ExecuteAsync(ToolkitAsked message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int position = text.Lines.GetPosition(new LinePosition(message.At.Line, message.At.Character));
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        Solution solution = context.Solution;
        string bare = message.Name.StartsWith('@') ? message.Name[1..] : message.Name;
        List<(ISymbol Made, string? Name)> made = [];
        switch (symbol)
        {
            case IFieldSymbol { ContainingType: { } type } field when Marked(field, ObservableProperty) && ToolkitNames.PropertyOf(field.Name) is { } property:
                string? renamed = ToolkitNames.PropertyOf(bare);
                made.AddRange(type.GetMembers(property).OfType<IPropertySymbol>().Where(p => IsGenerated(p, solution)).Select(p => ((ISymbol)p, renamed)));
                foreach (string change in (string[])["Changing", "Changed"])
                {
                    // The code's own part of a change method the generator declares, which its new name's no longer is.
                    made.AddRange(type.GetMembers($"On{property}{change}").OfType<IMethodSymbol>().Where(m => IsGenerated(m, solution))
                        .SelectMany(m => m.PartialImplementationPart is { } own ? (ISymbol[])[m, own] : [m])
                        .Select(m => (m, renamed is null ? null : $"On{renamed}{change}")));
                }
                break;
            case IMethodSymbol { MethodKind: MethodKind.Ordinary, ContainingType: { } type } method when Marked(method, RelayCommand):
                bool task = ReturnsTask(method);
                foreach (Func<string, string> command in (Func<string, string>[])[name => ToolkitNames.CommandOf(name, task), ToolkitNames.CancelCommandOf])
                {
                    made.AddRange(type.GetMembers(command(method.Name)).OfType<IPropertySymbol>().Where(p => IsGenerated(p, solution))
                        .Select(p => ((ISymbol)p, (string?)command(bare))));
                }
                break;
            default:
                return ToolkitFound.None;
        }
        List<ToolkitUse> uses = [];
        HashSet<(string, int)> seen = [];
        foreach ((ISymbol member, string? name) in made)
        {
            if (name is null)
            {
                // Whether the uses the new name leaves without a member are any.
                if ((await UsesOfAsync(solution, member, seen, string.Empty, cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    return new ToolkitFound([], $"the MVVM Toolkit would make no property of '{bare}' for {member.Name}'s uses");
                }
                continue;
            }
            uses.AddRange(await UsesOfAsync(solution, member, seen, name, cancellationToken).ConfigureAwait(false));
        }
        return new ToolkitFound(uses, null);
    }

    /// <summary>A member's declarations and uses in the solution's own files, a generator's left out, each to take <paramref name="name"/>.</summary>
    private static async Task<List<ToolkitUse>> UsesOfAsync(Solution solution, ISymbol member, HashSet<(string, int)> seen, string name, CancellationToken cancel)
    {
        List<(Document Document, TextSpan Span)> places = [];
        foreach (Location declared in member.Locations.Where(l => l.IsInSource && l.SourceTree is not null))
        {
            if (solution.GetDocument(declared.SourceTree) is { } document and not SourceGeneratedDocument)
            {
                places.Add((document, declared.SourceSpan));
            }
        }
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(member, solution, cancel).ConfigureAwait(false))
        {
            places.AddRange(referenced.Locations.Where(l => l.Document is not SourceGeneratedDocument && l.Location.IsInSource && !l.IsImplicit)
                .Select(l => (l.Document, l.Location.SourceSpan)));
        }
        List<ToolkitUse> uses = [];
        foreach ((Document document, TextSpan span) in places)
        {
            if (document.FilePath is not { Length: > 0 } path || !seen.Add((path, span.Start)))
            {
                continue;
            }
            SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
            uses.Add(new ToolkitUse(path, PlaceAt(text, span.Start), PlaceAt(text, span.End), name));
        }
        return uses;
    }

    /// <summary>Whether a method returns a Task or a type made from one, what the toolkit takes for asynchronous.</summary>
    private static bool ReturnsTask(IMethodSymbol method)
    {
        for (ITypeSymbol? type = method.ReturnType; type is not null; type = type.BaseType)
        {
            if (type is { Name: "Task", ContainingNamespace: { } home } && home.ToDisplayString() == "System.Threading.Tasks")
            {
                return true;
            }
        }
        return false;
    }

    private static bool Marked(ISymbol symbol, string attribute) => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attribute);

    private static bool IsGenerated(ISymbol symbol, Solution solution) =>
        symbol.DeclaringSyntaxReferences.Length > 0 && symbol.DeclaringSyntaxReferences.All(r => solution.GetDocument(r.SyntaxTree) is SourceGeneratedDocument);
}
