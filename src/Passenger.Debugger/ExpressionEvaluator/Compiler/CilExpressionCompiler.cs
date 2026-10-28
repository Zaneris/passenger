using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.ExpressionEvaluator;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Compiler;

internal sealed class CompiledEvaluationMethod : IDisposable
{
    private readonly MemoryStream _peStream;
    private readonly Dictionary<MethodDefinitionHandle, DecodedMethod> _decodedMethods = new();

    public CompiledEvaluationMethod(byte[] assembly, string typeName, string methodName)
    {
        Assembly = assembly;
        TypeName = typeName;
        MethodName = methodName;
        _peStream = new MemoryStream(assembly, writable: false);
        PeReader = new System.Reflection.PortableExecutable.PEReader(_peStream);
        MetadataReader = PeReader.GetMetadataReader();
        EntryMethod = FindEntryMethod(MetadataReader, typeName, methodName);
    }

    public byte[] Assembly { get; }
    public string TypeName { get; }
    public string MethodName { get; }
    public System.Reflection.PortableExecutable.PEReader PeReader { get; }
    public MetadataReader MetadataReader { get; }
    public MethodDefinitionHandle EntryMethod { get; }
    public Guid ModuleVersionId => MetadataReader.GetGuid(MetadataReader.GetModuleDefinition().Mvid);

    /// <summary>
    /// Returns the decoded CIL instructions and branch-target offsets map for a method in the evaluation assembly,
    /// decoding at most once per method body since the assembly is immutable for the lifetime of this object.
    /// </summary>
    public DecodedMethod GetDecodedMethod(MethodDefinitionHandle handle)
    {
        if (_decodedMethods.TryGetValue(handle, out DecodedMethod decoded))
        {
            return decoded;
        }

        MethodDefinition method = MetadataReader.GetMethodDefinition(handle);
        MethodBodyBlock body = PeReader.GetMethodBody(method.RelativeVirtualAddress);
        byte[]? ilBytes = body.GetILBytes();
        ArgumentNullException.ThrowIfNull(ilBytes);
        IReadOnlyList<CilInstruction> instructions = CilInstructionDecoder.Decode(ilBytes);
        Dictionary<int, int> offsets = instructions.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(x => x.Offset, x => x.index);
        decoded = new DecodedMethod(instructions, offsets);
        _decodedMethods[handle] = decoded;
        return decoded;
    }

    public void Dispose()
    {
        PeReader.Dispose();
        _peStream.Dispose();
    }

    private static MethodDefinitionHandle FindEntryMethod(MetadataReader reader, string typeName, string methodName)
    {
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != typeName)
            {
                continue;
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                if (reader.GetString(reader.GetMethodDefinition(methodHandle).Name) == methodName)
                {
                    return methodHandle;
                }
            }
        }

        throw new InvalidOperationException($"Generated evaluation method '{typeName}.{methodName}' was not found");
    }
}
internal sealed class CilExpressionCompiler(ManagedDebugger debugger)
{
    private static readonly PortableExecutableReference _intrinsicMethodsReference = MetadataReference.CreateFromImage(CreateIntrinsicMethodsAssembly());

    private const int CacheCapacity = 256;
    private readonly record struct CompileCacheKey(int ModuleVersion, CompileContextKind ContextKind, CORDB_ADDRESS Module, int Token, int IlOffset, string Expression, string? Exception, string ReturnValues);
    private enum CompileContextKind { Method, Type }
    private readonly Dictionary<CompileCacheKey, CacheEntry> _compileCache = new();
    private readonly LinkedList<CompileCacheKey> _compileLru = new();
    private readonly record struct CacheEntry(CompiledEvaluationMethod Value, LinkedListNode<CompileCacheKey> Node);

    private readonly record struct MetadataBlocksKey(int ModuleVersion, CORDB_ADDRESS PreferredModule);
    private readonly Dictionary<MetadataBlocksKey, ImmutableArray<MetadataBlock>> _metadataBlocksCache = new();
    private DelegateMaterializerAssembly? _delegateMaterializer;
    private int _cachedModuleVersion = -1;

    public CompiledEvaluationMethod? TryCompile(string expression, CompiledExpressionEvaluationContext context, out string? errorMessage) =>
        TryCompile(expression, context, out errorMessage, out _);

    /// <summary>Compiles an expression in the frame or type context, or says why not and which assemblies it needs that
    /// the program has not loaded (System.Linq, for a LINQ method on a list where the program has used none yet), to be
    /// loaded and the expression compiled again.</summary>
    public CompiledEvaluationMethod? TryCompile(string expression, CompiledExpressionEvaluationContext context, out string? errorMessage, out ImmutableArray<string> missingAssemblies)
    {
        errorMessage = null;
        missingAssemblies = [];
        if (context.RootValue is not null)
        {
            ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression);
            expression = new RemoveFormatSpecifierAlignmentRewriter().Visit(syntax)!.ToFullString();
        }
        ICorDebugILFrame frame = debugger.GetIlFrameForThreadIdAndStackDepth(context.ThreadId, context.StackDepth);
        ICorDebugModule preferredModule = context.RootValue is not null ? context.RootValue.ExactType.Class.Module : frame.Function.Module;
        // The exception's type in the program's assemblies, where the compiler looks, not the debugger's runtime's.
        string? exception = debugger.GetCurrentException(context.ThreadId) is { } thrown ? debugger.AliasTypeName(thrown.ExactType) : null;
        IReadOnlyList<(string Name, string Type)> returnValues = context.RootValue is null ? debugger.ReturnValueAliases(context.ThreadId) : [];
        EnsureModuleVersion();
        CompileCacheKey key = CreateCacheKey(expression, context, frame, preferredModule, exception, string.Join(';', returnValues.Select(r => r.Type)));
        if (TryGetCachedCompiled(key, out CompiledEvaluationMethod? cached))
        {
            return cached;
        }

        ImmutableArray<MetadataBlock> blocks = GetMetadataBlocks(preferredModule);
        EvaluationContext evaluationContext = context.RootValue is null
            ? CreateMethodContext(blocks, frame)
            : CreateTypeContext(blocks, context.RootValue);

        DiagnosticBag diagnostics = DiagnosticBag.GetInstance();
        try
        {
            ImmutableArray<Alias> aliases = GetAliases(exception, returnValues);
            string scoped = ScopeDeclaredVariables(expression);
            CompileResult? result = evaluationContext.CompileExpression(
                scoped,
                DkmEvaluationFlags.TreatAsExpression,
                aliases,
                diagnostics,
                out _,
                testData: null);
            if ((result is null || diagnostics.HasAnyErrors()) && scoped != expression)
            {
                // The expression as written, whose errors (or success) name what was written.
                diagnostics.Clear();
                result = evaluationContext.CompileExpression(expression, DkmEvaluationFlags.TreatAsExpression, aliases, diagnostics, out _, testData: null);
            }

            if (result is null || diagnostics.HasAnyErrors())
            {
                errorMessage = string.Join("; ", diagnostics.AsEnumerable()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.GetMessage()));
                evaluationContext.GetErrorMessageAndMissingAssemblyIdentities(diagnostics, DebuggerDiagnosticFormatter.Instance, preferredUICulture: null,
                    EvaluationContextBase.SystemLinqIdentity, out _, out ImmutableArray<AssemblyIdentity> missing);
                missingAssemblies = [.. missing.Select(identity => identity.Name)];
                return null;
            }

            return CacheCompiled(key, new CompiledEvaluationMethod(result.Assembly, result.TypeName, result.MethodName));
        }
        finally
        {
            diagnostics.Free();
        }
    }

    private CompileCacheKey CreateCacheKey(string expression, CompiledExpressionEvaluationContext context, ICorDebugILFrame frame, ICorDebugModule preferredModule, string? exception, string returnValues)
    {
        if (context.RootValue is not null)
        {
            return new CompileCacheKey(
                debugger.ModuleSet_Version,
                CompileContextKind.Type,
                preferredModule.BaseAddress,
                context.RootValue.ExactType.Class.Token,
                0,
                expression,
                exception,
                returnValues);
        }
        return new CompileCacheKey(
            debugger.ModuleSet_Version,
            CompileContextKind.Method,
            preferredModule.BaseAddress,
            frame.Function.Token,
            EvaluationContextBase.NormalizeILOffset((uint)frame.IP.pnOffset),
            expression,
            exception,
            returnValues);
    }

    private bool TryGetCachedCompiled(CompileCacheKey key, out CompiledEvaluationMethod result)
    {
        if (_compileCache.TryGetValue(key, out CacheEntry entry))
        {
            _compileLru.Remove(entry.Node);
            _compileLru.AddFirst(entry.Node);
            result = entry.Value;
            return true;
        }
        result = null!;
        return false;
    }

    private CompiledEvaluationMethod CacheCompiled(CompileCacheKey key, CompiledEvaluationMethod compiled)
    {
        if (_compileCache.Count >= CacheCapacity)
        {
            LinkedListNode<CompileCacheKey> evictedKey = _compileLru.Last!;
            _compileLru.RemoveLast();
            if (_compileCache.Remove(evictedKey.Value, out CacheEntry evicted))
            {
                evicted.Value.Dispose();
            }
        }
        _compileCache[key] = new CacheEntry(compiled, _compileLru.AddFirst(key));
        return compiled;
    }

    private void EnsureModuleVersion()
    {
        if (debugger.ModuleSet_Version == _cachedModuleVersion)
        {
            return;
        }

        _cachedModuleVersion = debugger.ModuleSet_Version;
        foreach (CacheEntry entry in _compileCache.Values)
        {
            entry.Value.Dispose();
        }

        _compileCache.Clear();
        _compileLru.Clear();
        _metadataBlocksCache.Clear();
    }

    /// <summary>The debugger's own names an expression may use: $exception while the thread has one, and the values the
    /// last step's calls returned, each typed as it is.</summary>
    private static ImmutableArray<Alias> GetAliases(string? exception, IReadOnlyList<(string Name, string Type)> returnValues) =>
    [
        .. exception is not null ? [new Alias(DkmClrAliasKind.Exception, "Error", "$exception", exception, Guid.Empty, null!)] : ImmutableArray<Alias>.Empty,
        .. returnValues.Select(returned => new Alias(DkmClrAliasKind.ReturnValue, returned.Name, returned.Name, returned.Type, Guid.Empty, null!)),
    ];

    internal DelegateMaterializerAssembly GetDelegateMaterializer(CompiledExpressionEvaluationContext context)
    {
        if (_delegateMaterializer is { } cached)
        {
            return cached;
        }

        ICorDebugILFrame frame = debugger.GetIlFrameForThreadIdAndStackDepth(context.ThreadId, context.StackDepth);
        ICorDebugModule preferredModule = context.RootValue is not null ? context.RootValue.ExactType.Class.Module : frame.Function.Module;
        CSharpCompilation referenceCompilation = GetMetadataBlocks(preferredModule)
            .ToCompilation(moduleId: default, MakeAssemblyReferencesKind.AllAssemblies);
        return _delegateMaterializer = DelegateMaterializerCompiler.Compile(referenceCompilation.References);
    }

    /// <summary>The metadata blocks Roslyn's evaluator binds against, one module per assembly identity (one assembly
    /// may be loaded into several load contexts): the module the evaluation binds against (the frame's, or the root
    /// value's for DebuggerDisplay) first, since the tokens emitted must match the instance being debugged.</summary>
    private ImmutableArray<MetadataBlock> GetMetadataBlocks(ICorDebugModule preferredModule)
    {
        MetadataBlocksKey key = new MetadataBlocksKey(debugger.ModuleSet_Version, preferredModule.BaseAddress);
        if (_metadataBlocksCache.TryGetValue(key, out ImmutableArray<MetadataBlock> cached))
        {
            return cached;
        }

        ImmutableArray<MetadataBlock> blocks = BuildMetadataBlocks(preferredModule);
        _metadataBlocksCache[key] = blocks;
        return blocks;
    }

    private ImmutableArray<MetadataBlock> BuildMetadataBlocks(ICorDebugModule preferredModule)
    {
        List<ModuleInfo> modules = GetMetadataModulesPreferringModule(preferredModule);
        ImmutableArray<MetadataBlock>.Builder builder = ImmutableArray.CreateBuilder<MetadataBlock>(modules.Count);
        foreach (ModuleInfo moduleInfo in modules)
        {
            nint pointer;
            int size;
            Guid generationId;
            if (moduleInfo.CurrentMetadata is { } updated)
            {
                // Hot Reload changed the module: its metadata as the updates made it, each update a generation of its own.
                pointer = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(updated, 0);
                size = updated.Length;
                generationId = new Guid([.. BitConverter.GetBytes(moduleInfo.MetadataReader.Generations), .. new byte[12]]);
            }
            else
            {
                IMetaDataTables2 tables = moduleInfo.Module.GetMetaDataInterface<IMetaDataTables2>();
                (pointer, size) = tables.MetaDataStorage;
                MetadataReader reader = moduleInfo.MetadataReader.PeMetadataReader;
                ModuleDefinition module = reader.GetModuleDefinition();
                generationId = module.GenerationId.IsNil ? Guid.Empty : reader.GetGuid(module.GenerationId);
            }
            builder.Add(new MetadataBlock(
                new ModuleId(moduleInfo.MetadataReader.Mvid, moduleInfo.ModuleName),
                generationId,
                pointer,
                checked((int)size)));
        }
        return builder.ToImmutable();
    }

    private List<ModuleInfo> GetMetadataModulesPreferringModule(ICorDebugModule preferredModule)
    {
        Dictionary<string, List<ModuleInfo>> modulesByKey = new Dictionary<string, List<ModuleInfo>>(StringComparer.Ordinal);
        // An emitted module's metadata is the runtime's own, moved as types are emitted into it, so no compilation holds it.
        foreach (ModuleInfo moduleInfo in debugger.AllModules.Where(module => !module.IsDynamic))
        {
            string key = GetAssemblyIdentityKey(moduleInfo);
            if (!modulesByKey.TryGetValue(key, out List<ModuleInfo>? list))
            {
                modulesByKey[key] = list = [];
            }

            list.Add(moduleInfo);
        }

        List<ModuleInfo> result = new List<ModuleInfo>(modulesByKey.Count);
        foreach (List<ModuleInfo> group in modulesByKey.Values)
        {
            result.Add(group.FirstOrDefault(module => module.Module == preferredModule) ?? group[0]);
        }
        return result;
    }

    private static string GetAssemblyIdentityKey(ModuleInfo moduleInfo)
    {
        MetadataReader reader = moduleInfo.MetadataReader.PeMetadataReader;
        if (!reader.IsAssembly)
        {
            return $"{moduleInfo.ModuleName}|{moduleInfo.MetadataReader.Mvid}";
        }

        AssemblyDefinition assembly = reader.GetAssemblyDefinition();
        string culture = reader.GetString(assembly.Culture);
        string publicKey = assembly.PublicKey.IsNil ? string.Empty : ToHexString(reader.GetBlobBytes(assembly.PublicKey));
        return $"{reader.GetString(assembly.Name)}|{assembly.Version}|{culture}|{publicKey}";
    }

    private static string ToHexString(byte[] bytes) => Convert.ToHexString(bytes);

    private EvaluationContext CreateMethodContext(ImmutableArray<MetadataBlock> blocks, ICorDebugILFrame frame)
    {
        ModuleInfo moduleInfo = debugger.GetModuleInfoForModule(frame.Function.Module);
        ModuleId moduleId = new ModuleId(moduleInfo.MetadataReader.Mvid, moduleInfo.ModuleName);
        CSharpCompilation compilation = AddRuntimeAccessAttributes(blocks.ToCompilation(moduleId: default, MakeAssemblyReferencesKind.AllAssemblies).AddReferences(_intrinsicMethodsReference));
        mdMethodDef methodToken = frame.Function.Token;
        MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)MetadataTokens.Handle(methodToken);
        PEMethodSymbol currentSourceMethod = compilation.GetSourceMethod(moduleId, methodHandle);
        PEMethodSymbol currentFrame = compilation.GetMethod(moduleId, methodHandle);
        CSharpEESymbolProvider symbolProvider = new CSharpEESymbolProvider(compilation.SourceAssembly, (PEModuleSymbol)currentFrame.ContainingModule, currentFrame);
        MetadataDecoder metadataDecoder = new MetadataDecoder((PEModuleSymbol)currentFrame.ContainingModule, currentFrame);
        StandaloneSignatureHandle localSignature = frame.Function.LocalVarSigToken == 0
            ? default
            : (StandaloneSignatureHandle)MetadataTokens.Handle(frame.Function.LocalVarSigToken);
        ImmutableArray<LocalInfo<TypeSymbol>> localInfo = metadataDecoder.GetLocalInfo(localSignature);
        int ilOffset = EvaluationContextBase.NormalizeILOffset((uint)frame.IP.pnOffset);
        // The frame's version of the method, read from the PDB delta of the update that made it, as Roslyn's reader reads one.
        int version = ModuleMetadataReader.VersionOf(frame) ?? 1;
        MetadataReader? pdbReader = moduleInfo.MetadataReader.DebugOf(methodToken, version)?.Reader;
        MethodDebugInfo<TypeSymbol, LocalSymbol> debugInfo = pdbReader is null
            ? MethodDebugInfo<TypeSymbol, LocalSymbol>.None
            : MethodDebugInfo<TypeSymbol, LocalSymbol>.ReadFromPortable(pdbReader, methodToken, ilOffset, symbolProvider, isVisualBasicMethod: false);
        ILSpan reuseSpan = debugInfo.ReuseSpan;
        ArrayBuilder<LocalSymbol> locals = ArrayBuilder<LocalSymbol>.GetInstance();
        MethodDebugInfo<TypeSymbol, LocalSymbol>.GetLocals(
            locals,
            symbolProvider,
            debugInfo.LocalVariableNames,
            localInfo,
            debugInfo.DynamicLocalMap,
            debugInfo.TupleLocalMap);
        ImmutableSortedSet<int> hoistedLocals = debugInfo.GetInScopeHoistedLocalIndices(ilOffset, ref reuseSpan);
        locals.AddRange(debugInfo.LocalConstants);

        return new EvaluationContext(
            new MethodContextReuseConstraints(moduleId, methodToken, methodVersion: version, reuseSpan),
            compilation,
            currentFrame,
            currentSourceMethod,
            locals.ToImmutableAndFree(),
            hoistedLocals,
            debugInfo);
    }

    private EvaluationContext CreateTypeContext(ImmutableArray<MetadataBlock> blocks, ICorDebugValue rootValue)
    {
        ICorDebugType rootType = rootValue.ExactType;
        ModuleInfo moduleInfo = debugger.GetModuleInfoForModule(rootType.Class.Module);
        ModuleId moduleId = new ModuleId(moduleInfo.MetadataReader.Mvid, moduleInfo.ModuleName);
        CSharpCompilation compilation = AddRuntimeAccessAttributes(blocks.ToCompilation(moduleId: default, MakeAssemblyReferencesKind.AllAssemblies).AddReferences(_intrinsicMethodsReference));
        PENamedTypeSymbol currentType = compilation.GetType(moduleId, rootType.Class.Token);
        SynthesizedContextMethodSymbol currentFrame = new SynthesizedContextMethodSymbol(currentType);
        return new EvaluationContext(
            null,
            compilation,
            currentFrame,
            currentSourceMethod: null,
            locals: default,
            inScopeHoistedLocalSlots: ImmutableSortedSet<int>.Empty,
            methodDebugInfo: MethodDebugInfo<TypeSymbol, LocalSymbol>.None);
    }

    private static CSharpCompilation AddRuntimeAccessAttributes(CSharpCompilation compilation)
    {
        string attributes = string.Join("\n", compilation.ReferencedAssemblyNames.Select(identity =>
            $"[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo({SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(identity.Name))})]"));
        string source = $$"""
			{{attributes}}

			namespace System.Runtime.CompilerServices
			{
				[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
				internal sealed class IgnoresAccessChecksToAttribute : System.Attribute
				{
					public IgnoresAccessChecksToAttribute(string assemblyName) { }
				}
			}
			""";
        return compilation.AddSyntaxTrees(SyntaxFactory.ParseSyntaxTree(source));
    }

    private static ImmutableArray<byte> CreateIntrinsicMethodsAssembly()
    {
        const string source = """
			namespace Microsoft.VisualStudio.Debugger.Clr;

			public static class IntrinsicMethods
			{
				public static void CreateVariable(System.Type type, string name, System.Guid customTypeInfoPayloadTypeId, byte[] customTypeInfoPayload) { }
				public static object GetObjectByAlias(string name) => throw null;
				public static ref T GetVariableAddress<T>(string name) => throw null;
				public static System.Exception GetException() => throw null;
				public static object GetReturnValue(int index) => throw null;
			}
			""";
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Passenger.DebuggerIntrinsics",
            [SyntaxFactory.ParseSyntaxTree(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream stream = new MemoryStream();
        EmitResult result = compilation.Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.GetMessage())));
        }
        return ImmutableArray.Create(stream.ToArray());
    }

    /// <summary>An expression that declares variables (a pattern's, an out argument's) as the one arm of a switch,
    /// <c>0 switch { _ => (e) }</c>, any format specifiers after it kept: a variable declared at an expression's top
    /// compiles as a debugger variable, which a pattern never assigns, and one an arm declares as a local of the
    /// expression's own. A deconstruction that declares its variables stays as written, no arm taking a declaration.</summary>
    private static string ScopeDeclaredVariables(string expression)
    {
        DiagnosticBag diagnostics = DiagnosticBag.GetInstance();
        try
        {
            ExpressionSyntax? syntax = SyntaxHelpers.ParseExpression(expression, isInFieldKeywordContext: false, diagnostics, allowFormatSpecifiers: true, out _);
            if (syntax is null || syntax is AssignmentExpressionSyntax { Left: DeclarationExpressionSyntax }
                || !syntax.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>().Any())
            {
                return expression;
            }
            return $"0 switch {{ _ => ({expression[..syntax.FullWidth]}) }}{expression[syntax.FullWidth..]}";
        }
        finally
        {
            diagnostics.Free();
        }
    }

    // Takes off an interpolation's alignment that is no number: a DebuggerDisplay fragment's format specifier (nq).
    private sealed class RemoveFormatSpecifierAlignmentRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInterpolation(InterpolationSyntax node)
        {
            InterpolationSyntax result = (InterpolationSyntax)base.VisitInterpolation(node)!;
            if (result.AlignmentClause is not null && result.AlignmentClause.Value is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression })
            {
                result = result.WithAlignmentClause(null);
            }
            return result;
        }
    }
}
