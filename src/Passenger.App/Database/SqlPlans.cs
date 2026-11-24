using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Passenger.App.Database;

/// <summary>A step of a statement's plan: what the database does (a scan, a join, a sort), what it does it to, what it
/// expects it to cost (with the steps under it, in the database's own units) and how many rows it expects, and the steps
/// it takes their rows from.</summary>
public sealed record PlanStep(string Operation, string Detail, double? Cost, double? Rows, IReadOnlyList<PlanStep> Steps)
{
    /// <summary>What the step costs alone: its cost less its steps'.</summary>
    public double? Own => Cost is { } cost ? Math.Max(0, cost - Steps.Sum(s => s.Cost ?? 0)) : null;
}

/// <summary>A statement's plan as each database explains it without running it: PostgreSQL's EXPLAIN (FORMAT JSON), MySQL's
/// and MariaDB's EXPLAIN FORMAT=JSON, SQL Server's SHOWPLAN_XML and SQLite's EXPLAIN QUERY PLAN (which says no cost or
/// rows), read into one tree of steps.</summary>
public static class SqlPlans
{
    /// <summary>What a database is asked to explain a statement: the statement read and planned, never run.</summary>
    public static string Asked(string statement, DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres => $"EXPLAIN (FORMAT JSON) {statement}",
        DatabaseKind.MySql => $"EXPLAIN FORMAT=JSON {statement}",
        DatabaseKind.Sqlite => $"EXPLAIN QUERY PLAN {statement}",
        _ => statement,
    };

    /// <summary>Whether a statement has a plan to ask for: a query or a change of rows, never an EXPLAIN already.</summary>
    public static bool Explains(string statement, DatabaseKind kind)
    {
        List<SqlText.Token> tokens = SqlText.Tokens(statement, kind);
        string first = tokens.Count == 0 || tokens[0].Quoted ? string.Empty : tokens[0].Word;
        return first is "SELECT" or "WITH" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "VALUES" or "TABLE" or "REPLACE" or "(";
    }

    /// <summary>PostgreSQL's plan: its JSON's Plan, each step's Plans under it.</summary>
    public static PlanStep FromPostgres(string json)
    {
        using JsonDocument read = JsonDocument.Parse(json);
        JsonElement plan = read.RootElement.ValueKind == JsonValueKind.Array && read.RootElement.GetArrayLength() > 0 ? read.RootElement[0] : read.RootElement;
        return Postgres(Passenger.Core.JsonMembers.Value(plan, "Plan") ?? plan);

        static PlanStep Postgres(JsonElement step)
        {
            string operation = Text(step, "Node Type");
            string join = Text(step, "Join Type");
            string strategy = Text(step, "Strategy");
            string detail = string.Join(" ", new[]
            {
                Text(step, "Relation Name") is { Length: > 0 } relation ? "on " + relation + (Text(step, "Alias") is { Length: > 0 } alias && alias != relation ? " " + alias : string.Empty) : string.Empty,
                Text(step, "Index Name") is { Length: > 0 } index ? "using " + index : string.Empty,
                Text(step, "Hash Cond") is { Length: > 0 } hash ? hash : Text(step, "Index Cond") is { Length: > 0 } cond ? cond : string.Empty,
                Text(step, "Filter") is { Length: > 0 } filter ? "filter " + filter : string.Empty,
            }.Where(w => w.Length > 0));
            List<PlanStep> steps = Passenger.Core.JsonMembers.Value(step, "Plans") is { ValueKind: JsonValueKind.Array } plans ? [.. plans.EnumerateArray().Select(Postgres)] : [];
            return new PlanStep(string.Join(" ", new[] { strategy is "Sorted" or "Hashed" or "Plain" && operation == "Aggregate" ? strategy : string.Empty, join is { Length: > 0 } && operation.Contains("Join", StringComparison.Ordinal) || operation is "Nested Loop" ? join : string.Empty, operation }.Where(w => w.Length > 0)),
                detail, Number(step, "Total Cost"), Number(step, "Plan Rows"), steps);
        }
    }

    /// <summary>MySQL's and MariaDB's plan: each query block, the tables it reads (their access, rows and cost) and the
    /// operations over them (a nested loop, an ordering, a grouping, a subquery).</summary>
    public static PlanStep FromMySql(string json)
    {
        using JsonDocument read = JsonDocument.Parse(json);
        return MySql("query_block", Passenger.Core.JsonMembers.Value(read.RootElement, "query_block") ?? read.RootElement);

        static PlanStep MySql(string name, JsonElement step)
        {
            List<PlanStep> steps = [];
            foreach (JsonProperty part in step.EnumerateObject())
            {
                if (part.Value.ValueKind == JsonValueKind.Object && part.Name is not ("cost_info" or "used_columns" or "possible_keys" or "attached_condition"))
                {
                    steps.Add(MySql(part.Name, part.Value));
                }
                else if (part.Value.ValueKind == JsonValueKind.Array && part.Value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object))
                {
                    steps.AddRange(part.Value.EnumerateArray().Select(e => e.EnumerateObject().ToList() is [var only] && only.Value.ValueKind == JsonValueKind.Object
                        ? MySql(only.Name, only.Value) : MySql(part.Name, e)));
                }
            }
            // A table's step stands for the table itself, as its access and keys say.
            string table = Text(step, "table_name");
            string detail = string.Join(" ", new[]
            {
                table.Length > 0 ? "on " + table : string.Empty,
                Text(step, "key") is { Length: > 0 } key ? "using " + key : string.Empty,
                Text(step, "attached_condition") is { Length: > 0 } condition ? "filter " + condition : string.Empty,
            }.Where(w => w.Length > 0));
            double? cost = Number(step, "cost") ?? (Passenger.Core.JsonMembers.Value(step, "cost_info") is { ValueKind: JsonValueKind.Object } info
                ? Number(info, "query_cost") ?? Number(info, "prefix_cost") ?? Number(info, "read_cost") : null);
            double? rows = Number(step, "rows") ?? Number(step, "rows_produced_per_join") ?? Number(step, "rows_examined_per_scan");
            string operation = table.Length > 0 ? $"{Text(step, "access_type") switch { "ALL" => "full scan", "index" => "index scan", "range" => "range scan", "ref" or "eq_ref" => "key lookup", "const" or "system" => "one row", var other => other }}"
                : name.Replace('_', ' ');
            return new PlanStep(operation, detail, cost, rows, steps);
        }
    }

    /// <summary>SQL Server's plan: each RelOp of its showplan, its physical and logical operation, estimated subtree cost
    /// and rows, the RelOps nearest under it its steps.</summary>
    public static PlanStep FromSqlServer(string xml)
    {
        XDocument plan = XDocument.Parse(xml);
        XElement? top = plan.Descendants().FirstOrDefault(e => e.Name.LocalName == "RelOp");
        if (top is null)
        {
            XElement? statement = plan.Descendants().FirstOrDefault(e => e.Name.LocalName == "StmtSimple");
            return new PlanStep(statement?.Attribute("StatementType")?.Value ?? "statement", string.Empty, Number(statement?.Attribute("StatementSubTreeCost")?.Value), null, []);
        }
        return SqlServer(top);

        static PlanStep SqlServer(XElement step)
        {
            string physical = step.Attribute("PhysicalOp")?.Value ?? string.Empty;
            string logical = step.Attribute("LogicalOp")?.Value ?? string.Empty;
            XElement? obj = step.Elements().SelectMany(e => e.Elements()).FirstOrDefault(e => e.Name.LocalName == "Object");
            string detail = string.Join(" ", new[]
            {
                logical.Length > 0 && logical != physical ? logical.ToLowerInvariant() : string.Empty,
                obj?.Attribute("Table")?.Value is { } table ? "on " + table.Trim('[', ']') : string.Empty,
                obj?.Attribute("Index")?.Value is { } index ? "using " + index.Trim('[', ']') : string.Empty,
            }.Where(w => w.Length > 0));
            return new PlanStep(physical, detail, Number(step.Attribute("EstimatedTotalSubtreeCost")?.Value), Number(step.Attribute("EstimateRows")?.Value),
                [.. Nearest(step).Select(SqlServer)]);
        }

        static IEnumerable<XElement> Nearest(XElement step)
        {
            foreach (XElement child in step.Elements())
            {
                if (child.Name.LocalName == "RelOp")
                {
                    yield return child;
                }
                else
                {
                    foreach (XElement under in Nearest(child))
                    {
                        yield return under;
                    }
                }
            }
        }
    }

    /// <summary>SQLite's plan: its rows' details, each under the row its parent names.</summary>
    public static PlanStep FromSqlite(IReadOnlyList<(long Id, long Parent, string Detail)> rows)
    {
        return new PlanStep("query plan", string.Empty, null, null, Under(0));

        List<PlanStep> Under(long parent) => [.. rows.Where(r => r.Parent == parent && r.Id != parent).Select(r => new PlanStep(r.Detail, string.Empty, null, null, Under(r.Id)))];
    }

    /// <summary>The step that costs the most alone, where the plan says costs; null where it says none.</summary>
    public static PlanStep? Costliest(PlanStep plan) => Each(plan).Where(s => s.Own is not null).MaxBy(s => s.Own);

    /// <summary>Every step of a plan, each before those under it.</summary>
    public static IEnumerable<PlanStep> Each(PlanStep plan) => [plan, .. plan.Steps.SelectMany(Each)];

    private static string Text(JsonElement step, string name) => Passenger.Core.JsonMembers.Value(step, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? string.Empty : string.Empty;

    private static double? Number(JsonElement step, string name) => Passenger.Core.JsonMembers.Value(step, name) switch
    {
        { ValueKind: JsonValueKind.Number } number => number.GetDouble(),
        { ValueKind: JsonValueKind.String } text => Number(text.GetString()),
        _ => null,
    };

    private static double? Number(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
}
