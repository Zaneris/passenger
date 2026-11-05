using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.UnitTests.Containers;

/// <summary>Whether a push would find a login for a registry, as the SDK looks for one: refused only where the SDK could
/// find none, the words saying how to make one. Files and variables are the test's own.</summary>
public sealed class RegistryLoginsTests
{
    private const string Home = "/home/ada";
    private const string Registry = "registry.example:5000";
    private static readonly string Docker = Path.Combine(Home, ".docker", "config.json");
    private static readonly string Podman = Path.Combine(Home, ".config", "containers", "auth.json");

    private static string? WhyNot(string registry, IReadOnlyDictionary<string, string> files, params (string Name, string Value)[] variables) =>
        RegistryLogins.WhyNot(registry, name => variables.FirstOrDefault(v => v.Name == name).Value, Home, file => files.GetValueOrDefault(file));

    private static Dictionary<string, string> InDocker(string config) => new() { [Docker] = config };

    [Fact]
    public void A_config_with_no_login_for_the_registry_is_refused_saying_how_to_make_one()
    {
        Assert.Equal($"No docker login for {Registry} was found in {Docker}: run docker login {Registry} in a terminal, then publish again",
            WhyNot(Registry, InDocker("""{ "auths": { "other.example": {} } }""")));
    }

    [Fact]
    public void No_config_at_all_is_refused_naming_dockers()
    {
        Assert.Contains(Docker, WhyNot(Registry, new Dictionary<string, string>()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_that_does_not_read_gives_no_login()
    {
        Assert.NotNull(WhyNot(Registry, InDocker("{ not json")));
    }

    [Fact]
    public void A_credsStore_is_left_to_answer_however_little_its_helper_lists()
    {
        Assert.Null(WhyNot(Registry, InDocker("""{ "credsStore": "ecr-login" }""")));
    }

    [Fact]
    public void An_auths_or_credHelpers_entry_for_the_registry_is_a_login()
    {
        Assert.Null(WhyNot(Registry, InDocker($$"""{ "auths": { "https://{{Registry}}/v2/": {} } }""")));
        Assert.Null(WhyNot(Registry, InDocker($$"""{ "credHelpers": { "{{Registry}}": "ecr-login" } }""")));
    }

    [Fact]
    public void Docker_Hubs_names_log_in_as_one()
    {
        Assert.Null(WhyNot("registry-1.docker.io", InDocker("""{ "auths": { "https://index.docker.io/v1/": {} } }""")));
    }

    [Fact]
    public void A_registry_on_this_machine_asks_for_no_login()
    {
        Assert.Null(WhyNot("localhost:5000", new Dictionary<string, string>()));
        Assert.Null(WhyNot("127.0.0.1:5000", new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData(RegistryVariables.PushUser, RegistryVariables.PushPassword)]
    [InlineData(RegistryVariables.User, RegistryVariables.Password)]
    [InlineData(RegistryVariables.SdkUser, RegistryVariables.SdkPassword)]
    public void A_name_and_password_the_SDK_reads_from_the_environment_is_a_login_with_nothing_in_any_config(string user, string password)
    {
        Assert.Null(WhyNot(Registry, new Dictionary<string, string>(), (user, "ada"), (password, "hidden")));
    }

    [Fact]
    public void A_name_without_its_password_in_the_environment_is_no_login()
    {
        Assert.NotNull(WhyNot(Registry, new Dictionary<string, string>(), (RegistryVariables.User, "ada")));
        Assert.NotNull(WhyNot(Registry, new Dictionary<string, string>(), (RegistryVariables.User, "ada"), (RegistryVariables.Password, string.Empty)));
        // The SDK reads a pair together, never one pair's name with another's password.
        Assert.NotNull(WhyNot(Registry, new Dictionary<string, string>(), (RegistryVariables.PushUser, "ada"), (RegistryVariables.Password, "hidden")));
    }

    [Fact]
    public void A_podman_login_is_found_where_the_SDK_reads_it_before_dockers_config()
    {
        Dictionary<string, string> files = new() { [Podman] = $$"""{ "auths": { "{{Registry}}": {} } }""", [Docker] = "{}" };
        Assert.Null(WhyNot(Registry, files));
        string runtime = Path.Combine("/run/user/1000", "containers", "auth.json");
        Assert.Null(WhyNot(Registry, new Dictionary<string, string> { [runtime] = $$"""{ "auths": { "{{Registry}}": {} } }""" }, (SocketFolders.RuntimeVariable, "/run/user/1000")));
        string elsewhere = Path.Combine("/etc/ada", "containers", "auth.json");
        Assert.Null(WhyNot(Registry, new Dictionary<string, string> { [elsewhere] = $$"""{ "auths": { "{{Registry}}": {} } }""" }, (RegistryVariables.ConfigFolder, "/etc/ada")));
    }

    [Fact]
    public void DOCKER_CONFIG_names_the_folder_of_dockers_config()
    {
        string config = Path.Combine("/etc/docker-ada", "config.json");
        Assert.Null(WhyNot(Registry, new Dictionary<string, string> { [config] = """{ "credsStore": "desktop" }""" }, (EngineVariables.DockerConfig, "/etc/docker-ada")));
    }

    [Fact]
    public void REGISTRY_AUTH_FILE_is_the_only_file_read_where_it_is_set()
    {
        Dictionary<string, string> files = new() { [Docker] = """{ "credsStore": "desktop" }""", ["/etc/auth.json"] = "{}" };
        Assert.Contains("/etc/auth.json", WhyNot(Registry, files, (RegistryVariables.AuthFile, "/etc/auth.json")), StringComparison.Ordinal);
    }
}
