using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0011: a number written in the culture of the machine the code runs on: a float, double or decimal, or a whole
/// number in a format that sets thousands, a fraction, a percent or an exponent apart, formatted with no provider, a null
/// one or the current culture's, as a hole of an interpolated string, by its ToString, joined to a string by +, or written
/// by a StringBuilder, TextWriter, Console or Convert; and any number given the current culture by name. A whole number
/// with no such format reads alike in every culture, and a provider the code chose itself passes, as does a string made
/// as a FormattableString, whose culture is chosen where it is written out. Looked at wherever Numbers can be reached but
/// in a test project (one that reaches xunit's Fact).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureNumbers() : HomeOnlyRule(Rules.CultureNumber, "Passenger.Core.Numbers", OperationKind.Interpolation,
    OperationKind.InterpolatedStringAppendFormatted, OperationKind.Invocation, OperationKind.Binary, OperationKind.CompoundAssignment)
{
    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("System.IFormatProvider") is not { } provider
            || compilation.GetTypeByMetadataName("Xunit.FactAttribute") is not null)
        {
            return null;
        }
        Kinds kinds = new(compilation, provider);
        return operation => operation switch
        {
            IInterpolationOperation hole => kinds.Hole(hole.Parent, hole.Expression, hole.FormatString),
            IInterpolatedStringAppendOperation { AppendCall: IInvocationOperation append } => kinds.Hole(operation.Parent,
                append.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value, Named(append, "format")),
            IInvocationOperation call => kinds.Call(call),
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } join =>
                kinds.Joined(join.LeftOperand) ?? kinds.Joined(join.RightOperand),
            ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } added => kinds.Joined(added.Value),
            _ => null,
        };
    }

    /// <summary>The argument given for the parameter of that name, if any.</summary>
    private static IOperation? Named(IInvocationOperation call, string name) => call.Arguments.FirstOrDefault(a => a.Parameter?.Name == name)?.Value;

    /// <summary>What the rule knows of this compilation's types.</summary>
    private sealed class Kinds(Compilation compilation, INamedTypeSymbol provider)
    {
        private readonly INamedTypeSymbol? culture = compilation.GetTypeByMetadataName("System.Globalization.CultureInfo");
        private readonly INamedTypeSymbol? numberFormat = compilation.GetTypeByMetadataName("System.Globalization.NumberFormatInfo");
        private readonly INamedTypeSymbol? thread = compilation.GetTypeByMetadataName("System.Threading.Thread");
        private readonly INamedTypeSymbol? formattable = compilation.GetTypeByMetadataName("System.FormattableString");
        private readonly INamedTypeSymbol? iformattable = compilation.GetTypeByMetadataName("System.IFormattable");
        private readonly INamedTypeSymbol? builder = compilation.GetTypeByMetadataName("System.Text.StringBuilder");
        private readonly INamedTypeSymbol? writer = compilation.GetTypeByMetadataName("System.IO.TextWriter");
        private readonly INamedTypeSymbol? console = compilation.GetTypeByMetadataName("System.Console");
        private readonly INamedTypeSymbol? convert = compilation.GetTypeByMetadataName("System.Convert");
        private readonly INamedTypeSymbol? half = compilation.GetTypeByMetadataName("System.Half");

        /// <summary>A hole of the interpolated string <paramref name="text"/> holding <paramref name="value"/> in
        /// <paramref name="format"/>.</summary>
        public object[]? Hole(IOperation? text, IOperation? value, IOperation? format)
        {
            if (Number(value) is not { } type || Culture(text) is not { } given)
            {
                return null;
            }
            return Flagged(type, Format(format), given);
        }

        /// <summary>A number's ToString, or a write that formats one.</summary>
        public object[]? Call(IInvocationOperation call)
        {
            IMethodSymbol method = call.TargetMethod;
            IOperation? given = call.Arguments.FirstOrDefault(a => Symbols.Same(a.Parameter?.Type, provider))?.Value;
            if (method.Name == "ToString" && Number(call.Instance) is { } type)
            {
                return Flagged(type, Format(Named(call, "format")), new Given(given));
            }
            bool writes = method.ContainingType is { } home && (method.Name is "Append" or "Insert" && Symbols.Same(home, builder)
                || method.Name is "Write" or "WriteLine" && (Derives(home, writer) || Symbols.Same(home, console))
                || method.Name == "ToString" && Symbols.Same(home, convert));
            if (!writes)
            {
                return null;
            }
            foreach (IArgumentOperation argument in call.Arguments)
            {
                if (argument.Parameter?.Type is { } parameter && Number(parameter) is { } written && Flagged(written, null, new Given(given)) is { } flagged)
                {
                    return flagged;
                }
            }
            return null;
        }

        /// <summary>One side of a string joined by +.</summary>
        public object[]? Joined(IOperation side) => Number(side) is { } type ? Flagged(type, null, new Given(null)) : null;

        /// <summary>The words of the rule's message where <paramref name="type"/> in <paramref name="format"/>, given
        /// <paramref name="given"/>, is written in the machine's culture; null where it is not.</summary>
        private object[]? Flagged(ITypeSymbol type, string? format, Given given)
        {
            if (!Machines(given.Provider))
            {
                return null;
            }
            bool named = given.Provider is not null && Unwrapped(given.Provider) is IPropertyReferenceOperation;
            if (!named && !Floating(type) && !SetsApart(format))
            {
                return null;
            }
            string shown = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return [format is null ? shown : $"{shown} in {format}"];
        }

        /// <summary>The culture the interpolated string <paramref name="text"/> is formatted in, null where it is chosen
        /// where the string is written out.</summary>
        private Given? Culture(IOperation? text)
        {
            IOperation? at = text;
            while (at?.Parent is IInterpolatedStringAdditionOperation)
            {
                at = at.Parent;
            }
            switch (at?.Parent)
            {
                case IInterpolatedStringHandlerCreationOperation { HandlerCreation: IObjectCreationOperation creation } handler:
                    IOperation? argument = creation.Arguments.FirstOrDefault(a => Symbols.Same(a.Parameter?.Type, provider))?.Value;
                    if (argument is null)
                    {
                        return new Given(null);
                    }
                    if (argument is IInterpolatedStringHandlerArgumentPlaceholderOperation
                        {
                            PlaceholderKind: InterpolatedStringArgumentPlaceholderKind.CallsiteArgument, ArgumentIndex: int index,
                        }
                        && handler.Parent is IArgumentOperation { Parent: { } passed })
                    {
                        IEnumerable<IArgumentOperation> arguments = passed switch
                        {
                            IInvocationOperation call => call.Arguments,
                            IObjectCreationOperation made => made.Arguments,
                            _ => [],
                        };
                        return new Given(arguments.FirstOrDefault(a => a.Parameter?.Ordinal == index)?.Value ?? argument);
                    }
                    return new Given(argument);
                case IConversionOperation conversion when Symbols.Same(conversion.Type, formattable) || Symbols.Same(conversion.Type, iformattable):
                    return null;
                default:
                    return new Given(null);
            }
        }

        /// <summary>Whether <paramref name="given"/>, a provider or none, formats in the machine's culture.</summary>
        private bool Machines(IOperation? given) => Unwrapped(given) switch
        {
            null => true,
            { ConstantValue: { HasValue: true, Value: null } } => true,
            IDefaultValueOperation => true,
            IPropertyReferenceOperation { Property: { } property } => property.Name is "CurrentCulture" or "CurrentUICulture"
                && (Symbols.Same(property.ContainingType, culture) || Symbols.Same(property.ContainingType, thread))
                || property.Name == "CurrentInfo" && Symbols.Same(property.ContainingType, numberFormat),
            _ => false,
        };

        /// <summary>The numeric type <paramref name="value"/> holds (a nullable's underlying one), null where it holds
        /// no number.</summary>
        private ITypeSymbol? Number(IOperation? value) => Unwrapped(value)?.Type is { } type ? Number(type) : null;

        private ITypeSymbol? Number(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullable)
            {
                type = nullable.TypeArguments[0];
            }
            return Floating(type) || type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
                or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64
                or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr ? type : null;
        }

        private bool Floating(ITypeSymbol type) => type.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
            || Symbols.Same(type, half);

        /// <summary>Whether a type is <paramref name="home"/> or derives from it.</summary>
        private static bool Derives(INamedTypeSymbol type, INamedTypeSymbol? home)
        {
            for (INamedTypeSymbol? at = type; at is not null; at = at.BaseType)
            {
                if (Symbols.Same(at, home))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>The provider a number is formatted with, none where none was given.</summary>
    private sealed class Given(IOperation? provider)
    {
        public IOperation? Provider { get; } = provider;
    }

    /// <summary>The operation under its implicit conversions (a boxing, a widening).</summary>
    private static IOperation? Unwrapped(IOperation? value)
    {
        IOperation? at = value;
        while (at is IConversionOperation { IsImplicit: true } conversion)
        {
            at = conversion.Operand;
        }
        return at;
    }

    /// <summary>A format written as a constant, "?" where it is made as the code runs, null where there is none.</summary>
    private static string? Format(IOperation? format) => format switch
    {
        null => null,
        { ConstantValue: { HasValue: true, Value: string written } } => written,
        { ConstantValue: { HasValue: true, Value: null } } => null,
        _ => "?",
    };

    /// <summary>Whether a whole number written in <paramref name="format"/> takes a separator, a percent or an exponent
    /// from the culture: a standard format of N, C, P, E or F, a custom one holding a comma, a point, a percent, a per mille
    /// or an exponent, or one made as the code runs.</summary>
    private static bool SetsApart(string? format)
    {
        if (format is null or "")
        {
            return false;
        }
        if (format == "?")
        {
            return true;
        }
        bool standard = char.IsLetter(format[0]) && format.Skip(1).All(char.IsDigit) && format.Length <= 4;
        return standard ? "NnCcPpEeFf".IndexOf(format[0]) >= 0 : format.IndexOfAny([',', '.', '%', '‰', 'E', 'e']) >= 0;
    }
}
