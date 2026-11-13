using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The override a container run lays over its compose files, as written for an engine and a project with no engine
/// asked: who a .NET service's app runs as to read what is kept for the person alone, and what it is given there; and
/// whether the dashboard it runs beside them is read from this machine.</summary>
[Collection(Beside.Name)]
public sealed class ContainerOverrideTests : IDisposable
{
    private const string Owner = "1000:1000";

    private readonly string folder = TestFolders.Create("ctr-override-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>On a rootless engine, Docker or Podman, a service runs as the container's root, which is the person on the
    /// host, never as their uid:gid, a subordinate id there that can read none of what they keep for themselves, nor with
    /// Podman's keep-id; on a rootful Linux engine it runs as the person; where no owner is given, as its image says.</summary>
    [Fact]
    public async Task A_service_on_a_rootless_engine_runs_as_its_containers_root_and_on_a_rootful_one_as_the_person()
    {
        EngineState docker = Engine(podman: false, rootless: false);
        foreach ((EngineState engine, string? owner, string? user) in ((EngineState, string?, string?)[])[
            (docker with { Rootless = true }, Owner, "0:0"),
            (Engine(podman: true, rootless: true), Owner, "0:0"),
            (docker, Owner, Owner),
            (docker with { Rootless = true }, null, null)])
        {
            foreach (ContainerMode mode in (ContainerMode[])[ContainerMode.Fast, ContainerMode.Images])
            {
                JsonObject web = await WebAsync(engine, owner, mode, new Dictionary<string, string> { [AspNetCoreVariables.HttpsPorts] = "8081" });
                string on = $"{engine.EngineName}{(engine.Rootless ? " rootless" : string.Empty)} with {owner ?? "no owner"} in {mode} mode";
                Assert.True(user == web["user"]?.GetValue<string>(), $"On {on} web was to run as {user ?? "its image's user"}, but the override said {web.ToJsonString()}");
                Assert.True(web["userns_mode"] is null, $"On {on} web was given a user namespace of its own: {web.ToJsonString()}");
            }
        }
    }

    /// <summary>A service whose files name its environment development in lower case runs in Development, as .NET reads the
    /// name, so its project's user secrets are mounted where it reads them, in fast mode and as deployed; one in Production
    /// is given none.</summary>
    [Fact]
    public async Task A_service_whose_environment_is_development_in_lower_case_has_its_user_secrets_mounted()
    {
        await WithSecretsAsync(async id =>
        {
            foreach ((string variable, string environment, bool reads) in ((string, string, bool)[])[
                (AspNetCoreVariables.AspNetCoreEnvironment, "development", true), (AspNetCoreVariables.DotnetEnvironment, "DEVELOPMENT", true), (AspNetCoreVariables.AspNetCoreEnvironment, "Production", false)])
            {
                await AssertSecretsAsync(id, new Dictionary<string, string> { [variable] = environment }, reads, $"{variable}: {environment}");
            }
        });
    }

    /// <summary>A service whose files set ASPNETCORE_ENVIRONMENT Staging and DOTNET_ENVIRONMENT Development runs in Staging,
    /// as ASP.NET Core reads ASPNETCORE_ENVIRONMENT first, so the user secrets only Development reads are not mounted; the
    /// other way round it runs in Development and they are.</summary>
    [Fact]
    public async Task A_service_whose_ASPNETCORE_ENVIRONMENT_is_Staging_runs_in_Staging_whatever_DOTNET_ENVIRONMENT_says()
    {
        await WithSecretsAsync(async id =>
        {
            foreach ((string aspNetCore, string dotnet, bool reads) in ((string, string, bool)[])[("Staging", "Development", false), ("Development", "Staging", true)])
            {
                Dictionary<string, string> environment = new()
                {
                    [AspNetCoreVariables.DotnetEnvironment] = dotnet,
                    [AspNetCoreVariables.AspNetCoreEnvironment] = aspNetCore,
                };
                await AssertSecretsAsync(id, environment, reads, $"ASPNETCORE_ENVIRONMENT: {aspNetCore} and DOTNET_ENVIRONMENT: {dotnet}");
            }
        });
    }

    /// <summary>Runs <paramref name="test"/> with user secrets of an id of its own in the run's folder, deleted after.</summary>
    private static async Task WithSecretsAsync(Func<string, Task> test)
    {
        string id = $"passenger-test-override-{Guid.NewGuid():N}";
        string secrets = TestFolders.Write(TestRun.UserSecrets, $"{id}/secrets.json", "{ \"Greeting\": \"from secrets\" }\n");
        try
        {
            await test(id);
        }
        finally
        {
            TestFolders.Delete(Path.GetDirectoryName(secrets)!);
        }
    }

    /// <summary>Whether web, given the environment, has its user secrets mounted where it reads them and APPDATA set to find
    /// them, in fast mode and as deployed, as <paramref name="reads"/> says.</summary>
    private async Task AssertSecretsAsync(string id, IReadOnlyDictionary<string, string> environment, bool reads, string given)
    {
        string mounted = $"{ContainerPlans.AppData}/Microsoft/UserSecrets/{id}";
        foreach (ContainerMode mode in (ContainerMode[])[ContainerMode.Fast, ContainerMode.Images])
        {
            JsonObject web = await WebAsync(Engine(podman: false, rootless: false), Owner, mode, environment, id);
            string[] targets = [.. (web["volumes"] as JsonArray ?? []).Select(v => v?["target"]?.GetValue<string>() ?? string.Empty)];
            Assert.True(reads == targets.Contains(mounted),
                $"With {given} in {mode} mode web's user secrets were {(reads ? string.Empty : "not ")}to be mounted at {mounted}: {web.ToJsonString()}");
            Assert.True(reads == (web["environment"]?["APPDATA"]?.GetValue<string>() == ContainerPlans.AppData),
                $"With {given} in {mode} mode web was {(reads ? string.Empty : "not ")}to read its user secrets from APPDATA: {web.ToJsonString()}");
        }
    }

    /// <summary>The override's entry for a .NET service web of a Web project, built from the folder, given the environment.</summary>
    private async Task<JsonObject> WebAsync(EngineState engine, string? owner, ContainerMode mode, IReadOnlyDictionary<string, string> environment, string? secretsId = null)
    {
        string project = TestFolders.Write(folder, "Web/Web.csproj", TestProjects.Web(secretsId is null ? [] : [$"<UserSecretsId>{secretsId}</UserSecretsId>"]));
        TestFolders.Write(folder, "Web/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nUSER $APP_UID\nWORKDIR /app\n");
        JsonObject service = new()
        {
            ["build"] = new JsonObject { ["context"] = folder, ["dockerfile"] = "Web/Dockerfile" },
            ["ports"] = new JsonArray(new JsonObject { ["target"] = 8081, ["published"] = "8081", ["protocol"] = "tcp" }),
            ["environment"] = new JsonObject([.. environment.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value))]),
        };
        using JsonDocument read = JsonDocument.Parse(new JsonObject { ["name"] = "shop", ["services"] = new JsonObject { ["web"] = service } }.ToJsonString());
        ComposeModel model = ComposeModel.Read(read.RootElement);
        DotnetService web = new("web", project, "Web", Path.Combine(folder, "Web", "bin", "Debug", TestProjects.Framework), ServiceStart.Run, "base", ContainerPaths.None);
        ProjectModel loaded = new(project, "Web", null, [TestProjects.Framework], [], [], Exists: true);
        JsonObject written = await ContainerPlans.OverrideAsync(model, "shop", [web], mode, [], [loaded], Directory.CreateDirectory(Path.Combine(folder, "state")).FullName,
            null, [], [], null, engine, owner, null, new Dictionary<string, IReadOnlyDictionary<string, string>>(), TestContext.Current.CancellationToken);
        return (JsonObject)written["services"]!["web"]!;
    }

    /// <summary>As deployed, a .NET service whose Dockerfile takes BUILD_CONFIGURATION builds its image in the configuration
    /// its project builds in under the folder's choice (Release, or a solution's own Staging), never a fixed Debug; one
    /// whose Dockerfile takes none, or one run from Passenger's build, is given none.</summary>
    [Fact]
    public async Task An_image_built_as_deployed_takes_its_projects_build_configuration_where_its_dockerfile_asks_for_one()
    {
        const string Takes = "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nWORKDIR /app\n\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\n"
            + "ARG BUILD_CONFIGURATION=Release\nWORKDIR /src\nCOPY . .\nRUN dotnet publish Web/Web.csproj -c $BUILD_CONFIGURATION -o /app/publish\n\n"
            + "FROM base AS final\nCOPY --from=build /app/publish .\nENTRYPOINT [\"dotnet\", \"Web.dll\"]\n";
        const string TakesNone = "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nWORKDIR /app\nENTRYPOINT [\"dotnet\", \"Web.dll\"]\n";
        foreach ((string dockerfile, ContainerMode mode, string configuration, string? given) in ((string, ContainerMode, string, string?)[])[
            (Takes, ContainerMode.Images, "Release", "Release"), (Takes, ContainerMode.Images, "Staging", "Staging"), (Takes, ContainerMode.Images, "Debug", "Debug"),
            (TakesNone, ContainerMode.Images, "Release", null), (Takes, ContainerMode.Fast, "Release", null)])
        {
            JsonObject web = await BuiltAsync(dockerfile, mode, configuration);
            string? said = web["build"]?["args"]?[ContainerPlans.BuildConfigurationArgument]?.GetValue<string>();
            Assert.True(said == given, $"Web built in {configuration} {(dockerfile == Takes ? "by a Dockerfile taking" : "by a Dockerfile taking no")} "
                + $"BUILD_CONFIGURATION in {mode} mode was to be given {given ?? "none"}, but the override gives {said ?? "none"}: {web.ToJsonString()}");
        }
    }

    /// <summary>The override's entry for a .NET service web whose project builds in a configuration, its image built from
    /// the Dockerfile given.</summary>
    private async Task<JsonObject> BuiltAsync(string dockerfile, ContainerMode mode, string configuration)
    {
        string project = TestFolders.Write(folder, "Web/Web.csproj", TestProjects.Web());
        TestFolders.Write(folder, "Web/Dockerfile", dockerfile);
        JsonObject service = new() { ["build"] = new JsonObject { ["context"] = folder, ["dockerfile"] = "Web/Dockerfile" } };
        using JsonDocument read = JsonDocument.Parse(new JsonObject { ["name"] = "shop", ["services"] = new JsonObject { ["web"] = service } }.ToJsonString());
        ComposeModel model = ComposeModel.Read(read.RootElement);
        DotnetService web = new("web", project, "Web", Path.Combine(folder, "Web", "bin", configuration, TestProjects.Framework), ServiceStart.Run, "base", ContainerPaths.None)
        {
            Configuration = configuration,
        };
        ProjectModel loaded = new(project, "Web", null, [TestProjects.Framework], [], [], Exists: true);
        JsonObject written = await ContainerPlans.OverrideAsync(model, "shop", [web], mode, [], [loaded], Directory.CreateDirectory(Path.Combine(folder, "state")).FullName,
            null, [], [], null, Engine(podman: false, rootless: false), null, null, new Dictionary<string, IReadOnlyDictionary<string, string>>(), TestContext.Current.CancellationToken);
        return (JsonObject)written["services"]!["web"]!;
    }

    /// <summary>The run's dashboard is read on this machine's loopback where the engine is this machine's, by a socket, a
    /// loopback address or a name under localhost, and refused naming the engine where it is another machine's, whose
    /// loopback this one cannot reach.</summary>
    [Fact]
    public void A_runs_dashboard_is_read_where_the_engine_is_this_machines_and_refused_where_it_is_another_machines()
    {
        EngineState engine = Engine(podman: false, rootless: false);
        foreach (string here in (string[])["unix:///run/user/1000/docker.sock", "tcp://127.0.0.1:2375", "tcp://localhost:2375", "tcp://engine.dev.localhost:2376", "ssh://me@[::1]"])
        {
            Assert.True(ContainerTelemetry.Elsewhere(engine with { Endpoint = here }) is null,
                $"An engine at {here} was taken for another machine's, so its run's dashboard would not be read: {ContainerTelemetry.Elsewhere(engine with { Endpoint = here })}");
        }
        foreach (string there in (string[])["tcp://10.0.0.5:2375", "ssh://me@build.example.com", "tcp://localhost.example.com:2375"])
        {
            Assert.True(ContainerTelemetry.Elsewhere(engine with { Endpoint = there }) is { } said && said.Contains(there, StringComparison.Ordinal),
                $"An engine at {there} was taken for this machine's, so its run's dashboard would be looked for on this machine's loopback");
        }
    }

    private static EngineState Engine(bool podman, bool rootless) =>
        new(EngineStatus.Ready, "docker", null, "29.0.0", "linux", "stand-in", "unix:///run/user/1000/docker.sock", "2.39.0", null) { IsPodman = podman, Rootless = rootless };
}
