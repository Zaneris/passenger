using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>An AppHost's logs and spans written as the IDE keeps them, a record at a time by one Utf8JsonWriter into one
/// pooled buffer, masked as they are written by ContainerSecrets' rule: a field named as a secret's holds the dots, as does
/// the value of an attribute whose key names one, and every other string a field holds has the dashboard's key and what
/// InValue masks masked; a string an array holds is kept as it is. An attribute or a body (an OTLP AnyValue) is written as
/// its string, or as the JSON of any other value, a string in it masked as InValue masks. Each record's bytes are copied
/// out once, their length what its bounds count. One reader's: a record at a time.</summary>
internal sealed class AspireRecords
{
    /// <summary>The most attributes looked through one by one for another of the same key; more are kept by key.</summary>
    private const int FewAttributes = 64;

    private static readonly JsonEncodedText MaskedValue = JsonEncodedText.Encode(ContainerSecrets.Masked);

    private static readonly JsonElement NoItems = JsonElement.Parse("[]");

    private readonly string apiKey;
    private readonly PooledBytes written = new();
    private readonly Utf8JsonWriter json;
    private readonly PooledBytes anyWritten = new();
    private readonly Utf8JsonWriter any;
    private char[] chars = new char[1024];

    public AspireRecords(string apiKey)
    {
        this.apiKey = apiKey;
        json = new Utf8JsonWriter(written);
        any = new Utf8JsonWriter(anyWritten);
    }

    /// <summary>What an AnyValue is written as: nothing (null), its string, which InValue masks before the field's own
    /// mask, or the JSON of any other value.</summary>
    private enum Said
    {
        Nothing,
        Text,
        Json,
    }

    /// <summary>A log record as kept: its bytes, and the resource and service names written in it.</summary>
    public (byte[] Json, string ResourceName, string ServiceName) Log(JsonElement row, string resource, string service)
    {
        // The attributes are read before the rest, as their reading may fail first.
        CheckAttributes(row);
        Begin();
        Span<char> stamp = stackalloc char[40];
        Field(Names.Timestamp, Timestamp(row, "timeUnixNano", stamp), twice: false);
        Field(Names.Severity, StringField(row, "severityText", out ReadOnlySpan<char> severity) ? severity : "Unknown", twice: false);
        string resourceName = Field(Names.ResourceName, resource);
        string serviceName = Field(Names.ServiceName, service);
        json.WritePropertyName(Names.Message.Text);
        if (Names.Message.Secret)
        {
            json.WriteStringValue(MaskedValue);
        }
        else if (row.TryGetProperty("body", out JsonElement body))
        {
            WriteSaid(Text(body, out ReadOnlySpan<char> message), message);
        }
        else
        {
            json.WriteNullValue();
        }
        Span<char> identity = stackalloc char[32];
        OptionalField(Names.TraceId, Identity(row, "traceId", identity));
        OptionalField(Names.SpanId, Identity(row, "spanId", identity[..16]));
        json.WritePropertyName(Names.Exception.Text);
        if (Names.Exception.Secret)
        {
            json.WriteStringValue(MaskedValue);
        }
        else
        {
            Exception(row);
        }
        json.WritePropertyName(Names.Attributes.Text);
        Attributes(row);
        return (End(), resourceName, serviceName);
    }

    /// <summary>A span as kept, its ids, times and status read already: its bytes, and the ids, source and service written
    /// in it, with its name for a span with no parent.</summary>
    public (byte[] Json, string SpanId, string? ParentSpanId, string? Name, string Source, string Service) Span(JsonElement row, string resource,
        string service, string traceId, string spanId, ulong start, ulong end, int status)
    {
        Begin();
        Field(Names.TraceId, traceId);
        string writtenSpanId = Field(Names.SpanId, spanId);
        Span<char> identity = stackalloc char[16];
        ReadOnlySpan<char> parent = Identity(row, "parentSpanId", identity);
        string? parentSpanId = parent.IsEmpty ? null : Names.ParentSpanId.Secret ? ContainerSecrets.Masked : Masked(new string(parent));
        OptionalField(Names.ParentSpanId, parentSpanId);
        // A trace is titled after its root's name, so only a root keeps its name as a string.
        string? name = null;
        json.WritePropertyName(Names.Name.Text);
        if (!StringField(row, "name", out ReadOnlySpan<char> named))
        {
            json.WriteNullValue();
        }
        else if (Names.Name.Secret || parentSpanId is null)
        {
            name = Names.Name.Secret ? ContainerSecrets.Masked : Masked(new string(named));
            json.WriteStringValue(name);
        }
        else
        {
            Value(named, twice: false);
        }
        string source = Field(Names.Source, resource);
        string writtenService = Field(Names.Service, service);
        Span<char> stamp = stackalloc char[40];
        Field(Names.StartTime, Timestamp(row, "startTimeUnixNano", stamp), twice: false);
        Field(Names.EndTime, Timestamp(row, "endTimeUnixNano", stamp), twice: false);
        Span<char> number = stackalloc char[20];
        Field(Names.StartTimeUnixNano, number[..Formatted(start, number)], twice: false);
        Field(Names.EndTimeUnixNano, number[..Formatted(end, number)], twice: false);
        json.WritePropertyName(Names.DurationMs.Text);
        if (Names.DurationMs.Secret)
        {
            json.WriteStringValue(MaskedValue);
        }
        else
        {
            json.WriteNumberValue((end - start) / 1_000_000d);
        }
        Field(Names.Status, status switch { 1 => "Ok", 2 => "Error", _ => "Unset" }, twice: false);
        CheckAttributes(row);
        json.WritePropertyName(Names.Attributes.Text);
        Attributes(row);
        Carried(Names.Events, row, "events");
        Carried(Names.Links, row, "links");
        return (End(), writtenSpanId, parentSpanId, name, source, writtenService);
    }

    /// <summary>A row's attributes by key (an attribute named twice, its last), each a secret's masked and any other as
    /// its string or its JSON, a string masked as InValue masks.</summary>
    public Dictionary<string, string?> Strings(JsonElement row)
    {
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        foreach (JsonElement item in Items(row, "attributes").EnumerateArray())
        {
            string key = Key(item).GetString()!;
            if (!item.TryGetProperty("value", out JsonElement value))
            {
                throw new InvalidDataException("Aspire telemetry omitted an attribute value.");
            }
            result[key] = ContainerSecrets.Secret(key) ? ContainerSecrets.Masked : Text(value, out ReadOnlySpan<char> text) switch
            {
                Said.Nothing => null,
                Said.Text => ContainerSecrets.InValue(new string(text)),
                _ => new string(text),
            };
        }
        return result;
    }

    /// <summary>An OTLP trace or span id, checked and in small letters, in <paramref name="into"/>, which is its length.</summary>
    public ReadOnlySpan<char> RequiredIdentity(JsonElement row, string field, Span<char> into) =>
        Identity(row, field, into, required: true);

    /// <summary>An OTLP time, nanoseconds since 1970 written as a string of digits.</summary>
    public ulong Nanoseconds(JsonElement row, string field)
    {
        if (row.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && ulong.TryParse(Chars(value), NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
        {
            return parsed;
        }
        throw new InvalidDataException("Aspire telemetry returned an invalid nanosecond timestamp.");
    }

    private void Begin()
    {
        written.Clear();
        json.Reset(written);
        json.WriteStartObject();
    }

    private byte[] End()
    {
        json.WriteEndObject();
        json.Flush();
        return written.Written.ToArray();
    }

    /// <summary>A field of a string the caller holds, written masked: the string written.</summary>
    private string Field(Name name, string value)
    {
        string said = name.Secret ? ContainerSecrets.Masked : Masked(value);
        json.WriteString(name.Text, said);
        return said;
    }

    private void OptionalField(Name name, string? value)
    {
        json.WritePropertyName(name.Text);
        if (value is null)
        {
            json.WriteNullValue();
        }
        else
        {
            json.WriteStringValue(value);
        }
    }

    private void OptionalField(Name name, ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            OptionalField(name, (string?)null);
        }
        else
        {
            Field(name, value, twice: false);
        }
    }

    private void Field(Name name, ReadOnlySpan<char> value, bool twice)
    {
        json.WritePropertyName(name.Text);
        if (name.Secret)
        {
            json.WriteStringValue(MaskedValue);
        }
        else
        {
            Value(value, twice);
        }
    }

    /// <summary>A string field's value as the mask leaves it: InValue's masks and the dashboard's key's (once InValue has
    /// masked it, <paramref name="twice"/>, as an AnyValue's string is).</summary>
    private void Value(ReadOnlySpan<char> value, bool twice)
    {
        if (!value.Contains(apiKey, StringComparison.Ordinal) && !ContainerSecrets.InValueMasks(value))
        {
            json.WriteStringValue(value);
            return;
        }
        string said = twice ? ContainerSecrets.InValue(new string(value)) : new string(value);
        json.WriteStringValue(ContainerSecrets.InValue(said.Replace(apiKey, ContainerSecrets.Masked, StringComparison.Ordinal)));
    }

    /// <summary>A string as <see cref="Value"/> writes it: the string itself when nothing in it is masked.</summary>
    private string Masked(string value) => !value.Contains(apiKey, StringComparison.Ordinal) && !ContainerSecrets.InValueMasks(value) ? value
        : ContainerSecrets.InValue(value.Replace(apiKey, ContainerSecrets.Masked, StringComparison.Ordinal));

    private void WriteSaid(Said said, ReadOnlySpan<char> text)
    {
        if (said == Said.Nothing)
        {
            json.WriteNullValue();
        }
        else
        {
            Value(text, twice: said == Said.Text);
        }
    }

    /// <summary>A log's exception: its last exception.stacktrace attribute's, else its last exception.message's.</summary>
    private void Exception(JsonElement row)
    {
        foreach (string key in (string[])["exception.stacktrace", "exception.message"])
        {
            if (LastNamed(row, key) is { } item)
            {
                if (ContainerSecrets.Secret(key))
                {
                    Value(ContainerSecrets.Masked, twice: false);
                    return;
                }
                Said said = Text(item.GetProperty("value"), out ReadOnlySpan<char> text);
                if (said != Said.Nothing)
                {
                    WriteSaid(said, text);
                    return;
                }
            }
        }
        json.WriteNullValue();
    }

    private static JsonElement? LastNamed(JsonElement row, string key)
    {
        JsonElement? last = null;
        foreach (JsonElement item in Items(row, "attributes").EnumerateArray())
        {
            if (item.GetProperty("key").ValueEquals(key))
            {
                last = item;
            }
        }
        return last;
    }

    /// <summary>Reads a row's attributes as the dictionary of them would, failing where it fails: each an object with a
    /// key and a value, a value of a key no secret's a valid AnyValue.</summary>
    private void CheckAttributes(JsonElement row)
    {
        foreach (JsonElement item in Items(row, "attributes").EnumerateArray())
        {
            JsonElement key = Key(item);
            if (!item.TryGetProperty("value", out JsonElement value))
            {
                throw new InvalidDataException("Aspire telemetry omitted an attribute value.");
            }
            if (!ContainerSecrets.Secret(Chars(key)))
            {
                Any(value, 0, null);
            }
        }
    }

    /// <summary>A row's checked attributes as an object of their keys, an attribute named twice where it was first named
    /// with its last value.</summary>
    private void Attributes(JsonElement row)
    {
        JsonElement items = Items(row, "attributes");
        // An attribute named key holds a secret's value when its own is a secret's name.
        bool secretAttribute = LastNamed(row, "key") is { } keyed && ContainerSecrets.Secret(DictionaryValue(keyed) ?? string.Empty);
        json.WriteStartObject();
        Dictionary<string, (int First, JsonElement Last)>? byKey = items.GetArrayLength() > FewAttributes ? ByKey(items) : null;
        int at = 0;
        foreach (JsonElement item in items.EnumerateArray())
        {
            JsonElement key = item.GetProperty("key");
            if (FirstNamed(items, item, key, at++, byKey) is not { } last)
            {
                continue;
            }
            ReadOnlySpan<char> name = Chars(key);
            bool secret = ContainerSecrets.Secret(name);
            bool masked = (secret && !name.SequenceEqual("key")) || (secretAttribute && name.SequenceEqual("value"));
            json.WritePropertyName(name);
            if (masked)
            {
                json.WriteStringValue(MaskedValue);
            }
            else if (secret)
            {
                Value(ContainerSecrets.Masked, twice: false);
            }
            else
            {
                WriteSaid(Text(last.GetProperty("value"), out ReadOnlySpan<char> text), text);
            }
        }
        json.WriteEndObject();
    }

    /// <summary>The last entry of an entry's key when the entry at <paramref name="at"/> is the first of it; none when an
    /// entry before it has its key. Looked for one by one, or by key in <paramref name="byKey"/> where there are many.</summary>
    private static JsonElement? FirstNamed(JsonElement items, JsonElement item, JsonElement key, int at, Dictionary<string, (int First, JsonElement Last)>? byKey)
    {
        if (byKey is not null)
        {
            (int first, JsonElement last) = byKey[key.GetString()!];
            return first == at ? last : null;
        }
        JsonElement found = item;
        int other = 0;
        foreach (JsonElement next in items.EnumerateArray())
        {
            if (other != at && SameText(next.GetProperty("key"), key))
            {
                if (other < at)
                {
                    return null;
                }
                found = next;
            }
            other++;
        }
        return found;
    }

    /// <summary>Many entries' first place and last entry by key.</summary>
    private static Dictionary<string, (int First, JsonElement Last)> ByKey(JsonElement items)
    {
        Dictionary<string, (int First, JsonElement Last)> byKey = new(StringComparer.Ordinal);
        int at = 0;
        foreach (JsonElement item in items.EnumerateArray())
        {
            string key = item.GetProperty("key").GetString()!;
            byKey[key] = byKey.TryGetValue(key, out (int First, JsonElement Last) was) ? (was.First, item) : (at, item);
            at++;
        }
        return byKey;
    }

    /// <summary>An attribute's value as the dictionary of them holds it.</summary>
    private string? DictionaryValue(JsonElement item)
    {
        string key = item.GetProperty("key").GetString()!;
        return ContainerSecrets.Secret(key) ? ContainerSecrets.Masked : Text(item.GetProperty("value"), out ReadOnlySpan<char> text) switch
        {
            Said.Nothing => null,
            Said.Text => ContainerSecrets.InValue(new string(text)),
            _ => new string(text),
        };
    }

    /// <summary>A value a span carries as the dashboard sent it (its events, its links), masked: an object's fields as a
    /// record's, a string an array holds as it is, a number as it was written.</summary>
    private void Carried(Name name, JsonElement row, string field)
    {
        json.WritePropertyName(name.Text);
        if (name.Secret)
        {
            json.WriteStringValue(MaskedValue);
        }
        else if (!row.TryGetProperty(field, out JsonElement value))
        {
            json.WriteNullValue();
        }
        else
        {
            try
            {
                // Written whole first, as only a value every string of which is whole is carried, masked or not.
                anyWritten.Clear();
                any.Reset(anyWritten);
                value.WriteTo(any);
            }
            catch (Exception unpaired) when (unpaired is InvalidOperationException or ArgumentException)
            {
                throw new JsonException($"The object or value could not be serialized. Path: $.{field}.", unpaired);
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                Value(Chars(value), twice: false);
            }
            else
            {
                Carried(value);
            }
        }
    }

    private void Carried(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                bool secretAttribute = value.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String && ContainerSecrets.Secret(Chars(key));
                json.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    ReadOnlySpan<char> name = PropertyChars(property);
                    bool masked = (!name.SequenceEqual("key") && ContainerSecrets.Secret(name)) || (secretAttribute && name.SequenceEqual("value"));
                    json.WritePropertyName(name);
                    if (masked)
                    {
                        json.WriteStringValue(MaskedValue);
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        Value(Chars(property.Value), twice: false);
                    }
                    else
                    {
                        Carried(property.Value);
                    }
                }
                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Carried(item);
                    }
                    else
                    {
                        item.WriteTo(json);
                    }
                }
                json.WriteEndArray();
                break;
            default:
                value.WriteTo(json);
                break;
        }
    }

    /// <summary>What an AnyValue is written as, and its text: a string's own, or the JSON of any other value.</summary>
    private Said Text(JsonElement value, out ReadOnlySpan<char> text)
    {
        text = default;
        if (value.ValueKind == JsonValueKind.Null || OnlyVariant(value) is not { } field)
        {
            return Said.Nothing;
        }
        if ((field.NameEquals("stringValue"u8) || field.NameEquals("bytesValue"u8)) && field.Value.ValueKind == JsonValueKind.String)
        {
            text = Chars(field.Value);
            return Said.Text;
        }
        if (field.NameEquals("doubleValue"u8) && field.Value.ValueKind == JsonValueKind.String && Literal(field.Value) is { } literal)
        {
            text = literal;
            return Said.Json;
        }
        anyWritten.Clear();
        any.Reset(anyWritten);
        Any(value, 0, any);
        any.Flush();
        ReadOnlySpan<byte> utf8 = anyWritten.Written;
        Ensure(utf8.Length);
        text = chars.AsSpan(0, Encoding.UTF8.GetChars(utf8, chars));
        return Said.Json;
    }

    /// <summary>An AnyValue checked as it is read, and written when a writer is given: null, a string (masked as InValue
    /// masks), a boolean, an integer, a double, an array or a key-value list (a key named twice where it was first named
    /// with its last value, a secret's key's value the dots); at most 16 deep and 1,024 to a list.</summary>
    private void Any(JsonElement value, int depth, Utf8JsonWriter? into)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            into?.WriteNullValue();
            return;
        }
        if (depth >= 16)
        {
            throw new InvalidDataException("Aspire telemetry AnyValue nesting exceeded 16 levels.");
        }
        if (OnlyVariant(value) is not { } field)
        {
            into?.WriteNullValue();
            return;
        }
        JsonElement said = field.Value;
        if ((field.NameEquals("stringValue"u8) || field.NameEquals("bytesValue"u8)) && said.ValueKind == JsonValueKind.String)
        {
            ReadOnlySpan<char> text = Chars(said);
            if (into is not null)
            {
                if (ContainerSecrets.InValueMasks(text))
                {
                    into.WriteStringValue(ContainerSecrets.InValue(new string(text)));
                }
                else
                {
                    into.WriteStringValue(text);
                }
            }
        }
        else if (field.NameEquals("boolValue"u8) && said.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            into?.WriteBooleanValue(said.GetBoolean());
        }
        else if (field.NameEquals("intValue"u8) && said.ValueKind == JsonValueKind.String
            && long.TryParse(Chars(said), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            into?.WriteNumberValue(integer);
        }
        else if (field.NameEquals("intValue"u8) && said.ValueKind == JsonValueKind.Number && said.TryGetInt64(out long numericInteger))
        {
            into?.WriteNumberValue(numericInteger);
        }
        else if (field.NameEquals("doubleValue"u8) && said.ValueKind == JsonValueKind.Number && said.TryGetDouble(out double number) && double.IsFinite(number))
        {
            into?.WriteNumberValue(number);
        }
        else if (field.NameEquals("doubleValue"u8) && said.ValueKind == JsonValueKind.String && Literal(said) is { } literal)
        {
            into?.WriteStringValue(literal);
        }
        else if (field.NameEquals("arrayValue"u8))
        {
            JsonElement items = AnyItems(said);
            into?.WriteStartArray();
            foreach (JsonElement item in items.EnumerateArray())
            {
                Any(item, depth + 1, into);
            }
            into?.WriteEndArray();
        }
        else if (field.NameEquals("kvlistValue"u8))
        {
            KeyValues(AnyItems(said), depth, into);
        }
        else
        {
            throw new InvalidDataException("Aspire telemetry returned an invalid AnyValue variant.");
        }
    }

    /// <summary>A key-value list's entries, all checked before any is written, as each was read before the list was.</summary>
    private void KeyValues(JsonElement items, int depth, Utf8JsonWriter? into)
    {
        foreach (JsonElement item in items.EnumerateArray())
        {
            JsonElement key = Key(item);
            if (!item.TryGetProperty("value", out JsonElement child))
            {
                throw new InvalidDataException("Aspire telemetry omitted an AnyValue attribute value.");
            }
            if (!ContainerSecrets.Secret(Chars(key)))
            {
                Any(child, depth + 1, null);
            }
        }
        if (into is null)
        {
            return;
        }
        into.WriteStartObject();
        Dictionary<string, (int First, JsonElement Last)>? byKey = items.GetArrayLength() > FewAttributes ? ByKey(items) : null;
        int at = 0;
        foreach (JsonElement item in items.EnumerateArray())
        {
            JsonElement key = item.GetProperty("key");
            if (FirstNamed(items, item, key, at++, byKey) is not { } last)
            {
                continue;
            }
            ReadOnlySpan<char> name = Chars(key);
            bool secret = ContainerSecrets.Secret(name);
            into.WritePropertyName(name);
            if (secret)
            {
                into.WriteStringValue(ContainerSecrets.Masked);
            }
            else
            {
                Any(last.GetProperty("value"), depth + 1, into);
            }
        }
        into.WriteEndObject();
    }

    /// <summary>An AnyValue's one field that is not null; none when it has none, and more refused.</summary>
    private static JsonProperty? OnlyVariant(JsonElement value)
    {
        JsonProperty? only = null;
        foreach (JsonProperty field in AspireJson.Object(value).EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (only is not null)
            {
                throw new InvalidDataException("Aspire telemetry returned multiple AnyValue variants.");
            }
            only = field;
        }
        return only;
    }

    private static string? Literal(JsonElement value) =>
        value.ValueEquals("NaN"u8) ? "NaN" : value.ValueEquals("Infinity"u8) ? "Infinity" : value.ValueEquals("-Infinity"u8) ? "-Infinity" : null;

    private static JsonElement AnyItems(JsonElement value)
    {
        JsonElement items = Items(value, "values");
        if (items.GetArrayLength() > 1024)
        {
            throw new InvalidDataException("Aspire telemetry AnyValue collection exceeded 1024 entries.");
        }
        return items;
    }

    /// <summary>An object's array field as <see cref="AspireJson.Array"/> reads it: an empty array when it is missing or
    /// null.</summary>
    private static JsonElement Items(JsonElement value, string name)
    {
        if (!AspireJson.Object(value).TryGetProperty(name, out JsonElement items) || items.ValueKind == JsonValueKind.Null)
        {
            return NoItems;
        }
        return items.ValueKind == JsonValueKind.Array ? items : throw new InvalidDataException($"Aspire JSON field '{name}' must be an array.");
    }

    /// <summary>An entry's key as <see cref="AspireJson.RequiredString"/> reads it, a string of something.</summary>
    private static JsonElement Key(JsonElement item)
    {
        if (!AspireJson.Object(item).TryGetProperty("key", out JsonElement key) || key.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("Aspire JSON is missing 'key'.");
        }
        if (key.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Aspire JSON field 'key' must be a string.");
        }
        return key.ValueEquals(""u8) ? throw new InvalidDataException("Aspire JSON is missing 'key'.") : key;
    }

    /// <summary>A string field as <see cref="AspireJson.String"/> reads it: false when it is missing or null.</summary>
    private bool StringField(JsonElement row, string name, out ReadOnlySpan<char> text)
    {
        text = default;
        if (!AspireJson.Object(row).TryGetProperty(name, out JsonElement field) || field.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        text = field.ValueKind == JsonValueKind.String ? Chars(field) : throw new InvalidDataException($"Aspire JSON field '{name}' must be a string.");
        return true;
    }

    /// <summary>An optional OTLP trace or span id, checked and in small letters, in <paramref name="into"/>, its length
    /// the id's; empty when it is missing or empty.</summary>
    private ReadOnlySpan<char> Identity(JsonElement row, string field, Span<char> into, bool required = false)
    {
        bool given = StringField(row, field, out ReadOnlySpan<char> value);
        if (!required && value.IsEmpty)
        {
            return [];
        }
        if (!given || value.Length != into.Length || value.ContainsAnyExcept(Hex))
        {
            throw new InvalidDataException("Aspire telemetry returned an invalid trace or span identity.");
        }
        value.ToLowerInvariant(into);
        return into;
    }

    private static readonly System.Buffers.SearchValues<char> Hex = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    private ReadOnlySpan<char> Timestamp(JsonElement row, string field, Span<char> into)
    {
        ulong ticks = Nanoseconds(row, field) / 100;
        if (ticks > (ulong)(DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UnixEpoch.Ticks))
        {
            throw new InvalidDataException("Aspire telemetry timestamp is outside the supported date range.");
        }
        DateTimeOffset.UnixEpoch.AddTicks((long)ticks).TryFormat(into, out int length, "O", CultureInfo.InvariantCulture);
        return into[..length];
    }

    private static int Formatted(ulong value, Span<char> into)
    {
        value.TryFormat(into, out int length, provider: CultureInfo.InvariantCulture);
        return length;
    }

    /// <summary>Whether two JSON strings say the same.</summary>
    private static bool SameText(JsonElement one, JsonElement other)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(one);
        ReadOnlySpan<byte> otherRaw = JsonMarshal.GetRawUtf8Value(other);
        return raw.Contains((byte)'\\') || otherRaw.Contains((byte)'\\') ? one.GetString() == other.GetString() : raw.SequenceEqual(otherRaw);
    }

    /// <summary>A JSON string's text, unescaped into the scratch characters, until the next asked for.</summary>
    private ReadOnlySpan<char> Chars(JsonElement value)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(value);
        Ensure(raw.Length);
        Utf8JsonReader reader = new(raw);
        reader.Read();
        return chars.AsSpan(0, reader.CopyString(chars));
    }

    /// <summary>A property's name, unescaped into the scratch characters, until the next asked for.</summary>
    private ReadOnlySpan<char> PropertyChars(JsonProperty property)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8PropertyName(property);
        if (raw.Contains((byte)'\\'))
        {
            return property.Name;
        }
        Ensure(raw.Length);
        return chars.AsSpan(0, Encoding.UTF8.GetChars(raw, chars));
    }

    private void Ensure(int length)
    {
        if (chars.Length < length)
        {
            chars = new char[Math.Max(length, chars.Length * 2)];
        }
    }

    /// <summary>A record's field: its name as written and whether it names a secret's value.</summary>
    private readonly record struct Name(JsonEncodedText Text, bool Secret)
    {
        public static Name Of(string name) => new(JsonEncodedText.Encode(name), ContainerSecrets.Secret(name));
    }

    /// <summary>The fields logs and spans are written with.</summary>
    private static class Names
    {
        public static readonly Name Timestamp = Name.Of("timestamp");
        public static readonly Name Severity = Name.Of("severity");
        public static readonly Name ResourceName = Name.Of("resourceName");
        public static readonly Name ServiceName = Name.Of("serviceName");
        public static readonly Name Message = Name.Of("message");
        public static readonly Name TraceId = Name.Of("traceId");
        public static readonly Name SpanId = Name.Of("spanId");
        public static readonly Name Exception = Name.Of("exception");
        public static readonly Name Attributes = Name.Of("attributes");
        public static readonly Name ParentSpanId = Name.Of("parentSpanId");
        public static readonly Name Name = Of("name");
        public static readonly Name Source = Of("source");
        public static readonly Name Service = Of("service");
        public static readonly Name StartTime = Of("startTime");
        public static readonly Name EndTime = Of("endTime");
        public static readonly Name StartTimeUnixNano = Of("startTimeUnixNano");
        public static readonly Name EndTimeUnixNano = Of("endTimeUnixNano");
        public static readonly Name DurationMs = Of("durationMs");
        public static readonly Name Status = Of("status");
        public static readonly Name Events = Of("events");
        public static readonly Name Links = Of("links");

        private static Name Of(string name) => Aspire.AspireRecords.Name.Of(name);
    }
}
