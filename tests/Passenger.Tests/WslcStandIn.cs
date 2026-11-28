namespace Passenger.Tests;

/// <summary>A stand-in for WSL Containers' <c>wslc</c>, answering in the shapes its CLI writes in WSL's own source
/// (microsoft/WSL, MIT: <c>src/windows/wslc</c>): its info, version, container and network lists, inspect and stats as its
/// JSON, one container and its session network held in its folder until removed (no image or volume), its events while
/// it is up, each call recorded a line a call; what it has no command for (compose, buildx, scout, context, pause) is
/// unrecognized, as its parser says. A Windows .cmd, else a shell script that stands in for the CLI's shape alone.</summary>
internal sealed class WslcStandIn
{
    private WslcStandIn(string folder, string cli, string id)
    {
        Folder = folder;
        Cli = cli;
        Id = id;
    }

    /// <summary>The folder it lives in, which a test puts first on the PATH.</summary>
    public string Folder { get; }

    /// <summary>The stand-in's program.</summary>
    public string Cli { get; }

    /// <summary>The whole id of the container it holds.</summary>
    public string Id { get; }

    /// <summary>Whether it still holds its container, which <c>rm</c> removes.</summary>
    public bool Holds => File.Exists(Path.Combine(Folder, "running"));

    /// <summary>Whether it still holds its session network, which <c>network rm</c> removes.</summary>
    public bool HoldsNetwork => File.Exists(Path.Combine(Folder, "network"));

    /// <summary>The calls made of it so far, a line each, as given.</summary>
    public IReadOnlyList<string> Calls => File.Exists(Path.Combine(Folder, "calls"))
        ? [.. Ui.Shared(Path.Combine(Folder, "calls")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [];

    /// <summary>Ends its events, as its session ending would.</summary>
    public void Down() => File.Delete(Path.Combine(Folder, "up"));

    /// <summary>A stand-in holding one running container of that id and name, of an AppHost's orchestrator (DCP's labels),
    /// on a session network of that name.</summary>
    public static WslcStandIn Write(string folder, string id, string name, string network)
    {
        const string Info = """{"Client":{"Version":"2.7.3.0","KernelVersion":"6.6.87.2-1","Direct3DVersion":"1.611.1-81528511","DxCoreVersion":"10.0.26100.1-240331-1435.ge-release","WindowsVersion":"10.0.26100.7171","SettingsFile":"C:\\Users\\person\\AppData\\Local\\wslc\\settings.yaml"},"Server":{"SessionManagerVersion":"2.7.3","Sessions":[{"ID":1,"CreatorPid":4242,"Name":"wslc-cli-person"}]}}""";
        const string Version = """{"Client":{"Version":"2.7.3.0"}}""";
        string labels = $"com.microsoft.developer.usvc-dev.creatorProcessId=4242,com.microsoft.developer.usvc-dev.persistent=false,com.microsoft.developer.usvc-dev.uid={name}";
        string Listed(string shownId) =>
            $$"""{"Command":"redis-server","CreatedAt":"2026-11-27 10:00:00 +0000 UTC","HealthStatus":"","ID":"{{shownId}}","Image":"docker.io/library/redis:8.0","Labels":"{{labels}}","LocalVolumes":"0","Mounts":"","Names":"{{name}}","Networks":"{{network}}","Platform":{"architecture":"amd64","os":"linux"},"Ports":"","RunningFor":"2 minutes ago","Size":"0B","State":"running","Status":"Up 2 minutes"}""";
        string networkListed = $$"""{"CreatedAt":"2026-11-27 10:00:00 +0000 UTC","Driver":"bridge","ID":"5e5510aa0001","IPv4":"true","IPv6":"false","Internal":"false","Labels":"com.microsoft.developer.usvc-dev.creatorProcessId=4242,com.microsoft.developer.usvc-dev.persistent=false","Name":"{{network}}","Scope":"local"}""";
        string inspected = """[{"Id":"@id","Name":"/@name","Created":"2026-11-27T10:00:00.000000000Z","Image":"sha256:5a5d","State":{"Status":"running","Running":true,"ExitCode":0,"StartedAt":"2026-11-27T10:00:01.000000000Z","FinishedAt":"0001-01-01T00:00:00Z"},"RestartCount":0,"HostConfig":{"NetworkMode":"@network","Memory":0,"NanoCpus":0,"Ulimits":[],"RestartPolicy":{"Name":"no","MaximumRetryCount":0},"CapAdd":[],"CapDrop":[]},"Config":{"Image":"docker.io/library/redis:8.0","Env":["PATH=/usr/local/bin:/usr/bin"],"Cmd":["redis-server"],"User":"","WorkingDir":"/data","Labels":{}},"Ports":{"6379/tcp":[{"HostIp":"127.0.0.1","HostPort":"50379"}]},"Mounts":[],"Labels":{},"NetworkSettings":{"Networks":{"@network":{"IPAddress":"172.18.0.2","Gateway":"172.18.0.1","MacAddress":"","IPPrefixLen":16,"Aliases":[],"Links":[],"DriverOpts":{}}}}}]"""
            .Replace("@id", id, StringComparison.Ordinal).Replace("@name", name, StringComparison.Ordinal).Replace("@network", network, StringComparison.Ordinal);
        string stats = $$"""{"ID":"{{id}}","Name":"{{name}}","CPUPerc":"1.50%","MemUsage":"10MiB / 1GiB","MemPerc":"0.98%","NetIO":"1.2kB / 3.4kB","BlockIO":"0B / 0B","PIDs":4}""";
        foreach (string state in (string[])["up", "running", "network"])
        {
            File.WriteAllText(Path.Combine(folder, state), state);
        }
        string here = OperatingSystem.IsWindows() ? "%~dp0" : "$here/";
        string cli = TestFolders.StandIn(folder, "wslc",
            string.Join("\r\n", (string[])[
                $"echo %*>>\"{here}calls\"",
                "set first=%~1",
                "set second=%~2",
                "if \"%first%\"==\"container\" (set first=%~2& set second=%~3)",
                "if \"%first%\"==\"info\" (echo " + Info + "& exit /b 0)",
                "if \"%first%\"==\"version\" (echo " + Version + "& exit /b 0)",
                "if \"%first%\"==\"events\" goto events",
                "if \"%first%\"==\"network\" goto network",
                "if \"%first%\"==\"images\" exit /b 0",
                "if \"%first%\"==\"volume\" exit /b 0",
                "if not exist \"" + here + "running\" (if not \"%first%\"==\"ps\" if not \"%first%\"==\"list\" if not \"%first%\"==\"ls\" (echo Container '%~2' not found.1>&2& exit /b 1))",
                "if \"%first%\"==\"ps\" goto list",
                "if \"%first%\"==\"list\" goto list",
                "if \"%first%\"==\"ls\" goto list",
                "if \"%first%\"==\"inspect\" (echo " + inspected + "& exit /b 0)",
                "if \"%first%\"==\"stats\" (echo " + stats.Replace("%", "%%", StringComparison.Ordinal) + "& exit /b 0)",
                "if \"%first%\"==\"logs\" (echo hello from the stand-in wslc& exit /b 0)",
                "if \"%first%\"==\"rm\" (del \"" + here + "running\"& echo " + id + "& exit /b 0)",
                "if \"%first%\"==\"remove\" (del \"" + here + "running\"& echo " + id + "& exit /b 0)",
                "if \"%first%\"==\"stop\" (echo " + id + "& exit /b 0)",
                "if \"%first%\"==\"exec\" exit /b 0",
                "echo Unrecognized command: '%first%'1>&2",
                "exit /b 1",
                ":list",
                "if not exist \"" + here + "running\" exit /b 0",
                "echo %*| findstr /c:\"--no-trunc\" >nul && (echo " + Listed(id) + "& exit /b 0)",
                "echo " + Listed(id[..12]),
                "exit /b 0",
                ":network",
                "if \"%second%\"==\"rm\" (del \"" + here + "network\"& echo %~3& exit /b 0)",
                "if exist \"" + here + "network\" echo " + networkListed,
                "exit /b 0",
                ":events",
                "if not exist \"" + here + "up\" exit /b 0",
                "ping -n 2 127.0.0.1 >nul",
                "goto events",
            ]),
            string.Join("\n", (string[])[
                "here=$(dirname \"$0\")",
                "echo \"$*\" >> \"$here/calls\"",
                "[ \"$1\" = container ] && shift",
                "case \"$1\" in",
                $"  info) printf '%s\\n' '{Info}'; exit 0 ;;",
                $"  version) printf '%s\\n' '{Version}'; exit 0 ;;",
                "  events) while [ -f \"$here/up\" ]; do sleep 0.2; done; exit 0 ;;",
                "  network) case \"$2\" in",
                "      rm) rm -f \"$here/network\"; echo \"$3\"; exit 0 ;;",
                $"      ls|list) [ -f \"$here/network\" ] && printf '%s\\n' '{networkListed}'; exit 0 ;;",
                "    esac; exit 1 ;;",
                "  images|volume) exit 0 ;;",
                "  ps|list|ls) [ -f \"$here/running\" ] || exit 0",
                $"    case \" $* \" in *' --no-trunc '*) printf '%s\\n' '{Listed(id)}' ;; *) printf '%s\\n' '{Listed(id[..12])}' ;; esac; exit 0 ;;",
                "  inspect|stats|logs|rm|remove|stop|exec) [ -f \"$here/running\" ] || { for word in \"$@\"; do last=$word; done; echo \"Container '$last' not found.\" >&2; exit 1; } ;;",
                "esac",
                "case \"$1\" in",
                $"  inspect) printf '%s\\n' '{inspected}' ;;",
                $"  stats) printf '%s\\n' '{stats}' ;;",
                "  logs) echo 'hello from the stand-in wslc' ;;",
                $"  rm|remove) rm -f \"$here/running\"; printf '%s\\n' '{id}' ;;",
                $"  stop) printf '%s\\n' '{id}' ;;",
                "  exec) ;;",
                "  *) echo \"Unrecognized command: '$1'\" >&2; exit 1 ;;",
                "esac",
            ]));
        return new WslcStandIn(folder, cli, id);
    }
}
