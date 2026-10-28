using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0005: a catch of every exception (`catch`, `catch (Exception)`, with no filter) that does not pass what it
/// caught on. A boundary passes it on before it can leave (<see cref="PassesOn"/>): it rethrows it, throws it inside
/// another, or gives it whole (itself, never only its Message) to a call, a value kept or returned, or a string made of
/// it, unless that is in a branch it may not take.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CatchAlls : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.CatchAll];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("System.Exception") is { } exception)
            {
                start.RegisterOperationAction(c => Check(c, exception), OperationKind.Try);
            }
        });
    }

    private static void Check(OperationAnalysisContext context, INamedTypeSymbol exception)
    {
        foreach (ICatchClauseOperation clause in ((ITryOperation)context.Operation).Catches)
        {
            if (clause.Filter is null && (clause.ExceptionType.SpecialType == SpecialType.System_Object || Same(clause.ExceptionType, exception))
                && !PassesOn(clause) && clause.Syntax is CatchClauseSyntax syntax)
            {
                Location place = Location.Create(syntax.SyntaxTree,
                    Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(syntax.CatchKeyword.SpanStart, syntax.Declaration?.Span.End ?? syntax.CatchKeyword.Span.End));
                context.ReportDiagnostic(Diagnostic.Create(Rules.CatchAll, place));
            }
        }
    }

    /// <summary>Whether the catch's block passes what it caught on in a statement it always reaches, before any that may
    /// leave it (a return, break, continue, goto or throw of something else).</summary>
    private static bool PassesOn(ICatchClauseOperation clause)
    {
        ILocalSymbol? caught = (clause.ExceptionDeclarationOrExpression as IVariableDeclaratorOperation)?.Symbol;
        return Passes(clause.Handler.Operations, caught);
    }

    private static bool Passes(ImmutableArray<IOperation> statements, ILocalSymbol? caught)
    {
        foreach (IOperation statement in statements)
        {
            if (Passes(statement, caught))
            {
                return true;
            }
            if (Leaves(statement))
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Whether running the statement passes what was caught on, whichever way it goes.</summary>
    private static bool Passes(IOperation statement, ILocalSymbol? caught) => statement switch
    {
        IBlockOperation block => Passes(block.Operations, caught),
        ILockOperation locked => Given(locked.LockedValue, caught) || Passes(locked.Body, caught),
        IUsingOperation used => Given(used.Resources, caught) || Passes(used.Body, caught),
        IConditionalOperation choice => Given(choice.Condition, caught)
            || (Passes(choice.WhenTrue, caught) && choice.WhenFalse is { } otherwise && Passes(otherwise, caught)),
        ILoopOperation or ITryOperation or ISwitchOperation or ILocalFunctionOperation => false,
        _ => Given(statement, caught),
    };

    /// <summary>Whether the operation, on the path it always takes (no branch of a ?:, &&, || or ??, nothing in a lambda),
    /// rethrows or hands on the exception caught.</summary>
    private static bool Given(IOperation operation, ILocalSymbol? caught)
    {
        switch (operation)
        {
            case IThrowOperation { Exception: null }:
                return true;
            case ILocalReferenceOperation local when caught is not null && Same(local.Local, caught):
                return Handed(local);
            case IAnonymousFunctionOperation or ILocalFunctionOperation:
                return false;
            case IConditionalOperation choice:
                return Given(choice.Condition, caught);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } either:
                return Given(either.LeftOperand, caught);
            case ICoalesceOperation coalesce:
                return Given(coalesce.Value, caught);
            default:
                foreach (IOperation child in operation.ChildOperations)
                {
                    if (Given(child, caught))
                    {
                        return true;
                    }
                }
                return false;
        }
    }

    /// <summary>Whether the exception, read whole, is handed on: thrown, an argument, a value kept or returned, an element,
    /// or part of a string or tuple that is itself handed on.</summary>
    private static bool Handed(IOperation reference)
    {
        IOperation at = reference;
        while (at.Parent is IConversionOperation or IParenthesizedOperation or IInterpolationOperation or IInterpolatedStringOperation or ITupleOperation
            || at.Parent is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String })
        {
            at = at.Parent;
        }
        return at.Parent switch
        {
            IThrowOperation or IArgumentOperation or IReturnOperation or IArrayInitializerOperation or ICollectionExpressionOperation
                or IVariableInitializerOperation => true,
            ISimpleAssignmentOperation assignment => assignment.Value == at,
            _ => false,
        };
    }

    /// <summary>Whether the statement may leave the catch: a return, break, continue, goto or throw outside a lambda.</summary>
    private static bool Leaves(IOperation statement)
    {
        if (statement is IReturnOperation or IBranchOperation or IThrowOperation)
        {
            return true;
        }
        foreach (IOperation child in statement.ChildOperations)
        {
            if (child is not (IAnonymousFunctionOperation or ILocalFunctionOperation) && Leaves(child))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Same(ISymbol? one, ISymbol? other) => one is not null && SymbolEqualityComparer.Default.Equals(one, other);
}
