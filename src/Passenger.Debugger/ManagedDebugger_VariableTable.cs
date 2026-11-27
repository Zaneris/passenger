using Passenger.Dap;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    public async Task<VariableTableResponse> ReadVariableTableAsync(VariableTableArguments request)
    {
        RequireStop();
        if (request.Start < 0 || request.Count is < 1 or > VariableTableArguments.PageLimit || request.TableIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (_variableManager.GetReference(request.VariablesReference) is not { ReferenceKind: StoredReferenceKind.StackVariable, ObjectValue: { } value } reference)
        {
            throw new InvalidOperationException("This value is no longer available at the current stop");
        }
        ICorDebugValue actual = value.UnwrapDebugValue();
        if (actual is ICorDebugArrayValue { Rank: 1 } array)
        {
            return await ReadCollectionTable(checked((int)array.Count), array.ExactType.FirstTypeParameter, reference, request);
        }
        if (actual is ICorDebugObjectValue obj)
        {
            if (StoredOf(obj) is { Kind: StoredKind.List } list)
            {
                return await ReadCollectionTable(list.Count, obj.ExactType.FirstTypeParameter, reference, request);
            }
            if (AdoType(obj.ExactType) is { } kind)
            {
                return await ReadAdoTable(value, kind, reference, request);
            }
        }
        throw new InvalidOperationException("The debugger cannot show this value as a table");
    }

    private async Task<VariableTableResponse> ReadCollectionTable(int total, ICorDebugType element,
        VariablesReference reference, VariableTableArguments request)
    {
        (ICorDebugType Type, IMetaDataImport Metadata, mdProperty Property, string Name)[] properties = [.. TableProperties(element)];
        int length = Math.Clamp(total - request.Start, 0, request.Count);
        List<IReadOnlyList<string>> rows = new(length);
        for (int row = request.Start; row < request.Start + length; row++)
        {
            ICorDebugValue item = ItemAt(row);
            if (properties.Length == 0)
            {
                rows.Add([await TableCell(item, reference.ThreadId, reference.FrameStackDepth)]);
                continue;
            }
            string[] cells = new string[properties.Length];
            if (item is ICorDebugReferenceValue { IsNull: true })
            {
                Array.Fill(cells, "null");
            }
            else
            {
                // The getters can run the program between cells. Hold a reference type, or reread a struct's slot.
                using EvaluationHandleScope handles = new();
                ICorDebugHandleValue? pinned = item is ICorDebugReferenceValue objectReference ? handles.CreateOwnedHandle(objectReference) : null;
                List<VariableInfo> found = [];
                for (int column = 0; column < properties.Length; column++)
                {
                    (ICorDebugType type, IMetaDataImport metadata, mdProperty property, _) = properties[column];
                    found.Clear();
                    await AddProperties([property], metadata, type, reference.ThreadId, reference.FrameStackDepth,
                        pinned ?? (column == 0 ? item : ItemAt(row)), found, table: true);
                    cells[column] = found.FirstOrDefault()?.Value ?? string.Empty;
                }
            }
            rows.Add(cells);
        }
        return new VariableTableResponse([], properties.Length == 0 ? ["Value"] : [.. properties.Select(property => property.Name)], total, rows);

        ICorDebugValue ItemAt(int row)
        {
            ICorDebugValue root = reference.ObjectValue!;
            ICorDebugArrayValue source = root.UnwrapDebugValue() is ICorDebugArrayValue direct ? direct
                : StoredOf(root.UnwrapDebugValueToObject())?.Items ?? throw new InvalidOperationException("The list's items are no longer available");
            return source.GetElementAtPosition(row);
        }
    }

    private static IEnumerable<(ICorDebugType Type, IMetaDataImport Metadata, mdProperty Property, string Name)> TableProperties(ICorDebugType element)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (ICorDebugType? current = element; current is { Type: CorElementType.CLASS or CorElementType.VALUETYPE }; current = current.Base)
        {
            ICorDebugClass type = current.Class;
            IMetaDataImport metadata = type.Module.GetMetaDataInterface<IMetaDataImport>();
            foreach (mdProperty property in metadata.EnumProperties(type.Token))
            {
                (string name, _, _, nint signature, int length, _, _, _, _, mdMethodDef getter, _, _) = metadata.GetPropertyProps(property);
                if (getter != 0 && property.IsPublic(metadata) && !property.IsStatic(metadata)
                    && !TakesParameters(signature, length) && BrowsableOf(metadata, property) is not System.Diagnostics.DebuggerBrowsableState.Never && seen.Add(name))
                {
                    yield return (current, metadata, property, name);
                }
            }
        }
    }

    private const string DataTableType = "System.Data.DataTable";
    private const string DataSetType = "System.Data.DataSet";

    private string? AdoType(ICorDebugType type)
    {
        for (ICorDebugType? current = type; current is not null; current = current.Base)
        {
            string name = GetCorDebugTypeFriendlyName(current);
            if (name is DataTableType or DataSetType &&
                FindModule(current.Class.Module) is { ModuleName: "System.Data.Common.dll" })
            {
                return name;
            }
        }
        return null;
    }

    private async Task<VariableTableResponse> ReadAdoTable(ICorDebugValue source, string kind, VariablesReference reference, VariableTableArguments request)
    {
        using EvaluationHandleScope held = new();
        bool wasExplicit = EvalStatus.Explicit;
        EvalStatus.Explicit = true;
        try
        {
            List<string> tables = [];
            ICorDebugValue table = source;
            if (kind == DataSetType)
            {
                ICorDebugValue children = await Read(source, "get_Tables");
                int count = await Count(children);
                if (count == 0)
                {
                    return new VariableTableResponse([], [], 0, []);
                }
                int selected = Math.Min(request.TableIndex, count - 1);
                for (int index = 0; index < count; index++)
                {
                    ICorDebugValue next = await Read(children, "get_Item", index);
                    tables.Add(await Name(next, "get_TableName"));
                    if (index == selected)
                    {
                        table = next;
                    }
                }
            }
            ICorDebugValue columnList = await Read(table, "get_Columns");
            int width = await Count(columnList);
            string[] columns = new string[width];
            for (int column = 0; column < width; column++)
            {
                columns[column] = await Name(await Read(columnList, "get_Item", column), "get_ColumnName");
            }
            ICorDebugValue rowList = await Read(table, "get_Rows");
            int total = await Count(rowList);
            int length = Math.Clamp(total - request.Start, 0, request.Count);
            List<IReadOnlyList<string>> rows = new(length);
            for (int offset = 0; offset < length; offset++)
            {
                ICorDebugValue row = await Read(rowList, "get_Item", request.Start + offset);
                try
                {
                    ICorDebugValue cells = await Read(row, "get_ItemArray");
                    string[] values = new string[width];
                    for (int column = 0; column < width; column++)
                    {
                        ICorDebugArrayValue array = (ICorDebugArrayValue)cells.UnwrapDebugValue();
                        values[column] = await TableCell(array.GetElementAtPosition(column), reference.ThreadId, reference.FrameStackDepth);
                    }
                    rows.Add(values);
                }
                catch (Exception error) when (error is EvalException || Failures.OfRuntime(error))
                {
                    rows.Add(width > 0 ? [error.Message] : []);
                }
            }
            return new VariableTableResponse(tables, columns, total, rows);
        }
        finally
        {
            EvalStatus.Explicit = wasExplicit;
        }

        async Task<int> Count(ICorDebugValue collection) =>
            BitConverter.ToInt32(((ICorDebugGenericValue)(await Read(collection, "get_Count")).UnwrapDebugValue()).GetValueAsBytes());

        async Task<string> Name(ICorDebugValue value, string getter) => ((ICorDebugStringValue)(await Read(value, getter)).UnwrapDebugValue()).String;

        async Task<ICorDebugValue> Read(ICorDebugValue value, string getter, int? index = null)
        {
            ICorDebugEval eval = _process!.GetThread(reference.ThreadId.Value).CreateEval();
            ICorDebugValue[] parameters = [];
            if (index is { } ordinal)
            {
                ICorDebugGenericValue argument = (ICorDebugGenericValue)eval.CreateValue(CorElementType.I4, null);
                argument.SetValue(BitConverter.GetBytes(ordinal));
                parameters = [argument];
            }
            ICorDebugType type = value.UnwrapDebugValue().ExactType;
            ICorDebugFunction function = FindMethodOnType(type, getter, parameters, searchStatic: false, idsEmpty: false)
                ?? throw new InvalidOperationException($"The runtime did not provide {getter}");
            ICorDebugType[] typeArguments = DeclaringTypeOf(type, function).TypeParameters;
            return held.Track(await eval.CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, function,
                typeArguments.Length, typeArguments, 1 + parameters.Length, [value, .. parameters], throwOnException: true))
                ?? throw new InvalidOperationException($"{getter} returned no value");
        }
    }

    private async Task<string> TableCell(ICorDebugValue value, ThreadId thread, FrameStackDepth frame)
    {
        FormattedValue shown = await FormatCorDebugValueAsync(ResolveCorDebugValue(value), thread, frame, escapeStringValue: true);
        return shown is { ResultIsError: false, FriendlyTypeName: "System.DBNull" } ? "DBNull" : shown.Value;
    }
}
