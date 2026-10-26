using Avalonia;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>A row of the Variables view: a local, a member, an element, a watch or an evaluated
/// result. One with a reference opens into its children, asked of the debugger the first time.</summary>
public sealed class VariableNode(string name, string value, string? type, int reference, int depth, VariableRole role) : Observable, ITreeRow
{
    /// <summary>The most of a value its row, the row's tooltip and a hover show: a string of megabytes laid out whole would
    /// hold the window for seconds at every stop. Copy Value and Set Value take all of it.</summary>
    public const int Longest = 1000;

    /// <summary>The names the debugger gives its ranges of elements, <see cref="IsRange"/>.</summary>
    private static readonly System.Text.RegularExpressions.Regex RangeName = new(@"^\[(\d+, )*(\d+\.\.\d+|\.\.\.)\]$");

    private bool isExpanded;
    private bool isEditing;

    /// <summary>The row this one opened from, if any.</summary>
    public VariableNode? Parent { get; init; }

    /// <summary>What the debugger says names the value in an expression, where its name does not: a call's value
    /// ("Count() returned") is $ReturnValue.</summary>
    public string? EvaluateName { get; init; }

    /// <summary>A value a call of the last step returned, listed at the top of the frame's values.</summary>
    public bool IsReturnValue => Depth == 0 && Role == VariableRole.Value && EvaluateName?.StartsWith("$ReturnValue", StringComparison.Ordinal) == true;

    /// <summary>The exception the program stopped at, $exception, or the failed binding, $bindingError.</summary>
    public bool IsException => Depth == 0 && Role == VariableRole.Value && Name is "$exception" or BindingStop.Name;

    /// <summary>A row the debugger adds among a value's members (an EF Core query's SQL), which no expression reaches.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>The protocol's lazy row: its value is read only as it is opened, since reading it runs code in the program,
    /// and the row read takes its place (<see cref="ReadAs"/>).</summary>
    public bool IsLazy { get; init; }

    /// <summary>A lazy row as it read once opened, in its place: opened again when the rows are read again.</summary>
    public bool WasRead { get; init; }

    /// <summary>The row this lazy one read as, once it has.</summary>
    public VariableNode? ReadAs { get; internal set; }

    /// <summary>The row of an EF Core query's SQL the debugger adds first among the query's members, read or not.</summary>
    public bool IsQuerySql => IsVirtual && Name == DapRows.QuerySql;

    /// <summary>An EF Core query's SQL as it read, whose text opens whole in a read-only editor tab.</summary>
    public bool IsQueryText => IsQuerySql && WasRead && Role != VariableRole.Error;

    /// <summary>The C# that reaches this value from the frame, for Set Value: a local's name, a member after its
    /// object's expression, an element's index after its array's. Null for a row no expression reaches (a
    /// scope, the static members, a result, a row the debugger adds).</summary>
    public string? Expression
    {
        get
        {
            if (Role == VariableRole.Watch)
            {
                return Name;
            }
            if (Role is VariableRole.Scope or VariableRole.Result or VariableRole.Error || IsVirtual)
            {
                return null;
            }
            if (Parent is null || Parent.Role == VariableRole.Scope)
            {
                return EvaluateName ?? Name;
            }
            if (IsRange)
            {
                return null;
            }
            string? owner = Parent.Reach;
            return owner is null ? null : Name.StartsWith('[') ? $"{owner}{Name}" : $"{owner}.{Name}";
        }
    }

    /// <summary>What the rows under this one reach their values from: its own expression, or for a row that only groups
    /// others the one above it's (the raw view lists the members of the object above it, a range the elements of the array
    /// above it, each by its whole index); null where no expression reaches them.</summary>
    private string? Reach => Name switch
    {
        "Static members" or "Results" => null,
        "Raw View" => Parent?.Reach,
        _ when IsRange => Parent?.Reach,
        _ => Expression,
    };

    /// <summary>One of the debugger's rows of an array's elements a hundred at a time ("[100..199]") or of a
    /// multi-dimensional array's row ("[1, ...]").</summary>
    private bool IsRange => Parent is not null && RangeName.IsMatch(Name);

    /// <summary>Its value is being typed over, in its row (Set Value, F2).</summary>
    public bool IsEditing
    {
        get => isEditing;
        set => Set(ref isEditing, value);
    }

    public string Name { get; } = name;
    public string Value { get; } = value;

    /// <summary>The value as its row shows it, on its one line (a query's SQL with each line break as a space), cut past
    /// <see cref="Longest"/>; its tooltip shows its lines (<see cref="Lines"/>).</summary>
    public string Shown => LineBreaks.Replace(Cut(Value), " ");

    /// <summary>The value with its lines, cut past <see cref="Longest"/>: its row's tooltip.</summary>
    public string Lines => Cut(Value);

    /// <summary>Line breaks with the blank lines and spaces round them, which a row shows as one space.</summary>
    private static readonly System.Text.RegularExpressions.Regex LineBreaks = new(@"[ \t]*[\r\n]\s*");

    /// <summary>A value as the debugger's rows and hovers show it, cut past <see cref="Longest"/>.</summary>
    public static string Cut(string value) => value.Length <= Longest ? value : value[..(Longest - 1)] + "…";
    public string? Type { get; } = type;
    public int Reference { get; } = reference;
    public int Depth { get; } = depth;
    public VariableRole Role { get; } = role;
    public bool HasChildren => Reference > 0;

    bool ITreeRow.Expands => HasChildren;

    /// <summary>The type in braces before the value, unless the value already says it.</summary>
    public string TypeLabel => Type is { Length: > 0 } t && Value != t && !Value.StartsWith(t, StringComparison.Ordinal) ? $"{{{t}}} " : string.Empty;

    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public List<VariableNode>? Children { get; set; }

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public static VariableNode From(DapVariable variable, int depth, VariableNode? parent = null) =>
        new(variable.Name, variable.Value, variable.Type, variable.VariablesReference, depth,
            variable.PresentationHint?.Attributes?.Contains("failedEvaluation") == true ? VariableRole.Error : VariableRole.Value)
        {
            Parent = parent,
            EvaluateName = variable.EvaluateName is { Length: > 0 } named && named != variable.Name ? named : null,
            IsVirtual = variable.PresentationHint?.Kind == "virtual",
            IsLazy = variable.PresentationHint?.Lazy == true && variable.VariablesReference > 0,
        };

    /// <summary>A lazy row as what it read: the debugger's one variable in its place, by its name, or why it read none.</summary>
    public static VariableNode Read(VariableNode lazy, DapVariable? read, string? refused) =>
        new(lazy.Name, read?.Value ?? refused ?? "The debugger read nothing", read?.Type, read?.VariablesReference ?? 0, lazy.Depth,
            read is not null && read.PresentationHint?.Attributes?.Contains("failedEvaluation") != true ? VariableRole.Value : VariableRole.Error)
        {
            Parent = lazy.Parent,
            EvaluateName = lazy.EvaluateName,
            IsVirtual = lazy.IsVirtual,
            WasRead = true,
        };
}

public enum VariableRole
{
    Value,
    Watch,
    Result,
    Error,

    /// <summary>A scope of the frame (its locals), when it has more than one.</summary>
    Scope,
}

/// <summary>The rows of the Variables view and of the debugger's hover card (<see cref="VariablesList"/>), flattened to
/// what is open so a virtualizing list draws only what is on screen; a node's children are fetched when it first opens.</summary>
public sealed class VariablesTree
{
    public TreeRows<VariableNode> Rows { get; } = [];

    /// <summary>Asks the debugger for a reference's children: the session's, while one runs.</summary>
    public Func<int, Task<IReadOnlyList<DapVariable>>>? Load { get; set; }

    public void Show(IEnumerable<VariableNode> top)
    {
        Rows.Clear();
        foreach (VariableNode node in top)
        {
            Rows.Add(node);
        }
    }

    /// <summary>Shows rows read again in place of these, those open opened again by the names that reach them: what the
    /// person had open is what they asked to see, each asked of the debugger once more.</summary>
    public async Task ShowAgain(IEnumerable<VariableNode> top)
    {
        HashSet<string> open = [.. Rows.Where(r => r.IsExpanded || r.WasRead).Select(PathOf)];
        Show(top);
        // Rows open in order, so a row's children, inserted after it, are reached after it.
        for (int at = 0; at < Rows.Count && open.Count > 0; at++)
        {
            VariableNode row = Rows[at];
            if (open.Remove(PathOf(row)))
            {
                await Expand(row);
            }
        }
    }

    /// <summary>The names from a top row down to this one, which say the same row in rows read again.</summary>
    public static string PathOf(VariableNode node) => node.Parent is { } parent ? $"{PathOf(parent)}\u001f{node.Name}" : node.Name;

    public Task Toggle(VariableNode node) => node.IsExpanded ? Collapse(node) : Expand(node);

    public async Task Expand(VariableNode node)
    {
        if (!node.HasChildren || node.IsExpanded || (node.Children is null && Load is null))
        {
            return;
        }
        if (node.IsLazy)
        {
            await Read(node);
            return;
        }
        if (node.Children is null)
        {
            // Held until the rows are in, so a picture taken after a row is opened shows what it opened into.
            using IDisposable held = Passenger.Core.BackgroundWork.Scope();
            try
            {
                node.Children = [.. (await Load!(node.Reference)).Select(v => VariableNode.From(v, node.Depth + 1, node))];
            }
            catch (DapException error)
            {
                node.Children = [new VariableNode("error", error.Reason, null, 0, node.Depth + 1, VariableRole.Error)];
            }
        }
        if (!Rows.Contains(node))
        {
            return;
        }
        node.IsExpanded = true;
        Rows.Open(node, TreeRows<VariableNode>.Shown(node.Children, child => child.Children));
    }

    /// <summary>Opens a lazy row: the debugger asked for its value, which runs code in the program, and the row it reads as
    /// put in its place, where it was (in its parent's children too), once.</summary>
    private async Task Read(VariableNode lazy)
    {
        if (lazy.ReadAs is not null || !reading.Add(lazy))
        {
            return;
        }
        VariableNode read;
        try
        {
            // Held until the row is in, so a picture taken after a row is opened shows what it read.
            using IDisposable held = Passenger.Core.BackgroundWork.Scope();
            try
            {
                read = VariableNode.Read(lazy, (await Load!(lazy.Reference)).FirstOrDefault(), null);
            }
            catch (DapException error)
            {
                read = VariableNode.Read(lazy, null, error.Reason);
            }
        }
        finally
        {
            reading.Remove(lazy);
        }
        lazy.ReadAs = read;
        if (lazy.Parent?.Children is { } siblings && siblings.IndexOf(lazy) is >= 0 and int among)
        {
            siblings[among] = read;
        }
        int at = Rows.IndexOf(lazy);
        if (at >= 0)
        {
            Rows[at] = read;
        }
    }

    /// <summary>The lazy rows being read now, each read once.</summary>
    private readonly HashSet<VariableNode> reading = [];

    public Task Collapse(VariableNode node)
    {
        if (!node.IsExpanded)
        {
            return Task.CompletedTask;
        }
        node.IsExpanded = false;
        Rows.Close(node);
        return Task.CompletedTask;
    }
}
