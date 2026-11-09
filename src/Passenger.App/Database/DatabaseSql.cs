namespace Passenger.App.Database;

public static class DatabaseSql
{
    public const int RowLimit = 1000;

    public static string Limited(string sql, DatabaseKind kind)
    {
        List<(string Word, int Start, int End, int Depth)> tokens = [];
        int depth = 0;
        for (int i = 0; i < sql.Length;)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] is not ('\r' or '\n')) { i++; }
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                int comments = 1;
                while (i < sql.Length && comments > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { comments++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { comments--; i += 2; }
                    else { i++; }
                }
                continue;
            }
            if (c is '\'' or '"' or '`' or '[')
            {
                char end = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i++] != end) { continue; }
                    if (i < sql.Length && sql[i] == end) { i++; continue; }
                    break;
                }
                continue;
            }
            if (c == '$')
            {
                int tag = i + 1;
                while (tag < sql.Length && (char.IsLetterOrDigit(sql[tag]) || sql[tag] == '_')) { tag++; }
                if (tag < sql.Length && sql[tag] == '$')
                {
                    string delimiter = sql[i..(tag + 1)];
                    int end = sql.IndexOf(delimiter, tag + 1, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + delimiter.Length;
                    continue;
                }
            }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { depth--; i++; continue; }
            int start = i++;
            if (char.IsLetter(c))
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) { i++; }
            }
            tokens.Add((sql[start..i].ToUpperInvariant(), start, i, depth));
        }
        List<(string Word, int Start, int End, int Depth)> top = [.. tokens.Where(t => t.Depth == 0)];
        if (top.Count == 0 || top[0].Word is not ("SELECT" or "WITH")) { return sql; }
        int select = top.FindIndex(t => t.Word == "SELECT");
        if (select < 0) { return sql; }
        int terminator = top.FindIndex(t => t.Word == ";");
        if (terminator >= 0 && terminator != top.Count - 1) { return sql; }
        int from = top.FindIndex(select, t => t.Word == "FROM");
        if (from < 0 || !top.Skip(select).Take(from - select).Any(t => t.Word == "*") || top.Any(t => t.Word is "LIMIT" or "FETCH" or "TOP")) { return sql; }
        int before = terminator >= 0 ? top[terminator].Start : sql.Length;
        if (kind == DatabaseKind.SqlServer)
        {
            if (top.Any(t => t.Word == "OFFSET")) { return sql.Insert(before, $"\nFETCH NEXT {RowLimit} ROWS ONLY "); }
            if (top.Any(t => t.Word is "UNION" or "EXCEPT" or "INTERSECT"))
            {
                string order = top.Any(t => t.Word == "ORDER") ? string.Empty : "\nORDER BY (SELECT NULL)";
                return sql.Insert(before, $"{order}\nOFFSET 0 ROWS FETCH NEXT {RowLimit} ROWS ONLY ");
            }
            int after = top.Count > select + 1 && top[select + 1].Word is "DISTINCT" or "ALL" ? top[select + 1].End : top[select].End;
            return sql.Insert(after, $" TOP ({RowLimit})");
        }
        int locking = top.FindIndex(t => t.Word == "FOR");
        if (locking >= 0) { before = top[locking].Start; }
        return sql.Insert(before, $"\nLIMIT {RowLimit} ");
    }
}
