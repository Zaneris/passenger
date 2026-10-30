using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>Bounded snapshots from the documented CLI JSON of Aspire 13.x from 13.6.0. Credentials stay internal; public resource snapshots mask values.
/// The CLI is the one on the PATH, or the program a window names (<see cref="MainWindow.AspireCliFrom"/>).</summary>
public sealed class AspireClient(string? program = null)
{
    internal const string SupportedVersion = "13.x (13.6.0 or newer)";
    public const int ResourceLimit = 256;
    public const int LogLimit = 1000;
    public const int TraceLimit = 50;
    public const int SpanLimit = 4000;
    public const int ResponseByteLimit = 8 * 1024 * 1024;
    public const int LineByteLimit = 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly HashSet<string> VisibleProperties =
        ["project.path", "executable.path", "executable.pid", "resource.uid", "container.id", "container.image", "container.ports", "container.lifetime"];

    public async Task<AspireAvailability> ProbeAsync(CancellationToken cancel = default)
    {
        if (Executable() is null)
        {
            return new(false, $"Aspire CLI was not found on PATH. Install Aspire {SupportedVersion}.");
        }
        try
        {
            string version = (await RunAsync(["--version"], null, cancel, TimeSpan.FromSeconds(5))).Trim();
            string[] tokens = version.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Version[] versions = tokens.Where(token => Regex.IsMatch(token, @"^\d+\.\d+\.\d+(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant))
                .Select(token => Version.TryParse(token.Split('+')[0], out Version? parsed) ? parsed : null)
                .OfType<Version>().ToArray();
            if (versions.Length != 1)
            {
                return new(false, "Aspire CLI did not report one valid stable version token. Version 13.6.0 or newer within 13.x is required.");
            }
            Version actual = versions[0];
            bool supported = actual.Major == 13 && actual >= new Version(13, 6, 0);
            return new(supported, supported ? $"Aspire CLI {actual}" : actual.Major > 13
                ? $"Aspire CLI {actual} uses an unverified protocol major. This view supports 13.x, starting at 13.6.0."
                : $"Aspire CLI {actual} is below the minimum supported version 13.6.0 (protocol major 13).");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return new(false, $"Aspire CLI could not report its version: {SanitizeDiagnostic(error.Message)}");
        }
    }

    public async Task<IReadOnlyList<AspireApp>> DiscoverRunningAppHostsAsync(CancellationToken cancel = default)
    {
        JsonElement json = await SnapshotAsync(["ps", "--format", "json"], null, cancel);
        IReadOnlyList<JsonElement> rows = AspireJson.Array(json);
        CheckCount(rows.Count, ResourceLimit, "running AppHosts");
        return rows.Select(row =>
        {
            string path = CanonicalAppHost(AspireJson.RequiredString(row, "appHostPath"));
            double pid = AspireJson.Number(row, "appHostPid");
            if (pid < 1 || pid > int.MaxValue || pid != Math.Truncate(pid))
            {
                throw new InvalidDataException("Aspire returned an invalid AppHost process ID.");
            }
            return new AspireApp(path, (int)pid, AspireJson.RequiredString(row, "status"));
        }).ToArray();
    }

    public async Task<IReadOnlyList<AspireResource>> GetResourcesAsync(string appHost, CancellationToken cancel = default)
    {
        JsonElement json = await SnapshotAsync(["describe", "--format", "json", "--include-disabled-commands"], appHost, cancel);
        IReadOnlyList<JsonElement> rows = AspireJson.Array(json, "resources", required: true);
        CheckCount(rows.Count, ResourceLimit, "resources");
        AspireResource[] resources = rows.Select(row => new AspireResource(MaskResourceValues(AspireJson.Object(row)))).ToArray();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (AspireResource resource in resources)
        {
            if (!names.Add(resource.Name))
            {
                throw new InvalidDataException("Aspire returned duplicate resource identities.");
            }
            _ = resource.DisplayName;
            _ = resource.Generation;
            _ = resource.State;
            _ = resource.HealthStatus;
            _ = resource.Source;
            foreach (AspireResourceCommand command in resource.Commands)
            {
                _ = command.DisplayName;
                _ = command.Description;
                _ = command.Enabled;
                _ = command.HasArguments;
                if (command.Data.TryGetProperty("sortOrder", out JsonElement order) && order.ValueKind != JsonValueKind.Null
                    && (order.ValueKind != JsonValueKind.Number || !order.TryGetInt32(out _)))
                {
                    throw new InvalidDataException("Aspire command 'sortOrder' must be an integer.");
                }
            }
            foreach (string field in new[] { "environment", "properties", "healthReports" })
            {
                _ = AspireJson.Properties(resource.Data, field).ToArray();
            }
            foreach (string field in new[] { "urls", "relationships", "volumes", "waitingFor" })
            {
                _ = AspireJson.Array(resource.Data, field);
            }
        }
        return resources;
    }

    internal async Task<(Uri Endpoint, string ApiKey)> ReadResourceConnectionAsync(string appHost, CancellationToken cancel = default)
    {
        try
        {
            JsonElement json = await SnapshotAsync(["describe", "--format", "json", "--include-hidden", "--include-disabled-commands"], appHost, cancel);
            IReadOnlyList<JsonElement> resources = AspireJson.Array(json, "resources", required: true);
            CheckCount(resources.Count, ResourceLimit, "resources");
            JsonElement[] dashboards = resources.Where(resource => AspireJson.String(resource, "displayName") == "aspire-dashboard").Take(2).ToArray();
            if (dashboards.Length != 1)
            {
                throw new IOException($"Aspire {SupportedVersion} must expose exactly one aspire-dashboard resource to connect to this AppHost");
            }
            if (!dashboards[0].TryGetProperty("environment", out JsonElement environment) || environment.ValueKind != JsonValueKind.Object)
            {
                throw new IOException($"The Aspire {SupportedVersion} dashboard did not expose its authenticated resource-service connection");
            }
            string? address = AspireJson.String(environment, AspireVariables.ResourceServiceEndpoint);
            string? apiKey = AspireJson.String(environment, "DASHBOARD__RESOURCESERVICECLIENT__APIKEY");
            string? authMode = AspireJson.String(environment, "DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE");
            if (string.IsNullOrWhiteSpace(apiKey) || !string.Equals(authMode, "ApiKey", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The Aspire {SupportedVersion} resource service must advertise API-key authentication; unsecured or other authentication modes are unsupported");
            }
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https")
                || !endpoint.IsLoopback || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            {
                throw new IOException($"The Aspire {SupportedVersion} resource service must advertise an absolute loopback HTTP or HTTPS endpoint without credentials, query, or fragment");
            }
            return (endpoint, apiKey);
        }
        catch (InvalidDataException)
        {
            throw new IOException($"Aspire {SupportedVersion} did not return a valid dashboard resource-service connection");
        }
    }

    private static JsonElement MaskResourceValues(JsonElement data)
    {
        JsonObject masked = JsonNode.Parse(data.GetRawText())!.AsObject();
        foreach (string field in new[] { "environment", "properties" })
        {
            if (masked[field] is not JsonObject values)
            {
                continue;
            }
            foreach (string name in values.Select(value => value.Key).ToArray())
            {
                // Environment values masked as a container's; a property shown only when known to name no secret.
                if (field == "environment" && values[name] is JsonValue value && value.TryGetValue(out string? text))
                {
                    values[name] = ContainerSecrets.Shown(name, text);
                }
                else if (field == "environment" || !VisibleProperties.Contains(name))
                {
                    values[name] = null;
                }
            }
        }
        if (masked["urls"] is JsonArray urls)
        {
            for (int i = 0; i < urls.Count; i++)
            {
                JsonObject url = urls[i]?.AsObject() ?? throw new InvalidDataException("Aspire returned an invalid resource URL.");
                urls[i] = new JsonObject
                {
                    ["name"] = url["name"]?.DeepClone(),
                    ["displayName"] = url["displayName"]?.DeepClone(),
                    ["url"] = ContainerSecrets.PublicHttpUrl(url["url"]?.GetValue<string>() ?? string.Empty),
                    ["isInternal"] = url["isInternal"]?.DeepClone(),
                    ["isInactive"] = url["isInactive"]?.DeepClone(),
                };
            }
        }
        return JsonSerializer.SerializeToElement(masked);
    }

    public async Task ExecuteCommandAsync(string appHost, AspireResource resource, string command, CancellationToken cancel = default)
    {
        ValidateName(resource.Name);
        ValidateName(command);
        AspireResource? current = (await GetResourcesAsync(appHost, cancel)).FirstOrDefault(r => r.Name == resource.Name);
        if (current is null || current.Generation != resource.Generation)
        {
            throw new InvalidOperationException("The resource changed. Refresh before running its command.");
        }
        AspireResourceCommand advertised = current.Commands.FirstOrDefault(c => c.Name == command)
            ?? throw new InvalidOperationException("The resource no longer advertises this command.");
        if (!advertised.Enabled)
        {
            throw new InvalidOperationException("This resource command is disabled.");
        }
        if (advertised.HasArguments)
        {
            throw new NotSupportedException("This command requires an argument form. Run it with the Aspire CLI; no command was sent.");
        }
        _ = await RunAsync(["resource", resource.Name, advertised.Name], CanonicalAppHost(appHost), cancel);
    }

    public static string CanonicalAppHost(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute AppHost path is required.", nameof(path));
        }
        return PathNames.Canonical(path);
    }

    private string? Executable() => program ?? Programs.OnPath(OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') || name.Any(char.IsControl))
        {
            throw new ArgumentException("An Aspire resource or command name cannot be empty, a flag, or contain control characters.");
        }
    }

    private static void CheckCount(int count, int limit, string kind)
    {
        if (count > limit)
        {
            throw new InvalidDataException($"Aspire returned more than {limit} {kind}. Nothing was silently truncated.");
        }
    }

    private async Task<JsonElement> SnapshotAsync(IEnumerable<string> arguments, string? appHost, CancellationToken cancel)
    {
        string output = await RunAsync(arguments, appHost is null ? null : CanonicalAppHost(appHost), cancel);
        try
        {
            using JsonDocument document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 64 });
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"Aspire returned malformed snapshot JSON. Check CLI version {SupportedVersion} and refresh.");
        }
    }

    /// <summary>A CLI command's output, found and started off the caller's thread (finding the CLI may wait out the login
    /// shell, and an agent's tools ask from the UI thread).</summary>
    private Task<string> RunAsync(IEnumerable<string> arguments, string? appHost, CancellationToken cancel, TimeSpan? timeout = null) =>
        BackgroundWork.Run(() => RunHereAsync(arguments, appHost, cancel, timeout));

    private async Task<string> RunHereAsync(IEnumerable<string> arguments, string? appHost, CancellationToken cancel, TimeSpan? timeout)
    {
        cancel.ThrowIfCancellationRequested();
        string executable = Executable() ?? throw new FileNotFoundException("Aspire CLI was not found on PATH.");
        List<string> command = [.. arguments, "--non-interactive", "--nologo"];
        if (appHost is not null)
        {
            command.AddRange(["--apphost", appHost]);
        }
        ProcessStartInfo start = ChildProcesses.Command(executable, command, appHost is null ? null : Path.GetDirectoryName(appHost));
        start.Environment[AspireVariables.TelemetryOptOut] = "1";
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        start.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        using CancellationTokenSource ending = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        ending.CancelAfter(timeout ?? CommandTimeout);
        using Process process = ChildProcesses.Start(start);
        using CancellationTokenRegistration registration = ending.Token.Register(() => ChildProcesses.Kill(process));
        try
        {
            Task<string> output = ReadBoundedAsync(process.StandardOutput, ending.Token);
            Task<string> errors = ReadBoundedAsync(process.StandardError, ending.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(ending.Token));
            if (process.ExitCode != 0)
            {
                string reason = string.IsNullOrWhiteSpace(await errors) ? await output : await errors;
                throw new InvalidOperationException($"Aspire command failed (exit {process.ExitCode}): {SanitizeDiagnostic(reason)}");
            }
            return await output;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("Aspire command timed out. Check the AppHost and refresh.");
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Aspire returned invalid UTF-8 output.");
        }
        finally
        {
            ChildProcesses.Kill(process);
        }
    }

    /// <summary>What the aspire CLI or a resource service said of a failure, in the same words wherever it is said: its
    /// secrets masked (<see cref="ContainerSecrets.InSaid"/>, the <paramref name="secrets"/> the caller holds among them), on
    /// one line, 2,048 characters at most.</summary>
    internal static string SanitizeDiagnostic(string value, params string[] secrets)
    {
        // Bounded before it is read through: nothing past what is shown is said.
        value = ContainerSecrets.InSaid(value[..Math.Min(value.Length, 16384)], known: secrets);
        value = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return value.Length == 0 ? "No diagnostic output was returned." : value[..Math.Min(value.Length, 2048)];
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancel)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        int total = 0;
        int line = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancel)) > 0)
        {
            int bytes = Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            total += bytes;
            if (total > ResponseByteLimit)
            {
                throw new InvalidDataException("Aspire response exceeded 8 MiB. Reduce the requested limit.");
            }
            for (int i = 0; i < read; i++)
            {
                line = buffer[i] == '\n' ? 0 : line + Encoding.UTF8.GetByteCount(buffer.AsSpan(i, 1));
                if (line > LineByteLimit)
                {
                    throw new InvalidDataException("Aspire response line exceeded 1 MiB.");
                }
            }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}
