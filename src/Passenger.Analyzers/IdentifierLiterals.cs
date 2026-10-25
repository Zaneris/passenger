using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0001: a string literal equal to an identifier of a home ([IdentifierHome]) outside it. One with a letter and a
/// '.', '_', '/', '-', '=' or ':' ("app.open", "Passenger.Accent", "PASSENGER_SETTINGS") is flagged wherever it is
/// written, being no ordinary word; a plain word ("run") only where it is given to, or compared with, a parameter,
/// property or field marked [IdentifierOf] its home's type. Homes are read from this compilation and the Passenger
/// assemblies it references, where it may see them. Passed over: the homes' own constants and generated code, the only
/// places that cannot name the member (an attribute's argument takes the constant like any other).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdentifierLiterals : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.IdentifierLiteral];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            Homes homes = new();
            homes.Add(Homes.Of(start.Compilation.Assembly), start.Compilation);
            foreach (IAssemblySymbol assembly in start.Compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (assembly.Name.StartsWith("Passenger", StringComparison.Ordinal))
                {
                    homes.Add(Homes.Of(assembly), start.Compilation);
                }
            }
            if (homes.Any)
            {
                start.RegisterOperationAction(operation => Check(operation, homes), OperationKind.Literal);
            }
        });
    }

    private static void Check(OperationAnalysisContext context, Homes homes)
    {
        if (context.Operation.ConstantValue is not { HasValue: true, Value: string text } || !homes.Holds(text)
            || homes.Contains(context.ContainingSymbol))
        {
            return;
        }
        IReadOnlyList<IFieldSymbol> named = Distinctive(text) ? homes.Named(text) : homes.Named(text, TakenBy(context.Operation));
        if (named.Count > 0)
        {
            string members = string.Join(" or ", named.Select(Name).Distinct());
            context.ReportDiagnostic(Diagnostic.Create(Rules.IdentifierLiteral, context.Operation.Syntax.GetLocation(), text, members));
        }
    }

    /// <summary>Text with a letter and a mark no ordinary word or argument carries, and no space.</summary>
    private static bool Distinctive(string text) =>
        text.Any(char.IsLetter) && text.IndexOfAny(['.', '_', '/', '-', '=', ':']) >= 0 && !text.Any(char.IsWhiteSpace);

    /// <summary>The homes' types the literal's place takes ([IdentifierOf]): the parameter it is given to, the property or
    /// field it is stored in, or the one it is compared with.</summary>
    private static IEnumerable<ITypeSymbol> TakenBy(IOperation literal)
    {
        IOperation value = literal;
        while (value.Parent is IConversionOperation conversion)
        {
            value = conversion;
        }
        ISymbol? place = value.Parent switch
        {
            IArgumentOperation argument => argument.Parameter,
            ISimpleAssignmentOperation assignment when assignment.Value == value => Of(assignment.Target),
            IPropertyInitializerOperation initializer => initializer.InitializedProperties.FirstOrDefault(),
            IFieldInitializerOperation initializer => initializer.InitializedFields.FirstOrDefault(),
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } comparison =>
                Of(comparison.LeftOperand == value ? comparison.RightOperand : comparison.LeftOperand),
            IPatternOperation pattern => Of(Matched(pattern)),
            ISingleValueCaseClauseOperation clause => Of(Matched(clause)),
            _ => null,
        };
        return place is null ? [] : place.GetAttributes()
            .Where(a => a.AttributeClass?.Name == IdentifierAttributes.Of && a.ConstructorArguments.Length == 1)
            .Select(a => a.ConstructorArguments[0].Value).OfType<ITypeSymbol>();
    }

    /// <summary>What a pattern or case holding the literal is matched against: an is's value, a switch's.</summary>
    private static IOperation? Matched(IOperation pattern)
    {
        for (IOperation? at = pattern.Parent; at is not null; at = at.Parent)
        {
            switch (at)
            {
                case IIsPatternOperation test:
                    return test.Value;
                case ISwitchExpressionOperation choice:
                    return choice.Value;
                case ISwitchOperation choice:
                    return choice.Value;
                case IPatternOperation or ISwitchExpressionArmOperation or IPatternCaseClauseOperation or ISwitchCaseOperation:
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    private static ISymbol? Of(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }
        return operation switch
        {
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            IParameterReferenceOperation parameter => parameter.Parameter,
            _ => null,
        };
    }

    private static string Name(IFieldSymbol field)
    {
        string name = field.Name;
        for (INamedTypeSymbol? type = field.ContainingType; type is not null; type = type.ContainingType)
        {
            name = type.Name + "." + name;
        }
        return name;
    }

    /// <summary>The identifiers of the homes one compilation sees: each by its text, and the types that hold them.</summary>
    private sealed class Homes
    {
        private readonly Dictionary<string, List<IFieldSymbol>> byText = new(StringComparer.Ordinal);
        private readonly HashSet<ISymbol> holders = new(SymbolEqualityComparer.Default);

        public bool Any => byText.Count > 0;

        public bool Holds(string text) => byText.ContainsKey(text);

        public IReadOnlyList<IFieldSymbol> Named(string text) => byText[text];

        public IReadOnlyList<IFieldSymbol> Named(string text, IEnumerable<ITypeSymbol> takenBy)
        {
            HashSet<ITypeSymbol> types = new(takenBy, SymbolEqualityComparer.Default);
            return types.Count == 0 ? [] : byText[text].Where(f => types.Contains(f.ContainingType)).ToList();
        }

        /// <summary>Whether code of this symbol is inside a home: a home's type or one of its constants.</summary>
        public bool Contains(ISymbol? symbol)
        {
            for (ISymbol? at = symbol; at is not null; at = at.ContainingSymbol)
            {
                if (holders.Contains(at))
                {
                    return true;
                }
                if (at is INamespaceSymbol)
                {
                    return false;
                }
            }
            return false;
        }

        public void Add(Homes other, Compilation compilation)
        {
            bool own = other.holders.All(h => SymbolEqualityComparer.Default.Equals(h.ContainingAssembly, compilation.Assembly));
            foreach (KeyValuePair<string, List<IFieldSymbol>> entry in other.byText)
            {
                foreach (IFieldSymbol field in entry.Value)
                {
                    if (own || compilation.IsSymbolAccessibleWithin(field, compilation.Assembly))
                    {
                        Add(entry.Key, field);
                    }
                }
            }
            holders.UnionWith(other.holders);
        }

        /// <summary>Every home in an assembly, whoever may see it.</summary>
        public static Homes Of(IAssemblySymbol assembly)
        {
            Homes homes = new();
            Stack<INamespaceOrTypeSymbol> pending = new();
            pending.Push(assembly.GlobalNamespace);
            while (pending.Count > 0)
            {
                INamespaceOrTypeSymbol at = pending.Pop();
                foreach (INamespaceOrTypeSymbol inner in at is INamespaceSymbol space
                    ? space.GetMembers() : ((INamedTypeSymbol)at).GetTypeMembers().Cast<INamespaceOrTypeSymbol>())
                {
                    pending.Push(inner);
                }
                if (at is INamedTypeSymbol type)
                {
                    homes.Read(type);
                }
            }
            return homes;
        }

        private void Read(INamedTypeSymbol type)
        {
            bool home = IsHome(type);
            if (home)
            {
                holders.Add(type);
            }
            foreach (ISymbol member in type.GetMembers())
            {
                if (member is IFieldSymbol { IsConst: true, ConstantValue: string text } field && text.Length > 0
                    && (home || IsHome(field)))
                {
                    Add(text, field);
                    holders.Add(field);
                }
            }
        }

        private void Add(string text, IFieldSymbol field)
        {
            if (!byText.TryGetValue(text, out List<IFieldSymbol>? fields))
            {
                byText[text] = fields = [];
            }
            fields.Add(field);
        }

        private static bool IsHome(ISymbol symbol) => symbol.GetAttributes().Any(a => a.AttributeClass?.Name == IdentifierAttributes.Home);
    }
}
