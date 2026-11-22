using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>The compose files a folder's configuration runs are those <c>docker compose</c> reads there, in its order:
/// compose.yaml, compose.yml, docker-compose.yml, then docker-compose.yaml, and one override, the first of
/// compose.override.yml, compose.override.yaml, docker-compose.override.yml and docker-compose.override.yaml. Any file named
/// compose or docker-compose, alone or before a '.', '-' or '_', is a compose file.</summary>
public sealed class ComposeFilesTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void A_folders_compose_files_are_those_docker_compose_reads_there_in_its_order_and_named_as_it_names_them() => Cases.All(
        ("a folder with both spellings of the file and of its override runs the yml of each as compose does", () => Apart(test => test.A_folder_with_both_spellings_of_the_file_and_of_its_override_runs_the_yml_of_each_as_compose_does())),
        ("the override compose reads is its first name there whatever name the file has", () => Apart(test => test.The_override_compose_reads_is_its_first_name_there_whatever_name_the_file_has())),
        ("a folder with an override and no file runs nothing", () => Apart(test => test.A_folder_with_an_override_and_no_file_runs_nothing())),
        ("a compose file is YAML named compose or docker compose alone or before a dot dash or underscore", () => Cases.Each<(string, bool)>(
        [
            ("compose.yaml", true),
            ("DOCKER-COMPOSE.YML", true),
            ("docker-compose.prod.yaml", true),
            ("compose-dev.yml", true),
            ("composer.yml", false),
            ("docker-composer.yaml", false),
            ("compose.json", false),
        ], row => Apart(test => test.A_compose_file_is_YAML_named_compose_or_docker_compose_alone_or_before_a_dot_dash_or_underscore(row.Item1, row.Item2)))),
        ("a configurations compose words name its project first where one is given and leave it to compose where none is", () => Apart(test => test.A_configurations_compose_words_name_its_project_first_where_one_is_given_and_leave_it_to_compose_where_none_is())));

    /// <summary>Runs a check on a test of its own, as each ran before, its state its own.</summary>
    private static void Apart(Action<ComposeFilesTests> check)
    {
        using ComposeFilesTests test = new();
        check(test);
    }

    private void A_folder_with_both_spellings_of_the_file_and_of_its_override_runs_the_yml_of_each_as_compose_does()
    {
        Write("docker-compose.yaml", "docker-compose.yml", "docker-compose.override.yaml", "docker-compose.override.yml");
        Assert.Equal(["docker-compose.yml", "docker-compose.override.yml"], Unasked());
    }

    private void The_override_compose_reads_is_its_first_name_there_whatever_name_the_file_has()
    {
        Write("compose.yaml", "compose.yml", "docker-compose.yml", "compose.override.yaml", "docker-compose.override.yml");
        Assert.Equal(["compose.yaml", "compose.override.yaml"], Unasked());
        File.Delete(Path.Combine(folder, "compose.override.yaml"));
        Assert.Equal(["compose.yaml", "docker-compose.override.yml"], Unasked());
    }

    private void A_folder_with_an_override_and_no_file_runs_nothing()
    {
        Write("compose.override.yml");
        Assert.Empty(Unasked());
        Assert.Null(ComposeFiles.In(folder));
    }

    private void A_compose_file_is_YAML_named_compose_or_docker_compose_alone_or_before_a_dot_dash_or_underscore(string name, bool compose)
    {
        Assert.True(ComposeFiles.IsCompose(Path.Combine(folder, name)) == compose,
            $"{name} was {(compose ? "not " : string.Empty)}taken for a compose file, so its checks, navigation, schema and the configuration it gives "
            + $"would {(compose ? "be missing" : "be read from a file Compose never runs")}");
    }

    private void A_configurations_compose_words_name_its_project_first_where_one_is_given_and_leave_it_to_compose_where_none_is()
    {
        string[] files = ["/shop/docker-compose.yml", "/shop/docker-compose.override.yml"];
        Assert.Equal(["compose", "-p", "my-shop", "-f", files[0], "-f", files[1], "--env-file", "/shop/.env", "--profile", "debug", "--project-directory", "/shop"],
            ComposeFiles.Words("my-shop", files, ["/shop/.env"], ["debug"], "/shop"));
        Assert.Equal(["compose", "-f", files[0], "-f", files[1], "--project-directory", "/shop"], ComposeFiles.Words(null, files, [], [], "/shop"));
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
