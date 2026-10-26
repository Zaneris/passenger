using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0004: a thread held waiting on a task: a Task's or ValueTask's Result, Task's Wait, WaitAll and WaitAny, and
/// an awaiter's GetResult. A read of a task known to have ended waits for nothing and is passed over (<see cref="Ended"/>):
/// the task is one the member cannot swap for another (<see cref="Held"/>), and the read sits where its having ended is
/// certain, as ending is for good.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingWaits : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> EndedProperties =
        ImmutableHashSet.Create(StringComparer.Ordinal, "IsCompleted", "IsCompletedSuccessfully", "IsFaulted", "IsCanceled");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.BlockingWait];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (Tasks.Of(start.Compilation) is { } tasks)
            {
                start.RegisterOperationAction(c => CheckRead(c, tasks), OperationKind.PropertyReference);
                start.RegisterOperationAction(c => CheckCall(c, tasks), OperationKind.Invocation);
            }
        });
    }

    private static void CheckRead(OperationAnalysisContext context, Tasks tasks)
    {
        IPropertyReferenceOperation read = (IPropertyReferenceOperation)context.Operation;
        if (read.Property.Name == "Result" && tasks.IsTask(read.Property.ContainingType) && !InNameOf(read)
            && !Ended(read.Instance, read, tasks))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.BlockingWait, read.Syntax.GetLocation(), "Result"));
        }
    }

    private static void CheckCall(OperationAnalysisContext context, Tasks tasks)
    {
        IInvocationOperation call = (IInvocationOperation)context.Operation;
        IMethodSymbol method = call.TargetMethod;
        (string? said, IOperation? task) = method.Name switch
        {
            "Wait" when tasks.IsTask(method.ContainingType) && !method.IsStatic => ("Wait", call.Instance),
            "WaitAll" or "WaitAny" when tasks.IsTask(method.ContainingType) && method.IsStatic => ("Task." + method.Name, null),
            "GetResult" when Tasks.IsAwaiter(method.ContainingType) => ("GetResult", Awaited(call.Instance)),
            _ => (null, null),
        };
        if (said is not null && !Ended(task, call, tasks))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.BlockingWait, call.Syntax.GetLocation(), said));
        }
    }

    /// <summary>The task an awaiter was got from (`task.GetAwaiter()`, `task.ConfigureAwait(false).GetAwaiter()`).</summary>
    private static IOperation? Awaited(IOperation? awaiter) =>
        Unwrapped(awaiter) is IInvocationOperation { TargetMethod.Name: "GetAwaiter", Instance: { } awaitable } ? Configured(awaitable) : null;

    private static IOperation? Configured(IOperation? awaitable) =>
        Unwrapped(awaitable) is IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: { } task } ? Unwrapped(task) : Unwrapped(awaitable);

    /// <summary>Whether the task is known to have ended where it is read: the task a WhenAny gave, a ContinueWith's
    /// antecedent, or one read where a condition on it, an early exit or an await of it has settled that it ended.</summary>
    private static bool Ended(IOperation? task, IOperation at, Tasks tasks)
    {
        if (Unwrapped(task) is IAwaitOperation awaited && tasks.IsWhenAny(Unwrapped(awaited.Operation)))
        {
            return true;
        }
        Path held = Held(task);
        if (held.IsEmpty || !Unchanged(held, at))
        {
            return false;
        }
        if (Antecedent(held, at, tasks))
        {
            return true;
        }
        bool before = true;
        IOperation child = at;
        for (IOperation? parent = at.Parent; parent is not null; child = parent, parent = parent.Parent)
        {
            bool known = parent switch
            {
                IConditionalOperation choice => (choice.WhenTrue == child && Implies(choice.Condition, true, held, tasks))
                    || (choice.WhenFalse == child && Implies(choice.Condition, false, held, tasks)),
                IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } both =>
                    both.RightOperand == child && Implies(both.LeftOperand, true, held, tasks),
                IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } either =>
                    either.RightOperand == child && Implies(either.LeftOperand, false, held, tasks),
                IBlockOperation block => before && Preceded(block.Operations, child, held, tasks),
                ISwitchCaseOperation section => before && Preceded(section.Body, child, held, tasks),
                _ => false,
            };
            if (known)
            {
                return true;
            }
            // A local function may be called before the statements ahead of it in its block.
            if (parent is ILocalFunctionOperation)
            {
                before = false;
            }
        }
        return false;
    }

    /// <summary>Whether the task is the first parameter of a lambda given to a task's ContinueWith, which runs it once the
    /// task has ended.</summary>
    private static bool Antecedent(Path held, IOperation at, Tasks tasks)
    {
        if (held.Symbols.Length != 1 || held.Symbols[0] is not IParameterSymbol { Ordinal: 0, ContainingSymbol: IMethodSymbol lambda })
        {
            return false;
        }
        for (IOperation? parent = at.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IAnonymousFunctionOperation function && Same(function.Symbol, lambda))
            {
                IOperation? given = function.Parent is IDelegateCreationOperation created ? created.Parent : function.Parent;
                return given is IArgumentOperation { Parent: IInvocationOperation { TargetMethod: { Name: "ContinueWith" } method } }
                    && tasks.IsTask(method.ContainingType);
            }
        }
        return false;
    }

    /// <summary>Whether a statement ahead of the child in its block leaves the task ended for good: an await of it, or an if
    /// on its not having ended whose branch never falls through to what follows (it exits, or ends awaiting it).</summary>
    private static bool Preceded(ImmutableArray<IOperation> statements, IOperation child, Path held, Tasks tasks)
    {
        for (int i = statements.IndexOf(child) - 1; i >= 0; i--)
        {
            IOperation statement = statements[i];
            if (statement is ILabeledOperation)
            {
                // A jump may land between what came before and the read.
                return false;
            }
            if (Awaits(statement, held, tasks))
            {
                return true;
            }
            if (statement is IConditionalOperation { WhenFalse: null, WhenTrue: { } branch } test
                && Implies(test.Condition, false, held, tasks) && (!FallsThrough(branch) || EndsAwaiting(branch, held, tasks)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool EndsAwaiting(IOperation branch, Path held, Tasks tasks) =>
        branch is IBlockOperation block ? block.Operations.Length > 0 && Awaits(block.Operations[block.Operations.Length - 1], held, tasks)
            : Awaits(branch, held, tasks);

    private static bool FallsThrough(IOperation statement)
    {
        if (statement.SemanticModel is not { } model || statement.Syntax is not StatementSyntax syntax)
        {
            return true;
        }
        ControlFlowAnalysis? flow = model.AnalyzeControlFlow(syntax);
        return flow is not { Succeeded: true } || flow.EndPointIsReachable;
    }

    /// <summary>Whether a statement awaits the task whole: `await task;`, `x = await task;`, `T x = await task;`, or an await
    /// of Task.WhenAll naming it.</summary>
    private static bool Awaits(IOperation statement, Path held, Tasks tasks)
    {
        IOperation? value = statement switch
        {
            IExpressionStatementOperation { Operation: ISimpleAssignmentOperation assignment } => assignment.Value,
            IExpressionStatementOperation expression => expression.Operation,
            IVariableDeclarationGroupOperation group when group.Declarations.Length == 1 && group.Declarations[0].Declarators.Length == 1 =>
                group.Declarations[0].Declarators[0].Initializer?.Value,
            _ => null,
        };
        if (Unwrapped(value) is not IAwaitOperation awaited)
        {
            return false;
        }
        IOperation? task = Configured(awaited.Operation);
        if (Held(task).Equals(held))
        {
            return true;
        }
        return task is IInvocationOperation all && tasks.IsWhenAll(all) && all.Arguments.SelectMany(a => Elements(a.Value)).Any(e => Held(e).Equals(held));
    }

    private static IEnumerable<IOperation> Elements(IOperation argument) => Unwrapped(argument) switch
    {
        IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues,
        ICollectionExpressionOperation collection => collection.Elements,
        { } single => [single],
        _ => [],
    };

    /// <summary>Whether the condition being the value means the task has ended.</summary>
    private static bool Implies(IOperation? condition, bool value, Path held, Tasks tasks)
    {
        switch (Unwrapped(condition))
        {
            case IPropertyReferenceOperation property when IsEnded(property.Property, tasks) && Held(property.Instance).Equals(held):
                return value;
            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } not:
                return Implies(not.Operand, !value, held, tasks);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } both:
                return value && (Implies(both.LeftOperand, true, held, tasks) || Implies(both.RightOperand, true, held, tasks));
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } either:
                return !value && (Implies(either.LeftOperand, false, held, tasks) || Implies(either.RightOperand, false, held, tasks));
            case IIsPatternOperation test:
                Path input = Held(test.Value);
                return Matches(test.Pattern, input.IsEmpty ? [] : [input], value, held, tasks);
            default:
                return false;
        }
    }

    /// <summary>Whether the pattern matching (or failing to, by the value) means the task has ended: a property pattern
    /// with one of its ended properties true, on the task or on a member of it that is the task, its input named by any
    /// of the paths or the local the pattern declares.</summary>
    private static bool Matches(IPatternOperation pattern, IReadOnlyList<Path> inputs, bool value, Path held, Tasks tasks)
    {
        switch (pattern)
        {
            case INegatedPatternOperation not:
                return Matches(not.Pattern, inputs, !value, held, tasks);
            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } both:
                return value && (Matches(both.LeftPattern, inputs, true, held, tasks) || Matches(both.RightPattern, inputs, true, held, tasks));
            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } either:
                return !value && (Matches(either.LeftPattern, inputs, false, held, tasks) || Matches(either.RightPattern, inputs, false, held, tasks));
            case IRecursivePatternOperation recursive when value:
                List<Path> names = [.. inputs];
                if (recursive.DeclaredSymbol is { } declared)
                {
                    names.Add(new Path([declared]));
                }
                foreach (IPropertySubpatternOperation member in recursive.PropertySubpatterns)
                {
                    if (Members(member.Member) is not { } chain)
                    {
                        continue;
                    }
                    if (chain.Count > 0 && chain[chain.Count - 1] is IPropertySymbol last && IsEnded(last, tasks)
                        && member.Pattern is IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: true } }
                        && chain.Take(chain.Count - 1).All(Fixed) && names.Any(n => n.With(chain.Take(chain.Count - 1)).Equals(held)))
                    {
                        return true;
                    }
                    List<Path> within = chain.All(Fixed) ? [.. names.Select(n => n.With(chain))] : [];
                    if (Matches(member.Pattern, within, true, held, tasks))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>The members a property subpattern reads from its input, outermost first (`{ Diff.IsCompleted: true }` reads
    /// Diff, then IsCompleted); null for anything else.</summary>
    private static List<ISymbol>? Members(IOperation? member)
    {
        List<ISymbol> chain = [];
        for (IOperation? at = member; ; )
        {
            switch (at)
            {
                case IPropertyReferenceOperation property:
                    chain.Insert(0, property.Property);
                    at = property.Instance;
                    break;
                case IFieldReferenceOperation field:
                    chain.Insert(0, field.Field);
                    at = field.Instance;
                    break;
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.PatternInput }:
                    return chain;
                default:
                    return null;
            }
        }
    }

    private static bool IsEnded(IPropertySymbol property, Tasks tasks) =>
        EndedProperties.Contains(property.Name) && tasks.IsTask(property.ContainingType);

    /// <summary>The task as what holds one task throughout the member: a local or parameter (not a reference to another's),
    /// this object, or a readonly field or get-only auto-property of one of them or of a type; empty for anything else.</summary>
    private static Path Held(IOperation? task)
    {
        switch (Unwrapped(task))
        {
            case ILocalReferenceOperation { Local.RefKind: RefKind.None } local:
                return new Path([local.Local]);
            case IParameterReferenceOperation { Parameter.RefKind: RefKind.None } parameter:
                return new Path([parameter.Parameter]);
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Type: { } type }:
                return new Path([type]);
            case IFieldReferenceOperation field when Fixed(field.Field):
                return field.Field.IsStatic ? new Path([field.Field]) : Held(field.Instance).With([field.Field]);
            case IPropertyReferenceOperation property when Fixed(property.Property):
                return property.Property.IsStatic ? new Path([property.Property]) : Held(property.Instance).With([property.Property]);
            default:
                return default;
        }
    }

    /// <summary>A member whose value only its object's making sets: a readonly field, or an auto-property with no setter or
    /// an init-only one.</summary>
    private static bool Fixed(ISymbol member) => member switch
    {
        IFieldSymbol field => field.IsReadOnly,
        IPropertySymbol { Parameters.Length: 0 } property => property.SetMethod is null or { IsInitOnly: true }
            && property.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => Same(f.AssociatedSymbol, property)),
        _ => false,
    };

    /// <summary>Whether nothing in the member writes any part of the task's path but its declaration (or, for a local declared
    /// with no value, the one assignment it is given, made once), so it is one task wherever it is read.</summary>
    private static bool Unchanged(Path held, IOperation at)
    {
        IOperation root = at;
        while (root.Parent is { } parent)
        {
            root = parent;
        }
        foreach (ISymbol symbol in held.Symbols)
        {
            if (symbol is ITypeSymbol)
            {
                continue;
            }
            List<IOperation> writes = [.. root.DescendantsAndSelf().Where(o => Names(o, symbol) && Written(o))];
            if (writes.Count == 0 || (writes.Count == 1 && symbol is ILocalSymbol local && AssignedOnce(local, writes[0], root)))
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool Names(IOperation operation, ISymbol symbol) => operation switch
    {
        ILocalReferenceOperation { IsDeclaration: false } local => Same(local.Local, symbol),
        IParameterReferenceOperation parameter => Same(parameter.Parameter, symbol),
        IFieldReferenceOperation field => Same(field.Field, symbol),
        IPropertyReferenceOperation property => Same(property.Property, symbol),
        _ => false,
    };

    /// <summary>Whether the one write of a local declared with no value runs once each time the local is declared: no loop,
    /// lambda or local function between it and the declaration, and no label a jump could go back to.</summary>
    private static bool AssignedOnce(ILocalSymbol local, IOperation write, IOperation root)
    {
        IOperation[] all = [.. root.DescendantsAndSelf()];
        if (all.OfType<IVariableDeclaratorOperation>().FirstOrDefault(d => Same(d.Symbol, local)) is not { Initializer: null } declarator
            || all.OfType<ILabeledOperation>().Any() || write.Parent is not ISimpleAssignmentOperation)
        {
            return false;
        }
        for (IOperation? at = write.Parent; at is not null; at = at.Parent)
        {
            if (at.Syntax.Span.Contains(declarator.Syntax.Span))
            {
                return true;
            }
            if (at is ILoopOperation or IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return false;
            }
        }
        return false;
    }

    private static bool Written(IOperation reference)
    {
        IOperation at = reference;
        while (at.Parent is IConversionOperation or IParenthesizedOperation or ITupleOperation)
        {
            at = at.Parent;
        }
        return at.Parent switch
        {
            IAssignmentOperation assignment => assignment.Target == at,
            IIncrementOrDecrementOperation => true,
            IArgumentOperation argument => argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out,
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => true,
            _ => false,
        };
    }

    private static bool InNameOf(IOperation operation)
    {
        for (IOperation? at = operation.Parent; at is not null; at = at.Parent)
        {
            if (at is INameOfOperation)
            {
                return true;
            }
        }
        return false;
    }

    private static IOperation? Unwrapped(IOperation? operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation)
        {
            operation = operation is IConversionOperation conversion ? conversion.Operand : ((IParenthesizedOperation)operation).Operand;
        }
        return operation;
    }

    private static bool Same(ISymbol? one, ISymbol? other) => one is not null && SymbolEqualityComparer.Default.Equals(one, other);

    /// <summary>A task named by where it is held: a local, parameter, this object's type or a type's member, then each member
    /// read from it; empty when it is not held anywhere fixed.</summary>
    private readonly struct Path(ImmutableArray<ISymbol> symbols) : IEquatable<Path>
    {
        public ImmutableArray<ISymbol> Symbols => symbols.IsDefault ? [] : symbols;

        public bool IsEmpty => Symbols.IsEmpty;

        public Path With(IEnumerable<ISymbol> members) => IsEmpty ? default : new Path(Symbols.AddRange(members));

        public bool Equals(Path other) => !IsEmpty && Symbols.SequenceEqual(other.Symbols, SymbolEqualityComparer.Default);

        public override bool Equals(object? obj) => obj is Path other && Equals(other);

        public override int GetHashCode() => Symbols.Length;
    }

    /// <summary>The task types of one compilation.</summary>
    private sealed class Tasks(INamedTypeSymbol task, INamedTypeSymbol? valueTask, INamedTypeSymbol? valueTaskOf)
    {
        public static Tasks? Of(Compilation compilation) =>
            compilation.GetTypeByMetadataName("System.Threading.Tasks.Task") is { } task
                ? new Tasks(task, compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"),
                    compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1"))
                : null;

        /// <summary>Task, Task&lt;T&gt;, ValueTask or ValueTask&lt;T&gt;.</summary>
        public bool IsTask(INamedTypeSymbol? type)
        {
            if (type is null)
            {
                return false;
            }
            INamedTypeSymbol definition = type.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(definition, valueTask) || SymbolEqualityComparer.Default.Equals(definition, valueTaskOf))
            {
                return true;
            }
            for (INamedTypeSymbol? at = type; at is not null; at = at.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(at.OriginalDefinition, task))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsWhenAny(IOperation? operation) => operation is IInvocationOperation { TargetMethod: { Name: "WhenAny" } method }
            && IsTask(method.ContainingType);

        public bool IsWhenAll(IInvocationOperation call) => call.TargetMethod.Name == "WhenAll" && IsTask(call.TargetMethod.ContainingType);

        /// <summary>A task's awaiter, plain or configured, whose GetResult waits for it.</summary>
        public static bool IsAwaiter(INamedTypeSymbol? type) =>
            type is not null && type.Name.EndsWith("Awaiter", StringComparison.Ordinal) && type.Name != "YieldAwaiter"
            && type.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices";
    }
}
