using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NuGet.Common;
using Passenger.Core;

namespace Passenger.Packages;

/// <summary>NuGet's credential providers (Azure Artifacts' and the rest), found where NuGet finds them and asked through
/// its plugin protocol without a dialog; what one gives is kept in memory for the session, never written or logged.</summary>
public sealed class CredentialProviders
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Answer = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, FeedCredentials> given = new(StringComparer.OrdinalIgnoreCase);

    public static CredentialProviders Default { get; } = new();

    /// <summary>Credentials for a feed from the first provider that gives any; a retry asks again, past what was given.</summary>
    public async Task<FeedCredentials?> GetAsync(Uri feed, bool isRetry, CancellationToken cancel)
    {
        if (!isRetry && given.TryGetValue(feed.AbsoluteUri, out FeedCredentials? known))
        {
            return known;
        }
        foreach (string plugin in Find())
        {
            try
            {
                if (await AskAsync(plugin, feed, isRetry, cancel) is { } credentials)
                {
                    given[feed.AbsoluteUri] = credentials;
                    return credentials;
                }
            }
            catch (Exception failed) when (failed is IOException or InvalidOperationException or TimeoutException or JsonException or KeyNotFoundException
                or System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                PassengerLog.Write($"NuGet: the credential provider {plugin} gave nothing for {feed}: {failed.Message}");
            }
        }
        return null;
    }

    /// <summary>The providers: the paths NUGET_NETCORE_PLUGIN_PATHS (else NUGET_PLUGIN_PATHS) names, or else each folder's
    /// assembly under ~/.nuget/plugins/netcore and the nuget-plugin-* programs on PATH.</summary>
    public static IReadOnlyList<string> Find()
    {
        string? named = Environment.GetEnvironmentVariable("NUGET_NETCORE_PLUGIN_PATHS") is { Length: > 0 } netcore ? netcore : Environment.GetEnvironmentVariable("NUGET_PLUGIN_PATHS");
        if (!string.IsNullOrWhiteSpace(named))
        {
            return [.. named.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(File.Exists)];
        }
        List<string> found = [];
        string folder = Path.Combine(NuGetEnvironment.GetFolderPath(NuGetFolderPath.NuGetHome), "plugins", "netcore");
        if (Directory.Exists(folder))
        {
            found.AddRange(Directory.EnumerateDirectories(folder).Select(d => Path.Combine(d, Path.GetFileName(d) + ".dll")).Where(File.Exists));
        }
        foreach (string place in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(place))
            {
                continue;
            }
            foreach (string program in Directory.EnumerateFiles(place, "nuget-plugin-*"))
            {
                bool runs = OperatingSystem.IsWindows()
                    ? program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    : (File.GetUnixFileMode(program) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                if (runs && !found.Contains(program, StringComparer.Ordinal))
                {
                    found.Add(program);
                }
            }
        }
        return found;
    }

    /// <summary>Starts a provider, shakes hands, asks whether it authenticates and then for the feed's credentials.</summary>
    private static async Task<FeedCredentials?> AskAsync(string plugin, Uri feed, bool isRetry, CancellationToken cancel)
    {
        ProcessStartInfo start = plugin.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ChildProcesses.DotnetCommand([plugin, "-Plugin"], Path.GetDirectoryName(plugin))
            : ChildProcesses.Command(plugin, ["-Plugin"], Path.GetDirectoryName(plugin));
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process process = await ChildProcesses.StartAsync(start);
        await using PluginConnection connection = new(process, Path.GetFileName(plugin));
        await connection.AskAsync("Handshake", new { ProtocolVersion = "2.0.0", MinimumProtocolVersion = "1.0.0" }, Step, cancel);
        await connection.AskAsync("Initialize", new { ClientVersion = "7.0.0", Culture = "en-US", RequestTimeout = "00:01:00" }, Step, cancel);
        JsonElement claims = await connection.AskAsync("GetOperationClaims", new { }, Step, cancel);
        if (!claims.TryGetProperty("Claims", out JsonElement list) || list.ValueKind != JsonValueKind.Array || !list.EnumerateArray().Any(c => c.GetString() == "Authentication"))
        {
            return null;
        }
        JsonElement answer = await connection.AskAsync("GetAuthenticationCredentials",
            new { Uri = feed.AbsoluteUri, IsRetry = isRetry, IsNonInteractive = true, CanShowDialog = false }, Answer, cancel);
        return answer.TryGetProperty("Username", out JsonElement user) && user.GetString() is { } username
            && answer.TryGetProperty("Password", out JsonElement secret) && secret.GetString() is { Length: > 0 } password
            ? new FeedCredentials(username, password)
            : null;
    }

    /// <summary>NuGet's plugin protocol over a provider's standard input and output. The provider's own requests (its
    /// handshake, its log lines) are answered; what it writes to its errors is read and dropped.</summary>
    private sealed class PluginConnection : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string name;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> waiting = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim writing = new(1, 1);
        private readonly Task reading;
        private readonly Task draining;

        public PluginConnection(Process process, string name)
        {
            this.process = process;
            this.name = name;
            // Not background work of its own: the search that asked holds screenshots until it has its answer.
            reading = Task.Run(ReadAsync);
            draining = process.StandardError.ReadToEndAsync();
        }

        /// <summary>A request's response payload, once it says Success.</summary>
        public async Task<JsonElement> AskAsync(string method, object payload, TimeSpan limit, CancellationToken cancel)
        {
            string id = Guid.NewGuid().ToString("D");
            TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting[id] = answer;
            await SendAsync(new { RequestId = id, Type = "Request", Method = method, Payload = payload });
            JsonElement response;
            try
            {
                response = await answer.Task.WaitAsync(limit, cancel);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"{name} did not answer {method} in {limit.TotalSeconds:0} seconds");
            }
            finally
            {
                waiting.TryRemove(id, out _);
            }
            string? code = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("ResponseCode", out JsonElement said) ? said.GetString() : null;
            return code == "Success" ? response : throw new InvalidOperationException($"{name} answered {method} with {code ?? "nothing"}");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await SendAsync(new { RequestId = Guid.NewGuid().ToString("D"), Type = "Request", Method = "Close" });
                process.StandardInput.Close();
            }
            catch (Exception gone) when (gone is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The provider ended already.
            }
            using CancellationTokenSource ending = new(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(ending.Token);
            }
            catch (OperationCanceledException)
            {
                ChildProcesses.Kill(process);
            }
            await Task.WhenAll(reading, draining).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
            writing.Dispose();
        }

        private async Task SendAsync(object message)
        {
            string line = JsonSerializer.Serialize(message);
            await writing.WaitAsync();
            try
            {
                await process.StandardInput.WriteLineAsync(line);
                await process.StandardInput.FlushAsync();
            }
            finally
            {
                writing.Release();
            }
        }

        private async Task ReadAsync()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Trim().Length == 0)
                    {
                        continue;
                    }
                    using JsonDocument read = JsonDocument.Parse(line);
                    JsonElement message = read.RootElement;
                    string id = message.GetProperty("RequestId").GetString() ?? string.Empty;
                    string type = message.GetProperty("Type").GetString() ?? string.Empty;
                    string method = message.TryGetProperty("Method", out JsonElement named) ? named.GetString() ?? string.Empty : string.Empty;
                    JsonElement payload = message.TryGetProperty("Payload", out JsonElement given) ? given.Clone() : default;
                    if (type == "Request")
                    {
                        await AnswerAsync(id, method);
                    }
                    else if (type == "Response" && waiting.TryGetValue(id, out TaskCompletionSource<JsonElement>? answer))
                    {
                        answer.TrySetResult(payload);
                    }
                    else if (type == "Fault" && waiting.TryGetValue(id, out TaskCompletionSource<JsonElement>? failed))
                    {
                        string why = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("Message", out JsonElement text) ? text.GetString() ?? "a fault" : "a fault";
                        failed.TrySetException(new InvalidOperationException($"{name} answered {method} with {why}"));
                    }
                }
            }
            catch (Exception broken) when (broken is IOException or JsonException or KeyNotFoundException or InvalidOperationException or ObjectDisposedException)
            {
                // A provider that writes what is no message, or ends, fails what was asked of it.
            }
            foreach (TaskCompletionSource<JsonElement> left in waiting.Values)
            {
                left.TrySetException(new IOException($"{name} ended before it answered"));
            }
        }

        /// <summary>Answers the provider's own request: its handshake, and its log lines, which are dropped.</summary>
        private Task AnswerAsync(string id, string method) => SendAsync(new
        {
            RequestId = id,
            Type = "Response",
            Method = method,
            Payload = method switch
            {
                "Handshake" => (object)new { ResponseCode = "Success", ProtocolVersion = "2.0.0" },
                "Log" or "SetLogLevel" => new { ResponseCode = "Success" },
                _ => new { ResponseCode = "NotFound" },
            },
        });
    }
}
