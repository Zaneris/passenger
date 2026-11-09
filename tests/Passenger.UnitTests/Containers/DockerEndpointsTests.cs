using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>A Docker context's endpoint as a Testcontainers test host is given it: Docker.DotNet takes a Windows named pipe
/// only as npipe://SERVER/pipe/NAME, where the CLI's context writes npipe:////SERVER/pipe/NAME (#1430).</summary>
public sealed class DockerEndpointsTests
{
    [Theory]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine", "npipe://./pipe/dockerDesktopLinuxEngine")]
    [InlineData("npipe:////./pipe/podman-machine-default", "npipe://./pipe/podman-machine-default")]
    [InlineData("NPIPE:////build-server/pipe/docker_engine", "npipe://build-server/pipe/docker_engine")]
    public void A_named_pipe_as_the_cli_writes_it_is_given_as_docker_dotnet_reads_it(string context, string given)
    {
        string said = DockerEndpoints.ForDotnetClient(context);

        Assert.True(said == given, $"{context} was given as {said}, which Docker.DotNet refuses, so a Testcontainers project's tests fail before any runs");
    }

    [Theory]
    [InlineData("npipe://./pipe/docker_engine")]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("unix:///Users/someone/.colima/default/docker.sock")]
    [InlineData("tcp://127.0.0.1:2375")]
    [InlineData("ssh://builder@host")]
    public void Any_other_endpoint_is_given_as_it_is(string context)
    {
        Assert.Equal(context, DockerEndpoints.ForDotnetClient(context));
    }
}
