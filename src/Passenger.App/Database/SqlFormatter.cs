using System.Text;
using Passenger.Text;

namespace Passenger.App.Database;

/// <summary>How Reformat Code writes SQL's keywords: upper-case, lower-case, or as written.</summary>
public enum SqlKeywordCase
{
    Upper,
    Lower,
    AsWritten,
}

/// <summary>How SQL is laid out: indented as its file indents (its .editorconfig, else its own lines), its keywords cased so.</summary>
public sealed record SqlFormatOptions(IndentOptions Indent, SqlKeywordCase Keywords = SqlKeywordCase.Upper)
{
    /// <summary>Four spaces a level and keywords upper-case.</summary>
    public static readonly SqlFormatOptions Default = new(new IndentOptions());
}

/// <summary>SQL laid out as its database reads it (<see cref="SqlText"/>): each clause on a line of its own, a SELECT's
/// and a SET's items and VALUES' rows one to a line under the first, AND and OR of a condition on lines of their own, a
/// subquery and a table's columns indented a level of the options' indentation, keywords cased as they say and one space
/// between words. Strings, quoted names and comments are kept as written, a line comment ending its line; a routine's or a
/// block's definition, SQL Server's GO lines and the blank lines between statements are kept as they stand. Laying out
/// laid-out SQL changes nothing.</summary>
public static class SqlFormatter
{

    private enum Kind
    {
        Word,
        Number,
        Symbol,
        Quoted,
        Comment,
    }

    /// <summary>A piece of the text: what it is, as written, where it stands, and the line breaks before it.</summary>
    private readonly record struct Piece(Kind Kind, string Text, int Start, int End, int BreaksBefore)
    {
        public string Upper => Text.ToUpperInvariant();
        public bool Is(string word) => Kind == Kind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The words written upper-case, each a reserved word or one that only begins or joins a clause.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "ILIKE", "BETWEEN", "EXISTS", "JOIN", "INNER", "LEFT", "RIGHT", "FULL",
        "OUTER", "CROSS", "NATURAL", "LATERAL", "ON", "USING", "AS", "GROUP", "BY", "HAVING", "ORDER", "ASC", "DESC", "LIMIT", "OFFSET", "FETCH", "NEXT",
        "ROWS", "ONLY", "UNION", "ALL", "DISTINCT", "EXCEPT", "INTERSECT", "CASE", "WHEN", "THEN", "ELSE", "END", "WITH", "RECURSIVE", "INSERT", "INTO",
        "VALUES", "UPDATE", "SET", "DELETE", "RETURNING", "CREATE", "TABLE", "VIEW", "INDEX", "UNIQUE", "PRIMARY", "FOREIGN", "KEY", "REFERENCES",
        "DEFAULT", "CHECK", "CONSTRAINT", "ALTER", "ADD", "DROP", "COLUMN", "TRUE", "FALSE", "TOP", "OVER", "PARTITION", "WINDOW", "NULLS", "TRUNCATE",
        "ESCAPE", "COLLATE", "CONFLICT", "NOTHING", "DO", "IF", "OUTPUT", "MERGE", "MATCHED", "TEMPORARY", "TEMP", "CASCADE", "RESTRICT", "ANY", "SOME",
    };

    /// <summary>The words that take a space before a bracket after them, where a function's name takes none.</summary>
    private static readonly HashSet<string> SpacedBeforeBracket = new(StringComparer.OrdinalIgnoreCase)
    {
        "IN", "VALUES", "EXISTS", "AS", "ON", "USING", "OVER", "FROM", "JOIN", "AND", "OR", "NOT", "WHERE", "SELECT", "INTO", "ALL", "ANY", "SOME",
        "THEN", "ELSE", "WHEN", "BY", "KEY", "REFERENCES", "CHECK", "UNIQUE", "TABLE", "WITH", "UNION", "EXCEPT", "INTERSECT", "LATERAL", "RETURNING",
    };

    /// <summary>The words a clause begins with, each on a line of its own at its statement's indent.</summary>
    private static readonly HashSet<string> Clauses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "HAVING", "LIMIT", "OFFSET", "FETCH", "VALUES", "SET", "RETURNING", "WINDOW", "UNION", "INTERSECT", "EXCEPT", "OUTPUT",
    };

    /// <summary>The text laid out, a statement at a time; the text as written where it holds nothing to lay out.</summary>
    public static string Format(string sql, DatabaseKind kind, SqlFormatOptions? options = null)
    {
        SqlFormatOptions how = options ?? SqlFormatOptions.Default;
        List<Piece> pieces = Pieces(sql, kind);
        StringBuilder text = new();
        int first = 0;
        while (first < pieces.Count)
        {
            int last = StatementEnd(pieces, first, kind);
            if (text.Length > 0)
            {
                text.Append(pieces[first].BreaksBefore > 1 ? "\n\n" : "\n");
            }
            if (Kept(pieces, first, last, kind))
            {
                text.Append(sql[pieces[first].Start..pieces[last].End]);
            }
            else
            {
                Lay(pieces, first, last, text, how);
            }
            first = last + 1;
        }
        string laid = text.ToString();
        return laid.Length == 0 ? sql : laid + (sql.EndsWith('\n') ? "\n" : string.Empty);
    }

    /// <summary>The last piece of the statement starting at <paramref name="first"/>: its semicolon, a GO line, a comment
    /// standing alone, or the text's end.</summary>
    private static int StatementEnd(List<Piece> pieces, int first, DatabaseKind kind)
    {
        if (pieces[first].Kind == Kind.Comment || IsGo(pieces, first, kind))
        {
            return first;
        }
        int blocks = 0;
        bool routine = Routine(pieces, first);
        for (int i = first; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];
            if (i > first && IsGo(pieces, i, kind))
            {
                return i - 1;
            }
            if (routine && piece.Kind == Kind.Word)
            {
                string next = i + 1 < pieces.Count ? pieces[i + 1].Upper : string.Empty;
                blocks += piece.Upper switch
                {
                    "BEGIN" => next is "TRAN" or "TRANSACTION" or "WORK" or ";" ? 0 : 1,
                    "CASE" => i > 0 && pieces[i - 1].Is("END") ? 0 : 1,
                    "END" => next is "IF" or "LOOP" or "WHILE" or "REPEAT" or "FOR" ? 0 : -1,
                    _ => 0,
                };
            }
            if (piece.Text == ";" && blocks <= 0)
            {
                // A comment after the semicolon, on its line, is the statement's.
                while (i + 1 < pieces.Count && pieces[i + 1] is { Kind: Kind.Comment, BreaksBefore: 0 })
                {
                    i++;
                }
                return i;
            }
        }
        return pieces.Count - 1;
    }

    private static bool IsGo(List<Piece> pieces, int at, DatabaseKind kind) => kind == DatabaseKind.SqlServer && pieces[at].Is("GO") && pieces[at].BreaksBefore > 0
        && (at + 1 >= pieces.Count || pieces[at + 1].BreaksBefore > 0);

    private static bool Routine(List<Piece> pieces, int first) => pieces[first].Is("CREATE") || pieces[first].Is("ALTER")
        ? pieces.Skip(first + 1).Take(10).TakeWhile(p => p.Text != "(" && !p.Is("AS") && !p.Is("TABLE")).Any(p => p.Upper is "PROCEDURE" or "PROC" or "FUNCTION" or "TRIGGER" or "EVENT")
        : false;

    /// <summary>Whether a statement is kept as written: a comment or GO line, a routine's definition, a block (BEGIN, IF,
    /// DECLARE, DO), or one holding a dollar quote's body.</summary>
    private static bool Kept(List<Piece> pieces, int first, int last, DatabaseKind kind) =>
        pieces[first].Kind == Kind.Comment || IsGo(pieces, first, kind) || Routine(pieces, first)
        || pieces[first].Upper is "BEGIN" or "IF" or "WHILE" or "DECLARE" or "DO" or "DELIMITER"
        || pieces.Skip(first).Take(last - first + 1).Any(p => p.Kind == Kind.Quoted && p.Text.StartsWith('$') || p.Is("BEGIN") && kind == DatabaseKind.SqlServer);

    /// <summary>A statement laid out.</summary>
    private static void Lay(List<Piece> pieces, int first, int last, StringBuilder text, SqlFormatOptions how)
    {
        Layout layout = new(text, how);
        for (int i = first; i <= last; i++)
        {
            layout.Take(pieces, i, first, last);
        }
    }

    /// <summary>A statement's layout as it is written: the levels of its brackets, each with its indent and the clause it is in.</summary>
    private sealed class Layout(StringBuilder text, SqlFormatOptions how)
    {
        /// <summary>The columns a level indents by.</summary>
        private readonly int step = how.Indent.UseTabs ? how.Indent.TabWidth : how.Indent.Size;

        private sealed class Level(int indent, bool query)
        {
            public int Indent { get; } = indent;
            public bool Query { get; } = query;
            public string Clause { get; set; } = string.Empty;
            public bool Between { get; set; }
            public bool Columns { get; set; }
        }

        private readonly Stack<Level> levels = new([new Level(0, true)]);
        private readonly int start = text.Length;
        private int lineStart = text.Length;
        private Piece? previous;
        private bool breakNext;
        private bool createTable;

        private Level Here => levels.Peek();

        public void Take(List<Piece> pieces, int i, int first, int last)
        {
            Piece piece = pieces[i];
            Piece? next = i < last ? pieces[i + 1] : null;
            string upper = piece.Upper;
            bool word = piece.Kind == Kind.Word;
            if (piece.Kind == Kind.Comment)
            {
                Comment(piece);
                return;
            }
            if (word && i == first && upper == "CREATE")
            {
                createTable = pieces.Skip(i + 1).Take(4).Any(p => p.Is("TABLE"));
            }
            if (piece.Text == ")" && levels.Count > 1)
            {
                Level closed = levels.Pop();
                if (closed.Query || closed.Columns)
                {
                    Line(Here.Indent);
                }
                Write(piece, spaced: false);
                return;
            }
            if (word && Here.Query && i > first && previous?.Text != "(" && Breaks(pieces, i, last))
            {
                Break(Here.Indent);
            }
            if (word && Here.Query && (Clauses.Contains(upper) || upper is "GROUP" or "ORDER" or "INSERT" or "UPDATE" or "DELETE"))
            {
                Here.Clause = upper;
                Here.Between = false;
            }
            if (word && upper is "AND" or "OR" && Here.Query && Here.Clause is "WHERE" or "HAVING" or "ON" or "JOIN")
            {
                if (upper == "AND" && Here.Between)
                {
                    Here.Between = false;
                }
                else
                {
                    Break(Here.Indent + step);
                }
            }
            if (word && upper == "BETWEEN")
            {
                Here.Between = true;
            }
            if (word && upper is "ON" or "JOIN" && Here.Query)
            {
                Here.Clause = upper;
            }
            Write(piece, Spaced(piece, next));
            if (piece.Text == "(")
            {
                bool query = next is { } inner && (inner.Is("SELECT") || inner.Is("WITH"));
                bool columns = createTable && levels.Count == 1 && !query;
                createTable &= !columns;
                levels.Push(new Level(query || columns ? Here.Indent + step : Here.Indent, query) { Columns = columns });
                if (query || columns)
                {
                    breakNext = true;
                }
                return;
            }
            if (piece.Text == "," && (Here.Columns || Here.Query && Here.Clause is "SELECT" or "SET" or "VALUES"))
            {
                breakNext = true;
                pendingIndent = Here.Columns ? Here.Indent : Here.Indent + Here.Clause.Length + 1;
            }
        }

        private int pendingIndent = -1;

        /// <summary>Whether a word begins a line of its own: a clause's first word (FROM, but not DELETE's), a join, ON
        /// CONFLICT, or a statement's INSERT, UPDATE or DELETE after a WITH's queries.</summary>
        private bool Breaks(List<Piece> pieces, int i, int last)
        {
            Piece piece = pieces[i];
            Piece? next = i < last ? pieces[i + 1] : null;
            return piece.Upper switch
            {
                "GROUP" or "ORDER" => next?.Is("BY") == true,
                "ON" => next?.Is("CONFLICT") == true,
                "FROM" => previous?.Is("DELETE") != true,
                "INSERT" or "UPDATE" or "DELETE" or "MERGE" => previous?.Text == ")",
                _ when Clauses.Contains(piece.Text) => true,
                _ => Joins(pieces, i, last),
            };
        }

        /// <summary>Whether a join (JOIN, or its first word: LEFT, INNER, CROSS, NATURAL …) begins at a piece.</summary>
        private static bool Joins(List<Piece> pieces, int i, int last)
        {
            if (pieces[i].Is("JOIN"))
            {
                return i == 0 || pieces[i - 1].Upper is not ("INNER" or "LEFT" or "RIGHT" or "FULL" or "OUTER" or "CROSS" or "NATURAL");
            }
            if (pieces[i].Upper is not ("INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "NATURAL") || i > 0 && pieces[i - 1].Upper is "NATURAL")
            {
                return false;
            }
            for (int j = i + 1; j <= Math.Min(last, i + 3); j++)
            {
                if (pieces[j].Is("JOIN"))
                {
                    return true;
                }
                if (pieces[j].Upper is not ("OUTER" or "LEFT" or "RIGHT" or "FULL" or "INNER"))
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>Whether a space stands before a piece, after the one before it.</summary>
        private bool Spaced(Piece piece, Piece? next)
        {
            if (previous is not { } before)
            {
                return false;
            }
            string text = piece.Text;
            if (text is "," or ";" or ")" or "." || before.Text is "(" or "." || text == "::" || before.Text == "::")
            {
                return false;
            }
            if (piece.Kind == Kind.Quoted && before.Kind == Kind.Word && piece.Start == before.End)
            {
                // A string's prefix (N'…', E'…', X'…') stays against it.
                return false;
            }
            if (text == "(" && before.Kind is Kind.Word or Kind.Quoted)
            {
                return before.Kind == Kind.Word && SpacedBeforeBracket.Contains(before.Text) || piece.Start > before.End;
            }
            if (before.Text is "-" or "+" && previous2 is { } sign && (sign.Kind == Kind.Symbol && sign.Text is not (")" or "]") || sign.Kind == Kind.Word && Keywords.Contains(sign.Text)))
            {
                // A sign, not an operator: kept against what it signs.
                return piece.Start > before.End;
            }
            return true;
        }

        private Piece? previous2;

        private void Write(Piece piece, bool spaced)
        {
            if (breakNext)
            {
                breakNext = false;
                Line(pendingIndent >= 0 ? pendingIndent : Here.Indent);
                pendingIndent = -1;
                spaced = false;
            }
            else if (spaced && text.Length > lineStart && text[^1] != ' ')
            {
                text.Append(' ');
            }
            text.Append(piece.Kind != Kind.Word || !Keywords.Contains(piece.Text) ? piece.Text : how.Keywords switch
            {
                SqlKeywordCase.Upper => piece.Upper,
                SqlKeywordCase.Lower => piece.Text.ToLowerInvariant(),
                _ => piece.Text,
            });
            previous2 = previous;
            previous = piece;
        }

        /// <summary>A comment kept as written: a line comment ends its line, one standing on its own line stays there.</summary>
        private void Comment(Piece piece)
        {
            if (piece.BreaksBefore > 0 && text.Length > lineStart)
            {
                Line(Here.Indent);
            }
            else if (text.Length > lineStart)
            {
                text.Append(' ');
            }
            text.Append(piece.Text);
            if (piece.Text.StartsWith("--", StringComparison.Ordinal) || piece.Text.StartsWith('#'))
            {
                // What follows goes on the next line, where it would have gone, or under the clause it goes on in.
                pendingIndent = breakNext ? pendingIndent : Here.Indent + (Here.Clause.Length > 0 ? step : 0);
                breakNext = true;
            }
            previous2 = previous;
            previous = piece;
        }

        /// <summary>A line of the statement's own begun, in place of one a comma or a comment would have begun.</summary>
        private void Break(int indent)
        {
            breakNext = false;
            pendingIndent = -1;
            Line(indent);
        }

        private void Line(int indent)
        {
            while (text.Length > start && text[^1] is ' ' or '\t')
            {
                text.Length--;
            }
            if (text.Length > lineStart)
            {
                text.Append('\n');
            }
            lineStart = text.Length;
            text.Append(how.Indent.Whitespace(indent));
            lineStart = text.Length;
        }
    }

    /// <summary>The text's pieces, each string, quoted name, comment and dollar quote whole, with the line breaks before each.</summary>
    private static List<Piece> Pieces(string sql, DatabaseKind kind)
    {
        List<Piece> pieces = [];
        int breaks = 0;
        for (int i = 0; i < sql.Length;)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                breaks += c == '\n' ? 1 : 0;
                i++;
                continue;
            }
            int start = i;
            Kind kindOf;
            if (SqlText.Skipped(sql, i, kind) is { } past)
            {
                kindOf = c is '-' or '#' or '/' ? Kind.Comment : Kind.Quoted;
                i = past;
            }
            else if (char.IsLetter(c) || c is '_' or '@' or '#' && kind == DatabaseKind.SqlServer)
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '@' or '#'))
                {
                    i++;
                }
                kindOf = Kind.Word;
            }
            else if (char.IsAsciiDigit(c) || c == '.' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]))
            {
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '.' || sql[i] is '+' or '-' && sql[i - 1] is 'e' or 'E'))
                {
                    i++;
                }
                kindOf = Kind.Number;
            }
            else if (c is '$' or ':' or '?' or '@' && i + 1 < sql.Length && (char.IsLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_'))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }
                kindOf = Kind.Word;
            }
            else
            {
                i += Operator(sql, i);
                kindOf = Kind.Symbol;
            }
            pieces.Add(new Piece(kindOf, sql[start..i], start, i, breaks));
            breaks = 0;
        }
        return pieces;
    }

    private static readonly string[] Operators = ["->>", "#>>", "<=>", "::", "<=", ">=", "<>", "!=", "||", "->", "#>", ":=", "=>", "@>", "<@", "&&", "**", "!~*", "~*", "!~"];

    private static int Operator(string sql, int at)
    {
        foreach (string op in Operators)
        {
            if (string.CompareOrdinal(sql, at, op, 0, op.Length) == 0)
            {
                return op.Length;
            }
        }
        return 1;
    }
}
