using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Dap;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A call Smart Step Into can step straight into: what it is called, and where it is in its source (lines and
/// columns from one), when the source could be read.</summary>
public sealed record StepInTargetInfo(int Id, string Label, int? Line, int? Column, int? EndLine, int? EndColumn);

/// <summary>Smart Step Into: the calls of the statement a thread is stopped at, in the order they run, each one a step
/// can go straight into, lambdas (whose bodies run when what they are given to calls them) and awaited async methods
/// included. The calls are read from the method's IL between the statement's sequence points, and matched by name, in
/// order, to the invocations, object creations and lambdas of the statement's source, which give each its place on the
/// line. The step is a step over the statement with a breakpoint at the chosen method's first line (an async method's
/// state machine's): it stops there when the method is entered from the chosen call (a lambda's, from whatever the
/// statement called), and ends at the next line as a step over would when it never is.</summary>
public partial class ManagedDebugger
{
    private enum EntryKind
    {
        /// <summary>Called by the statement itself, or through code that is not the user's (an async method's start).</summary>
        Call,

        /// <summary>A delegate's method (a lambda's body), run by whatever the statement handed it to.</summary>
        Delegate,
    }

    /// <summary>A target as the engine keeps it until the program runs on: the method a step into it first stops in (an async
    /// method's MoveNext) and at what IL offset, how it is entered, the IL offset of a call's own instruction in the
    /// statement, and the thread, frame and IL of the statement it is called from.</summary>
    private sealed record StepInPlan(ModuleInfo Module, int MethodToken, int EntryOffset, EntryKind Kind, int CallOffset, int ThreadId, StackRange Frame, int Start, int End);

    private readonly Dictionary<int, StepInPlan> _stepInTargets = [];

    private int _nextStepInTarget = 1;

    /// <summary>A Smart Step Into under way: its target, and the breakpoint at the target's first line.</summary>
    private sealed record SmartStep(StepInPlan Plan, ICorDebugFunctionBreakpoint Breakpoint);

    private SmartStep? _smartStep;

    /// <summary>The calls a thread's top frame can step into from its statement, in the order they run, its source read
    /// with the preprocessor symbols and C# version its project compiles it with (<see cref="SourceParsing"/>); none from
    /// another frame, or from code without symbols.</summary>
    public List<StepInTargetInfo> StepInTargets(int frameId, IReadOnlyList<string>? preprocessorSymbols = null, string? languageVersion = null)
    {
        _stepInTargets.Clear();
        if (_frameReferenceManager.GetFrameInfoById(frameId) is not var (threadId, depth) || depth.Value != 0)
        {
            return [];
        }
        RequireStop();
        if (GetFrameForThreadIdAndStackDepth(threadId, depth) is not ICorDebugILFrame frame || RangeOf(frame) is not { } range
            || FindModule(frame.Function.Module) is not { MetadataReader.HasSymbols: true } module)
        {
            return [];
        }
        int token = frame.Function.Token;
        (int ip, CorDebugMappingResult mapping) = frame.IP;
        int? version = ModuleMetadataReader.VersionOf(frame);
        IReadOnlyList<ModuleMetadataReader.Point> points = module.MetadataReader.VisibleSequencePoints(token, version);
        int at = -1;
        for (int i = 0; i < points.Count && points[i].Offset <= ip; i++)
        {
            at = i;
        }
        if (at < 0)
        {
            return [];
        }
        // The statement and any after it on its line, up to the next line's.
        int last = at;
        while (last + 1 < points.Count && points[last + 1].StartLine == points[at].StartLine)
        {
            last++;
        }
        int start = points[at].Offset;
        int end = last + 1 < points.Count ? points[last + 1].Offset : frame.Function.ILCode.Size;
        // A call before the stop has run already (the one a step out returned from, mid-statement): left out.
        List<ModuleMetadataReader.CallSite> calls = [.. module.MetadataReader.CallSites(token, start, end, version)
            .Where(call => call.Offset > ip || (call.Offset == ip && mapping.HasFlag(CorDebugMappingResult.MAPPING_EXACT)))];

        _logger?.Invoke($"Step-in calls of IL_{start:X4}..IL_{end:X4}, lines {points[at].StartLine}-{points[last].EndLine}: {calls.Count}");
        string? path = ModuleMetadataReader.DocumentOf(points[at]);
        SourceText? text = path is not null && File.Exists(path) ? SourceText.From(File.ReadAllText(path)) : null;
        List<(StepInPlan Plan, string Label, LinePositionSpan? Place)> found = text is null
            ? FromCalls(module, calls, threadId, range, start, end)
            : FromSource(module, calls, text, SourceParsing(preprocessorSymbols, languageVersion), points[at], points[last], threadId, range, start, end);

        List<StepInTargetInfo> targets = [];
        foreach ((StepInPlan plan, string label, LinePositionSpan? place) in found)
        {
            int id = _nextStepInTarget++;
            _stepInTargets[id] = plan;
            targets.Add(new StepInTargetInfo(id, label, place?.Start.Line + 1, place?.Start.Character + 1, place?.End.Line + 1, place?.End.Character + 1));
        }
        _logger?.Invoke($"Step-in targets at IL_{ip:X4}: {string.Join(", ", targets.Select(t => t.Label))}");
        return targets;
    }

    /// <summary>What a frame's source is parsed with: the symbols and C# version the client says its project compiles it
    /// with; the compiler's defaults when it says neither, a version this parser does not know its preview.</summary>
    private static CSharpParseOptions SourceParsing(IReadOnlyList<string>? symbols, string? version) =>
        symbols is null && version is null ? CSharpParseOptions.Default
        : new CSharpParseOptions(version is not null && LanguageVersionFacts.TryParse(version, out LanguageVersion known) ? known : LanguageVersion.Preview,
            DocumentationMode.None, preprocessorSymbols: symbols);

    /// <summary>The statement's calls matched to its source's invocations, object creations and lambdas, in the order they
    /// run: each invocation by its name to the next call of that name in the IL, a lambda to the delegate made of the
    /// method its body is, which runs once what it is handed to is called, so it comes after that call.</summary>
    private List<(StepInPlan, string, LinePositionSpan?)> FromSource(ModuleInfo module, List<ModuleMetadataReader.CallSite> calls, SourceText text,
        CSharpParseOptions parsing, ModuleMetadataReader.Point first, ModuleMetadataReader.Point last, ThreadId threadId, StackRange range, int start, int end)
    {
        int Position(int line, int column) => line < 1 || line > text.Lines.Count ? 0 : Math.Min(text.Lines[line - 1].Start + column - 1, text.Lines[line - 1].End);
        TextSpan statement = TextSpan.FromBounds(Position(first.StartLine, first.StartColumn), Math.Max(Position(first.StartLine, first.StartColumn), Position(last.EndLine, last.EndColumn)));
        List<SyntaxNode> nodes = [];
        Dictionary<SyntaxNode, List<SyntaxNode>> handedLambdas = [];
        Visit(CSharpSyntaxTree.ParseText(text, parsing).GetRoot());

        List<(StepInPlan, string, LinePositionSpan?)> targets = [];
        HashSet<int> usedDelegates = [];
        int next = 0;
        foreach (SyntaxNode node in nodes)
        {
            if (node is AnonymousFunctionExpressionSyntax lambda)
            {
                if (calls.FirstOrDefault(call => call.Kind == ModuleMetadataReader.CallKind.Delegate && !usedDelegates.Contains(call.Offset)
                        && BodyIsWithin(module, call.Token, lambda.Span, text)) is { Token: not 0 } made
                    && Plan(module, made.Token, EntryKind.Delegate, made.Offset, threadId, range, start, end) is { } lambdaPlan)
                {
                    usedDelegates.Add(made.Offset);
                    targets.Add((lambdaPlan, Shortened(lambda.ToString()), text.Lines.GetLinePositionSpan(LambdaHead(lambda))));
                }
                continue;
            }
            (string name, string? type, TextSpan place) = node switch
            {
                InvocationExpressionSyntax invocation => InvokedName(invocation) is { } invoked ? (invoked.Text, (string?)null, invoked.Span) : ("", null, default),
                ObjectCreationExpressionSyntax creation => (".ctor", RightmostName(creation.Type), TextSpan.FromBounds(creation.NewKeyword.SpanStart, creation.Type.Span.End)),
                ImplicitObjectCreationExpressionSyntax creation => (".ctor", null, creation.NewKeyword.Span),
                _ => ("", null, default),
            };
            if (name.Length == 0)
            {
                continue;
            }
            for (int k = next; k < calls.Count; k++)
            {
                ModuleMetadataReader.CallSite call = calls[k];
                if (call.Kind == ModuleMetadataReader.CallKind.Delegate || module.MetadataReader.DescribeCall(call.Token) is not { } called
                    || !Names(called, name, type, call.Kind == ModuleMetadataReader.CallKind.NewObject))
                {
                    continue;
                }
                next = k + 1;
                if (Plan(module, call.Token, EntryKind.Call, call.Offset, threadId, range, start, end) is { } plan)
                {
                    targets.Add((plan, Label(called), text.Lines.GetLinePositionSpan(place)));
                }
                break;
            }
        }
        return targets;

        // The calls as emitted: post-order, a lambda after the call it is handed to, a conditional's alternative first.
        void Visit(SyntaxNode node)
        {
            foreach (SyntaxNode child in node is ConditionalExpressionSyntax conditional ? [conditional.Condition, conditional.WhenFalse, conditional.WhenTrue] : node.ChildNodes())
            {
                if (!child.Span.OverlapsWith(statement))
                {
                    continue;
                }
                if (child is AnonymousFunctionExpressionSyntax lambda)
                {
                    if (statement.Contains(lambda.Span))
                    {
                        if (lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: { } receiver } } && statement.Contains(receiver.Span)
                            && receiver is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
                        {
                            (handedLambdas.TryGetValue(receiver, out List<SyntaxNode>? handed) ? handed : handedLambdas[receiver] = []).Add(lambda);
                        }
                        else
                        {
                            nodes.Add(lambda);
                        }
                    }
                    continue;
                }
                if (child is LocalFunctionStatementSyntax)
                {
                    continue;
                }
                Visit(child);
                if (statement.Contains(child.Span) && child is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
                {
                    nodes.Add(child);
                    if (handedLambdas.Remove(child, out List<SyntaxNode>? handed))
                    {
                        nodes.AddRange(handed);
                    }
                }
            }
        }
    }

    /// <summary>The statement's calls with no source to place them: the IL's own calls of the user's methods, labelled by
    /// their names.</summary>
    private List<(StepInPlan, string, LinePositionSpan?)> FromCalls(ModuleInfo module, List<ModuleMetadataReader.CallSite> calls, ThreadId threadId, StackRange range, int start, int end)
    {
        List<(StepInPlan, string, LinePositionSpan?)> targets = [];
        foreach (ModuleMetadataReader.CallSite call in calls.Where(call => call.Kind != ModuleMetadataReader.CallKind.Delegate))
        {
            if (module.MetadataReader.DescribeCall(call.Token) is not { } called || IsCompilersOwn(called))
            {
                continue;
            }
            if (Plan(module, call.Token, EntryKind.Call, call.Offset, threadId, range, start, end) is { } plan)
            {
                targets.Add((plan, Label(called), null));
            }
        }
        return targets;
    }

    /// <summary>Where a step into a call's method first stops, when it is one a step can stop in: the user's (Just My Code),
    /// with lines, an async or iterator method's state machine's first line rather than its own.</summary>
    private StepInPlan? Plan(ModuleInfo module, int token, EntryKind kind, int callOffset, ThreadId threadId, StackRange range, int start, int end)
    {
        if (ResolveCalledMethod(module, token) is not var (target, method))
        {
            return null;
        }
        ModuleMetadataReader reader = target.MetadataReader;
        if (!reader.HasSymbols)
        {
            return null;
        }
        // An async method's own body is the compiler's, marked [DebuggerStepThrough]: its state machine's is the user's.
        int entered = reader.GetStateMachineMoveNextToken(method) ?? method;
        if (reader.IsHidden(entered) || (_justMyCode && (!target.IsUserCode || reader.IsNonUserCode(entered))))
        {
            return null;
        }
        return reader.VisibleSequencePoints(entered) is [var first, ..]
            ? new StepInPlan(target, entered, first.Offset, kind, callOffset, threadId.Value, range, start, end)
            : null;
    }

    /// <summary>Whether a delegate's method (a lambda's) has its first line inside a span of the statement's source.</summary>
    private bool BodyIsWithin(ModuleInfo module, int token, TextSpan span, SourceText text)
    {
        if (ResolveCalledMethod(module, token) is not var (target, method) || target.MetadataReader.VisibleSequencePoints(method) is not [var first, ..]
            || first.StartLine < 1 || first.StartLine > text.Lines.Count)
        {
            return false;
        }
        int position = text.Lines[first.StartLine - 1].Start + first.StartColumn - 1;
        return span.Contains(position);
    }

    /// <summary>Whether a call names the method an invocation or object creation of the source names: by its name, a local
    /// function by the name it was written with, a constructor by its type.</summary>
    private static bool Names(ModuleMetadataReader.CalledMethod called, string name, string? type, bool creation)
    {
        if (name == ".ctor")
        {
            return creation && called.Name == ".ctor" && (type is null || CSharpTypeNames.WithoutTypeArguments(called.DeclaringType) == type);
        }
        return !creation && (called.Name == name || called.Name.Contains($">g__{name}|", StringComparison.Ordinal));
    }

    /// <summary>A call's label: its name and its parameters' types, a constructor's as new and its type.</summary>
    private static string Label(ModuleMetadataReader.CalledMethod called)
    {
        string parameters = string.Join(", ", called.Parameters);
        if (called.Name == ".ctor")
        {
            return $"new {CSharpTypeNames.WithoutTypeArguments(called.DeclaringType)}({parameters})";
        }
        string name = called.Name;
        int local = name.IndexOf(">g__", StringComparison.Ordinal);
        if (local >= 0 && name.IndexOf('|', local) is int bar and > 0)
        {
            name = name[(local + 4)..bar];
        }
        return $"{name}({parameters})";
    }

    /// <summary>A lambda's source on one line, cut short when long.</summary>
    private static string Shortened(string source)
    {
        string flat = Regex.Replace(source, @"\s+", " ").Trim();
        return flat.Length <= 60 ? flat : flat[..59] + "…";
    }

    /// <summary>Where a lambda is shown as a target: its parameters and arrow ("n =>"), or "delegate".</summary>
    private static TextSpan LambdaHead(AnonymousFunctionExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => TextSpan.FromBounds(simple.SpanStart, simple.ArrowToken.Span.End),
        ParenthesizedLambdaExpressionSyntax parenthesized => TextSpan.FromBounds(parenthesized.SpanStart, parenthesized.ArrowToken.Span.End),
        AnonymousMethodExpressionSyntax anonymous => anonymous.DelegateKeyword.Span,
        _ => lambda.Span,
    };

    /// <summary>The name an invocation calls, and where it is written; none for a delegate invoked through an expression.</summary>
    private static (string Text, TextSpan Span)? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => (identifier.Identifier.ValueText, identifier.Identifier.Span),
        GenericNameSyntax generic => (generic.Identifier.ValueText, generic.Identifier.Span),
        MemberAccessExpressionSyntax access => (access.Name.Identifier.ValueText, access.Name.Identifier.Span),
        MemberBindingExpressionSyntax binding => (binding.Name.Identifier.ValueText, binding.Name.Identifier.Span),
        _ => null,
    };

    private static string? RightmostName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The method definition a call's token names, in whichever loaded module defines it: one of its own module, a
    /// generic method's instantiation, or a reference to another assembly's, matched by its type's name and its signature.</summary>
    private (ModuleInfo Module, int Token)? ResolveCalledMethod(ModuleInfo module, int token)
    {
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            return (module, MetadataTokens.GetToken(handle));
        }
        if (handle.Kind != HandleKind.MemberReference)
        {
            return null;
        }
        MemberReference member = reader.GetMemberReference((MemberReferenceHandle)handle);
        if (member.GetKind() != MemberReferenceKind.Method)
        {
            return null;
        }
        EntityHandle parent = member.Parent;
        if (parent.Kind == HandleKind.TypeSpecification && GenericTypeOf(reader, (TypeSpecificationHandle)parent) is { } generic)
        {
            parent = generic;
        }
        (ModuleInfo Module, TypeDefinitionHandle Type)? declaring = parent.Kind switch
        {
            HandleKind.TypeDefinition => (module, (TypeDefinitionHandle)parent),
            HandleKind.TypeReference => ResolveTypeReference(module, (TypeReferenceHandle)parent),
            _ => null,
        };
        if (declaring is not var (owner, type))
        {
            return null;
        }
        string name = reader.GetString(member.Name);
        string signature = SignatureKeyProvider.Of(member.DecodeMethodSignature(SignatureKeyProvider.Instance, null));
        MetadataReader ownerReader = owner.MetadataReader.PeMetadataReader;
        foreach (MethodDefinitionHandle candidate in ownerReader.GetTypeDefinition(type).GetMethods())
        {
            MethodDefinition definition = ownerReader.GetMethodDefinition(candidate);
            if (ownerReader.GetString(definition.Name) == name && SignatureKeyProvider.Of(definition.DecodeSignature(SignatureKeyProvider.Instance, null)) == signature)
            {
                return (owner, MetadataTokens.GetToken(candidate));
            }
        }
        return null;
    }

    /// <summary>The generic type a type specification instantiates (List`1 of List&lt;int&gt;).</summary>
    private static EntityHandle? GenericTypeOf(MetadataReader reader, TypeSpecificationHandle handle)
    {
        BlobReader blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return null;
        }
        blob.ReadCompressedInteger();
        return blob.ReadTypeHandle();
    }

    /// <summary>The type a reference names, in the loaded module of the assembly it names (or its own, or the type it is
    /// nested in).</summary>
    private (ModuleInfo Module, TypeDefinitionHandle Type)? ResolveTypeReference(ModuleInfo module, TypeReferenceHandle handle)
    {
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        string @namespace = reader.GetString(reference.Namespace);
        switch (reference.ResolutionScope.Kind)
        {
            case HandleKind.TypeReference when ResolveTypeReference(module, (TypeReferenceHandle)reference.ResolutionScope) is var (owner, outer):
            {
                MetadataReader ownerReader = owner.MetadataReader.PeMetadataReader;
                foreach (TypeDefinitionHandle nested in ownerReader.GetTypeDefinition(outer).GetNestedTypes())
                {
                    if (ownerReader.GetString(ownerReader.GetTypeDefinition(nested).Name) == name)
                    {
                        return (owner, nested);
                    }
                }
                return null;
            }
            case HandleKind.AssemblyReference:
            {
                string assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                foreach (ModuleInfo candidate in _modules.Values.Where(m => m.MetadataReader.PeMetadataReader is { IsAssembly: true } r && r.GetString(r.GetAssemblyDefinition().Name) == assembly))
                {
                    if (TopLevelType(candidate, @namespace, name) is { } found)
                    {
                        return (candidate, found);
                    }
                }
                return null;
            }
            case HandleKind.ModuleDefinition:
                return TopLevelType(module, @namespace, name) is { } own ? (module, own) : null;
            default:
                return null;
        }

        static TypeDefinitionHandle? TopLevelType(ModuleInfo module, string @namespace, string name)
        {
            MetadataReader reader = module.MetadataReader.PeMetadataReader;
            foreach (TypeDefinitionHandle type in reader.TypeDefinitions)
            {
                TypeDefinition definition = reader.GetTypeDefinition(type);
                if (definition.GetDeclaringType().IsNil && reader.GetString(definition.Name) == name && reader.GetString(definition.Namespace) == @namespace)
                {
                    return type;
                }
            }
            return null;
        }
    }

    /// <summary>Takes a Smart Step Into: a step over the statement the targets were listed for, with a breakpoint at the
    /// chosen method's first line. Refused, saying why, when the program has gone on since, or the thread cannot step.</summary>
    public async Task StepIntoTarget(int threadId, int targetId)
    {
        _logger?.Invoke($"Smart Step Into target {targetId} on thread {threadId}");
        ArgumentNullException.ThrowIfNull(_process);
        if (!_stepInTargets.TryGetValue(targetId, out StepInPlan? plan) || plan.ThreadId != threadId)
        {
            throw new InvalidOperationException("That call is not one the program can step into from where it is now");
        }
        if (!_threads.TryGetValue(threadId, out ThreadInfo? threadInfo))
        {
            throw new InvalidOperationException($"The program has no thread {threadId} to step: it has ended");
        }
        if (_process.TryIsRunning(out bool running) is Cor.S_OK && running)
        {
            throw new InvalidOperationException("The program is running, so it cannot step");
        }
        if (_stepper is not null || _stepAfterEvaluation is not null || EvalStatus.Abandoned.ContainsKey(threadId))
        {
            throw new InvalidOperationException("A step operation is already in progress");
        }
        ICorDebugThread thread = threadInfo.Thread;
        if (EnumerateFramesForThread(thread).FirstOrDefault() is not ICorDebugILFrame frame || RangeOf(frame) != plan.Frame)
        {
            throw new InvalidOperationException("The thread is no longer in the frame the call was listed for");
        }
        ForgetReturnedValues();
        ICorDebugFunctionBreakpoint breakpoint = plan.Module.Module.GetFunctionFromToken(plan.MethodToken).ILCode.CreateBreakpoint(plan.EntryOffset);
        breakpoint.Activate(true);
        _smartStep = new SmartStep(plan, breakpoint);
        if (_asyncStepper is not null)
        {
            // An await on the statement is stepped over as Step Over steps over it.
            (bool handled, bool? simple) = await _asyncStepper.TrySetupAsyncStep(thread, StepType.StepOver);
            if (handled && simple is false)
            {
                Continue(clearVariables: true);
                return;
            }
        }
        SetupStepper(thread, StepType.StepOver, through: (plan.Start, plan.End));
        Continue(clearVariables: true);
    }

    /// <summary>Whether a hit of a Smart Step Into's breakpoint is its target entered from the statement: on the thread
    /// that steps, with the statement's frame below; a call's target from the chosen call itself, the statement's frame in
    /// that call's instruction, with no frame of the user's between (an async method's start is not), so another call of
    /// the same method, from the statement or from another method it called, is passed.</summary>
    private bool EnteredFromStatement(ICorDebugThread thread, StepInPlan plan)
    {
        if (thread.Id != plan.ThreadId)
        {
            return false;
        }
        bool first = true;
        foreach (ICorDebugFrame frame in EnumerateFramesForThread(thread))
        {
            if (first)
            {
                first = false;
                continue;
            }
            if (RangeOf(frame) == plan.Frame)
            {
                return plan.Kind != EntryKind.Call || InCallAt(frame, plan.CallOffset);
            }
            if (plan.Kind == EntryKind.Call && frame is ICorDebugILFrame between && FindModule(between.Function.Module) is { IsUserCode: true } module
                && module.MetadataReader.VisibleSequencePoints(between.Function.Token).Count > 0)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>A hit of the Smart Step Into's breakpoint: the stop, in the target, once it is entered from the chosen
    /// call; else the program goes on.</summary>
    private void TakeSmartStepEntry(ICorDebugThread thread, SmartStep smart)
    {
        if (!EnteredFromStatement(thread, smart.Plan))
        {
            Continue(clearVariables: true);
            return;
        }
        EndStepForStop();
        if (GetSourceInfoAtFrame(thread.ActiveFrame, _justMyCode is false) is { } source)
        {
            ReportStop(thread.Id, StopReasons.Step, source.FilePath, source.StartLine, source.StartColumn);
        }
        else
        {
            ReportStop(thread.Id, StopReasons.Step);
        }
    }

    /// <summary>Whether a caller's frame is in the call at an IL offset. A caller's IP names the IL its call's instruction
    /// is compiled in, from the IL after the call before it: the call is the first one from there.</summary>
    private bool InCallAt(ICorDebugFrame frame, int callOffset)
    {
        if (frame is not ICorDebugILFrame caller || FindModule(caller.Function.Module) is not { } module)
        {
            return false;
        }
        int ip = caller.IP.pnOffset;
        return module.MetadataReader.CallSites(caller.Function.Token, ip, callOffset + 1, ModuleMetadataReader.VersionOf(caller))
            .FirstOrDefault(call => call.Kind != ModuleMetadataReader.CallKind.Delegate) is { } first && first.Offset == callOffset;
    }

    private void EndSmartStep()
    {
        if (_smartStep is { } smart)
        {
            _smartStep = null;
            smart.Breakpoint.TryActivate(false);
        }
    }
}
