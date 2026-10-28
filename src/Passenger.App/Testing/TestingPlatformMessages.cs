using System.Text.Json;
using System.Text.Json.Serialization;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>A test node as Microsoft.Testing.Platform tells one, read from its properties ("location.type",
/// "execution-state", ...).</summary>
internal sealed record PlatformNode(string Uid, string DisplayName, bool IsTest, string? State, string FullyQualifiedName)
{
    public string? File { get; init; }

    /// <summary>The line the framework says it is on, from one (xUnit and MSTest say their attribute's).</summary>
    public int Line { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Traits { get; init; } = [];
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    public string? Output { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>The files its result carries, each a node's FileArtifactProperty, which the platform writes as
    /// "attachments.N.uri" (the file's path) and "attachments.N.display-name" (the test's name for it).</summary>
    public IReadOnlyList<TestAttachment> Attachments { get; init; } = [];

    /// <summary>An attempt that a retry of the same test follows, so not its result.</summary>
    public bool Superseded { get; init; }
}

/// <summary>The platform's testing/testUpdates/tests: the test nodes a request changed or, with none, the request's end.
/// Read straight from the bytes it came in by <see cref="Reader"/>, the names its nodes share (a file, a type, a method, a
/// trait) read once and handed out again (<see cref="SharedNames"/>); a document of it is made only while protocol traces
/// are on, for its trace.</summary>
[JsonConverter(typeof(Reader))]
internal sealed class TestUpdate
{
    /// <summary>The request it is of; null when its runId is not one.</summary>
    public Guid? Run { get; private init; }

    /// <summary>The nodes changed, each with a uid; null when it has no changes, which ends its request.</summary>
    public IReadOnlyList<PlatformNode>? Changes { get; private init; }

    /// <summary>Its JSON as it came, kept while protocol traces are on.</summary>
    public string? Json { get; private init; }

    /// <summary>An update read property by property: a property not a string where a string is read, or not a number
    /// where a number is, is taken for none, and a property named twice says what its last says.</summary>
    private sealed class Reader : JsonConverter<TestUpdate>
    {
        /// <summary>The longest property name read on the stack: any the reader looks for is shorter, even with each of its
        /// characters escaped (\u0075, six bytes).</summary>
        private const int LongestName = 64 * 6;

        public override TestUpdate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? json = IdeLog.Traces ? TracedJson.Text(reader) : null;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return new TestUpdate { Json = json };
            }
            Guid? run = null;
            List<PlatformNode>? changes = null;
            Located located = new();
            Span<byte> unescaped = stackalloc byte[LongestName];
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                ReadOnlySpan<byte> name = unescaped[..Name(ref reader, unescaped)];
                reader.Read();
                if (name.SequenceEqual("runId"u8))
                {
                    run = reader.TokenType == JsonTokenType.String ? RunOf(ref reader) : null;
                }
                else if (name.SequenceEqual("changes"u8))
                {
                    changes = reader.TokenType == JsonTokenType.StartArray ? Changes(ref reader, located) : null;
                }
                reader.Skip();
            }
            return new TestUpdate { Run = run, Changes = changes, Json = json };
        }

        public override void Write(Utf8JsonWriter writer, TestUpdate value, JsonSerializerOptions options) =>
            throw new NotSupportedException("A test update is only read");

        /// <summary>The length of the property name the reader is on, unescaped into <paramref name="into"/>; none when
        /// longer than any looked for.</summary>
        private static int Name(ref Utf8JsonReader reader, scoped Span<byte> into)
        {
            long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            return length > into.Length ? 0 : reader.CopyString(into);
        }

        /// <summary>A runId as <see cref="Guid.TryParse(string, out Guid)"/> reads it.</summary>
        private static Guid? RunOf(ref Utf8JsonReader reader)
        {
            long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if (length > 256)
            {
                return Guid.TryParse(reader.GetString(), out Guid written) ? written : null;
            }
            Span<char> text = stackalloc char[256];
            return Guid.TryParse(text[..reader.CopyString(text)], out Guid run) ? run : null;
        }

        private static List<PlatformNode> Changes(ref Utf8JsonReader reader, Located located)
        {
            List<PlatformNode> nodes = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject && Change(ref reader, located) is { } node)
                {
                    nodes.Add(node);
                }
                reader.Skip();
            }
            return nodes;
        }

        /// <summary>A change's node, the reader left at the change's end; null for one without a node of a uid.</summary>
        private static PlatformNode? Change(ref Utf8JsonReader reader, Located located)
        {
            PlatformNode? node = null;
            Span<byte> unescaped = stackalloc byte[LongestName];
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isNode = unescaped[..Name(ref reader, unescaped)].SequenceEqual("node"u8);
                reader.Read();
                if (isNode)
                {
                    node = reader.TokenType == JsonTokenType.StartObject ? Node(ref reader, located) : null;
                }
                reader.Skip();
            }
            return node;
        }

        /// <summary>A node, the reader left at its end; null for one without a uid.</summary>
        private static PlatformNode? Node(ref Utf8JsonReader reader, Located located)
        {
            string? uid = null, display = null, type = null, space = null, method = null, vstestName = null, file = null, state = null;
            string? message = null, stackTrace = null, standardOutput = null, standardError = null;
            double? line = null, duration = null;
            bool group = false, superseded = false;
            KeyValuePair<string, string>[]? traits = null;
            List<(int At, string? Uri, string? Name)>? attachments = null;
            Span<byte> unescaped = stackalloc byte[LongestName];
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                ReadOnlySpan<byte> name = unescaped[..Name(ref reader, unescaped)];
                reader.Read();
                bool text = reader.TokenType == JsonTokenType.String;
                if (name.SequenceEqual("uid"u8))
                {
                    uid = text ? reader.GetString() : null;
                }
                else if (name.SequenceEqual("display-name"u8))
                {
                    display = text ? reader.GetString() : null;
                }
                else if (name.SequenceEqual("node-type"u8))
                {
                    group = text && reader.ValueTextEquals("group"u8);
                }
                else if (name.SequenceEqual("execution-state"u8))
                {
                    state = text ? SharedNames.Read(ref reader) : null;
                }
                else if (name.SequenceEqual("location.file"u8))
                {
                    file = text ? SharedNames.Read(ref reader) : null;
                }
                else if (name.SequenceEqual("location.type"u8))
                {
                    type = text ? SharedNames.Read(ref reader) : null;
                }
                else if (name.SequenceEqual("location.namespace"u8))
                {
                    space = text ? SharedNames.Read(ref reader) : null;
                }
                else if (name.SequenceEqual("location.method"u8))
                {
                    method = text ? SharedNames.Read(ref reader) : null;
                }
                else if (name.SequenceEqual("vstest.TestCase.FullyQualifiedName"u8))
                {
                    vstestName = text ? SharedNames.Read(ref reader) : null;
                }
                else if (name.SequenceEqual("location.line-start"u8))
                {
                    line = Number(ref reader);
                }
                else if (name.SequenceEqual("time.duration-ms"u8))
                {
                    duration = Number(ref reader);
                }
                else if (name.SequenceEqual("traits"u8))
                {
                    traits = reader.TokenType == JsonTokenType.StartArray ? Traits(ref reader) : null;
                }
                else if (name.SequenceEqual("error.message"u8))
                {
                    message = text ? reader.GetString() : null;
                }
                else if (name.SequenceEqual("error.stacktrace"u8))
                {
                    stackTrace = text ? reader.GetString() : null;
                }
                else if (name.SequenceEqual("standardOutput"u8))
                {
                    standardOutput = text ? reader.GetString() : null;
                }
                else if (name.SequenceEqual("standardError"u8))
                {
                    standardError = text ? reader.GetString() : null;
                }
                else if (name.SequenceEqual("retry.is-superseded"u8))
                {
                    superseded = reader.TokenType == JsonTokenType.True;
                }
                else if (Attachment(name) is { } attachment)
                {
                    attachments ??= [];
                    int at = IndexOf(attachments, attachment.At);
                    (int At, string? Uri, string? Name) was = at >= 0 ? attachments[at] : (attachment.At, null, null);
                    string? said = text ? (attachment.IsUri ? reader.GetString() : SharedNames.Read(ref reader)) : null;
                    (int At, string? Uri, string? Name) now = attachment.IsUri ? was with { Uri = said } : was with { Name = said };
                    if (at >= 0)
                    {
                        attachments[at] = now;
                    }
                    else
                    {
                        attachments.Add(now);
                    }
                }
                reader.Skip();
            }
            if (uid is not { Length: > 0 })
            {
                return null;
            }
            string shown = display ?? uid;
            string? output = string.IsNullOrEmpty(standardOutput) ? string.IsNullOrEmpty(standardError) ? null : standardError
                : string.IsNullOrEmpty(standardError) ? standardOutput : $"{standardOutput}\n{standardError}";
            return new PlatformNode(uid, shown, !group, state, located.Name(type, space, method) ?? vstestName ?? shown)
            {
                File = file,
                Line = line is { } start and > 0 ? (int)start : 0,
                Traits = traits ?? [],
                Message = message,
                StackTrace = stackTrace,
                Output = output,
                Duration = duration is { } ms and >= 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero,
                Superseded = superseded,
                Attachments = Attachments(attachments),
            };
        }

        private static double? Number(ref Utf8JsonReader reader) =>
            reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double number) ? number : null;

        /// <summary>"attachments.N.uri" or "attachments.N.display-name": N as an int is written, with no zero before it.</summary>
        private static (int At, bool IsUri)? Attachment(ReadOnlySpan<byte> name)
        {
            if (!name.StartsWith("attachments."u8))
            {
                return null;
            }
            ReadOnlySpan<byte> rest = name["attachments."u8.Length..];
            int dot = rest.IndexOf((byte)'.');
            // Past nine digits an attachment follows a billion others, which no node carries.
            if (dot is <= 0 or > 9 || (dot > 1 && rest[0] == (byte)'0') || rest[..dot].ContainsAnyExceptInRange((byte)'0', (byte)'9'))
            {
                return null;
            }
            int at = 0;
            foreach (byte digit in rest[..dot])
            {
                at = (at * 10) + (digit - '0');
            }
            ReadOnlySpan<byte> which = rest[(dot + 1)..];
            return which.SequenceEqual("uri"u8) ? (at, true) : which.SequenceEqual("display-name"u8) ? (at, false) : null;
        }

        /// <summary>The files a node carries, from attachment 0 on while each has a path.</summary>
        private static IReadOnlyList<TestAttachment> Attachments(List<(int At, string? Uri, string? Name)>? said)
        {
            if (said is null)
            {
                return [];
            }
            List<TestAttachment> read = [];
            for (int at = 0; IndexOf(said, at) is var found and >= 0 && said[found].Uri is { Length: > 0 } file; at++)
            {
                read.Add(TestAttachment.Of(said[found].Name ?? string.Empty, file));
            }
            return read;
        }

        private static int IndexOf(List<(int At, string? Uri, string? Name)> attachments, int at)
        {
            for (int i = 0; i < attachments.Count; i++)
            {
                if (attachments[i].At == at)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>A node's traits: each an object of one name and its value (a category alone has none, and is a
        /// Category), or of Key and Value; the reader left at the array's end.</summary>
        private static KeyValuePair<string, string>[] Traits(ref Utf8JsonReader reader)
        {
            Utf8JsonReader counting = reader;
            int count = 0;
            while (counting.Read() && counting.TokenType != JsonTokenType.EndArray)
            {
                if (counting.TokenType == JsonTokenType.StartObject)
                {
                    count += Trait(ref counting, []);
                }
                counting.Skip();
            }
            if (count == 0)
            {
                reader.Skip();
                return [];
            }
            KeyValuePair<string, string>[] traits = new KeyValuePair<string, string>[count];
            int at = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    at += Trait(ref reader, traits.AsSpan(at));
                }
                reader.Skip();
            }
            return traits;
        }

        /// <summary>How many traits one trait object makes, written into <paramref name="into"/> unless it is empty; the
        /// reader left at the object's end. Of two properties, one a Key, it is that key and its value.</summary>
        private static int Trait(ref Utf8JsonReader reader, Span<KeyValuePair<string, string>> into)
        {
            Utf8JsonReader looking = reader;
            int properties = 0;
            bool keyed = false;
            while (looking.Read() && looking.TokenType == JsonTokenType.PropertyName)
            {
                properties++;
                bool key = looking.ValueTextEquals("Key"u8) || looking.ValueTextEquals("key"u8);
                looking.Read();
                keyed |= key && looking.TokenType == JsonTokenType.String;
                looking.Skip();
            }
            bool pair = properties == 2 && keyed;
            int made = pair ? 1 : properties;
            if (into.IsEmpty)
            {
                reader.Skip();
                return made;
            }
            string? keyUpper = null, keyLower = null, valueUpper = null, valueLower = null;
            int at = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (pair)
                {
                    bool isKeyUpper = reader.ValueTextEquals("Key"u8), isKeyLower = reader.ValueTextEquals("key"u8);
                    bool isValueUpper = reader.ValueTextEquals("Value"u8), isValueLower = reader.ValueTextEquals("value"u8);
                    reader.Read();
                    string? said = reader.TokenType == JsonTokenType.String && (isKeyUpper || isKeyLower || isValueUpper || isValueLower) ? SharedNames.Read(ref reader) : null;
                    keyUpper = isKeyUpper ? said : keyUpper;
                    keyLower = isKeyLower ? said : keyLower;
                    valueUpper = isValueUpper ? said : valueUpper;
                    valueLower = isValueLower ? said : valueLower;
                }
                else
                {
                    string name = SharedNames.Read(ref reader)!;
                    reader.Read();
                    bool valued = reader.TokenType == JsonTokenType.String && (reader.HasValueSequence ? !reader.ValueSequence.IsEmpty : !reader.ValueSpan.IsEmpty);
                    into[at++] = valued ? new(name, SharedNames.Read(ref reader)!) : new("Category", name);
                }
                reader.Skip();
            }
            if (pair)
            {
                into[0] = new(keyUpper ?? keyLower!, valueUpper ?? valueLower ?? string.Empty);
            }
            return made;
        }
    }

    /// <summary>The full name a node's location makes ("Space.Type.Method"), kept for the next node of the same location,
    /// as a theory's rows are; null where the location names no type or method.</summary>
    private sealed class Located
    {
        private string? type, space, method, name;
        private bool known;

        public string? Name(string? type, string? space, string? method)
        {
            if (known && type == this.type && space == this.space && method == this.method)
            {
                return name;
            }
            string? full = type;
            // The platform's type is the namespace's and the type's names together; before 1.9 the namespace came apart.
            if (space is { Length: > 0 } && full is not null && !(full.Length > space.Length && full[space.Length] == '.' && full.StartsWith(space, StringComparison.Ordinal)))
            {
                full = $"{space}.{full}";
            }
            string? named = method is null ? null : TestItem.MethodOf(method);
            (this.type, this.space, this.method, known) = (type, space, method, true);
            name = full is { Length: > 0 } && named is { Length: > 0 } ? $"{full}.{named}" : null;
            return name;
        }
    }
}

/// <summary>A message the IDE reads nothing of but its trace: its JSON as it came while protocol traces are on, else passed
/// over unread.</summary>
[JsonConverter(typeof(Reader))]
internal sealed class TracedJson
{
    private static readonly TracedJson Unread = new(null);

    private TracedJson(string? json)
    {
        Json = json;
    }

    public string? Json { get; }

    /// <summary>The JSON of the value a copy of the reader is on, as it came.</summary>
    public static string Text(Utf8JsonReader reader)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    private sealed class Reader : JsonConverter<TracedJson>
    {
        public override TracedJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? json = IdeLog.Traces ? Text(reader) : null;
            reader.Skip();
            return json is null ? Unread : new TracedJson(json);
        }

        public override void Write(Utf8JsonWriter writer, TracedJson value, JsonSerializerOptions options) =>
            throw new NotSupportedException("A traced message is only read");
    }
}

/// <summary>What a test host's output device says (client/log, client/showMessage): its words and their level, each
/// none unless a string.</summary>
[JsonConverter(typeof(Reader))]
internal sealed class PlatformLog
{
    public string? Message { get; private init; }
    public string? Level { get; private init; }

    private sealed class Reader : JsonConverter<PlatformLog>
    {
        public override PlatformLog Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return new PlatformLog();
            }
            string? message = null, level = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isMessage = reader.ValueTextEquals("message"u8), isLevel = reader.ValueTextEquals("level"u8);
                reader.Read();
                string? said = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                message = isMessage ? said : message;
                level = isLevel ? said : level;
                reader.Skip();
            }
            return new PlatformLog { Message = message, Level = level };
        }

        public override void Write(Utf8JsonWriter writer, PlatformLog value, JsonSerializerOptions options) =>
            throw new NotSupportedException("A test host's log is only read");
    }
}
