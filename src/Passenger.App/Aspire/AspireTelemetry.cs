using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Passenger.App.Containers;

namespace Passenger.App.Aspire;

/// <summary>View-scoped Dashboard streams. Snapshots are bounded recent history, not lossless telemetry storage.</summary>
public static class AspireTelemetry
{
    /// <summary>Calls back on the reader thread with what each envelope changed of the recent logs kept, until canceled. Does
    /// not retry.</summary>
    public static Task WatchLogsAsync(Uri baseUri, string dashboardApiKey,
        Action<AspireLogBatch> changed, CancellationToken cancel)
        => LogsAsync(baseUri, dashboardApiKey, new RecentLogs(), changed, cancel);

    /// <summary>Reads bounded recent logs without opening a live stream. An unresolved OTLP resource filter returns no records; call again explicitly to refresh.</summary>
    public static async Task<IReadOnlyList<AspireStructuredLog>> ReadLogsAsync(Uri baseUri, string dashboardApiKey,
        string? resource = null, string? traceId = null, int limit = 200, CancellationToken cancel = default)
    {
        RecentLogs kept = new();
        await LogsAsync(baseUri, dashboardApiKey, kept, _ => { }, cancel, follow: false,
            SnapshotQuery(resource, traceId, Math.Clamp(limit, 1, AspireClient.LogLimit))).ConfigureAwait(false);
        return kept.Kept;
    }

    private static Task LogsAsync(Uri baseUri, string dashboardApiKey, RecentLogs logs,
        Action<AspireLogBatch> changed, CancellationToken cancel, bool follow = true, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(changed);
        AspireRecords records = new(dashboardApiKey);
        return ReadAsync(baseUri, dashboardApiKey, "logs", envelope =>
        {
            List<AspireStructuredLog> added = [];
            List<AspireStructuredLog> evicted = [];
            foreach ((JsonElement row, string resource, string service) in Records(records, envelope, "resourceLogs", "scopeLogs", "logRecords"))
            {
                (byte[] json, string resourceName, string serviceName) = records.Log(row, resource, service);
                added.Add(logs.Add(json, resourceName, serviceName, resource, evicted));
            }
            changed(new AspireLogBatch(added, evicted));
        }, cancel, follow, query);
    }

    /// <summary>The recent logs a reader keeps, oldest first, within the dashboard's bounds: each log counted as it comes and
    /// goes (its bytes, its resource), so a log costs what it adds, never a look over all those kept.</summary>
    private sealed class RecentLogs
    {
        private readonly Queue<(AspireStructuredLog Log, string Resource, int Bytes)> logs = new();
        private readonly Dictionary<string, int> resources = new(StringComparer.Ordinal);
        private int bytes;
        private long heard;

        public IReadOnlyList<AspireStructuredLog> Kept => Array.AsReadOnly(logs.Select(item => item.Log).ToArray());

        /// <summary>Keeps a log, the oldest past the bounds evicted into <paramref name="evicted"/>, oldest first.</summary>
        public AspireStructuredLog Add(byte[] json, string resourceName, string serviceName, string resource, List<AspireStructuredLog> evicted)
        {
            AspireStructuredLog log = new(json, resourceName, serviceName) { Sequence = ++heard };
            int size = json.Length;
            logs.Enqueue((log, resource, size));
            bytes += size;
            resources[resource] = resources.GetValueOrDefault(resource) + 1;
            while (logs.Count > AspireClient.LogLimit || bytes > AspireClient.ResponseByteLimit || resources.Count > AspireClient.ResourceLimit)
            {
                (AspireStructuredLog old, string from, int oldBytes) = logs.Dequeue();
                bytes -= oldBytes;
                if (resources[from] == 1)
                {
                    resources.Remove(from);
                }
                else
                {
                    resources[from]--;
                }
                evicted.Add(old);
            }
            return log;
        }
    }

    /// <summary>Assembles recent traces by trace/span identity. Late spans update retained traces; evicted spans are not recovered.</summary>
    public static Task WatchTracesAsync(Uri baseUri, string dashboardApiKey,
        Action<IReadOnlyList<AspireTrace>> changed, CancellationToken cancel)
        => TracesAsync(baseUri, dashboardApiKey, changed, cancel);

    /// <summary>Reads bounded recent traces, or one trace with all retained spans, without CLI subprocesses. An unresolved OTLP resource filter returns no recent traces; missing explicit trace IDs remain errors.</summary>
    public static async Task<IReadOnlyList<AspireTrace>> ReadTracesAsync(Uri baseUri, string dashboardApiKey,
        string? resource = null, string? traceId = null, int limit = 10, CancellationToken cancel = default)
    {
        ValidateTraceId(traceId);
        IReadOnlyList<AspireTrace> result = [];
        await TracesAsync(baseUri, dashboardApiKey, traces => result = traces, cancel, follow: false,
            SnapshotQuery(resource, null, Math.Clamp(limit, 1, AspireClient.TraceLimit)), traceId).ConfigureAwait(false);
        return resource is null ? result : Array.AsReadOnly(result.Where(trace => trace.Spans.Any(span => Telemetry.TelemetrySource.Of(span.Service, span.Source, resource))).ToArray());
    }

    private static Task TracesAsync(Uri baseUri, string dashboardApiKey,
        Action<IReadOnlyList<AspireTrace>> changed, CancellationToken cancel, bool follow = true, string? query = null, string? requestedTraceId = null)
    {
        ArgumentNullException.ThrowIfNull(changed);
        RecentTraces traces = new();
        AspireRecords records = new(dashboardApiKey);
        return ReadAsync(baseUri, dashboardApiKey, follow ? "spans" : requestedTraceId is null ? "traces" : $"traces/{requestedTraceId.ToLowerInvariant()}", envelope =>
        {
            Span<char> identity = stackalloc char[32];
            foreach ((JsonElement row, string resource, string service) in Records(records, envelope, "resourceSpans", "scopeSpans", "spans"))
            {
                string traceId = traces.Known(records.RequiredIdentity(row, "traceId", identity));
                string spanId = new(records.RequiredIdentity(row, "spanId", identity[..16]));
                ulong start = records.Nanoseconds(row, "startTimeUnixNano");
                ulong end = records.Nanoseconds(row, "endTimeUnixNano");
                if (end < start)
                {
                    throw new InvalidDataException("Aspire telemetry returned a span ending before it starts.");
                }
                int status = row.TryGetProperty("status", out JsonElement statusData)
                    && statusData.TryGetProperty("code", out JsonElement code) && code.TryGetInt32(out int number) ? number : 0;
                (byte[] json, string writtenSpanId, string? parentSpanId, string? name, string source, string writtenService) =
                    records.Span(row, resource, service, traceId, spanId, start, end, status);
                AspireSpan span = new(json, writtenSpanId, parentSpanId, name, source, writtenService);
                traces.Add(traceId, new KeptSpan(span, spanId, resource, start, end, json.Length, status == 2));
            }
            changed(traces.Snapshot());
        }, cancel, follow, query);
    }

    /// <summary>A span a reader keeps, with what its trace is made from and what its bounds count, each read once.</summary>
    private readonly record struct KeptSpan(AspireSpan Span, string Id, string Source, ulong Start, ulong End, int Bytes, bool Failed);

    /// <summary>The recent traces a reader keeps, newest first, within the dashboard's bounds: each span counted as it comes
    /// and goes (its bytes, its resource), each trace moved to the front in place and made again only when an envelope
    /// touched it.</summary>
    private sealed class RecentTraces
    {
        private sealed class Kept(LinkedListNode<string> place)
        {
            public LinkedListNode<string> Place { get; } = place;
            public Dictionary<string, KeptSpan> Spans { get; } = new(StringComparer.Ordinal);
            public AspireTrace? Made { get; set; }
        }

        private readonly Dictionary<string, Kept> traces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Kept>.AlternateLookup<ReadOnlySpan<char>> byChars;
        private readonly LinkedList<string> recent = new();
        private readonly Dictionary<string, int> sources = new(StringComparer.Ordinal);
        private int bytes;
        private int count;

        public RecentTraces()
        {
            byChars = traces.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        /// <summary>A trace's id as the trace kept has it, made a string only for a trace not kept.</summary>
        public string Known(ReadOnlySpan<char> traceId) => byChars.TryGetValue(traceId, out string? kept, out _) ? kept : new string(traceId);

        public void Add(string traceId, KeptSpan span)
        {
            if (traces.TryGetValue(traceId, out Kept? kept))
            {
                recent.Remove(kept.Place);
                recent.AddFirst(kept.Place);
            }
            else
            {
                traces.Add(traceId, kept = new Kept(recent.AddFirst(traceId)));
            }
            if (kept.Spans.Remove(span.Id, out KeptSpan previous))
            {
                Forget(previous);
            }
            kept.Spans.Add(span.Id, span);
            kept.Made = null;
            bytes += span.Bytes;
            count++;
            sources[span.Source] = sources.GetValueOrDefault(span.Source) + 1;
            while (traces.Count > AspireClient.TraceLimit || count > AspireClient.SpanLimit || bytes > AspireClient.ResponseByteLimit
                || sources.Count > AspireClient.ResourceLimit)
            {
                string oldest = recent.Last!.Value;
                recent.RemoveLast();
                foreach (KeptSpan removed in traces[oldest].Spans.Values)
                {
                    Forget(removed);
                }
                traces.Remove(oldest);
            }
        }

        private void Forget(KeptSpan span)
        {
            bytes -= span.Bytes;
            count--;
            if (sources[span.Source] == 1)
            {
                sources.Remove(span.Source);
            }
            else
            {
                sources[span.Source]--;
            }
        }

        /// <summary>The traces kept, newest first, each untouched since the last snapshot the same object as in it.</summary>
        public IReadOnlyList<AspireTrace> Snapshot()
        {
            AspireTrace[] made = new AspireTrace[recent.Count];
            int at = 0;
            foreach (string id in recent)
            {
                Kept kept = traces[id];
                made[at++] = kept.Made ??= Trace(id, kept.Spans.Values);
            }
            return Array.AsReadOnly(made);
        }
    }

    private static AspireTrace Trace(string id, Dictionary<string, KeptSpan>.ValueCollection values)
    {
        KeptSpan[] spans = new KeptSpan[values.Count];
        values.CopyTo(spans, 0);
        Array.Sort(spans, static (one, other) => one.Start != other.Start ? one.Start.CompareTo(other.Start) : string.CompareOrdinal(one.Id, other.Id));
        ulong end = 0;
        bool failed = false;
        AspireSpan? root = null;
        AspireSpan[] kept = new AspireSpan[spans.Length];
        for (int i = 0; i < spans.Length; i++)
        {
            end = Math.Max(end, spans[i].End);
            failed |= spans[i].Failed;
            kept[i] = spans[i].Span;
            root ??= spans[i].Span.ParentSpanId is null ? spans[i].Span : null;
        }
        return new AspireTrace(JsonSerializer.SerializeToElement(new
        {
            traceId = id,
            title = (root ?? spans[0].Span).Name,
            durationMs = (end - spans[0].Start) / 1_000_000d,
            hasError = failed,
        }), Array.AsReadOnly(kept));
    }

    private static async Task ReadAsync(Uri baseUri, string apiKey, string kind, Action<JsonElement> receive, CancellationToken cancel,
        bool follow = true, string? query = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!baseUri.IsAbsoluteUri || !baseUri.IsLoopback || baseUri.Scheme is not ("http" or "https")
            || baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
        {
            throw new ArgumentException("Telemetry requires a known loopback HTTP or HTTPS address without credentials, query, or fragment.", nameof(baseUri));
        }
        using SocketsHttpHandler handler = new() { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(30) };
        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        CancellationToken requested = cancel;
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (!follow)
        {
            bounded.CancelAfter(TimeSpan.FromSeconds(30));
        }
        cancel = bounded.Token;
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, $"/api/telemetry/{kind}?{(follow ? "follow=true" : query)}"));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Accept.ParseAdd(follow ? "application/x-ndjson" : "application/json");
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            bool missingResource = !follow && (kind is "logs" or "traces")
                && query?.Contains("&resource=", StringComparison.Ordinal) == true
                && response.StatusCode == System.Net.HttpStatusCode.NotFound
                && (string.Equals(response.Content.Headers.ContentType?.MediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase));
            if (!response.IsSuccessStatusCode && !missingResource)
            {
                throw new IOException($"Aspire telemetry {kind} request failed (HTTP {(int)response.StatusCode}; media type {response.Content.Headers.ContentType?.MediaType ?? "none"}; resource filter {query?.Contains("&resource=", StringComparison.Ordinal) == true}).");
            }
            if (!follow)
            {
                if (!missingResource && !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Aspire telemetry did not return application/json.");
                }
                await using Stream snapshot = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                using MemoryStream body = new();
                byte[] chunk = new byte[8192];
                int length;
                while ((length = await snapshot.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
                {
                    if (body.Length + length > AspireClient.ResponseByteLimit)
                    {
                        throw new InvalidDataException("Aspire telemetry snapshot exceeded 8 MiB.");
                    }
                    body.Write(chunk, 0, length);
                }
                using JsonDocument document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 64 });
                if (missingResource)
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any()
                        || AspireJson.String(document.RootElement, "title") == "Resource not found"
                        && document.RootElement.TryGetProperty("status", out JsonElement status) && status.TryGetInt32(out int number) && number == 404)
                    {
                        return;
                    }
                    throw new IOException($"Aspire telemetry {kind} request failed (HTTP 404; fields {string.Join(",", document.RootElement.EnumerateObject().Take(8).Select(property => property.Name.Length <= 32 && property.Name.All(char.IsAsciiLetter) ? property.Name : "other"))}).");
                }
                if (!document.RootElement.TryGetProperty("data", out JsonElement data))
                {
                    throw new InvalidDataException("Aspire telemetry omitted its snapshot data.");
                }
                receive(AspireJson.Object(data));
                return;
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Aspire telemetry did not return application/x-ndjson.");
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using MemoryStream line = new();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (line.Length > 0)
                        {
                            using JsonDocument document = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length), new JsonDocumentOptions { MaxDepth = 64 });
                            cancel.ThrowIfCancellationRequested();
                            receive(AspireJson.Object(document.RootElement));
                            line.SetLength(0);
                        }
                    }
                    else
                    {
                        if (line.Length >= AspireClient.LineByteLimit)
                        {
                            throw new InvalidDataException("Aspire telemetry line exceeded 1 MiB.");
                        }
                        line.WriteByte(buffer[i]);
                    }
                }
            }
            cancel.ThrowIfCancellationRequested();
            throw new IOException("The Aspire telemetry stream ended. Reopen the view to reconnect.");
        }
        catch (OperationCanceledException) when (!follow && !requested.IsCancellationRequested)
        {
            throw new TimeoutException("The Aspire telemetry snapshot did not complete within 30 seconds.");
        }
        catch (HttpRequestException error)
        {
            string reason = error.StatusCode is { } status ? $"HTTP {(int)status}"
                : error.InnerException is System.Net.Sockets.SocketException socket ? socket.SocketErrorCode.ToString()
                : error.HttpRequestError.ToString();
            throw new IOException($"Aspire telemetry {kind} connection to {baseUri.GetLeftPart(UriPartial.Authority)} failed ({reason}).");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Aspire telemetry returned malformed OTLP JSON.");
        }
    }

    private static string SnapshotQuery(string? resource, string? traceId, int limit)
    {
        ValidateTraceId(traceId);
        if (resource is not null && (resource.Length is 0 or > 1024 || resource.Any(char.IsControl)))
        {
            throw new ArgumentException("Invalid Aspire telemetry resource identity", nameof(resource));
        }
        return $"limit={limit.ToString(CultureInfo.InvariantCulture)}"
            + (resource is null ? string.Empty : $"&resource={Uri.EscapeDataString(resource)}")
            + (traceId is null ? string.Empty : $"&traceId={traceId.ToLowerInvariant()}");
    }

    private static void ValidateTraceId(string? traceId)
    {
        if (traceId is not null && (traceId.Length != 32 || !traceId.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException("traceId must contain 32 hexadecimal characters", nameof(traceId));
        }
    }

    /// <summary>Each record of an envelope with its resource as the dashboard names it (its service's name, then its instance
    /// after a dash) and its service's name alone, which a sibling's name with a dash in it can never be mistaken for.</summary>
    private static IEnumerable<(JsonElement Row, string Resource, string Service)> Records(AspireRecords reader, JsonElement envelope, string resources, string scopes, string records)
    {
        foreach (JsonElement group in AspireJson.Array(envelope, resources, required: true))
        {
            if (!group.TryGetProperty("resource", out JsonElement resource))
            {
                throw new InvalidDataException("Aspire telemetry omitted its OTLP resource.");
            }
            Dictionary<string, string?> attributes = reader.Strings(resource);
            string name = attributes.GetValueOrDefault("service.name") ?? "unknown";
            string? instance = attributes.GetValueOrDefault("service.instance.id");
            string identity = string.IsNullOrEmpty(instance) ? name : $"{name}-{instance}";
            if (identity.Length > 1024 || identity.Any(char.IsControl))
            {
                throw new InvalidDataException("Aspire telemetry returned an invalid resource identity.");
            }
            foreach (JsonElement scope in AspireJson.Array(group, scopes, required: true))
            {
                foreach (JsonElement row in AspireJson.Array(scope, records, required: true))
                {
                    if (System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(row).Length > AspireClient.LineByteLimit)
                    {
                        throw new InvalidDataException("Aspire telemetry record exceeded 1 MiB.");
                    }
                    yield return (AspireJson.Object(row), identity, name);
                }
            }
        }
    }
}
