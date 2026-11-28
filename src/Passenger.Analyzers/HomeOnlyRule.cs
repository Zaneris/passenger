using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Passenger.Analyzers;

/// <summary>A rule that something is done only inside its home type: looked at in every compilation that can reach the
/// home (by its metadata name) and that <see cref="Finder"/> gives a finder for, and reported at each operation of the
/// kinds given that the finder names, or that <see cref="Asked"/>'s finds where the code's .editorconfig asks for more,
/// unless the operation is inside the home.</summary>
public abstract class HomeOnlyRule(DiagnosticDescriptor rule, string home, params OperationKind[] kinds) : DiagnosticAnalyzer
{
    public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [rule];

    public sealed override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName(home) is not { } type || Finder(start.Compilation) is not { } find)
            {
                return;
            }
            Func<IOperation, AnalyzerConfigOptions, object[]?>? asked = Asked(start.Compilation);
            start.RegisterOperationAction(c =>
            {
                if ((find(c.Operation) ?? asked?.Invoke(c.Operation, c.Options.AnalyzerConfigOptionsProvider.GetOptions(c.Operation.Syntax.SyntaxTree))) is { } said
                    && !Symbols.Within(c.ContainingSymbol, type))
                {
                    c.ReportDiagnostic(Diagnostic.Create(rule, c.Operation.Syntax.GetLocation(), said));
                }
            }, kinds);
        });
    }

    /// <summary>What finds, in this compilation, the operations done only in the home: for each, the arguments of the
    /// rule's message, or null where the operation is another. Null where the compilation has nothing to find.</summary>
    protected abstract Func<IOperation, object[]?>? Finder(Compilation compilation);

    /// <summary>What finds, where the code's .editorconfig options ask for them, more operations than the rule finds by
    /// default, as <see cref="Finder"/>'s are named; null, as by default, for a rule that finds no more.</summary>
    protected virtual Func<IOperation, AnalyzerConfigOptions, object[]?>? Asked(Compilation compilation) => null;
}
