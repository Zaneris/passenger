using System.Runtime.InteropServices;
using Passenger.Dap;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;
using Passenger.Debugger.PresentationHintModels;

namespace Passenger.Debugger;

/// <summary>An EF Core query's SQL: a query whose type is EF Core's own (a DbSet, or what its provider's operators made
/// of one) lists first a row of the SQL it would run, given as the protocol's lazy row, so the program runs
/// ToQueryString() only as the row is opened, through the debugger's own evaluation and its time limit; what keeps it
/// from one (a query its provider cannot translate, an evaluation refused or past its time) is said as the row's
/// value.</summary>
public partial class ManagedDebugger
{
    private const string EfCoreModule = "Microsoft.EntityFrameworkCore.dll";
    private const string QueryableExtensions = "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions";

    /// <summary>EF Core's module, when a value of this type is one of its queries: an IQueryable whose type, or a type it
    /// derives from, is EF Core's; null for any other, whose provider is not EF Core's.</summary>
    private ICorDebugModule? EfQueryModule(ICorDebugType type)
    {
        if (!Implements(type, static name => name is "System.Linq.IQueryable"))
        {
            return null;
        }
        for (ICorDebugType? current = type; current is not null; current = current.Base)
        {
            if (FindModule(current.Class.Module) is { ModuleName: EfCoreModule } ef)
            {
                return ef.Module;
            }
        }
        return null;
    }

    /// <summary>The SQL row a query's members begin with, unread until it is opened.</summary>
    private void AddQuerySqlRow(VariablesReference query, List<VariableInfo> result) => result.Add(new VariableInfo
    {
        Name = DapRows.QuerySql,
        Value = DapRows.QuerySqlUnread,
        Type = null,
        PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Virtual, Lazy = true },
        VariablesReference = _variableManager.CreateReference(query with { ReferenceKind = StoredReferenceKind.QuerySql, DebuggerProxyInstance = null }),
    });

    /// <summary>The SQL row opened: the query's ToQueryString(), run on the stopped thread as an evaluation asked for by name
    /// (opening the row asks for it), else why it gave none.</summary>
    private async Task<VariableInfo> ReadQuerySql(VariablesReference query)
    {
        try
        {
            return new VariableInfo { Name = DapRows.QuerySql, Value = await QuerySqlAsync(query), Type = null, VariablesReference = 0 };
        }
        catch (Exception error) when (error is EvalException || Failures.OfRuntime(error))
        {
            return FailedRow(DapRows.QuerySql, error.Message);
        }
    }

    private async Task<string> QuerySqlAsync(VariablesReference query)
    {
        ICorDebugValue value = query.ObjectValue ?? throw new InvalidOperationException("The query is no longer there to read");
        ICorDebugModule ef = EfQueryModule(value.UnwrapDebugValueToObject().ExactType)
            ?? throw new InvalidOperationException("This is not an EF Core query");
        IMetaDataImport metadata = ef.GetMetaDataInterface<IMetaDataImport>();
        mdMethodDef toQueryString;
        try
        {
            mdTypeDef extensions = metadata.FindTypeDefByNameOrNull(QueryableExtensions, mdToken.Nil)
                ?? throw new EvalException("This EF Core has no ToQueryString(), which came with EF Core 5");
            toQueryString = metadata.FindMethod(extensions, "ToQueryString", 0, 0);
        }
        catch (COMException)
        {
            throw new EvalException("This EF Core has no ToQueryString(), which came with EF Core 5");
        }

        ICorDebugEval eval = _process!.GetThread(query.ThreadId.Value).CreateEval();
        bool wasExplicit = EvalStatus.Explicit;
        EvalStatus.Explicit = true;
        ICorDebugValue? returned;
        try
        {
            returned = await eval.CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, ef.GetFunctionFromToken(toQueryString), 0, [], 1, [value]);
        }
        finally
        {
            EvalStatus.Explicit = wasExplicit;
        }
        try
        {
            return returned?.UnwrapDebugValue() switch
            {
                ICorDebugStringValue sql => sql.String,
                // A string or nothing returns; anything else is the exception it threw, a query its provider cannot translate.
                ICorDebugObjectValue thrown => throw new EvalException(Threw(thrown, query)),
                _ => throw new EvalException("ToQueryString() returned nothing"),
            };
        }
        finally
        {
            if (returned is ICorDebugHandleValue handle)
            {
                handle.TryDispose();
            }
        }
    }

    /// <summary>What an exception ToQueryString() threw says, from the message it was made with, read with no more code run.</summary>
    private string Threw(ICorDebugObjectValue thrown, VariablesReference query)
    {
        string type = GetCorDebugTypeFriendlyName(thrown.ExactType);
        type = type[(type.LastIndexOf('.') + 1)..];
        ICorDebugValue? message = thrown.GetClassFieldValue(GetIlFrameForThreadIdAndStackDepth(query.ThreadId, query.FrameStackDepth), "_message");
        return message?.UnwrapDebugValue() is ICorDebugStringValue { String: { Length: > 0 } said }
            ? $"ToQueryString() threw {type}: {said}"
            : $"ToQueryString() threw {type}";
    }
}
