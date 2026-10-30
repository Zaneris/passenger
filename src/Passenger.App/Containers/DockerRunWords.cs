using System.Globalization;
using System.Text.Json.Nodes;

namespace Passenger.App.Containers;

/// <summary>A configuration's <c>docker run</c> words as the keys of the one compose service a Dockerfile or SDK container
/// run is: each option in every form docker takes it (<c>-p 8080:80</c>, <c>-p8080:80</c>, <c>--publish 8080:80</c>,
/// <c>--publish=8080:80</c>, flags joined as <c>-it</c>), set where Compose keeps it (ports, environment, mem_limit…),
/// and every word it has no place for named in <see cref="Uncarried"/>, never dropped unsaid.</summary>
public sealed record DockerRunWords(JsonObject Keys, JsonObject Environment, IReadOnlyList<string> Uncarried)
{
    private enum Takes
    {
        List,
        Value,
        Flag,
    }

    /// <summary>The long options Compose has a key for, with that key and whether it lists, sets or turns on.</summary>
    private static readonly Dictionary<string, (string Key, Takes Takes)> Options = new(StringComparer.Ordinal)
    {
        ["publish"] = ("ports", Takes.List), ["volume"] = ("volumes", Takes.List), ["env-file"] = ("env_file", Takes.List),
        ["add-host"] = ("extra_hosts", Takes.List), ["dns"] = ("dns", Takes.List), ["label"] = ("labels", Takes.List), ["expose"] = ("expose", Takes.List),
        ["cap-add"] = ("cap_add", Takes.List), ["cap-drop"] = ("cap_drop", Takes.List), ["security-opt"] = ("security_opt", Takes.List),
        ["tmpfs"] = ("tmpfs", Takes.List), ["device"] = ("devices", Takes.List), ["group-add"] = ("group_add", Takes.List),
        ["memory"] = ("mem_limit", Takes.Value), ["memory-reservation"] = ("mem_reservation", Takes.Value), ["memory-swap"] = ("memswap_limit", Takes.Value),
        ["cpus"] = ("cpus", Takes.Value), ["cpu-shares"] = ("cpu_shares", Takes.Value), ["cpuset-cpus"] = ("cpuset", Takes.Value),
        ["workdir"] = ("working_dir", Takes.Value), ["user"] = ("user", Takes.Value), ["hostname"] = ("hostname", Takes.Value),
        ["entrypoint"] = ("entrypoint", Takes.Value), ["restart"] = ("restart", Takes.Value), ["platform"] = ("platform", Takes.Value),
        ["shm-size"] = ("shm_size", Takes.Value), ["pids-limit"] = ("pids_limit", Takes.Value), ["stop-signal"] = ("stop_signal", Takes.Value),
        ["init"] = ("init", Takes.Flag), ["privileged"] = ("privileged", Takes.Flag), ["read-only"] = ("read_only", Takes.Flag),
        ["tty"] = ("tty", Takes.Flag), ["interactive"] = ("stdin_open", Takes.Flag),
    };

    /// <summary>The long options read apart: the environment, every exposed port published, a network, and those Compose
    /// does itself (a container removed, run in the background).</summary>
    private static readonly string[] Own = ["env", "publish-all", "network", "net", "rm", "detach"];

    /// <summary>The one-letter options docker takes, by the long option each stands for.</summary>
    private static readonly Dictionary<char, string> Letters = new()
    {
        ['p'] = "publish", ['v'] = "volume", ['e'] = "env", ['m'] = "memory", ['c'] = "cpu-shares", ['w'] = "workdir", ['u'] = "user",
        ['h'] = "hostname", ['l'] = "label", ['P'] = "publish-all", ['t'] = "tty", ['i'] = "interactive", ['d'] = "detach",
    };

    /// <summary>The words read: <paramref name="exposed"/> the ports <c>-P</c> publishes, <paramref name="folder"/> where an
    /// env file's path is from.</summary>
    public static DockerRunWords Read(IReadOnlyList<string> words, IEnumerable<int> exposed, string folder)
    {
        JsonObject keys = [];
        JsonObject environment = [];
        List<string> uncarried = [];
        for (int i = 0; i < words.Count; i++)
        {
            string word = words[i];
            string? Next() => i + 1 < words.Count ? words[++i] : null;
            if (word.StartsWith("--", StringComparison.Ordinal) && word.Length > 2)
            {
                int equals = word.IndexOf('=', StringComparison.Ordinal);
                string name = equals < 0 ? word[2..] : word[2..equals];
                string? given = equals < 0 ? null : word[(equals + 1)..];
                if (!Options.ContainsKey(name) && !Own.Contains(name))
                {
                    // An option it has no place for, with the word after it where that is its value.
                    uncarried.Add(given is null && i + 1 < words.Count && !words[i + 1].StartsWith('-') ? $"{word} {Next()}" : word);
                }
                else if (Carried(name, given, Next, keys, environment, exposed, folder) is { } refused)
                {
                    uncarried.Add(refused);
                }
            }
            else if (word.StartsWith('-') && word.Length > 1)
            {
                for (int at = 1; at < word.Length; at++)
                {
                    if (!Letters.TryGetValue(word[at], out string? name))
                    {
                        uncarried.Add(at == 1 ? word : $"-{word[at..]}");
                        break;
                    }
                    bool flag = name is "publish-all" or "detach" || Options.GetValueOrDefault(name).Takes == Takes.Flag;
                    // A letter taking a value takes the rest of the word, else the next word.
                    string? given = flag || at + 1 == word.Length ? null : word[(at + 1)..];
                    if (Carried(name, given, Next, keys, environment, exposed, folder) is { } refused)
                    {
                        uncarried.Add(refused);
                    }
                    if (!flag)
                    {
                        break;
                    }
                }
            }
            else
            {
                uncarried.Add(word);
            }
        }
        return new DockerRunWords(keys, environment, uncarried);
    }

    /// <summary>Sets what a long option of <see cref="Options"/> or <see cref="Own"/> says where Compose keeps it, its value
    /// <paramref name="given"/> with it or the next word: null once set, else the words of it Compose has no place for.</summary>
    private static string? Carried(string name, string? given, Func<string?> next, JsonObject keys, JsonObject environment, IEnumerable<int> exposed, string folder)
    {
        switch (name)
        {
            case "rm" or "detach":
                return null;
            case "publish-all":
                if (On(given))
                {
                    foreach (int port in exposed.Distinct())
                    {
                        Listed(keys, "ports").Add(port.ToString(CultureInfo.InvariantCulture));
                    }
                }
                return null;
            case "env":
                if ((given ?? next())?.Split('=', 2) is [{ Length: > 0 } key, .. string[] rest])
                {
                    // A name alone takes its value from where Compose runs, as docker run takes it from where it runs.
                    environment[key] = rest is [string set] ? set : null;
                }
                return null;
            case "network" or "net":
                // Compose's own network mode for these; a network of the engine's by name is the file's networks' to join.
                string? network = given ?? next();
                if (network is "host" or "none" or "bridge" || network?.StartsWith("container:", StringComparison.Ordinal) == true)
                {
                    keys["network_mode"] = network;
                    return null;
                }
                return $"--{name}={network}";
        }
        (string Key, Takes Takes) option = Options[name];
        if (option.Takes == Takes.Flag)
        {
            keys[option.Key] = On(given);
            return null;
        }
        if ((given ?? next()) is not { } value)
        {
            return null;
        }
        if (option.Takes == Takes.Value)
        {
            keys[option.Key] = value;
        }
        else
        {
            Listed(keys, option.Key).Add(option.Key == "env_file" ? Path.GetFullPath(Path.Combine(folder, value)) : value);
        }
        return null;
    }

    /// <summary>Whether a flag is on: given alone, or as true.</summary>
    private static bool On(string? given) => given is null || !string.Equals(given, "false", StringComparison.OrdinalIgnoreCase);

    private static JsonArray Listed(JsonObject keys, string key) => (JsonArray)(keys[key] ??= new JsonArray());

    /// <summary>The service's keys set on it, its environment over <paramref name="environment"/>'s.</summary>
    public void SetOn(JsonObject service, JsonObject environment)
    {
        foreach ((string key, JsonNode? value) in Keys)
        {
            service[key] = value?.DeepClone();
        }
        foreach ((string key, JsonNode? value) in Environment)
        {
            environment[key] = value?.DeepClone();
        }
    }
}
