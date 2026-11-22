using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>A Docker context's endpoint as a Testcontainers test host is given it: Docker.DotNet takes a Windows named pipe
/// only as npipe://SERVER/pipe/NAME, where the CLI's context writes npipe:////SERVER/pipe/NAME (#1430).</summary>
public sealed class DockerEndpointsTests
{
    [Fact]
    public void A_Docker_contexts_endpoint_is_given_to_a_Testcontainers_host_as_Docker_DotNet_reads_it() => Cases.All(
        ("a named pipe as the cli writes it is given as docker dotnet reads it", () => Cases.Each<(string, string)>(
        [
            ("npipe:////./pipe/dockerDesktopLinuxEngine", "npipe://./pipe/dockerDesktopLinuxEngine"),
            ("NPIPE:////build-server/pipe/docker_engine", "npipe://build-server/pipe/docker_engine"),
        ], row => A_named_pipe_as_the_cli_writes_it_is_given_as_docker_dotnet_reads_it(row.Item1, row.Item2))),
        ("any other endpoint is given as it is", () => Cases.Each<string>(
        [
            "npipe://./pipe/docker_engine",
            "unix:///var/run/docker.sock",
            "tcp://127.0.0.1:2375",
        ], Any_other_endpoint_is_given_as_it_is)));

    private void A_named_pipe_as_the_cli_writes_it_is_given_as_docker_dotnet_reads_it(string context, string given)
    {
        string said = DockerEndpoints.ForDotnetClient(context);

        Assert.True(said == given, $"{context} was given as {said}, which Docker.DotNet refuses, so a Testcontainers project's tests fail before any runs");
    }

    private void Any_other_endpoint_is_given_as_it_is(string context)
    {
        Assert.Equal(context, DockerEndpoints.ForDotnetClient(context));
    }
}
