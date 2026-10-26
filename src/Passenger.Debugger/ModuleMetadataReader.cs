using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Passenger.Debugger.Interop;
using ZLinq;

namespace Passenger.Debugger;

/// <summary>
/// Reads module metadata and, when available, portable PDB metadata.
/// </summary>
public partial class ModuleMetadataReader : IDisposable
{
    /// <summary>The module's image; none for one made as the program runs (System.Reflection.Emit), read from its metadata alone.</summary>
    private readonly PEReader? _peReader;
    private readonly MetadataReaderProvider? _metadataProvider;
    private MetadataReader _peMetadataReader;

    /// <summary>The image's own metadata, whose RVAs find its methods' IL, whatever Hot Reload has made of the metadata since.</summary>
    private readonly MetadataReader _imageMetadataReader;
    private MetadataReaderProvider? _pdbProvider;
    private MetadataReader? _pdbMetadataReader;
    internal MetadataReader PeMetadataReader => _peMetadataReader;
    internal MetadataReader? PdbMetadataReader => _pdbMetadataReader;
    private Guid? _mvid;
    internal Guid Mvid => _mvid ??= _peMetadataReader.GetGuid(_peMetadataReader.GetModuleDefinition().Mvid);
    public bool HasSymbols => _pdbMetadataReader is not null;

    /// <summary>The pointer size the module is built for: CoreLib's is its runtime's.</summary>
    internal int PointerSize => _peReader?.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus ? 8 : 4;

    /// <summary>The methods a module's own code says are not the user's: marked [DebuggerNonUserCode],
    /// [DebuggerStepThrough] or [DebuggerHidden], or declared in a type (or a type nested in one) marked with either of
    /// the first two. Just My Code leaves them out.</summary>
    internal HashSet<int> NonUserCodeMethodTokens() => _nonUserCode ??= [.. FindNonUserCodeMethodTokens()];

    /// <summary>Whether the module's own code says a method is not the user's.</summary>
    internal bool IsNonUserCode(int methodToken) => NonUserCodeMethodTokens().Contains(methodToken);

    /// <summary>Whether a method is [DebuggerHidden], which no debugger stops in, Just My Code or not.</summary>
    internal bool IsHidden(int methodToken)
    {
        _hidden ??= [.. _peMetadataReader.MethodDefinitions.Where(m => HasAttribute(_peMetadataReader, _peMetadataReader.GetMethodDefinition(m).GetCustomAttributes(), ["System.Diagnostics.DebuggerHiddenAttribute"])).Select(m => MetadataTokens.GetToken(m))];
        return _hidden.Contains(methodToken);
    }

    private HashSet<int>? _nonUserCode;
    private HashSet<int>? _hidden;

    private IEnumerable<int> FindNonUserCodeMethodTokens()
    {
        MetadataReader reader = _peMetadataReader;
        Dictionary<TypeDefinitionHandle, bool> typeOptsOut = [];
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            bool wholeType = OptsOut(typeHandle);
            foreach (MethodDefinitionHandle methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
            {
                if (wholeType || HasAttribute(reader, reader.GetMethodDefinition(methodHandle).GetCustomAttributes(), AttributeConstants.JmcMethodAttributeNames))
                {
                    yield return MetadataTokens.GetToken(methodHandle);
                }
            }
        }

        bool OptsOut(TypeDefinitionHandle handle)
        {
            if (!typeOptsOut.TryGetValue(handle, out bool optsOut))
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                TypeDefinitionHandle declaring = type.GetDeclaringType();
                optsOut = HasAttribute(reader, type.GetCustomAttributes(), AttributeConstants.JmcTypeAttributeNames)
                    || (!declaring.IsNil && OptsOut(declaring));
                typeOptsOut[handle] = optsOut;
            }
            return optsOut;
        }
    }

    private static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, string[] names)
    {
        foreach (CustomAttributeHandle handle in attributes)
        {
            EntityHandle constructor = reader.GetCustomAttribute(handle).Constructor;
            EntityHandle type = constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default,
            };
            string? name = type.Kind switch
            {
                HandleKind.TypeReference when reader.GetTypeReference((TypeReferenceHandle)type) is var reference =>
                    $"{reader.GetString(reference.Namespace)}.{reader.GetString(reference.Name)}",
                HandleKind.TypeDefinition when reader.GetTypeDefinition((TypeDefinitionHandle)type) is var definition =>
                    $"{reader.GetString(definition.Namespace)}.{reader.GetString(definition.Name)}",
                _ => null,
            };
            if (name is not null && names.Contains(name, StringComparer.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    internal (int MethodToken, int ILOffset)? ResolveManagedEntryPoint()
    {
        CorHeader? corHeader = _peReader?.PEHeaders.CorHeader;
        if (corHeader is null || (corHeader.Flags & CorFlags.NativeEntryPoint) != 0)
        {
            return null;
        }

        int entryPointToken = corHeader.EntryPointTokenOrRelativeVirtualAddress;
        if (entryPointToken == 0)
        {
            return null;
        }

        Handle entryPointHandle = MetadataTokens.Handle(entryPointToken);
        if (entryPointHandle.Kind is not HandleKind.MethodDefinition)
        {
            return null;
        }

        MethodDefinition entryPoint = _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)entryPointHandle);
        if (_peMetadataReader.GetString(entryPoint.Name) is "<Main>" && TryResolveAsyncMainEntryPoint(entryPoint.GetDeclaringType()) is { } asyncEntryPoint)
        {
            return asyncEntryPoint;
        }

        return (entryPointToken, ResolveBreakpointAtMethodEntry(entryPointToken)?.ILOffset ?? 0);
    }

    /// Lines and columns are 1 based
    public record ResolvedBreakpoint(
        int MethodToken,
        int ILOffset,
        int StartLine,
        int EndLine,
        int StartColumn,
        int EndColumn,
        string DocumentPath
    )
    {
        /// <summary>Where the breakpoint goes in the IL: its sequence point's own offset, or for a point the JIT maps no
        /// code to (Avalonia's XAML compiler writes them mid-expression) the nearest before it that it does map.</summary>
        public int BindOffset { get; init; } = ILOffset;

        /// <summary>Whether a frame stopped here shows this place rather than its IL offset's sequence point: bound short of
        /// its point, or an XAML attribute with no point of its own.</summary>
        public bool Placed => BindOffset != ILOffset || Attributed;

        /// <summary>An XAML attribute's setter that XamlIl compiled under the point of its element (or of an attribute before
        /// it): bound at that point, and shown at the attribute.</summary>
        public bool Attributed { get; init; }
    }

    /// <summary>
    /// Information about an await block in an async method
    /// </summary>
    public record AsyncAwaitInfo(uint YieldOffset, uint ResumeOffset);

    /// <summary>
    /// Complete async method stepping information
    /// </summary>
    public class AsyncMethodSteppingInfo
    {
        public List<AsyncAwaitInfo> AwaitInfos { get; set; } = new();
        public int LastUserCodeIlOffset { get; set; }
    }

    private ModuleMetadataReader(PEReader peReader)
    {
        _peReader = peReader;
        _peMetadataReader = peReader.GetMetadataReader(MetadataReaderOptions.None);
        _imageMetadataReader = _peMetadataReader;
    }

    private ModuleMetadataReader(MetadataReaderProvider metadataProvider)
    {
        _metadataProvider = metadataProvider;
        _peMetadataReader = metadataProvider.GetMetadataReader(MetadataReaderOptions.None);
        _imageMetadataReader = _peMetadataReader;
    }

    /// <summary>A module known by its metadata alone, a copy of the runtime's (a module emitted as the program runs), with
    /// no symbols; null when the bytes are not metadata.</summary>
    public static ModuleMetadataReader? TryLoadFromMetadata(byte[] metadata)
    {
        MetadataReaderProvider? provider = null;
        try
        {
            provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(metadata));
            return new ModuleMetadataReader(provider);
        }
        catch (Exception error) when (Failures.OfReading(error))
        {
            provider?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Try to load module metadata and any matching portable PDB.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly (.dll)</param>
    /// <returns>A reader for a valid managed PE, whether or not symbols were found.</returns>
    public static ModuleMetadataReader? TryLoad(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            return TryLoadInternal(stream, assemblyPath);
        }
        catch (Exception error) when (Failures.OfReading(error))
        {
            return null;
        }
    }

    public bool TryLoadSymbols(string pdbPath)
    {
        if (!File.Exists(pdbPath))
        {
            return false;
        }

        MetadataReaderProvider? provider = null;
        try
        {
            // Read whole and closed at once, sharing delete: another adapter may rename a fresh copy over it meanwhile.
            provider = MetadataReaderProvider.FromPortablePdbStream(new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
                MetadataStreamOptions.PrefetchMetadata);
            SetPdbProvider(provider);
            return true;
        }
        catch (Exception error) when (Failures.OfReading(error))
        {
            provider?.Dispose();
            return false;
        }
    }

    public static ModuleMetadataReader? TryLoadFromBytes(byte[] inMemoryModuleBytes)
    {
        try
        {
            using MemoryStream stream = new MemoryStream(inMemoryModuleBytes, writable: false);
            return TryLoadInternal(stream);
        }
        catch (Exception error) when (Failures.OfReading(error))
        {
            return null;
        }
    }

    private static ModuleMetadataReader? TryLoadInternal(Stream assemblyStream, string? assemblyPath = null)
    {
        PEReader? peReader = null;
        try
        {
            peReader = new PEReader(assemblyStream, PEStreamOptions.PrefetchEntireImage);
            ModuleMetadataReader result = new ModuleMetadataReader(peReader);

            DebugDirectoryEntry codeViewEntry = default;
            DebugDirectoryEntry embeddedPdbEntry = default;

            foreach (DebugDirectoryEntry entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    const ushort PortableCodeViewVersionMagic = 0x504d;
                    if (entry.MinorVersion == PortableCodeViewVersionMagic)
                    {
                        codeViewEntry = entry;
                    }
                }
                else if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    embeddedPdbEntry = entry;
                }
            }

            if (codeViewEntry.DataSize != 0)
            {
                result.TryLoadFromCodeView(codeViewEntry, assemblyPath);
            }

            if (embeddedPdbEntry.DataSize != 0)
            {
                result.TryLoadEmbeddedPdb(embeddedPdbEntry);
            }

            return result;
        }
        catch (Exception error) when (Failures.OfReading(error))
        {
            peReader?.Dispose();
            return null;
        }
    }

    private bool TryLoadFromCodeView(DebugDirectoryEntry codeViewEntry, string? assemblyPath)
    {
        MetadataReaderProvider? provider = null;
        try
        {
            CodeViewDebugDirectoryData codeViewData = _peReader!.ReadCodeViewDebugDirectoryData(codeViewEntry);
            string pdbPath = codeViewData.Path;

            string? assemblyDir = Path.GetDirectoryName(assemblyPath);
            if (assemblyDir is not null)
            {
                string pdbFileName = Path.GetFileName(pdbPath);
                pdbPath = Path.Combine(assemblyDir, pdbFileName);
            }

            if (!File.Exists(pdbPath))
            {
                return false;
            }

            // Don't need to dispose stream, FromPortablePdbStream disposes of it internally
            FileStream pdbStream = File.OpenRead(pdbPath);
            provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            // Metadata without a PDB's header is no portable PDB of the assembly's.
            if (provider.GetMetadataReader().DebugMetadataHeader is not { } header)
            {
                provider.Dispose();
                return false;
            }
            BlobContentId pdbId = new BlobContentId(header.Id);
            BlobContentId expectedId = new BlobContentId(codeViewData.Guid, codeViewEntry.Stamp);

            if (codeViewData.Age == 1 && pdbId == expectedId)
            {
                SetPdbProvider(provider);
                return true;
            }

            provider.Dispose();
            return false;
        }
        catch (Exception error) when (Failures.OfReading(error))
        {
            provider?.Dispose();
            return false;
        }
    }

    private bool TryLoadEmbeddedPdb(DebugDirectoryEntry embeddedPdbEntry)
    {
        try
        {
            SetPdbProvider(_peReader!.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdbEntry));
            return true;
        }
        catch (Exception error) when (Failures.OfReading(error))
        {
            return false;
        }
    }

    private void SetPdbProvider(MetadataReaderProvider provider)
    {
        MetadataReader reader = provider.GetMetadataReader();
        _pdbProvider?.Dispose();
        _pdbProvider = provider;
        _pdbMetadataReader = reader;
    }

    /// <summary>Where a method's code at an IL offset is written, for the code at a version (the newest without one).</summary>
    public (string sourceFilePath, int startLine, int endLine, int startColumn, int endColumn)? GetSourceLocationForOffset(int methodToken, int ilOffset, int? version = null)
    {
        if (GetSequencePointForOffset(methodToken, ilOffset, version, skipHidden: true) is not { Document: { } document } point)
        {
            return null;
        }
        return (document, point.StartLine, point.EndLine, point.StartColumn, point.EndColumn);
    }

    /// <summary>
    /// Returns whether the sequence point containing <paramref name="ilOffset"/> is hidden.
    /// Unlike source-location lookup, this must retain hidden points so stepping does not
    /// incorrectly map a compiler-generated async continuation back to prior user code.
    /// </summary>
    public bool IsCurrentSequencePointHidden(int methodToken, int ilOffset, int? version = null) => GetSequencePointForOffset(methodToken, ilOffset, version)?.IsHidden is true;

    /// <summary>The statement a method's code at a version is in at an IL offset: its last visible sequence point at or before it.</summary>
    internal Point? StatementAt(int methodToken, int ilOffset, int version) => GetSequencePointForOffset(methodToken, ilOffset, version, skipHidden: true);

    private Point? GetSequencePointForOffset(int methodToken, int ilOffset, int? version, bool skipHidden = false)
    {
        // PDB sequence points are ordered by offset; retain the last eligible point at or before the IP.
        Point? currentPoint = null;
        foreach (Point sequencePoint in Points(methodToken, version))
        {
            if (sequencePoint.Offset > ilOffset)
            {
                break;
            }

            if (skipHidden && sequencePoint.IsHidden)
            {
                continue;
            }

            currentPoint = sequencePoint;
        }

        return currentPoint;
    }

    internal ResolvedBreakpoint? ResolveBreakpointAtMethodEntry(int methodToken)
    {
        if (Points(methodToken).FirstOrDefault(p => !p.IsHidden) is not { Document: { } document } point)
        {
            return null;
        }
        return new ResolvedBreakpoint(methodToken, point.Offset, point.StartLine, point.EndLine, point.StartColumn, point.EndColumn, document);
    }

    public ImmutableArray<string> GetImportedNamespaces(int methodToken)
    {
        Handle handle = MetadataTokens.Handle(methodToken);
        if (handle.Kind is not HandleKind.MethodDefinition)
        {
            throw new ArgumentException("methodToken is not a valid MethodDefinition token");
        }

        MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)handle;
        ImmutableArray<string>.Builder namespaces = ImmutableArray.CreateBuilder<string>();

        if (DebugOf(methodToken) is { Reader: { } reader } debug)
        {
            foreach (LocalScopeHandle scopeHandle in reader.GetLocalScopes(debug.Handle))
            {
                LocalScope scope = reader.GetLocalScope(scopeHandle);
                ImportScope importScope = reader.GetImportScope(scope.ImportScope);
                foreach (ImportDefinition import in importScope.GetImports())
                {
                    if (import.Kind == ImportDefinitionKind.ImportNamespace)
                    {
                        BlobReader blobReader = reader.GetBlobReader(import.TargetNamespace);
                        string namespaceName = blobReader.ReadUTF8(blobReader.Length);
                        namespaces.Add(namespaceName);
                    }
                }
            }
        }
        MethodDefinition methodDef = _peMetadataReader.GetMethodDefinition(methodHandle);
        TypeDefinitionHandle typeDef = methodDef.GetDeclaringType();
        TypeDefinition typeDefObj = _peMetadataReader.GetTypeDefinition(typeDef);
        string typeNamespaceName = _peMetadataReader.GetString(typeDefObj.Namespace);
        if (string.IsNullOrEmpty(typeNamespaceName) is false && namespaces.Contains(typeNamespaceName) is false)
        {
            namespaces.Add(typeNamespaceName);
        }
        namespaces.Add(""); // global namespace

        return namespaces.ToImmutable();
    }

    public string? GetLocalVariableName(int methodToken, int localIndex, int currentIlOffset, int? version = null)
    {
        if (DebugOf(methodToken, version) is not { Reader: { } reader } debug)
        {
            return null;
        }

        LocalScopeHandleCollection localScopes = reader.GetLocalScopes(debug.Handle);
        foreach (LocalScopeHandle scopeHandle in localScopes)
        {
            LocalScope scope = reader.GetLocalScope(scopeHandle);

            if (currentIlOffset < scope.StartOffset || currentIlOffset >= scope.StartOffset + scope.Length)
            {
                continue;
            }

            foreach (LocalVariableHandle variableHandle in scope.GetLocalVariables())
            {
                LocalVariable variable = reader.GetLocalVariable(variableHandle);

                if (variable.Index == localIndex)
                {
                    if (variable.Attributes is LocalVariableAttributes.DebuggerHidden)
                    {
                        return "HIDDEN";
                    }

                    if (variable.Name.IsNil)
                    {
                        return "NIL";
                    }

                    return reader.GetString(variable.Name);
                }
            }
        }

        return null;
    }

    public string? GetArgumentName(int methodToken, int paramIndex)
    {
        MethodDefinitionHandle methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
        MethodDefinition methodDef = _peMetadataReader.GetMethodDefinition(methodHandle);

        ParameterHandleCollection parameters = methodDef.GetParameters();

        int currentIndex = 0;
        foreach (ParameterHandle paramHandle in parameters)
        {
            if (currentIndex == paramIndex)
            {
                Parameter param = _peMetadataReader.GetParameter(paramHandle);

                if (param.Name.IsNil)
                {
                    return null;
                }

                return _peMetadataReader.GetString(param.Name);
            }
            currentIndex++;
        }

        return null;
    }

    public (int ilStartOffset, int ilEndOffset)? GetStartAndEndSequencePointIlOffsetsForIlOffset(int methodToken, int ip, int? version = null)
    {
        List<Point?> points = Points(methodToken, version)
            .Where(sp => sp.StartLine != 0 && sp.IsHidden is false)
            .OrderBy(sp => sp.Offset)
            .Cast<Point?>()
            .ToList();

        if (points.Count is 0)
        {
            return null;
        }

        Point? startPoint = points.LastOrDefault(sp => sp!.Value.Offset <= ip); // e.g. ip = 0, it is possible that there is no matching sequence point

        Point? endPoint = points.FirstOrDefault(sp => sp!.Value.Offset > ip);

        int ilStartOffset = startPoint?.Offset ?? ip;
        int ilEndOffset = endPoint?.Offset ?? ilStartOffset;

        // Calling method will handle when ilEndOffset == ilStartOffset, and change it to method size
        return (ilStartOffset, ilEndOffset);
    }

    public (int currentIlOffset, int? nextUserCodeIlOffset) GetFrameCurrentIlOffsetAndNextUserCodeIlOffset(ICorDebugILFrame ilFrame)
    {
        ICorDebugFunction method = ilFrame.Function;
        mdMethodDef methodToken = method.Token;
        (int pnOffset, CorDebugMappingResult pMappingResult) ipResult = ilFrame.IP;
        if (ipResult.pMappingResult is CorDebugMappingResult.MAPPING_UNMAPPED_ADDRESS or CorDebugMappingResult.MAPPING_NO_INFO)
        {
            throw new InvalidOperationException("IL Frame IP is unmapped or has no info");
        }
        int? nextUserCodeIlOffset = GetNextUserCodeIlOffset(methodToken, ipResult.pnOffset, VersionOf(ilFrame));
        return (ipResult.pnOffset, nextUserCodeIlOffset);
    }

    /// <summary>The EnC version of the code a frame runs (1 for the build's), or null when the runtime will not say.</summary>
    internal static int? VersionOf(ICorDebugFrame frame) =>
        frame.TryGetCode(out ICorDebugCode code) is Cor.S_OK && code.TryGetVersionNumber(out uint version) is Cor.S_OK ? (int)version : null;

    public int? GetNextUserCodeIlOffset(int methodToken, int currentIlOffset, int? version = null)
    {
        foreach (Point sequencePoint in Points(methodToken, version))
        {
            if (sequencePoint.StartLine is 0 or SequencePoint.HiddenLine)
            {
                continue;
            }

            if (sequencePoint.Offset >= currentIlOffset)
            {
                int nextUserCodeIlOffset = sequencePoint.Offset;
                return nextUserCodeIlOffset;
            }
        }
        return null;
    }

    // Guid for async method stepping information from Roslyn
    // https://github.com/dotnet/roslyn/blob/afd10305a37c0ffb2cfb2c2d8446154c68cfa87a/src/Dependencies/CodeAnalysis.Debugging/PortableCustomDebugInfoKinds.cs#L13
    private static readonly Guid _asyncMethodSteppingInformationBlob = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");

    /// <summary>A method's async stepping information: its awaits' yield and resume offsets, and the last IL offset of
    /// its user code; null for a method without awaits.</summary>
    public AsyncMethodSteppingInfo? GetAsyncMethodSteppingInfo(int methodToken, int? version = null)
    {
        if (DebugOf(methodToken, version) is not { Reader: { } reader } debug)
        {
            return null;
        }

        AsyncMethodSteppingInfo result = new AsyncMethodSteppingInfo();
        bool foundOffset = false;
        foreach (CustomDebugInformationHandle cdiHandle in reader.GetCustomDebugInformation(debug.Handle))
        {
            CustomDebugInformation cdi = reader.GetCustomDebugInformation(cdiHandle);

            if (reader.GetGuid(cdi.Kind) == _asyncMethodSteppingInformationBlob)
            {
                BlobReader blobReader = reader.GetBlobReader(cdi.Value);

                // Skip catch_handler_offset
                blobReader.ReadUInt32();

                // Read yield_offset, resume_offset, compressed_token tuples
                while (blobReader.Offset < blobReader.Length)
                {
                    uint yieldOffset = blobReader.ReadUInt32();
                    uint resumeOffset = blobReader.ReadUInt32();
                    uint token = (uint)blobReader.ReadCompressedInteger();

                    result.AwaitInfos.Add(new AsyncAwaitInfo(yieldOffset, resumeOffset));
                }
            }
        }

        if (result.AwaitInfos.Count == 0)
        {
            return null;
        }

        foreach (Point sp in Points(methodToken, version))
        {
            if (sp.StartLine == 0 || sp.IsHidden || sp.Offset < 0)
            {
                continue;
            }

            result.LastUserCodeIlOffset = sp.Offset;
            foundOffset = true;
        }

        if (!foundOffset)
        {
            return null;
        }

        return result;
    }

    /// <summary>
    /// Get all source files referenced in the PDB
    /// </summary>
    public IEnumerable<string> GetSourceFiles() => DocumentNames();

    public void Dispose()
    {
        foreach (MetadataReaderProvider updated in _updatedMetadata)
        {
            updated.Dispose();
        }
        foreach (Generation generation in _generations)
        {
            generation.Dispose();
        }
        _pdbProvider?.Dispose();
        _peReader?.Dispose();
        _metadataProvider?.Dispose();
    }
}
