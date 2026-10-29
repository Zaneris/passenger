using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Passenger.Debugger;

/// <summary>A module's code as Hot Reload has changed it: each update applied (a generation) brings the debug
/// information of the methods it changed (its PDB delta, whose debug tables name a method by its row among the delta's
/// own), their new IL, the lines that moved in methods it did not change, and how it changed each document's text. A
/// method's code at a version (the runtime's EnC version, 1 for the build's) is that of the last generation before it
/// that changed the method; its sequence points' lines are carried through each later generation's moves, or its text's
/// change where it changed the method, so they are where its code now is, but for a point whose lines it rewrote.</summary>
public partial class ModuleMetadataReader
{
    /// <summary>A sequence point of a method's code as the program now has it, lines and columns from one as the PDB has
    /// them, and the name of its document as the PDB records it.</summary>
    internal readonly record struct Point(int Offset, int StartLine, int StartColumn, int EndLine, int EndColumn, string? Document)
    {
        public bool IsHidden => StartLine == SequencePoint.HiddenLine;

        /// <summary>Whether a later update rewrote this point's lines: the code it marks is no longer in the text, and its
        /// lines are those it had before.</summary>
        public bool Rewritten { get; init; }
    }

    /// <summary>A method's debug information at a version: the reader it is in (the build's PDB or a delta's) and the handle
    /// that reader knows it by.</summary>
    internal readonly record struct MethodDebug(MetadataReader Reader, MethodDefinitionHandle Handle, int Generation)
    {
        public MethodDebugInformation Info => Reader.GetMethodDebugInformation(Handle.ToDebugInformationHandle());
    }

    private sealed class Generation(int number) : IDisposable
    {
        public int Number { get; } = number;
        public MetadataReaderProvider? Pdb { get; init; }
        public MetadataReader? PdbReader { get; init; }

        /// <summary>Each method's handle in the delta PDB's own tables, by its token.</summary>
        public Dictionary<int, MethodDefinitionHandle> Relative { get; } = [];

        /// <summary>The methods whose code the update replaced or added.</summary>
        public HashSet<int> Methods { get; } = [];

        /// <summary>Each method's new IL body, by its token.</summary>
        public Dictionary<int, byte[]> IL { get; } = [];

        /// <summary>Lines moved with no change to their code, by document name: from each old line (from zero) on, lines
        /// move to the new line's, up to the next.</summary>
        public Dictionary<string, (int Old, int New)[]> Lines { get; } = new(StringComparer.Ordinal);

        /// <summary>How the update changed each document's text, by document name.</summary>
        public Dictionary<string, TextChange> Texts { get; } = new(StringComparer.Ordinal);

        public void Dispose() => Pdb?.Dispose();
    }

    private readonly List<Generation> _generations = [];

    /// <summary>Each method's points at a version, read once per update applied.</summary>
    private readonly Dictionary<(int Token, int Generation), IReadOnlyList<Point>> _points = [];

    /// <summary>How many updates have been applied to the module.</summary>
    internal int Generations => _generations.Count;

    /// <summary>Takes an update the runtime applied: its metadata and IL deltas (the new bodies at their RVAs in the IL),
    /// its PDB delta, the methods it changed, the lines it moved in each document, and how it changed their text.</summary>
    internal void AddGeneration(byte[] metadataDelta, byte[] ilDelta, byte[] pdbDelta, IReadOnlyList<int> updatedMethods,
        IReadOnlyList<(string Document, IReadOnlyList<(int Old, int New)> Updates)> lines, IReadOnlyList<TextChange> texts)
    {
        MetadataReaderProvider? pdb = null;
        MetadataReader? pdbReader = null;
        if (pdbDelta.Length > 0)
        {
            pdb = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(pdbDelta));
            pdbReader = pdb.GetMetadataReader();
        }
        Generation generation = new(_generations.Count + 1) { Pdb = pdb, PdbReader = pdbReader };
        generation.Methods.UnionWith(updatedMethods);
        if (pdbReader is not null)
        {
            int row = 1;
            foreach (EntityHandle handle in pdbReader.GetEditAndContinueMapEntries())
            {
                if (handle.Kind == HandleKind.MethodDebugInformation)
                {
                    generation.Relative[MetadataTokens.GetToken(((MethodDebugInformationHandle)handle).ToDefinitionHandle())] = MetadataTokens.MethodDefinitionHandle(row++);
                }
            }
        }
        if (metadataDelta.Length > 0)
        {
            using MetadataReaderProvider metadata = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(metadataDelta));
            MetadataReader reader = metadata.GetMetadataReader();
            int row = 1;
            foreach (EntityHandle handle in reader.GetEditAndContinueMapEntries())
            {
                if (handle.Kind != HandleKind.MethodDefinition)
                {
                    continue;
                }
                int rva = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row++)).RelativeVirtualAddress;
                if (rva > 0 && rva < ilDelta.Length && Body(ilDelta, rva) is { } body)
                {
                    generation.IL[MetadataTokens.GetToken(handle)] = body;
                }
            }
        }
        foreach ((string document, IReadOnlyList<(int Old, int New)> updates) in lines)
        {
            generation.Lines[document] = [.. updates.OrderBy(u => u.Old)];
        }
        foreach (TextChange text in texts)
        {
            generation.Texts[text.Document] = text;
        }
        _generations.Add(generation);
        _points.Clear();
        _documents = null;
        _bestDocuments.Clear();
    }

    /// <summary>The metadata the updates made, kept while the module is: what names the members they added.</summary>
    private readonly List<MetadataReaderProvider> _updatedMetadata = [];

    /// <summary>Takes the module's metadata as the updates have made it (the debugger's own copy, saved whole) in place of
    /// the image's, so the members an update added (a method, a member reference, a type) are found by their tokens. The
    /// image's methods keep their RVAs in it, so their IL is still read from the image.</summary>
    internal void TakeMetadata(byte[] saved)
    {
        MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(saved));
        MetadataReader reader = provider.GetMetadataReader(MetadataReaderOptions.None);
        _updatedMetadata.Add(provider);
        _peMetadataReader = reader;
        _nonUserCode = null;
        _hidden = null;
    }

    /// <summary>A method body's IL at an offset of an IL delta, or null when it does not read as one.</summary>
    private static unsafe byte[]? Body(byte[] il, int rva)
    {
        try
        {
            fixed (byte* start = il)
            {
                return MethodBodyBlock.Create(new BlobReader(start + rva, il.Length - rva)).GetILBytes();
            }
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>The generation a method's code at a version comes from: the last update before that version that changed
    /// the method (0, the build, if none did); with no version, the newest.</summary>
    private int GenerationOf(int methodToken, int? version)
    {
        int newest = version is int v ? Math.Min(v - 1, _generations.Count) : _generations.Count;
        for (int g = newest; g >= 1; g--)
        {
            if (_generations[g - 1].Methods.Contains(methodToken))
            {
                return g;
            }
        }
        return 0;
    }

    /// <summary>A method's debug information at a version (the newest without one), or null where it has none.</summary>
    internal MethodDebug? DebugOf(int methodToken, int? version = null)
    {
        int generation = GenerationOf(methodToken, version);
        if (generation > 0)
        {
            Generation update = _generations[generation - 1];
            return update.PdbReader is { } delta && update.Relative.TryGetValue(methodToken, out MethodDefinitionHandle relative)
                ? new MethodDebug(delta, relative, generation)
                : null;
        }
        return _pdbMetadataReader is { } reader && MetadataTokens.GetRowNumber(MetadataTokens.MethodDefinitionHandle(methodToken)) <= reader.GetTableRowCount(TableIndex.MethodDebugInformation)
            ? new MethodDebug(reader, MetadataTokens.MethodDefinitionHandle(methodToken), 0)
            : null;
    }

    /// <summary>A method's sequence points at a version (the newest without one), in IL order, where its code now is.</summary>
    internal IReadOnlyList<Point> Points(int methodToken, int? version = null)
    {
        if (DebugOf(methodToken, version) is not { } debug)
        {
            return [];
        }
        if (_generations.Count == 0)
        {
            return Read(debug);
        }
        if (!_points.TryGetValue((methodToken, debug.Generation), out IReadOnlyList<Point>? points))
        {
            points = _points[(methodToken, debug.Generation)] = Moved(Read(debug), methodToken, debug.Generation);
        }
        return points;
    }

    private static List<Point> Read(MethodDebug debug)
    {
        MethodDebugInformation info = debug.Info;
        if (info.SequencePointsBlob.IsNil)
        {
            return [];
        }
        MetadataReader reader = debug.Reader;
        List<Point> points = [];
        string? single = info.Document.IsNil ? null : reader.GetString(reader.GetDocument(info.Document).Name);
        DocumentHandle last = default;
        string? lastName = null;
        foreach (SequencePoint point in info.GetSequencePoints())
        {
            string? document = single;
            if (!point.Document.IsNil)
            {
                if (point.Document != last)
                {
                    last = point.Document;
                    lastName = reader.GetString(reader.GetDocument(point.Document).Name);
                }
                document = lastName;
            }
            points.Add(new Point(point.Offset, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn, document));
        }
        return points;
    }

    /// <summary>A method's points read from a generation, their lines moved by each later generation's moves of their
    /// documents, or, by one that changed the method, by its change to their text.</summary>
    private List<Point> Moved(List<Point> points, int methodToken, int generation)
    {
        for (int g = generation + 1; g <= _generations.Count; g++)
        {
            Generation later = _generations[g - 1];
            if (later.Methods.Contains(methodToken))
            {
                Rewrite(points, later.Texts);
                continue;
            }
            Dictionary<string, (int Old, int New)[]> moves = later.Lines;
            if (moves.Count == 0)
            {
                continue;
            }
            for (int i = 0; i < points.Count; i++)
            {
                Point point = points[i];
                if (point.IsHidden || point.Document is not { } document || !moves.TryGetValue(document, out (int Old, int New)[]? updates))
                {
                    continue;
                }
                int delta = 0;
                foreach ((int old, int moved) in updates)
                {
                    if (old > point.StartLine - 1)
                    {
                        break;
                    }
                    delta = moved - old;
                }
                if (delta != 0)
                {
                    points[i] = point with { StartLine = point.StartLine + delta, EndLine = point.EndLine + delta };
                }
            }
        }
        return points;
    }

    /// <summary>Points of a method's code an update replaced, carried through its change to their documents' text: a point
    /// above the change stays, one below it moves with the text, and one the change reaches is marked rewritten.</summary>
    private static void Rewrite(List<Point> points, Dictionary<string, TextChange> texts)
    {
        for (int i = 0; i < points.Count; i++)
        {
            Point point = points[i];
            if (point.IsHidden || point.Rewritten || point.Document is not { } document || !texts.TryGetValue(document, out TextChange? text)
                || point.EndLine - 1 < text.Line)
            {
                continue;
            }
            int moved = text.TailNow - text.TailWas;
            points[i] = point.StartLine - 1 >= text.TailWas
                ? point with { StartLine = point.StartLine + moved, EndLine = point.EndLine + moved }
                : point with { Rewritten = true };
        }
    }

    /// <summary>Every method with debug information: the build's, then those updates added.</summary>
    internal IEnumerable<int> MethodsWithPoints()
    {
        HashSet<int> seen = [];
        if (_pdbMetadataReader is { } reader)
        {
            foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
            {
                int token = MetadataTokens.GetToken(handle.ToDefinitionHandle());
                seen.Add(token);
                yield return token;
            }
        }
        foreach (Generation generation in _generations)
        {
            foreach (int token in generation.Relative.Keys)
            {
                if (seen.Add(token))
                {
                    yield return token;
                }
            }
        }
    }

    /// <summary>The methods that may have a sequence point in a document, in <see cref="MethodsWithPoints()"/>'s order: the
    /// build's with one there, and every method an update brought. What a breakpoint in a file is looked for in, rather than
    /// every method of the module, each read again for every breakpoint bound.</summary>
    internal IEnumerable<int> MethodsWithPoints(string document)
    {
        int[] built = BuiltMethodsByDocument().GetValueOrDefault(document) ?? [];
        if (_generations.Count == 0)
        {
            return built;
        }
        HashSet<int> may = [.. built];
        foreach (Generation generation in _generations)
        {
            may.UnionWith(generation.Relative.Keys);
        }
        return MethodsWithPoints().Where(may.Contains);
    }

    private Dictionary<string, int[]>? _builtMethodsByDocument;

    /// <summary>The build's methods by each document they have a sequence point in, in token order, read once: an update's
    /// methods are its own, and its moves of lines keep a point in its document.</summary>
    private Dictionary<string, int[]> BuiltMethodsByDocument()
    {
        if (_builtMethodsByDocument is not null)
        {
            return _builtMethodsByDocument;
        }
        Dictionary<string, List<int>> byDocument = new(StringComparer.Ordinal);
        if (_pdbMetadataReader is { } reader)
        {
            Dictionary<DocumentHandle, string> names = [];
            foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
            {
                MethodDebugInformation info = reader.GetMethodDebugInformation(handle);
                if (info.SequencePointsBlob.IsNil)
                {
                    continue;
                }
                int token = MetadataTokens.GetToken(handle.ToDefinitionHandle());
                DocumentHandle last = default;
                foreach (SequencePoint point in info.GetSequencePoints())
                {
                    // As Read names a point's document.
                    DocumentHandle document = point.Document.IsNil ? info.Document : point.Document;
                    if (document.IsNil || document == last)
                    {
                        continue;
                    }
                    last = document;
                    if (!names.TryGetValue(document, out string? name))
                    {
                        names[document] = name = reader.GetString(reader.GetDocument(document).Name);
                    }
                    if (!byDocument.TryGetValue(name, out List<int>? methods))
                    {
                        byDocument[name] = methods = [];
                    }
                    if (methods.Count == 0 || methods[^1] != token)
                    {
                        methods.Add(token);
                    }
                }
            }
        }
        return _builtMethodsByDocument = byDocument.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    private string[]? _documents;

    /// <summary>The names of every document the module's code is in: the build's, and those updates brought.</summary>
    internal IReadOnlyList<string> DocumentNames()
    {
        if (_documents is not null)
        {
            return _documents;
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        List<string> ordered = [];
        foreach (MetadataReader reader in new[] { _pdbMetadataReader }.Concat(_generations.Select(g => g.PdbReader)).OfType<MetadataReader>())
        {
            foreach (DocumentHandle handle in reader.Documents)
            {
                string name = reader.GetString(reader.GetDocument(handle).Name);
                if (names.Add(name))
                {
                    ordered.Add(name);
                }
            }
        }
        return _documents = [.. ordered];
    }

    /// <summary>A method's IL at a version (the newest without one): the update's that last changed it, else the image's;
    /// null for a module with no image or a method with no body.</summary>
    internal byte[]? MethodIL(int methodToken, int? version = null)
    {
        int generation = GenerationOf(methodToken, version);
        if (generation > 0 && _generations[generation - 1].IL.TryGetValue(methodToken, out byte[]? updated))
        {
            return updated;
        }
        if (_peReader is null)
        {
            return null;
        }
        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (MetadataTokens.GetRowNumber(handle) > _imageMetadataReader.GetTableRowCount(TableIndex.MethodDef))
        {
            return null;
        }
        MethodDefinition method = _imageMetadataReader.GetMethodDefinition(handle);
        return method.RelativeVirtualAddress == 0 ? null : _peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
    }

    /// <summary>Whether an IL offset of a method at a version lies in one of its catch, finally, fault or filter blocks,
    /// where the runtime puts off an abort until the block is left. False where the body is not read: an update's, whose
    /// blocks are not kept, and a module's with no image.</summary>
    internal bool InHandler(int methodToken, int ilOffset, int? version = null)
    {
        if (GenerationOf(methodToken, version) > 0 || _peReader is null)
        {
            return false;
        }
        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (MetadataTokens.GetRowNumber(handle) > _imageMetadataReader.GetTableRowCount(TableIndex.MethodDef)
            || _imageMetadataReader.GetMethodDefinition(handle).RelativeVirtualAddress is not (> 0 and int rva))
        {
            return false;
        }
        foreach (ExceptionRegion region in _peReader.GetMethodBody(rva).ExceptionRegions)
        {
            int start = region.Kind is ExceptionRegionKind.Filter ? region.FilterOffset : region.HandlerOffset;
            if (ilOffset >= start && ilOffset < region.HandlerOffset + region.HandlerLength)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The string a method returns as a constant (its body <c>ldstr; ret</c>), found by its type's full name and
    /// its name: how the debugger reads what a runtime says of itself without running anything in it. Null when there is
    /// no such method, or it does something else.</summary>
    internal string? ConstantString(string typeFullName, string methodName)
    {
        int dot = typeFullName.LastIndexOf('.');
        string space = dot < 0 ? string.Empty : typeFullName[..dot];
        string name = typeFullName[(dot + 1)..];
        foreach (TypeDefinitionHandle typeHandle in _peMetadataReader.TypeDefinitions)
        {
            TypeDefinition type = _peMetadataReader.GetTypeDefinition(typeHandle);
            if (!_peMetadataReader.StringComparer.Equals(type.Name, name) || !_peMetadataReader.StringComparer.Equals(type.Namespace, space))
            {
                continue;
            }
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                if (!_peMetadataReader.StringComparer.Equals(_peMetadataReader.GetMethodDefinition(methodHandle).Name, methodName))
                {
                    continue;
                }
                byte[]? il = MethodIL(MetadataTokens.GetToken(methodHandle));
                // ldstr (0x72) and its token, then ret (0x2A).
                if (il is [0x72, _, _, _, 0x70, 0x2A])
                {
                    int token = BitConverter.ToInt32(il, 1);
                    return _peMetadataReader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF));
                }
                return null;
            }
        }
        return null;
    }
}
