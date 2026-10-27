using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.Profiler;
using Passenger.Roslyn.Extension;

namespace Passenger.App.Endpoints;

/// <summary>A database command a request issued: its text, time, the rows it read or changed (once known), why it failed,
/// how many times its text ran in its request (a command run again and again for one request is an N+1), and whether it was slow.</summary>
public sealed class LiveCommand(string id, string text, double milliseconds, int? rows, string? error)
{
    /// <summary>A command over this long is marked slow.</summary>
    public const double SlowMilliseconds = 100;

    /// <summary>A text run this often within one request is marked as repeated.</summary>
    public const int RepeatedAt = 3;

    public string Id { get; } = id;
    public string Text { get; } = text;
    public double Milliseconds { get; } = milliseconds;
    /// <summary>The rows a non-query changed.</summary>
    public int? Rows { get; } = rows;

    /// <summary>How many reads its reader made (EF Core's ReadCount: each Read asked, the last that found no row too).</summary>
    public int? Reads { get; internal set; }

    public string? Error { get; } = error;
    public int Runs { get; internal set; } = 1;

    /// <summary>Its parameters as EF Core logged them ("@__id_0='2'"), their values masked by ContainerSecrets' rule by their
    /// names or their columns' (<see cref="CommandParameters"/>), '?' where the app logs none; null until heard.</summary>
    public string? Parameters { get; internal set; }

    public bool Repeated => Runs >= RepeatedAt;
    public bool Slow => Milliseconds >= SlowMilliseconds;
}

/// <summary>A request the app served, as it told: its method, path and query, when it began, its status and time once it
/// ended, the exception that ended it, the commands it issued, and the route of the folder's code it reached.</summary>
public sealed class LiveRequest(string trace, string method, string path, string query, DateTime began)
{
    public string Trace { get; } = trace;
    public string Method { get; } = method;
    public string Path { get; } = path;
    public string Query { get; } = query;
    public DateTime Began { get; } = began;
    public int? Status { get; internal set; }
    public double? Milliseconds { get; internal set; }
    public string? Error { get; internal set; }
    public List<LiveCommand> Commands { get; } = [];

    /// <summary>EF Core's logs of commands not yet told of, their values masked.</summary>
    internal List<string> Logged { get; } = [];

    /// <summary>Its commands by their text, so a command's runs are counted by its text alone, never by comparing it with each
    /// command before it (an N+1 of a thousand commands).</summary>
    internal Dictionary<string, List<LiveCommand>> ByText { get; } = new(StringComparer.Ordinal);

    /// <summary>Its own id, as the app told it, by which it is found and forgotten.</summary>
    internal string Request { get; init; } = string.Empty;

    public WebRoute? Route { get; internal set; }

    /// <summary>The program that served it, as the window names it.</summary>
    public string From { get; internal set; } = string.Empty;

    /// <summary>The project of the program that served it, when known.</summary>
    public string? Project { get; internal set; }

    /// <summary>Whether a command of it ran again and again (an N+1).</summary>
    public bool HasRepeats
    {
        get
        {
            foreach (LiveCommand command in Commands)
            {
                if (command.Repeated)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

/// <summary>What a batch of events changed: the requests that came (oldest first), those already listed that changed (an end,
/// a command, a route), and those that went past <see cref="RequestLog.Most"/>; or everything, the list cleared.</summary>
public sealed record RequestChanges(IReadOnlyList<LiveRequest> Came, IReadOnlyCollection<LiveRequest> Changed, IReadOnlyCollection<LiveRequest> Went, bool All = false);

/// <summary>The live requests of the app followed, newest last, as many as <see cref="Most"/>: each event the stream thread
/// hears queued and taken on the UI thread a batch at a time, a command tied to its request by its activity's root id. What
/// the Requests window shows, kept whether or not its view is built (#447), with what the window says it follows.</summary>
public sealed class RequestLog
{
    /// <summary>The most requests kept, the oldest going first.</summary>
    public const int Most = 500;

    /// <summary>The folder of the state's cache SQL is opened from, read-only: a command's text, or an EF Core query's SQL the
    /// debugger read.</summary>
    public const string CommandCopies = "requests";

    /// <summary>Whether a file is SQL copied to open it (<see cref="CommandCopies"/>), which the editor never writes.</summary>
    public static bool IsCommandCopy(string path) =>
        System.IO.Path.GetDirectoryName(path) is { } folder && System.IO.Path.GetFileName(folder) == CommandCopies
        && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(folder)) == "cache" && path.EndsWith(".sql", StringComparison.Ordinal);

    private readonly ConcurrentQueue<(WebEvent Told, string From, string? Project)> heard = new();
    private readonly List<LiveRequest> requests = [];
    /// <summary>Requests by the program that served them and their activity's root id, and by that program and their own id:
    /// a request one service makes of another carries its trace on, so a root id alone names a request in each.</summary>
    private readonly Dictionary<(string From, string Id), LiveRequest> byTrace = [];
    private readonly Dictionary<(string From, string Id), LiveRequest> byRequest = [];

    /// <summary>A program's request by its own id, else by its activity's root id.</summary>
    private LiveRequest? Of(string from, string request, string trace) =>
        request.Length > 0 && byRequest.TryGetValue((from, request), out LiveRequest? found) ? found
        : trace.Length > 0 && byTrace.TryGetValue((from, trace), out LiveRequest? traced) ? traced
        : null;
    private readonly Func<string, string, string?, WebRoute?> routeOf;
    private int posted;

    /// <param name="routeOf">The route a method and path reach in a project (any when null), from the folder's routes.</param>
    public RequestLog(Func<string, string, string?, WebRoute?> routeOf) => this.routeOf = routeOf;

    public IReadOnlyList<LiveRequest> Requests => requests;

    /// <summary>What the Requests window says it follows, or why it follows nothing.</summary>
    public string Following
    {
        get => following;
        set
        {
            UiThread.Check();
            if (following != value)
            {
                following = value;
                FollowingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private string following = string.Empty;

    /// <summary>What the window says it follows changed.</summary>
    public event EventHandler? FollowingChanged;

    /// <summary>Requests came, ended, issued commands or went: what a batch changed, so a list redraws that alone.</summary>
    public event EventHandler<RequestChanges>? Changed;

    private readonly List<LiveRequest> came = [];
    private readonly HashSet<LiveRequest> changed = [];
    private readonly HashSet<LiveRequest> went = [];

    /// <summary>An event heard on the stream's thread, taken on the UI thread with those heard meanwhile, with the program that told it
    /// and its project.</summary>
    public void Hear(WebEvent told, string from, string? project)
    {
        heard.Enqueue((told, from, project));
        if (Interlocked.Exchange(ref posted, 1) == 0)
        {
            UiThread.Post(Take);
        }
    }

    /// <summary>The routes were read again: each request that reached none known yet is looked for again.</summary>
    public void Reroute()
    {
        UiThread.Check();
        foreach (LiveRequest request in requests)
        {
            if (request.Route is not null)
            {
                continue;
            }
            request.Route = routeOf(request.Method, request.Path, request.Project);
            if (request.Route is not null)
            {
                changed.Add(request);
            }
        }
        Tell();
    }

    /// <summary>Forgets every request, as a new run begins or the person clears the list.</summary>
    public void Clear()
    {
        UiThread.Check();
        requests.Clear();
        byTrace.Clear();
        byRequest.Clear();
        came.Clear();
        changed.Clear();
        went.Clear();
        Changed?.Invoke(this, new RequestChanges([], [], [], All: true));
    }

    private void Take()
    {
        Volatile.Write(ref posted, 0);
        while (heard.TryDequeue(out (WebEvent Told, string From, string? Project) one))
        {
            Apply(one.Told, one.From, one.Project);
        }
        Tell();
    }

    /// <summary>Says what the batch changed, if anything: a request that came and went within it is neither.</summary>
    private void Tell()
    {
        if (came.Count + changed.Count + went.Count == 0)
        {
            return;
        }
        HashSet<LiveRequest> fresh = [.. came];
        RequestChanges told = new([.. came.Where(r => !went.Contains(r))], [.. changed.Where(r => !went.Contains(r) && !fresh.Contains(r))],
            [.. went.Where(r => !fresh.Contains(r))]);
        came.Clear();
        changed.Clear();
        went.Clear();
        Changed?.Invoke(this, told);
    }

    private void Apply(WebEvent told, string from, string? project)
    {
        switch (told)
        {
            case RequestBegan began:
                LiveRequest request = new(began.Trace, began.Method, began.Path, began.Query, began.At)
                {
                    From = from, Project = project, Route = routeOf(began.Method, began.Path, project), Request = began.Request,
                };
                requests.Add(request);
                came.Add(request);
                if (began.Trace.Length > 0)
                {
                    byTrace[(from, began.Trace)] = request;
                }
                if (began.Request.Length > 0)
                {
                    byRequest[(from, began.Request)] = request;
                }
                if (requests.Count > Most)
                {
                    LiveRequest oldest = requests[0];
                    if (byTrace.TryGetValue((oldest.From, oldest.Trace), out LiveRequest? traced) && traced == oldest)
                    {
                        byTrace.Remove((oldest.From, oldest.Trace));
                    }
                    if (byRequest.TryGetValue((oldest.From, oldest.Request), out LiveRequest? named) && named == oldest)
                    {
                        byRequest.Remove((oldest.From, oldest.Request));
                    }
                    requests.RemoveAt(0);
                    went.Add(oldest);
                }
                return;
            case RequestEnded ended when Of(from, ended.Request, ended.Trace) is { } done:
                done.Status = ended.Status;
                done.Milliseconds = (ended.At - done.Began).TotalMilliseconds;
                changed.Add(done);
                return;
            case RequestFailed failed when Of(from, failed.Request, failed.Trace) is { } broke:
                broke.Error = failed.Error;
                changed.Add(broke);
                return;
            case CommandRan ran when byTrace.TryGetValue((from, ran.Trace), out LiveRequest? issuer):
                LiveCommand command = new(ran.Id, ran.Text, ran.Milliseconds, ran.Rows, ran.Error);
                if (!issuer.ByText.TryGetValue(ran.Text, out List<LiveCommand>? same))
                {
                    issuer.ByText[ran.Text] = same = [];
                }
                same.Add(command);
                foreach (LiveCommand each in same)
                {
                    each.Runs = same.Count;
                }
                issuer.Commands.Add(command);
                // EF Core logs a command before it tells of it: its log, heard first and masked then, gives it its parameters.
                for (int i = 0; i < issuer.Logged.Count; i++)
                {
                    if (Logs(issuer.Logged[i], ran.Text))
                    {
                        command.Parameters = CommandParameters.OfMasked(issuer.Logged[i]);
                        issuer.Logged.RemoveAt(i);
                        break;
                    }
                }
                changed.Add(issuer);
                return;
            case CommandRead read when byTrace.TryGetValue((from, read.Trace), out LiveRequest? reader) && Last(reader, read.Id) is { } readCommand:
                readCommand.Reads = read.Reads;
                changed.Add(reader);
                return;
            case CommandLogged log when byTrace.TryGetValue((from, log.Trace), out LiveRequest? logger):
                if (Unnamed(logger, log.Message) is { } unnamed)
                {
                    unnamed.Parameters = CommandParameters.Of(log.Message);
                    changed.Add(logger);
                    return;
                }
                // Kept with its values masked until its command comes: what the app logged in the clear is never held.
                logger.Logged.Add(CommandParameters.Masked(log.Message));
                return;
        }
    }

    /// <summary>A request's last command of this id.</summary>
    private static LiveCommand? Last(LiveRequest request, string id)
    {
        for (int i = request.Commands.Count - 1; i >= 0; i--)
        {
            if (request.Commands[i].Id == id)
            {
                return request.Commands[i];
            }
        }
        return null;
    }

    /// <summary>A request's first command with no parameters yet that a log is of.</summary>
    private static LiveCommand? Unnamed(LiveRequest request, string logged)
    {
        foreach (LiveCommand command in request.Commands)
        {
            if (command.Parameters is null && Logs(logged, command.Text))
            {
                return command;
            }
        }
        return null;
    }

    /// <summary>Whether EF Core's log of a command is of this text: it logs the text trimmed, and SaveChanges' commands end
    /// with a line break.</summary>
    private static bool Logs(string logged, string text) => logged.AsSpan().Contains(text.AsSpan().TrimEnd(), StringComparison.Ordinal);
}

/// <summary>A command's parameters as EF Core's log of it writes them (<c>[Parameters=[@__id_0='2' (DbType = Int32)], …]</c>),
/// each value masked as ContainerSecrets masks a value of that name, or as a secret's when the column it fills or meets is
/// named as one (<see cref="Columns"/>: EF's <c>@p0</c>, <c>@p1</c>… say nothing of their columns), so no secret the app
/// logs is kept or shown in the clear. A command's text is read for its columns once (an N+1 runs one text again and
/// again), as ranges of its own characters.</summary>
public static partial class CommandParameters
{
    /// <summary>The command texts whose columns are kept: past this many, they are read again.</summary>
    private const int KeptTexts = 256;

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> columnsOf = new(StringComparer.Ordinal);

    /// <summary>The parameters a log names, masked, as "@name='value'" apart by commas; empty for a command with none.</summary>
    public static string Of(string logged) => OfMasked(Masked(logged));

    /// <summary>The parameters a log <see cref="Masked"/> before names, as <see cref="Of"/> gives them, masked no more.</summary>
    public static string OfMasked(string masked)
    {
        Match list = List().Match(masked);
        if (!list.Success)
        {
            return string.Empty;
        }
        Group named = list.Groups["list"];
        ReadOnlySpan<char> parameters = masked.AsSpan(named.Index, named.Length);
        StringBuilder joined = new();
        foreach (ValueMatch one in One().EnumerateMatches(parameters))
        {
            if (joined.Length > 0)
            {
                joined.Append(", ");
            }
            joined.Append(parameters.Slice(one.Index, one.Length).Trim());
        }
        return joined.ToString();
    }

    /// <summary>A log's text with each parameter's value masked as <see cref="Of"/> shows it, the command's text read for
    /// the parameters' columns only when one has a value to show.</summary>
    public static string Masked(string logged)
    {
        IReadOnlyDictionary<string, string>? columns = null;
        return One().Replace(logged, p =>
        {
            string name = p.Groups["name"].Value;
            string value = p.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal);
            string rest = p.Groups["rest"].Value;
            if (value.Length == 0 || value == ContainerSecrets.Masked)
            {
                return p.Value;
            }
            columns ??= ColumnsOf(logged);
            string shown = columns.TryGetValue(name, out string? column) && ContainerSecrets.Secret(column) ? ContainerSecrets.Masked
                : ContainerSecrets.Shown(name.TrimStart('@', ':', '$'), value);
            // A secret's size says something of it: its dots stand alone.
            return $"{name}='{shown.Replace("'", "''", StringComparison.Ordinal)}'{(shown == ContainerSecrets.Masked ? Size().Replace(rest, string.Empty) : rest)}";
        });
    }

    /// <summary>The columns of the command a log is of, read once for each text.</summary>
    private static IReadOnlyDictionary<string, string> ColumnsOf(string logged)
    {
        ReadOnlySpan<char> sql = TextOf(logged);
        ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>.AlternateLookup<ReadOnlySpan<char>> read = columnsOf.GetAlternateLookup<ReadOnlySpan<char>>();
        if (read.TryGetValue(sql, out IReadOnlyDictionary<string, string>? known))
        {
            return known;
        }
        if (columnsOf.Count >= KeptTexts)
        {
            columnsOf.Clear();
        }
        IReadOnlyDictionary<string, string> columns = Columns(sql);
        read.TryAdd(sql, columns);
        return columns;
    }

    /// <summary>The command's text in EF Core's log of it: what follows the line that names its parameters (the whole log
    /// when it has none).</summary>
    private static ReadOnlySpan<char> TextOf(string logged)
    {
        Match list = List().Match(logged);
        if (!list.Success)
        {
            return logged;
        }
        int line = logged.IndexOf('\n', list.Index + list.Length);
        return line < 0 ? [] : logged.AsSpan(line + 1);
    }

    /// <summary>The column each parameter of a command's text fills or meets, by the parameter's name: an INSERT's column
    /// list against each row of its VALUES (SQL Server's MERGE of several rows against the columns its source's alias
    /// names), and a <c>column = @p</c> or <c>@p = column</c> (a SET's, a WHERE's; <c>&lt;&gt;</c>, <c>LIKE</c> and the
    /// rest of the comparisons too), the column the last name of a qualified one (<c>"c"."Password"</c>).</summary>
    private static Dictionary<string, string> Columns(ReadOnlySpan<char> sql)
    {
        Dictionary<string, string> columns = new(StringComparer.OrdinalIgnoreCase);
        SqlToken[] rented = ArrayPool<SqlToken>.Shared.Rent(Math.Max(1, sql.Length));
        try
        {
            SqlTokens tokens = new(sql, rented.AsSpan(0, Tokenize(sql, rented)));
            // The "(" of the INSERT's column list the next VALUES fills, or -1.
            int inserting = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == SqlKind.Parameter)
                {
                    if (i >= 2 && Compares(tokens, i - 1) && tokens.IsName(i - 2))
                    {
                        Add(columns, tokens, i, i - 2);
                    }
                    else if (i + 2 < tokens.Count && Compares(tokens, i + 1) && tokens.IsName(i + 2))
                    {
                        int last = i + 2;
                        while (last + 2 < tokens.Count && tokens.Is(last + 1, ".") && tokens.IsName(last + 2))
                        {
                            last += 2;
                        }
                        Add(columns, tokens, i, last);
                    }
                }
                else if (tokens.Is(i, ";"))
                {
                    inserting = -1;
                }
                else if (tokens.Is(i, "INSERT"))
                {
                    // INSERT [INTO] [table] (columns): MERGE's INSERT names no table.
                    int at = i + 1 < tokens.Count && tokens.Is(i + 1, "INTO") ? i + 2 : i + 1;
                    while (at < tokens.Count && tokens.IsName(at))
                    {
                        at += at + 1 < tokens.Count && tokens.Is(at + 1, ".") ? 2 : 1;
                        if (!tokens.Is(at - 1, "."))
                        {
                            break;
                        }
                    }
                    inserting = Names(tokens, at) >= 0 ? at : -1;
                }
                else if (tokens.Is(i, "VALUES"))
                {
                    int after = Rows(tokens, i + 1, null, -1);
                    // MERGE … USING (VALUES (…), (…)) AS i (columns): the source's columns, else the INSERT's.
                    int named = after + 3 < tokens.Count && tokens.Is(after, ")") && tokens.Is(after + 1, "AS") && tokens.IsName(after + 2)
                        && Names(tokens, after + 3) >= 0 ? after + 3 : inserting;
                    if (named >= 0)
                    {
                        Rows(tokens, i + 1, columns, named);
                    }
                    inserting = -1;
                    i = after - 1;
                }
            }
        }
        finally
        {
            ArrayPool<SqlToken>.Shared.Return(rented);
        }
        return columns;
    }

    /// <summary>A parameter's column, unless it has one already: the two made strings only then.</summary>
    private static void Add(Dictionary<string, string> columns, SqlTokens tokens, int parameter, int column)
    {
        if (!columns.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(tokens.Span(parameter)))
        {
            columns.Add(tokens.Text(parameter), tokens.Text(column));
        }
    }

    /// <summary>Whether a token compares what stands on either side of it.</summary>
    private static bool Compares(SqlTokens tokens, int at) => tokens.Is(at, "=") || tokens.Is(at, "<>") || tokens.Is(at, "!=") || tokens.Is(at, "<")
        || tokens.Is(at, ">") || tokens.Is(at, "<=") || tokens.Is(at, ">=") || tokens.Is(at, "LIKE");

    /// <summary>How many names a parenthesised list starting at a token holds, as an INSERT's columns are written (the
    /// k-th at <c>open + 1 + 2k</c>); -1 for anything else.</summary>
    private static int Names(SqlTokens tokens, int open)
    {
        if (open >= tokens.Count || !tokens.Is(open, "("))
        {
            return -1;
        }
        for (int at = open + 1; at < tokens.Count; at += 2)
        {
            if (!tokens.IsName(at) || at + 1 >= tokens.Count)
            {
                return -1;
            }
            if (tokens.Is(at + 1, ")"))
            {
                return (at - open + 1) / 2;
            }
            if (!tokens.Is(at + 1, ","))
            {
                return -1;
            }
        }
        return -1;
    }

    /// <summary>Walks the rows of a VALUES from a token on, giving each item that is a parameter alone the column the
    /// names list at <paramref name="names"/> has in its place (when <paramref name="columns"/> are given), and gives the
    /// token after the last row.</summary>
    private static int Rows(SqlTokens tokens, int from, Dictionary<string, string>? columns, int names)
    {
        int count = columns is null ? 0 : Names(tokens, names);
        int at = from;
        while (at < tokens.Count && tokens.Is(at, "("))
        {
            int item = 0;
            int depth = 0;
            int items = 0;
            int alone = -1;
            at++;
            for (; at < tokens.Count; at++)
            {
                if (depth == 0 && (tokens.Is(at, ",") || tokens.Is(at, ")")))
                {
                    if (columns is not null && items == 1 && tokens[alone].Kind == SqlKind.Parameter && item < count)
                    {
                        Add(columns, tokens, alone, names + 1 + (2 * item));
                    }
                    item++;
                    items = 0;
                    if (tokens.Is(at, ")"))
                    {
                        break;
                    }
                    continue;
                }
                depth += tokens.Is(at, "(") ? 1 : tokens.Is(at, ")") ? -1 : 0;
                alone = at;
                items++;
            }
            at++;
            if (at < tokens.Count && tokens.Is(at, ",") && at + 1 < tokens.Count && tokens.Is(at + 1, "("))
            {
                at++;
            }
            else
            {
                break;
            }
        }
        return at;
    }

    /// <summary>A command's text as words, quoted names, parameters, literals and symbols, its comments passed over: enough
    /// of SQL's for <see cref="Columns"/>, in every provider's quoting (<c>"Password"</c>, <c>[Password]</c>,
    /// <c>`Password`</c>). Each token is a range of the text, written into <paramref name="into"/>; gives how many.</summary>
    private static int Tokenize(ReadOnlySpan<char> sql, SqlToken[] into)
    {
        int count = 0;
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && next == '-')
            {
                int end = sql[i..].IndexOf('\n');
                i = end < 0 ? sql.Length : i + end + 1;
            }
            else if (c == '/' && next == '*')
            {
                int end = sql[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                i = end < 0 ? sql.Length : i + 2 + end + 2;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                char close = c == '[' ? ']' : c;
                bool doubled = false;
                int at = i + 1;
                while (sql[at..].IndexOf(close) is int found and >= 0)
                {
                    at += found;
                    if (at + 1 < sql.Length && sql[at + 1] == close)
                    {
                        doubled = true;
                        at += 2;
                        continue;
                    }
                    break;
                }
                at = Math.Min(at, sql.Length);
                if (at < sql.Length && sql[at] != close)
                {
                    at = sql.Length;
                }
                into[count++] = new SqlToken(c == '\'' ? SqlKind.Literal : SqlKind.Quoted, i + 1, at - i - 1, doubled ? close : '\0');
                i = at + 1;
            }
            else if (c is '@' or ':' or '$' && (char.IsLetterOrDigit(next) || next == '_') && !(c == ':' && i > 0 && sql[i - 1] == ':'))
            {
                int at = i + 1;
                while (at < sql.Length && (char.IsLetterOrDigit(sql[at]) || sql[at] == '_'))
                {
                    at++;
                }
                into[count++] = new SqlToken(SqlKind.Parameter, i, at - i);
                i = at;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                int at = i + 1;
                while (at < sql.Length && (char.IsLetterOrDigit(sql[at]) || sql[at] is '_' or '$' or '#'))
                {
                    at++;
                }
                into[count++] = new SqlToken(char.IsDigit(c) ? SqlKind.Number : SqlKind.Word, i, at - i);
                i = at;
            }
            else
            {
                int length = (c, next) is ('<', '>') or ('!', '=') or ('<', '=') or ('>', '=') or (':', ':') ? 2 : 1;
                into[count++] = new SqlToken(SqlKind.Symbol, i, length);
                i += length;
            }
        }
        return count;
    }

    private enum SqlKind
    {
        Word,
        Quoted,
        Parameter,
        Literal,
        Number,
        Symbol,
    }

    /// <summary>A word of a command's text, as the range of the text it takes: a keyword or bare name, a quoted name, a
    /// parameter, a literal, a number or a symbol; a quoted one's range is inside its quotes, <see cref="Doubled"/> the
    /// quote it writes twice for one, if it does.</summary>
    private readonly record struct SqlToken(SqlKind Kind, int Start, int Length, char Doubled = '\0');

    /// <summary>A command's tokens over its text, compared where they lie: a string is made only of a name kept.</summary>
    private readonly ref struct SqlTokens(ReadOnlySpan<char> sql, ReadOnlySpan<SqlToken> tokens)
    {
        private readonly ReadOnlySpan<char> sql = sql;
        private readonly ReadOnlySpan<SqlToken> tokens = tokens;

        public int Count => tokens.Length;

        public SqlToken this[int at] => tokens[at];

        /// <summary>A name a column can have: a bare word or a quoted one.</summary>
        public bool IsName(int at) => tokens[at].Kind is SqlKind.Word or SqlKind.Quoted;

        /// <summary>Whether it is this keyword (a bare word, in any case) or symbol.</summary>
        public bool Is(int at, string word) => tokens[at].Kind is SqlKind.Word or SqlKind.Symbol && Span(at).Equals(word, StringComparison.OrdinalIgnoreCase);

        public ReadOnlySpan<char> Span(int at) => sql.Slice(tokens[at].Start, tokens[at].Length);

        /// <summary>Its text, a quoted one's doubled quotes made one.</summary>
        public string Text(int at) => tokens[at].Doubled is char quote and not '\0'
            ? new string(Span(at)).Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal)
            : new string(Span(at));
    }

    [GeneratedRegex(@"\[Parameters=\[(?<list>.*?)\], CommandType=", RegexOptions.Singleline)]
    private static partial Regex List();

    [GeneratedRegex(@"(?<name>[@:$]\w+)='(?<value>(?:[^']|'')*)'(?<rest>(?: \([^)]*\))*)")]
    private static partial Regex One();

    /// <summary>The size EF Core's log gives a value (<c>(Size = 9)</c>).</summary>
    [GeneratedRegex(@" \(Size = \d+\)")]
    private static partial Regex Size();
}
