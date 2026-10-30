using System.Text.Json.Nodes;
using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>A Dockerfile or SDK container run's <c>docker run</c> words become its compose service's keys in every form
/// docker takes them, and a word Compose has no place for is named, never dropped unsaid.</summary>
public sealed class DockerRunWordsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "web");

    [Fact]
    public void Options_joined_to_their_values_set_the_environment_port_and_memory_limit()
    {
        DockerRunWords read = DockerRunWords.Read(["--env=MODE=prod", "--publish=8080:80", "--memory=512m"], [], Folder);
        Assert.Equal("""{"MODE":"prod"}""", read.Environment.ToJsonString());
        Assert.Equal("""{"ports":["8080:80"],"mem_limit":"512m"}""", read.Keys.ToJsonString());
        Assert.Empty(read.Uncarried);
    }

    [Fact]
    public void Each_form_of_an_option_reads_alike_and_one_letter_flags_join()
    {
        DockerRunWords spaced = DockerRunWords.Read(["-p", "8080:80", "-e", "A=1", "-m", "1g", "-v", "data:/data", "--env-file", "web.env", "-i", "-t"], [], Folder);
        DockerRunWords joined = DockerRunWords.Read(["-p8080:80", "-eA=1", "-m1g", "--volume=data:/data", "--env-file=web.env", "-it"], [], Folder);
        Assert.Equal(spaced.Keys.ToJsonString(), joined.Keys.ToJsonString());
        Assert.Equal(spaced.Environment.ToJsonString(), joined.Environment.ToJsonString());
        Assert.Equal(Path.Combine(Folder, "web.env"), joined.Keys["env_file"]![0]!.GetValue<string>());
        Assert.True(joined.Keys["tty"]!.GetValue<bool>() && joined.Keys["stdin_open"]!.GetValue<bool>(), joined.Keys.ToJsonString());
    }

    [Fact]
    public void Limits_hosts_and_published_exposed_ports_are_carried_and_a_name_alone_takes_its_value_from_where_compose_runs()
    {
        DockerRunWords read = DockerRunWords.Read(["--cpus", "1.5", "--add-host=db:10.0.0.2", "-P", "-e", "TOKEN", "--rm", "-d", "--network", "host"], [8080, 8081], Folder);
        Assert.Equal("""{"cpus":"1.5","extra_hosts":["db:10.0.0.2"],"ports":["8080","8081"],"network_mode":"host"}""", read.Keys.ToJsonString());
        Assert.Equal("""{"TOKEN":null}""", read.Environment.ToJsonString());
        Assert.Empty(read.Uncarried);
    }

    [Fact]
    public void An_option_compose_has_no_place_for_is_named_with_its_value()
    {
        DockerRunWords read = DockerRunWords.Read(["--gpus", "all", "--ulimit=nofile=1024", "-p", "80", "--network", "backend", "--sig-proxy", "-x", "stray"], [], Folder);
        Assert.Equal(["--gpus all", "--ulimit=nofile=1024", "--network=backend", "--sig-proxy", "-x", "stray"], read.Uncarried);
        Assert.Equal("""{"ports":["80"]}""", read.Keys.ToJsonString());
    }

    [Fact]
    public void The_service_takes_the_keys_and_the_words_environment_over_the_configurations()
    {
        JsonObject service = new() { ["image"] = "web" };
        JsonObject environment = new() { ["MODE"] = "dev", ["KEEP"] = "1" };
        DockerRunWords.Read(["-e", "MODE=prod", "-p", "80"], [], Folder).SetOn(service, environment);
        Assert.Equal("""{"image":"web","ports":["80"]}""", service.ToJsonString());
        Assert.Equal("""{"MODE":"prod","KEEP":"1"}""", environment.ToJsonString());
    }
}
