using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>The compose files a folder's configuration runs are those <c>docker compose</c> reads there, in its order:
/// compose.yaml, compose.yml, docker-compose.yml, then docker-compose.yaml, and one override, the first of
/// compose.override.yml, compose.override.yaml, docker-compose.override.yml and docker-compose.override.yaml.</summary>
public sealed class ComposeFilesTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void A_folder_with_both_spellings_of_the_file_and_of_its_override_runs_the_yml_of_each_as_compose_does()
    {
        Write("docker-compose.yaml", "docker-compose.yml", "docker-compose.override.yaml", "docker-compose.override.yml");
        Assert.Equal(["docker-compose.yml", "docker-compose.override.yml"], Unasked());
    }

    [Fact]
    public void The_override_compose_reads_is_its_first_name_there_whatever_name_the_file_has()
    {
        Write("compose.yaml", "compose.yml", "docker-compose.yml", "compose.override.yaml", "docker-compose.override.yml");
        Assert.Equal(["compose.yaml", "compose.override.yaml"], Unasked());
        File.Delete(Path.Combine(folder, "compose.override.yaml"));
        Assert.Equal(["compose.yaml", "docker-compose.override.yml"], Unasked());
    }

    [Fact]
    public void A_folder_with_an_override_and_no_file_runs_nothing()
    {
        Write("compose.override.yml");
        Assert.Empty(Unasked());
        Assert.Null(ComposeFiles.In(folder));
    }

    private void Write(params string[] names)
    {
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(folder, name), "services: {}\n");
        }
    }

    private string[] Unasked() => [.. ComposeFiles.Unasked(folder).Select(Path.GetFileName).OfType<string>()];
}
