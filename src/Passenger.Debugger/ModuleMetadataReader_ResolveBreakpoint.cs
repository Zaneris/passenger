using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.Debugger;
public partial class ModuleMetadataReader
{
    /// <summary>Each source file's best document, as worked out, and whether the file was on disk then: a breakpoint's file
    /// is asked of every module with symbols at each bind, and of all of them again as each such module loads while it
    /// waits, each asking a match of every document. Forgotten as an update brings documents.</summary>
    private readonly Dictionary<string, (string? Document, int Score, bool OnDisk)> _bestDocuments = new(StringComparer.Ordinal);

    /// <summary>The name of the module's document that names a source file best, by the PDB's name of it (the build's, or
    /// one an update brought). A match worked out while the file was not on disk stands only until it is, when its
    /// canonical name may match a document no name of it did.</summary>
    private (string? Document, int Score) BestDocument(string sourceFilePath)
    {
        bool worked = _bestDocuments.TryGetValue(sourceFilePath, out (string? Document, int Score, bool OnDisk) known);
        if (worked && known.OnDisk)
        {
            return (known.Document, known.Score);
        }
        bool onDisk = SourcePaths.OnDisk(sourceFilePath);
        if (worked && !onDisk)
        {
            return (known.Document, known.Score);
        }
        (string? Document, int Score) best = (null, 0);
        foreach (string name in DocumentNames())
        {
            int score = SourcePaths.Match(sourceFilePath, name);
            if (score > best.Score)
            {
                best = (name, score);
            }
        }
        _bestDocuments[sourceFilePath] = (best.Document, best.Score, onDisk);
        return best;
    }

    private sealed record MethodCandidate(
    int Token,
    Point FirstSp,      // smallest End where EndLine >= line (next-line snapping)
    Point LastSp,       // largest End where EndLine >= line
    Point? CoveringSp,  // latest Start where the requested source position is covered
    string DocPath)
    {
        public bool CoversLine => CoveringSp.HasValue;
    }

    /// <summary>How well this module's symbols name a source file (<see cref="SourcePaths.Match"/>):
    /// 0 when no document of its is that file.</summary>
    public int DocumentMatch(string sourceFilePath) => BestDocument(sourceFilePath).Score;

    /// <summary>Where a line, or a place given a column, of a source file binds: with no column, the statement that starts
    /// first on it, a lambda's body only where it starts the line; with <paramref name="inMethod"/>, a place in that method
    /// alone; with <paramref name="running"/>, that method's code at a version (a frame's) where it is, the newest
    /// elsewhere.</summary>
    public ResolvedBreakpoint? ResolveBreakpoint(string sourceFilePath, int line, int? column = null, int? inMethod = null, (int Method, int? Version)? running = null)
    {
        if (_pdbMetadataReader is null || BestDocument(sourceFilePath).Document is not { } document)
        {
            return null;
        }

        List<MethodCandidate> candidates = MethodsWithPoints(document)
            .Where(token => inMethod is null || token == inMethod)
            .Select(token => CollectCandidate(token, running is { } code && code.Method == token ? code.Version : null, document, line, column))
            .OfType<MethodCandidate>()
            .ToList();
        bool rewritten = IsRewrittenDocument(document);
        if (rewritten && column is int requested && !candidates.Any(c => c.CoversLine) && UnpointedAttribute(document, new LineCol(line, requested)) is { } setter)
        {
            ResolvedBreakpoint attributed = new(setter.Token, setter.Point.Offset, line, line, requested, requested, document) { Attributed = true };
            return attributed with { BindOffset = JitBoundaryAtOrBefore(attributed.MethodToken, attributed.ILOffset) };
        }
        ResolvedBreakpoint? resolved = Resolve(candidates, rewritten, column is null);
        return resolved is not null && rewritten ? resolved with { BindOffset = JitBoundaryAtOrBefore(resolved.MethodToken, resolved.ILOffset) } : resolved;
    }

    /// <summary>Where the setter of an XAML attribute that XamlIl gave no point of its own runs: XamlX keeps one point
    /// pending, so of a tag's attributes about every other one has none, its setter run under its element's point again,
    /// which comes in the IL right after the point of the attribute before it. That point is the one written last before the
    /// attribute (a template's builder's over the template's own, which begins its method); the point after it in the IL
    /// when that goes back to the element, else it itself. Null when nothing is written before the attribute.</summary>
    private (int Token, Point Point)? UnpointedAttribute(string document, LineCol requested)
    {
        (int Token, Point Point)? prior = null;
        foreach (int token in MethodsWithPoints(document))
        {
            foreach (Point point in Points(token))
            {
                LineCol at = point.Start();
                if (point.IsHidden || point.Document != document || at > requested)
                {
                    continue;
                }
                if (prior is not { } best || at > best.Point.Start() || (at == best.Point.Start() && point.Offset < best.Point.Offset))
                {
                    prior = (token, point);
                }
            }
        }
        if (prior is not { } found)
        {
            return null;
        }
        Point? next = VisibleSequencePoints(found.Token).FirstOrDefault(p => p.Offset > found.Point.Offset) is { IsHidden: false, Document: not null } after && after.Offset > found.Point.Offset ? after : null;
        return next is { } back && back.Start() < found.Point.Start() ? (found.Token, back) : found;
    }

    /// <summary>Whether a document's sequence points were written by a rewriter of the compiled IL rather than a compiler:
    /// Avalonia's XAML compiler (XamlIl, through Cecil) names no language for the .axaml files it maps its code to, and
    /// places a point at each element and attribute, mid-expression, where C#'s compiler places them where the stack is
    /// empty. A document only an update brings is C#'s.</summary>
    private bool IsRewrittenDocument(string document)
    {
        MetadataReader reader = _pdbMetadataReader!;
        foreach (DocumentHandle handle in reader.Documents)
        {
            Document found = reader.GetDocument(handle);
            if (reader.GetString(found.Name) == document)
            {
                return found.Language.IsNil || reader.GetGuid(found.Language) == Guid.Empty;
            }
        }
        return false;
    }

    /// <summary>The nearest IL offset at or before <paramref name="offset"/> that CoreCLR's JIT maps to native code for a
    /// debugger: the method's start, a nop, or the instruction after a call (its call-site boundaries; it also maps where the
    /// stack is empty, which needs no move). A breakpoint anywhere else binds and is never hit. The instructions passed over
    /// call nothing, so the program stops in the same state, only those pushes before. Never moved back over a branch's
    /// target or a jump away, where another path would miss it; then the offset is kept as it is.</summary>
    internal int JitBoundaryAtOrBefore(int methodToken, int offset)
    {
        if (_peReader is null)
        {
            return offset;
        }
        if (MethodIL(methodToken) is not { } il)
        {
            return offset;
        }
        IReadOnlyList<CilInstruction> instructions;
        try
        {
            instructions = CilInstructionDecoder.Decode(il);
        }
        catch (NotSupportedException)
        {
            return offset;
        }
        HashSet<int> targets = [];
        foreach (CilInstruction instruction in instructions)
        {
            switch (instruction.Operand)
            {
                case int target when instruction.OpCode.OperandType is System.Reflection.Emit.OperandType.InlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                    targets.Add(target);
                    break;
                case int[] cases:
                    targets.UnionWith(cases);
                    break;
            }
        }
        int at = instructions.Count - 1;
        while (at >= 0 && instructions[at].Offset > offset)
        {
            at--;
        }
        if (at < 0 || instructions[at].Offset != offset)
        {
            return offset;
        }
        for (int i = at; i >= 0; i--)
        {
            CilInstruction here = instructions[i];
            if (i == 0 || here.Code == ILOpCode.Nop || instructions[i - 1].Code is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Calli)
            {
                return here.Offset;
            }
            if (targets.Contains(here.Offset) || instructions[i - 1].Code is ILOpCode.Br or ILOpCode.Br_s or ILOpCode.Leave or ILOpCode.Leave_s
                or ILOpCode.Ret or ILOpCode.Throw or ILOpCode.Rethrow or ILOpCode.Endfinally or ILOpCode.Jmp)
            {
                return offset;
            }
        }
        return offset;
    }

    private ResolvedBreakpoint? Resolve(List<MethodCandidate> candidates, bool rewritten, bool wholeLine)
    {
        List<MethodCandidate> covering = candidates.Where(c => c.CoversLine).ToList();

        if (covering.Count == 0)
        {
            return candidates
                .OrderBy(c => c.FirstSp.Start())
                .Select(c => NewResolvedBreakpoint(c.Token, c.FirstSp, c.DocPath))
                .FirstOrDefault();
        }

        if (covering.Count == 1)
        {
            return NewResolvedBreakpoint(covering[0].Token, covering[0].CoveringSp!.Value, covering[0].DocPath);
        }

        // The candidates whose covering point starts latest: the innermost lambda, not the method spanning its body.
        LineCol maxCoverStart = covering.Max(c => c.CoveringSp!.Value.Start());
        if (wholeLine && !rewritten)
        {
            // The line's first statement there, so a lambda written after it on the line is not taken for the line.
            int latestLine = maxCoverStart.Line;
            maxCoverStart = covering.Select(c => c.CoveringSp!.Value.Start()).Where(start => start.Line == latestLine).Min();
        }
        List<MethodCandidate> primary = covering.Where(c => c.CoveringSp!.Value.Start() == maxCoverStart).ToList();

        if (primary.Count == 1)
        {
            return NewResolvedBreakpoint(primary[0].Token, primary[0].CoveringSp!.Value, primary[0].DocPath);
        }

        if (rewritten)
        {
            // A template's root is mapped where it is made and in its content's builder, which runs per control made.
            MethodCandidate builder = primary.MinBy(c => c.CoveringSp!.Value.Offset)!;
            return NewResolvedBreakpoint(builder.Token, builder.CoveringSp!.Value, builder.DocPath);
        }

        // https://github.com/Samsung/netcoredbg/blob/8b8b22200fecdb1aec5f47af63215462d8c79a4b/src/managed/SymbolReader.cs#L801-L817

        // Methods whose covering points start at one place (a lambda on its call's line): netcoredbg's two cases.
        List<MethodCandidate> sorted = primary.OrderBy(c => c.FirstSp.Start()).ToList();
        MethodCandidate outer = sorted[^2];
        MethodCandidate nested = sorted[^1];

        // Case 1: lambda range fully inside outer's first SP → BP is on the call-site line
        if (nested.FirstSp.Start() > outer.FirstSp.Start() &&
            nested.LastSp.End() < outer.FirstSp.End())
        {
            return NewResolvedBreakpoint(outer.Token, outer.CoveringSp!.Value, outer.DocPath);
        }

        // Case 2: outer's first SP ends after nested's -> BP is closer to the lambda body
        if (outer.FirstSp.End() > nested.FirstSp.End())
        {
            return NewResolvedBreakpoint(nested.Token, nested.CoveringSp!.Value, nested.DocPath);
        }

        return NewResolvedBreakpoint(outer.Token, outer.CoveringSp!.Value, outer.DocPath);
    }

    private MethodCandidate? CollectCandidate(int token, int? version, string document, int line, int? column)
    {
        Point? firstSP = null;
        Point? lastSP = null;
        Point? coveringSP = null;
        string? docPath = null;

        foreach (Point sp in Points(token, version))
        {
            if (sp.IsHidden)
            {
                continue;
            }

            if (sp.Document != document || sp.IsBeforeRequestedPosition(line, column))
            {
                continue;
            }

            docPath ??= document;

            if (firstSP is null || sp.End() < firstSP.Value.End())
            {
                firstSP = sp;
            }

            if (lastSP is null || sp.End() > lastSP.Value.End())
            {
                lastSP = sp;
            }

            if (sp.CoversRequestedPosition(line, column) && sp.ShouldReplaceCoveringSequencePoint(coveringSP, column))
            {
                coveringSP = sp;
            }
        }

        if (firstSP is null)
        {
            return null;
        }

        return new MethodCandidate(token, firstSP.Value, lastSP!.Value, coveringSP, docPath!);
    }
    private static ResolvedBreakpoint NewResolvedBreakpoint(int token, Point sp, string docPath) =>
        new(token, sp.Offset, sp.StartLine, sp.EndLine, sp.StartColumn, sp.EndColumn, docPath) { Rewritten = sp.Rewritten };
}

internal readonly record struct LineCol(int Line, int Column) : IComparable<LineCol>
{
    public int CompareTo(LineCol other)
    {
        int c = Line.CompareTo(other.Line);
        return c != 0 ? c : Column.CompareTo(other.Column);
    }
    public static bool operator <(LineCol a, LineCol b) => a.CompareTo(b) < 0;
    public static bool operator >(LineCol a, LineCol b) => a.CompareTo(b) > 0;
    public static bool operator <=(LineCol a, LineCol b) => a.CompareTo(b) <= 0;
    public static bool operator >=(LineCol a, LineCol b) => a.CompareTo(b) >= 0;
}

file static class LineColExtensions
{
    public static LineCol Start(this ModuleMetadataReader.Point sp) => new(sp.StartLine, sp.StartColumn);
    public static LineCol End(this ModuleMetadataReader.Point sp) => new(sp.EndLine, sp.EndColumn);
}
file static class SequencePointExtensions
{
    public static bool IsBeforeRequestedPosition(this ModuleMetadataReader.Point sp, int requestedLine, int? requestedColumn)
    {
        return requestedColumn is null
            ? sp.EndLine < requestedLine
            : sp.End() < new LineCol(requestedLine, requestedColumn.Value);
    }

    public static bool CoversRequestedPosition(this ModuleMetadataReader.Point sp, int requestedLine, int? requestedColumn)
    {
        return requestedColumn is null
            ? sp.StartLine <= requestedLine
            : new LineCol(requestedLine, requestedColumn.Value) is var requestedPosition && sp.Start() <= requestedPosition && requestedPosition <= sp.End();
    }

    public static bool ShouldReplaceCoveringSequencePoint(this ModuleMetadataReader.Point sp, ModuleMetadataReader.Point? coveringSp, int? requestedColumn)
    {
        if (coveringSp is null)
        {
            return true;
        }

        return requestedColumn is null
            ? sp.StartLine > coveringSp.Value.StartLine ||
              (sp.StartLine == coveringSp.Value.StartLine && sp.StartColumn < coveringSp.Value.StartColumn)
            : sp.Start() > coveringSp.Value.Start();
    }
}
