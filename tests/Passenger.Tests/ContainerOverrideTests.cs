using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The override a container run lays over its compose files, as written for an engine and a project with no engine
/// asked: who a .NET service's app runs as to read what is kept for the person alone, and what it is given there.</summary>
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
                JsonObject web = await WebAsync(engine, owner, mode, new Dictionary<string, string> { ["ASPNETCORE_HTTPS_PORTS"] = "8081" });
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
        string id = $"passenger-test-override-{Guid.NewGuid():N}";
        string secrets = TestFolders.Write(TestRun.UserSecrets, $"{id}/secrets.json", "{ \"Greeting\": \"from secrets\" }\n");
        try
        {
            string mounted = $"{ContainerPlans.AppData}/Microsoft/UserSecrets/{id}";
            foreach ((string variable, string environment, bool reads) in ((string, string, bool)[])[
                ("ASPNETCORE_ENVIRONMENT", "development", true), ("DOTNET_ENVIRONMENT", "DEVELOPMENT", true), ("ASPNETCORE_ENVIRONMENT", "Production", false)])
            {
                foreach (ContainerMode mode in (ContainerMode[])[ContainerMode.Fast, ContainerMode.Images])
                {
                    JsonObject web = await WebAsync(Engine(podman: false, rootless: false), Owner, mode, new Dictionary<string, string> { [variable] = environment }, id);
                    string[] targets = [.. (web["volumes"] as JsonArray ?? []).Select(v => v?["target"]?.GetValue<string>() ?? string.Empty)];
                    Assert.True(reads == targets.Contains(mounted),
                        $"With {variable}: {environment} in {mode} mode web's user secrets were {(reads ? string.Empty : "not ")}to be mounted at {mounted}: {web.ToJsonString()}");
                    Assert.True(reads == (web["environment"]?["APPDATA"]?.GetValue<string>() == ContainerPlans.AppData),
                        $"With {variable}: {environment} in {mode} mode web was {(reads ? string.Empty : "not ")}to read its user secrets from APPDATA: {web.ToJsonString()}");
                }
            }
        }
        finally
        {
            TestFolders.Delete(Path.GetDirectoryName(secrets)!);
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

    private static EngineState Engine(bool podman, bool rootless) =>
        new(EngineStatus.Ready, "docker", null, "29.0.0", "linux", "stand-in", "unix:///run/user/1000/docker.sock", "2.39.0", null) { IsPodman = podman, Rootless = rootless };
}
