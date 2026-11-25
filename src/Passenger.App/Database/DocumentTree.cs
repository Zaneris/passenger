using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>A field of a document as its tree shows it: its name (an array's item by its place), its value or how many it
/// holds, and the fields or items under it.</summary>
public sealed class DocumentRow(string name, string value, string kind, int depth) : Observable, ITreeRow
{
    private bool expanded;

    public string Name { get; } = name;

    /// <summary>A plain value as written, or what a document or array holds ("3 fields", "2 items").</summary>
    public string Value { get; } = value;

    /// <summary>The kind of value: string, number, bool, null, object, array, or MongoDB's own (objectId, date).</summary>
    public string Kind { get; } = kind;

    public int Depth { get; } = depth;
    public List<DocumentRow> Children { get; } = [];
    public bool Expands => Children.Count > 0;
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
}

/// <summary>A document as an expandable tree of its fields beside the grid of its collection's fields: each field's name,
/// value and kind, a document or array opening to what it holds; MongoDB's own values (an object id, a date) shown as
/// their shell writes them rather than as the JSON standing for them. The document's top level opens at once.</summary>
public sealed class DocumentTree : DockPanel
{
    private readonly TreeList list = new() { Name = "DocumentTree", Classes = { StyleClasses.Tree } };
    private readonly TreeRows<DocumentRow> rows = [];
    private readonly EmptyState empty = new() { Text = "Choose a document to see its fields." };
    private string? shown;

    public DocumentTree()
    {
        list.ItemsSource = rows;
        ReusedRows.Use(list, Row);
        TreeKeys.Attach<DocumentRow>(list, Toggle);
        Children.Add(new Panel { Children = { list, empty } });
    }

    /// <summary>The rows shown, for a test to read.</summary>
    public IReadOnlyList<DocumentRow> Rows => rows;

    /// <summary>Shows a document, given as JSON; none clears the tree.</summary>
    public void Show(string? json)
    {
        if (json == shown)
        {
            return;
        }
        shown = json;
        List<DocumentRow> top = [];
        if (json is not null)
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject document)
                {
                    top.AddRange(document.Select(field => Of(field.Key, field.Value, 0)));
                }
            }
            catch (JsonException unread)
            {
                top.Add(new DocumentRow("document", unread.Message, "error", 0));
            }
        }
        rows.Show([.. TreeRows<DocumentRow>.Shown(top, r => r.IsExpanded ? r.Children : null)]);
        empty.IsVisible = rows.Count == 0;
    }

    /// <summary>A field's row and those under it, MongoDB's extended JSON ({"$oid": …}, {"$date": …}) read as the value it stands for.</summary>
    private static DocumentRow Of(string name, JsonNode? value, int depth)
    {
        switch (value)
        {
            case JsonObject { Count: 1 } own when own.First() is { Key: var key, Value: var inner } && Extended(key, inner) is { } said:
                return new DocumentRow(name, said.Value, said.Kind, depth);
            case JsonObject document:
                DocumentRow holder = new(name, Plural.Of(document.Count, "field"), "object", depth);
                holder.Children.AddRange(document.Select(field => Of(field.Key, field.Value, depth + 1)));
                return holder;
            case JsonArray items:
                DocumentRow list = new(name, Plural.Of(items.Count, "item"), "array", depth);
                list.Children.AddRange(items.Select((item, at) => Of($"[{at.ToString(CultureInfo.InvariantCulture)}]", item, depth + 1)));
                return list;
            case JsonValue plain:
                return plain.GetValueKind() switch
                {
                    JsonValueKind.String => new DocumentRow(name, plain.GetValue<string>(), "string", depth),
                    JsonValueKind.True or JsonValueKind.False => new DocumentRow(name, plain.ToJsonString(), "bool", depth),
                    _ => new DocumentRow(name, plain.ToJsonString(), "number", depth),
                };
            default:
                return new DocumentRow(name, "null", "null", depth);
        }
    }

    /// <summary>A value MongoDB's extended JSON writes as a document of one $ field, as its shell writes it; null for another document.</summary>
    private static (string Value, string Kind)? Extended(string key, JsonNode? inner) => (key, inner) switch
    {
        ("$oid", JsonValue id) when id.GetValueKind() == JsonValueKind.String => ($"ObjectId(\"{id.GetValue<string>()}\")", "objectId"),
        ("$date", JsonValue at) => (at.GetValueKind() == JsonValueKind.String ? at.GetValue<string>() : at.ToJsonString(), "date"),
        ("$date", JsonObject { Count: 1 } wrapped) when wrapped["$numberLong"] is JsonValue ticks => (ticks.ToJsonString(), "date"),
        ("$numberDecimal" or "$numberLong" or "$numberInt" or "$numberDouble", JsonValue number) =>
            (number.GetValueKind() == JsonValueKind.String ? number.GetValue<string>() : number.ToJsonString(), "number"),
        ("$binary", _) => ("binary", "binary"),
        ("$regularExpression", JsonObject regex) => ($"/{regex["pattern"]?.GetValue<string>()}/{regex["options"]?.GetValue<string>()}", "regex"),
        ("$timestamp", _) => (inner!.ToJsonString(), "timestamp"),
        _ => null,
    };

    private void Toggle(DocumentRow row)
    {
        if (!row.Expands)
        {
            return;
        }
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded)
        {
            rows.Open(row, TreeRows<DocumentRow>.Shown(row.Children, r => r.IsExpanded ? r.Children : null));
        }
        else
        {
            rows.Close(row);
        }
    }

    private Control Row()
    {
        RowGlyphs glyphs = new();
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock value = MutedText.Of(string.Empty);
        value.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        value.VerticalAlignment = VerticalAlignment.Center;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        TextBlock kind = MutedText.Of(string.Empty);
        kind.Margin = new Thickness(Gaps.Wide, 0, 0, 0);
        kind.VerticalAlignment = VerticalAlignment.Center;
        DockPanel line = new() { Height = ListRow.Height, Children = { glyphs, kind, name, value } };
        SetDock(glyphs, Avalonia.Controls.Dock.Left);
        SetDock(kind, Avalonia.Controls.Dock.Right);
        SetDock(name, Avalonia.Controls.Dock.Left);
        glyphs.ChevronPressed += (_, e) =>
        {
            if (line.DataContext is DocumentRow row)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        return line.Showing<DocumentRow>(row =>
        {
            line.Margin = new Thickness(RowExpander.Column * row.Depth, 0, Gaps.Normal, 0);
            glyphs.HasChevron = row.Expands;
            glyphs.Turned = row.IsExpanded;
            glyphs.IconKey = row.Kind switch
            {
                "object" => IconKeys.DatabaseTable,
                "array" => IconKeys.SolutionFolder,
                _ => IconKeys.DatabaseColumn,
            };
            name.Text = ContainerSecrets.InSaid(row.Name);
            value.Text = ContainerSecrets.InSaid(row.Expands ? row.Value : Shortened(row.Value));
            kind.Text = row.Kind;
        });
    }

    private static string Shortened(string value) => value.Length > 300 ? value[..300] + "…" : value;
}
