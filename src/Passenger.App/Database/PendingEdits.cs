using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using Passenger.App.Containers;
using Passenger.App.Tables;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A row by its primary key's values, as read, equal to another holding the same values.</summary>
public sealed class RowKey(object?[] values) : IEquatable<RowKey>
{
    public object?[] Values { get; } = values;

    public bool Equals(RowKey? other) => other is not null && Values.Length == other.Values.Length
        && Values.Zip(other.Values).All(pair => PendingEdits.Same(pair.First, pair.Second));

    public override bool Equals(object? obj) => Equals(obj as RowKey);

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (object? value in Values)
        {
            hash.Add(value is null ? 0 : Convert.ToString(value, CultureInfo.InvariantCulture)?.GetHashCode(StringComparison.Ordinal) ?? 0);
        }
        return hash.ToHashCode();
    }

    public override string ToString() => ContainerSecrets.InSaid(string.Join(", ", Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
}

/// <summary>A row of the table changed: as it was read, and the text written in each of its cells changed, by column (null
/// for NULL).</summary>
public sealed record ChangedRow(object?[] Read, ImmutableDictionary<int, string?> Written);

/// <summary>A row added, the text written in each of its cells given (null for NULL), the rest left to their defaults.</summary>
public sealed record AddedRow(int Id, ImmutableDictionary<int, string?> Written);

/// <summary>A table's changes not yet written: its rows changed and deleted, by their primary keys and as each was read, and
/// its rows added, in the table's columns' order. Each edit makes a new set, so Undo and Redo go back to the one before.</summary>
public sealed record PendingEdits
{
    public static readonly PendingEdits None = new();

    public ImmutableDictionary<RowKey, ChangedRow> Changed { get; init; } = ImmutableDictionary<RowKey, ChangedRow>.Empty;
    public ImmutableDictionary<RowKey, object?[]> Deleted { get; init; } = ImmutableDictionary<RowKey, object?[]>.Empty;
    public ImmutableList<AddedRow> Added { get; init; } = [];

    public bool IsEmpty => Changed.IsEmpty && Deleted.IsEmpty && Added.IsEmpty;

    /// <summary>How many rows it changes, deletes and adds, said in a line.</summary>
    public string Said => SaidOf("row");

    /// <summary>How many it changes, deletes and adds, said in a line of what a row is (a document).</summary>
    public string SaidOf(string noun) => IsEmpty ? "No changes pending"
        : string.Join(", ", new[] { (Changed.Count, "changed"), (Deleted.Count, "deleted"), (Added.Count, "added") }
            .Where(c => c.Item1 > 0).Select(c => $"{Plural.Of(c.Item1, noun)} {c.Item2}")) + " pending";

    /// <summary>A cell of a row read written, or put back as read where what is written is <paramref name="asRead"/>.</summary>
    public PendingEdits Write(RowKey key, object?[] read, int column, string? text, bool asRead)
    {
        ChangedRow row = Changed.GetValueOrDefault(key) ?? new ChangedRow(read, ImmutableDictionary<int, string?>.Empty);
        ImmutableDictionary<int, string?> written = asRead ? row.Written.Remove(column) : row.Written.SetItem(column, text);
        return this with { Changed = written.IsEmpty ? Changed.Remove(key) : Changed.SetItem(key, row with { Written = written }) };
    }

    /// <summary>A cell of a row added written; a new row added where <paramref name="id"/> is none of them.</summary>
    public PendingEdits WriteAdded(int id, int column, string? text)
    {
        int at = Added.FindIndex(a => a.Id == id);
        return at < 0 ? this with { Added = Added.Add(new AddedRow(id, ImmutableDictionary<int, string?>.Empty.SetItem(column, text))) }
            : this with { Added = Added.SetItem(at, Added[at] with { Written = Added[at].Written.SetItem(column, text) }) };
    }

    /// <summary>Rows read deleted, their cells' changes with them, and rows added taken out.</summary>
    public PendingEdits Delete(IEnumerable<(RowKey Key, object?[] Read)> read, IEnumerable<int> added) => this with
    {
        Deleted = Deleted.SetItems(read.Select(r => KeyValuePair.Create(r.Key, r.Read))),
        Changed = Changed.RemoveRange(read.Select(r => r.Key)),
        Added = Added.RemoveAll(a => added.Contains(a.Id)),
    };

    /// <summary>Whether two values read are the same: arrays and bytes by what they hold, a type of no value equality by its words.</summary>
    public static bool Same(object? a, object? b) => (a, b) switch
    {
        (null or DBNull, null or DBNull) => true,
        (null or DBNull, _) or (_, null or DBNull) => false,
        (IStructuralEquatable, _) => StructuralComparisons.StructuralEqualityComparer.Equals(a, b),
        _ when a.GetType() != b.GetType() => false,
        (string or ValueType, _) => a.Equals(b),
        _ => Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(b, CultureInfo.InvariantCulture),
    };
}

/// <summary>A page of a table's rows with its changes not yet written laid over it: each cell changed showing what was
/// written, the rows deleted marked, the rows added after the page's and an empty row last that a cell written in adds.
/// A table that takes no change (no primary key, a view, a read-only connection) has no empty row and no cell written.
/// One per page read, its changes set as they change, so the grid keeps its columns' widths.</summary>
public sealed class PendingTable(DatabaseRows page, PendingEdits edits, bool editable) : Table
{
    public DatabaseRows Page { get; } = page;
    public PendingEdits Edits { get; set; } = edits;
    public bool Editable { get; set; } = editable;

    public override int RowCount => Page.RowCount + Edits.Added.Count + (Editable ? 1 : 0);
    public override int RecordCount => Page.RowCount + Edits.Added.Count;
    public override int ColumnCount => Page.ColumnCount;
    public override string Header(int column) => Page.Header(column);
    public override (int Column, bool Descending)? Sort => Page.Sort;

    /// <summary>The row read at a place, null for a row added or the empty row.</summary>
    public bool IsRead(int row) => row < Page.RowCount;

    /// <summary>The row added at a place, null for a row read or the empty row.</summary>
    public AddedRow? AddedAt(int row) => row >= Page.RowCount && row - Page.RowCount < Edits.Added.Count ? Edits.Added[row - Page.RowCount] : null;

    public override bool IsNewRow(int row) => Editable && row == RowCount - 1;

    /// <summary>A row read's primary key, null where a key's column holds nothing or the table has none.</summary>
    public RowKey? KeyOf(int row)
    {
        if (!IsRead(row) || Page.Schema is not { } schema || !schema.Columns.Any(c => c.Primary))
        {
            return null;
        }
        object?[] values = [.. schema.Columns.Where(c => c.Primary).Select(c => Array.IndexOf(Page.Columns, c.Name) is >= 0 and int at ? Page.Rows[row][at] : null)];
        return values.Any(v => v is null) ? null : new RowKey(values);
    }

    public override int RowNumber(int row) => Page.Offset + row + 1;

    public override string RowLabel(int row) => IsNewRow(row) ? "+" : base.RowLabel(row);

    public override string Cell(int row, int column)
    {
        if (IsRead(row))
        {
            return KeyOf(row) is { } key && Edits.Changed.TryGetValue(key, out ChangedRow? changed) && changed.Written.TryGetValue(column, out string? text)
                ? Shown(text) : Page.Cell(row, column);
        }
        return AddedAt(row) is { } added && added.Written.TryGetValue(column, out string? written) ? Shown(written) : string.Empty;
    }

    private static string Shown(string? text) => ContainerSecrets.InSaid(text ?? "NULL");

    /// <summary>A cell's text whole, as <see cref="Cell"/> shows it masked: as written and not yet submitted, else as read.</summary>
    public string Whole(int row, int column)
    {
        if (IsRead(row))
        {
            return KeyOf(row) is { } key && Edits.Changed.TryGetValue(key, out ChangedRow? changed) && changed.Written.TryGetValue(column, out string? text)
                ? text ?? "NULL" : Page.Text(row, column);
        }
        return AddedAt(row) is { } added && added.Written.TryGetValue(column, out string? written) ? written ?? "NULL" : string.Empty;
    }

    /// <summary>A cell's value as written and not yet submitted (its text, or none for NULL), else as read.</summary>
    public override object? ValueAt(int row, int column)
    {
        if (IsRead(row))
        {
            return KeyOf(row) is { } key && Edits.Changed.TryGetValue(key, out ChangedRow? changed) && changed.Written.TryGetValue(column, out string? text)
                ? text : Page.ValueAt(row, column);
        }
        return AddedAt(row) is { } added && added.Written.TryGetValue(column, out string? written) ? written : null;
    }

    public override string TypeOf(int column) => Page.TypeOf(column);

    public override RowWriter? Inserts(TextWriter into, string lineBreak) => Page.Inserts(into, lineBreak);

    /// <summary>Whether a cell holds a number: as read, or as written where its column holds numbers and the words read as one,
    /// so a value waiting stands where its column's numbers do.</summary>
    public override bool IsNumber(int row, int column)
    {
        string? written = IsRead(row) ? KeyOf(row) is { } key && Edits.Changed.TryGetValue(key, out ChangedRow? changed) && changed.Written.TryGetValue(column, out string? text) ? text : null
            : AddedAt(row) is { } added && added.Written.TryGetValue(column, out string? given) ? given : null;
        if (written is null)
        {
            return IsRead(row) && Page.IsNumber(row, column);
        }
        return double.TryParse(written, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)
            && Enumerable.Range(0, Page.RowCount).Any(r => Page.IsNumber(r, column));
    }

    public override CellValue ValueOf(int row, int column) => IsRead(row) ? Page.ValueOf(row, column) : new CellValue(CellKind.Text);

    /// <summary>A field a read document lacks, until a cell is written in it.</summary>
    public override bool IsAbsent(int row, int column) => IsRead(row) && Page.IsAbsent(row, column)
        && !(KeyOf(row) is { } key && Edits.Changed.TryGetValue(key, out ChangedRow? changed) && changed.Written.ContainsKey(column));

    public override bool IsEditable(int row, int column)
    {
        if (!Editable || Page.Schema is not { } schema || schema.Columns.FirstOrDefault(c => c.Name == Page.Columns[column]) is not { } shown)
        {
            return false;
        }
        if (!IsRead(row))
        {
            return !shown.Generated;
        }
        return KeyOf(row) is { } key && !Edits.Deleted.ContainsKey(key) && Page.IsEditable(row, column);
    }

    /// <summary>What a row is, as its menus name it: a Row, or a Document of a collection.</summary>
    public string Noun { get; init; } = "Row";

    public override string? DeleteTitle(int rows) => Editable ? $"Delete {Plural.Noun(rows, Noun)}" : null;

    public override CellMark MarkOf(int row, int column)
    {
        if (!IsRead(row))
        {
            return AddedAt(row) is null ? CellMark.None : CellMark.Added;
        }
        if (KeyOf(row) is not { } key)
        {
            return CellMark.None;
        }
        return Edits.Deleted.ContainsKey(key) ? CellMark.Deleted
            : Edits.Changed.TryGetValue(key, out ChangedRow? changed) && changed.Written.ContainsKey(column) ? CellMark.Changed : CellMark.None;
    }

    public override CellMark RowMark(int row)
    {
        if (!IsRead(row))
        {
            return AddedAt(row) is null ? CellMark.None : CellMark.Added;
        }
        return KeyOf(row) is not { } key ? CellMark.None : Edits.Deleted.ContainsKey(key) ? CellMark.Deleted
            : Edits.Changed.ContainsKey(key) ? CellMark.Changed : CellMark.None;
    }
}
