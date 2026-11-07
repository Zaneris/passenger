using Passenger.Dap;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

/// <summary>The type presentations the client gives (<see cref="DapTypePresentation"/>), kept by the type's full name: a
/// library's type that carries no debugger attributes shown as a [DebuggerDisplay] on it would show it, and one with rows
/// opened to the members the program's own types deriving from it declare, those marked first, then its rows, the rest
/// under Raw View.</summary>
public partial class ManagedDebugger
{
    private Dictionary<string, DapTypePresentation[]> _presentations = new(StringComparer.Ordinal);

    /// <summary>Takes the client's presentations for this session, before it launches or attaches.</summary>
    public void Present(IReadOnlyList<DapTypePresentation> presentations) =>
        _presentations = presentations.GroupBy(p => p.Type, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

    /// <summary>The presentation given for a class of the full name its metadata gives, declared in the assembly it names.</summary>
    private DapTypePresentation? PresentationOf(ICorDebugClass corClass, string name) =>
        _presentations.Count > 0 && _presentations.TryGetValue(name, out DapTypePresentation[]? given)
        && AssemblyNameOf(GetModuleInfoForModule(corClass.Module)) is { } assembly
            ? given.LastOrDefault(p => string.Equals(p.Assembly, assembly, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>The presentation with rows that opens a value of this type, and how many of its types, from its own up, are
    /// declared outside the presented one's assembly (the program's own, whose members are listed); none where a type below
    /// it carries a [DebuggerDisplay] of its own, or an assembly declares one for it.</summary>
    private (DapTypePresentation Presentation, int Own)? OpenedBy(ICorDebugType type)
    {
        if (_presentations.Count == 0)
        {
            return null;
        }
        List<string?> assemblies = [];
        for (ICorDebugType? each = type; each is not null; each = each.Base)
        {
            ICorDebugClass corClass = each.Class;
            IMetaDataImport metadata = corClass.Module.GetMetaDataInterface<IMetaDataImport>();
            string name = metadata.GetTypeDefProps(corClass.Token).szTypeDef;
            if (name is "System.Object" or "System.ValueType"
                || metadata.TryGetCustomAttributeByName(corClass.Token, "System.Diagnostics.DebuggerDisplayAttribute", out _, out _) is Cor.S_OK
                || TargetedAt(each).Any(t => t.Display is not null))
            {
                return null;
            }
            if (PresentationOf(corClass, name) is { } given)
            {
                int own = assemblies.TakeWhile(a => !string.Equals(a, given.Assembly, StringComparison.OrdinalIgnoreCase)).Count();
                return given.Rows is null ? null : (given, own);
            }
            assemblies.Add(AssemblyNameOf(GetModuleInfoForModule(corClass.Module)));
        }
        return null;
    }

    /// <summary>A presented value's rows: the members of its <paramref name="own"/> types (<see cref="OpenedBy"/>), those
    /// carrying the marking attribute first, then each of its rows evaluated on the value (one that fails saying why), then
    /// Raw View.</summary>
    private async Task AddPresentedRows(VariablesReference opened, ICorDebugObjectValue objectValue, DapTypePresentation presentation, int own, List<VariableInfo> result)
    {
        // Read before any getter runs, which leaves the object read from the reference unusable.
        ICorDebugType type = objectValue.ExactType;
        HashSet<string> marked = presentation.First is { } attribute ? MarkedMembers(type, own, attribute) : [];
        List<VariableInfo> members = [];
        if (own > 0)
        {
            await AddMembers(opened.ObjectValue!, type, opened.ThreadId, opened.FrameStackDepth, members, declared: opened.Declared, owner: opened.Expression, levels: own);
        }
        result.AddRange(members.Where(m => marked.Contains(m.Member ?? m.Name)));
        result.AddRange(members.Where(m => !marked.Contains(m.Member ?? m.Name)));
        foreach (DapPresentedRow row in presentation.Rows ?? [])
        {
            await WithFailureHandling(result, row.Name, async () =>
            {
                CompiledExpressionEvaluationContext context = new(_process!.GetThread(opened.ThreadId.Value), opened.ThreadId, opened.FrameStackDepth, opened.ObjectValue);
                using EvaluationResult evaluated = await _expressionEvaluator!.Evaluate(row.Expression, context);
                if (evaluated.Error is not null || evaluated.Value is null)
                {
                    result.Add(FailedRow(row.Name, evaluated.Error ?? "no value"));
                    return;
                }
                VariableInfo shown = await ValueRow(row.Name, RowExpressions.Called(opened.Expression, row.Expression), evaluated.Value, opened.ThreadId, opened.FrameStackDepth, evaluated.Declared);
                if (shown.VariablesReference != 0)
                {
                    evaluated.RelinquishResultHandleOwnership();
                }
                result.Add(shown);
            });
        }
        result.Add(GroupRow(DapRows.RawView, opened with { ReferenceKind = StoredReferenceKind.RawView }));
    }

    /// <summary>The names of the fields and properties a type and the types above it, as far as <paramref name="levels"/>
    /// go, declare with an attribute of this full name.</summary>
    private static HashSet<string> MarkedMembers(ICorDebugType type, int levels, string attribute)
    {
        HashSet<string> marked = new(StringComparer.Ordinal);
        ICorDebugType? each = type;
        for (int level = 0; level < levels && each is not null; level++, each = each.Base)
        {
            IMetaDataImport metadata = each.Class.Module.GetMetaDataInterface<IMetaDataImport>();
            foreach (mdFieldDef field in metadata.EnumFields(each.Class.Token))
            {
                if (metadata.TryGetCustomAttributeByName(field, attribute, out _, out _) is Cor.S_OK)
                {
                    marked.Add(metadata.GetFieldProps(field).szField);
                }
            }
            foreach (mdProperty property in metadata.EnumProperties(each.Class.Token))
            {
                if (metadata.TryGetCustomAttributeByName(property, attribute, out _, out _) is Cor.S_OK)
                {
                    marked.Add(metadata.GetPropertyProps(property).szProperty);
                }
            }
        }
        return marked;
    }
}
