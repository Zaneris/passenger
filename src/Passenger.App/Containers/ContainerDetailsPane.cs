using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>One run of a container's health check: when it began, its exit code and what it printed.</summary>
public sealed record HealthProbe(DateTimeOffset? Started, int ExitCode, string Output);

/// <summary>A mount: its kind (bind, volume, tmpfs), what is mounted (a folder, a volume's name), where, and whether read-only.</summary>
public sealed record ContainerMount(string Type, string Source, string Destination, bool ReadOnly);

/// <summary>A network a container is on, with its address there and the names it answers to.</summary>
public sealed record ContainerNetwork(string Name, string? Address, IReadOnlyList<string> Aliases);

/// <summary>A Compose service a container's service waits for (its <c>depends_on</c>), the condition it waits on, and
/// whether a restart of that service restarts it.</summary>
public sealed record ServiceDependency(string Service, string Condition, bool Restart);

/// <summary>What <c>docker inspect</c> says of a container, as its Details tab shows it: its image and state, its restarts
/// and last exit, its health check with the outputs of its last runs (so "unhealthy" says why), its environment with each
/// secret's value masked (<see cref="ContainerSecrets"/>), its mounts, networks and ports, and the services its own waits
/// for (Compose's <c>com.docker.compose.depends_on</c> label).</summary>
public sealed record ContainerDetails
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Image { get; init; } = string.Empty;

    /// <summary>running, exited, paused, restarting, created or dead.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>healthy, unhealthy or starting; null for a container with no health check.</summary>
    public string? Health { get; init; }

    /// <summary>How many of its health check's runs in a row have failed.</summary>
    public int FailingStreak { get; init; }

    /// <summary>The health check's command; null for none.</summary>
    public string? HealthCheck { get; init; }

    /// <summary>The health check's last runs, the latest first.</summary>
    public IReadOnlyList<HealthProbe> Probes { get; init; } = [];

    public int RestartCount { get; init; }

    /// <summary>When the engine restarts it (no, always, on-failure, unless-stopped); null for never.</summary>
    public string? RestartPolicy { get; init; }

    /// <summary>The exit code of its last run, and when it ended; null while it has never ended or runs again.</summary>
    public (int Code, DateTimeOffset At)? LastExit { get; init; }

    /// <summary>Why the engine could not run it, or that the kernel ended it for memory.</summary>
    public string? Error { get; init; }

    /// <summary>Its environment, each secret's value masked, by name.</summary>
    public IReadOnlyList<(string Name, string Value)> Environment { get; init; } = [];

    public IReadOnlyList<ContainerMount> Mounts { get; init; } = [];

    public IReadOnlyList<ContainerNetwork> Networks { get; init; } = [];

    /// <summary>Its published ports, each on the host's port.</summary>
    public IReadOnlyList<ContainerPort> Ports { get; init; } = [];

    /// <summary>The ports it exposes that are not published, as "8080/tcp".</summary>
    public IReadOnlyList<string> Unpublished { get; init; } = [];

    public IReadOnlyList<ServiceDependency> DependsOn { get; init; } = [];

    internal static ContainerDetails Read(JsonElement said)
    {
        JsonElement state = Child(said, "State");
        JsonElement config = Child(said, "Config");
        JsonElement health = Child(state, "Health");
        JsonElement settings = Child(said, "NetworkSettings");
        List<ContainerPort> ports = [];
        List<string> unpublished = [];
        if (Child(settings, "Ports") is { ValueKind: JsonValueKind.Object } exposed)
        {
            foreach (JsonProperty port in exposed.EnumerateObject().OrderBy(p => int.TryParse(p.Name.Split('/')[0], CultureInfo.InvariantCulture, out int n) ? n : 0))
            {
                string[] inside = port.Name.Split('/');
                string protocol = inside.Length > 1 ? inside[1] : "tcp";
                List<ContainerPort> bound = port.Value.ValueKind != JsonValueKind.Array ? []
                    : [.. port.Value.EnumerateArray().Select(b => ContainerEngine.Text(b, "HostPort")).OfType<string>().Where(h => h.Length > 0).Distinct(StringComparer.Ordinal)
                        .Select(host => new ContainerPort(host, inside[0], protocol))];
                if (bound.Count == 0)
                {
                    unpublished.Add(port.Name);
                }
                ports.AddRange(bound);
            }
        }
        string? errors = ContainerEngine.Text(state, "Error") is { Length: > 0 } error ? error : null;
        if (ContainerEngine.Text(state, "OOMKilled") == "true")
        {
            errors = errors is null ? "Ended by the kernel for want of memory" : $"{errors}; ended by the kernel for want of memory";
        }
        string status = ContainerEngine.Text(state, "Status") ?? string.Empty;
        string[] test = Strings(Child(Child(config, "Healthcheck"), "Test"));
        string? policy = ContainerEngine.Text(said, "HostConfig", "RestartPolicy", "Name");
        string id = ContainerEngine.Text(said, "Id") ?? string.Empty;
        return new ContainerDetails
        {
            Id = id,
            Name = (ContainerEngine.Text(said, "Name") ?? string.Empty).TrimStart('/'),
            Image = ContainerEngine.Text(config, "Image") ?? string.Empty,
            State = status,
            Health = ContainerEngine.Text(health, "Status") is { Length: > 0 } now && now != "none" ? now : null,
            FailingStreak = int.TryParse(ContainerEngine.Text(health, "FailingStreak"), CultureInfo.InvariantCulture, out int streak) ? streak : 0,
            // ["CMD", words…] runs the words, ["CMD-SHELL", line] the line in the image's shell, ["NONE"] none.
            HealthCheck = test is ["CMD-SHELL", var line] ? line : test is ["CMD", .. var words] ? string.Join(' ', words) : null,
            Probes = Child(health, "Log") is { ValueKind: JsonValueKind.Array } log
                ? [.. log.EnumerateArray().Reverse().Select(p => new HealthProbe(Time(ContainerEngine.Text(p, "Start")),
                    int.TryParse(ContainerEngine.Text(p, "ExitCode"), CultureInfo.InvariantCulture, out int code) ? code : 0,
                    (ContainerEngine.Text(p, "Output") ?? string.Empty).Trim()))]
                : [],
            RestartCount = int.TryParse(ContainerEngine.Text(said, "RestartCount"), CultureInfo.InvariantCulture, out int restarts) ? restarts : 0,
            RestartPolicy = policy is { Length: > 0 } and not "no" ? policy : null,
            LastExit = status is not ("running" or "paused") && Time(ContainerEngine.Text(state, "FinishedAt")) is { } finished
                && int.TryParse(ContainerEngine.Text(state, "ExitCode"), CultureInfo.InvariantCulture, out int exit) ? (exit, finished) : null,
            Error = errors,
            Environment = [.. Strings(Child(config, "Env"))
                .Select(e => e.Split('=', 2))
                .Select(e => (e[0], ContainerSecrets.Shown(e[0], e.Length > 1 ? e[1] : string.Empty)))
                .OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase)],
            Mounts = Child(said, "Mounts") is { ValueKind: JsonValueKind.Array } mounts
                ? [.. mounts.EnumerateArray().Select(m => new ContainerMount(ContainerEngine.Text(m, "Type") ?? string.Empty,
                    (ContainerEngine.Text(m, "Type") == "volume" ? ContainerEngine.Text(m, "Name") : null) ?? ContainerEngine.Text(m, "Source") ?? string.Empty,
                    ContainerEngine.Text(m, "Destination") ?? string.Empty, ContainerEngine.Text(m, "RW") == "false"))]
                : [],
            Networks = Child(settings, "Networks") is { ValueKind: JsonValueKind.Object } networks
                ? [.. networks.EnumerateObject().Select(n => new ContainerNetwork(n.Name, ContainerEngine.Text(n.Value, "IPAddress") is { Length: > 0 } address ? address : null,
                    // The engine also answers to the start of its id, which says nothing the row does not.
                    [.. Strings(Child(n.Value, "Aliases")).Concat(Strings(Child(n.Value, "DNSNames"))).Distinct(StringComparer.Ordinal)
                        .Where(a => id.Length == 0 || !id.StartsWith(a, StringComparison.Ordinal))]))]
                : [],
            Ports = ports,
            Unpublished = unpublished,
            DependsOn = ContainerEngine.Text(config, "Labels", "com.docker.compose.depends_on") is { Length: > 0 } waits
                ? [.. waits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.Split(':'))
                    .Select(w => new ServiceDependency(w[0], w.Length > 1 ? w[1] : "service_started", w.Length > 2 && w[2] == "true"))]
                : [],
        };
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child) ? child : default;

    private static string[] Strings(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : [];

    /// <summary>A time as the engine writes it (RFC 3339 to the nanosecond); null for none, or its zero time.</summary>
    private static DateTimeOffset? Time(string? said)
    {
        if (said is null)
        {
            return null;
        }
        // .NET reads seven digits of a second's fraction, the engine writes nine.
        int dot = said.IndexOf('.', StringComparison.Ordinal);
        int end = dot < 0 ? -1 : said.IndexOfAny(['Z', '+', '-'], dot);
        string read = dot >= 0 && end > dot + 8 ? said[..(dot + 8)] + said[end..] : said;
        return DateTimeOffset.TryParse(read, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) && at.Year > 1 ? at : null;
    }
}

/// <summary>A container's Details tab: what <c>docker inspect</c> says of it, read as it is chosen and again each time the
/// window reads the engine (its events say what changed): the container, its health check's last outputs, its ports
/// (links to the browser), the services it waits for, its environment (secrets masked), mounts and networks. A service's
/// row shows its container's (its running one, of several).</summary>
public sealed class ContainerDetailsPane : DetailsTables
{
    private readonly Func<ContainerEngine> engine;
    private readonly Action<Uri> browse;
    private readonly Func<string?> endpoint;
    private ContainerInfo? container;
    private int reads;

    public ContainerDetailsPane(Func<ContainerEngine> engine, Action<Uri> browse, Func<string?> endpoint)
    {
        this.engine = engine;
        this.browse = browse;
        this.endpoint = endpoint;
    }

    /// <summary>What was read of the container shown; null while none is, or before its read lands.</summary>
    public ContainerDetails? Details { get; private set; }

    /// <summary>Reads a row's container (a service's, its running one first), or shows nothing; the same snapshot's container
    /// again keeps what was read, a later snapshot's is read again.</summary>
    public void Show(ContainerRow? row)
    {
        UiThread.Check();
        ContainerInfo? next = row is null ? null : row.Container ?? row.Containers.FirstOrDefault(c => c.IsRunning) ?? row.Containers.FirstOrDefault();
        if (ReferenceEquals(next, container))
        {
            return;
        }
        container = next;
        reads++;
        if (next is null)
        {
            Details = null;
            Say(null);
            return;
        }
        if (Details?.Id != next.Id)
        {
            Details = null;
            Say($"Reading what the engine says of {next.Name}…");
        }
        IdeErrors.Observe(ReadAsync(next, reads));
    }

    private async Task ReadAsync(ContainerInfo shown, int read)
    {
        ContainerEngine asked = engine();
        (ContainerDetails? details, string? problem) = await BackgroundWork.Run(async () =>
        {
            try
            {
                JsonElement inspected = await asked.JsonAsync(["inspect", "--type", "container", shown.Id]);
                JsonElement one = inspected.ValueKind == JsonValueKind.Array ? inspected.EnumerateArray().FirstOrDefault() : inspected;
                return one.ValueKind == JsonValueKind.Object ? (ContainerDetails.Read(one), (string?)null) : (null, $"The engine says nothing of {shown.Name}");
            }
            catch (Exception failed) when (failed is ContainerCommandException or TimeoutException or JsonException or FileNotFoundException
                or System.ComponentModel.Win32Exception)
            {
                return ((ContainerDetails?)null, failed.Message);
            }
        });
        if (read != reads)
        {
            return;
        }
        Details = details;
        if (details is null)
        {
            Say(problem ?? string.Empty);
        }
        else
        {
            // Read again as the engine's events come: tables that show what they showed stay, a press on a port's link kept.
            Show(() => Fill(details));
        }
    }

    private void Fill(ContainerDetails details)
    {
        List<(string, Control)> about =
        [
            ("Image", Words(details.Image)),
            ("State", State(details.State, details.Health)),
            ("Restarts", Words(details.RestartPolicy is { } policy ? $"{details.RestartCount} (restarted {Policy(policy)})" : $"{details.RestartCount}")),
        ];
        if (details.LastExit is { } exit)
        {
            about.Add(("Last exit", Words($"Exit code {exit.Code}, {When(exit.At)}")));
        }
        if (details.Error is { } error)
        {
            about.Add(("Error", Words(error, brush: TokenKeys.Error)));
        }
        Section("Container", about);
        if (details.HealthCheck is not null || details.Health is not null)
        {
            List<(string, Control)> health = [];
            if (details.HealthCheck is { } check)
            {
                health.Add(("Check", Words(check, code: true)));
            }
            if (details.Health is { } now)
            {
                health.Add(("Status", Words(now == "unhealthy" && details.FailingStreak > 0
                    ? $"unhealthy: its last {Plural.Of(details.FailingStreak, "check")} failed" : now,
                    brush: now == "unhealthy" ? TokenKeys.Error : null)));
            }
            foreach (HealthProbe probe in details.Probes)
            {
                health.Add(($"{(probe.Started is { } at ? When(at) : "A check")}, exit {probe.ExitCode}",
                    Words(probe.Output.Length > 0 ? probe.Output : "(printed nothing)", code: probe.Output.Length > 0)));
            }
            Section("Health", health);
        }
        if (details.Ports.Count > 0 || details.Unpublished.Count > 0)
        {
            Section("Ports", [.. details.Ports.Select(p => ($"{p.Inside}/{p.Protocol}", Port(p, details.State == "running"))),
                .. details.Unpublished.Select(p => (p, (Control)Words("Not published", brush: TokenKeys.TextMuted)))]);
        }
        if (details.DependsOn.Count > 0)
        {
            Section("Depends on", [.. details.DependsOn.Select(d => (d.Service, (Control)Words($"{Condition(d.Condition)}{(d.Restart ? ", restarted with it" : string.Empty)}")))]);
        }
        if (details.Environment.Count > 0)
        {
            Section("Environment", [.. details.Environment.Select(e => (e.Name, (Control)Words(e.Value, code: true)))]);
        }
        if (details.Mounts.Count > 0)
        {
            Section("Mounts", [.. details.Mounts.Select(m => (m.Destination, (Control)Words($"{m.Type} {m.Source}{(m.ReadOnly ? " (read-only)" : string.Empty)}")))]);
        }
        if (details.Networks.Count > 0)
        {
            Section("Networks", [.. details.Networks.Select(n => (n.Name, (Control)Words(string.Join("  ·  ",
                ((string?[])[n.Address, n.Aliases.Count > 0 ? string.Join(", ", n.Aliases) : null]).OfType<string>()))))]);
        }
    }

    /// <summary>A published port: a link to it on the engine's machine while the container runs, else its port in words.</summary>
    private Control Port(ContainerPort port, bool running) => running && port.LinkOn(endpoint()) is { } link
        ? Link($"{link.Host}:{port.HostPort}", link.ToString(), () => browse(link), "detailsPort")
        : Words($"Host port {port.HostPort}");

    /// <summary>A health check's state as a tag in its colour.</summary>
    internal static Border HealthTag(string health)
    {
        TextBlock said = new() { Text = health, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        said.Bind(TextBlock.ForegroundProperty, said.GetResourceObservable(health == "healthy" ? TokenKeys.Test_Passed : health == "unhealthy" ? TokenKeys.Error : TokenKeys.Warning));
        return new Border { Classes = { "tag" }, Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, Child = said };
    }

    private static string Condition(string condition) => condition switch
    {
        "service_healthy" => "Once it is healthy",
        "service_completed_successfully" => "Once it has run to success",
        _ => "Once it has started",
    };

    private static string Policy(string policy) => policy switch
    {
        "always" => "always",
        "unless-stopped" => "unless stopped",
        "on-failure" => "on failure",
        _ => policy,
    };

    /// <summary>A time in the person's own zone: its time of day today, else its date too.</summary>
    internal static string When(DateTimeOffset at)
    {
        DateTimeOffset local = at.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
