using System.Net;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.UnitTests.Containers;

/// <summary>Whether a push would find a login for a registry, as the SDK looks for one, the words saying how to make one, and
/// whether the registry asks for one, as the SDK reaches it. Files, variables and answers are the test's own.</summary>
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
    public void A_push_finds_a_login_for_its_registry_where_the_SDK_reads_one_else_is_refused_saying_how_to_make_one() => Cases.All(
        ("a config with no login for the registry is refused saying how to make one", A_config_with_no_login_for_the_registry_is_refused_saying_how_to_make_one),
        ("no config at all is refused naming dockers", No_config_at_all_is_refused_naming_dockers),
        ("a config that does not read gives no login", A_config_that_does_not_read_gives_no_login),
        ("a credsStore is left to answer however little its helper lists", A_credsStore_is_left_to_answer_however_little_its_helper_lists),
        ("an auths or credHelpers entry for the registry is a login", An_auths_or_credHelpers_entry_for_the_registry_is_a_login),
        ("docker Hubs names log in as one", Docker_Hubs_names_log_in_as_one),
        ("a registry on this machine finds its login as any other", A_registry_on_this_machine_finds_its_login_as_any_other),
        ("a name and password the SDK reads from the environment is a login with nothing in any config", () => Cases.Each<(string, string)>(
        [
            (RegistryVariables.PushUser, RegistryVariables.PushPassword),
            (RegistryVariables.SdkUser, RegistryVariables.SdkPassword),
        ], row => A_name_and_password_the_SDK_reads_from_the_environment_is_a_login_with_nothing_in_any_config(row.Item1, row.Item2))),
        ("a name without its password in the environment is no login", A_name_without_its_password_in_the_environment_is_no_login),
        ("a podman login is found where the SDK reads it before dockers config", A_podman_login_is_found_where_the_SDK_reads_it_before_dockers_config),
        ("DOCKER CONFIG names the folder of dockers config", DOCKER_CONFIG_names_the_folder_of_dockers_config),
        ("REGISTRY AUTH FILE is the only file read where it is set", REGISTRY_AUTH_FILE_is_the_only_file_read_where_it_is_set));

    private void A_config_with_no_login_for_the_registry_is_refused_saying_how_to_make_one()
    {
        Assert.Equal($"No docker login for {Registry} was found in {Docker}: run docker login {Registry} in a terminal, then publish again",
            WhyNot(Registry, InDocker("""{ "auths": { "other.example": {} } }""")));
    }

    private void No_config_at_all_is_refused_naming_dockers()
    {
        Assert.Contains(Docker, WhyNot(Registry, new Dictionary<string, string>()), StringComparison.Ordinal);
    }

    private void A_config_that_does_not_read_gives_no_login()
    {
        Assert.NotNull(WhyNot(Registry, InDocker("{ not json")));
    }

    private void A_credsStore_is_left_to_answer_however_little_its_helper_lists()
    {
        Assert.Null(WhyNot(Registry, InDocker("""{ "credsStore": "ecr-login" }""")));
    }

    private void An_auths_or_credHelpers_entry_for_the_registry_is_a_login()
    {
        Assert.Null(WhyNot(Registry, InDocker($$"""{ "auths": { "https://{{Registry}}/v2/": {} } }""")));
        Assert.Null(WhyNot(Registry, InDocker($$"""{ "credHelpers": { "{{Registry}}": "ecr-login" } }""")));
    }

    private void Docker_Hubs_names_log_in_as_one()
    {
        Assert.Null(WhyNot("registry-1.docker.io", InDocker("""{ "auths": { "https://index.docker.io/v1/": {} } }""")));
    }

    /// <summary>A registry on this machine may ask for a login too, so its name settles nothing.</summary>
    private void A_registry_on_this_machine_finds_its_login_as_any_other()
    {
        Assert.NotNull(WhyNot("localhost:5000", new Dictionary<string, string>()));
        Assert.Null(WhyNot("localhost:5000", InDocker("""{ "auths": { "localhost:5000": {} } }""")));
    }

    private void A_name_and_password_the_SDK_reads_from_the_environment_is_a_login_with_nothing_in_any_config(string user, string password)
    {
        Assert.Null(WhyNot(Registry, new Dictionary<string, string>(), (user, "ada"), (password, "hidden")));
    }

    private void A_name_without_its_password_in_the_environment_is_no_login()
    {
        Assert.NotNull(WhyNot(Registry, new Dictionary<string, string>(), (RegistryVariables.User, "ada")));
        Assert.NotNull(WhyNot(Registry, new Dictionary<string, string>(), (RegistryVariables.User, "ada"), (RegistryVariables.Password, string.Empty)));
        // The SDK reads a pair together, never one pair's name with another's password.
        Assert.NotNull(WhyNot(Registry, new Dictionary<string, string>(), (RegistryVariables.PushUser, "ada"), (RegistryVariables.Password, "hidden")));
    }

    private void A_podman_login_is_found_where_the_SDK_reads_it_before_dockers_config()
    {
        Dictionary<string, string> files = new() { [Podman] = $$"""{ "auths": { "{{Registry}}": {} } }""", [Docker] = "{}" };
        Assert.Null(WhyNot(Registry, files));
        string runtime = Path.Combine("/run/user/1000", "containers", "auth.json");
        Assert.Null(WhyNot(Registry, new Dictionary<string, string> { [runtime] = $$"""{ "auths": { "{{Registry}}": {} } }""" }, (SocketFolders.RuntimeVariable, "/run/user/1000")));
        string elsewhere = Path.Combine("/etc/ada", "containers", "auth.json");
        Assert.Null(WhyNot(Registry, new Dictionary<string, string> { [elsewhere] = $$"""{ "auths": { "{{Registry}}": {} } }""" }, (RegistryVariables.ConfigFolder, "/etc/ada")));
    }

    private void DOCKER_CONFIG_names_the_folder_of_dockers_config()
    {
        string config = Path.Combine("/etc/docker-ada", "config.json");
        Assert.Null(WhyNot(Registry, new Dictionary<string, string> { [config] = """{ "credsStore": "desktop" }""" }, (EngineVariables.DockerConfig, "/etc/docker-ada")));
    }

    [Fact]
    public Task A_registry_is_asked_for_a_login_only_on_its_own_401_reached_as_the_SDK_reaches_it_and_sent_none_anonymously() => Cases.AllAsync(
        ("a registry answering an anonymous request asks for no login", A_registry_answering_an_anonymous_request_asks_for_no_login),
        ("a registry answering 401 asks for a login", A_registry_answering_401_asks_for_a_login),
        ("any other answer is left to the push", () => Cases.EachAsync<HttpStatusCode>(
        [
            HttpStatusCode.Forbidden,
            HttpStatusCode.InternalServerError,
        ], Any_other_answer_is_left_to_the_push)),
        ("a registry speaking no TLS is asked again over HTTP as the SDK falls back", A_registry_speaking_no_TLS_is_asked_again_over_HTTP_as_the_SDK_falls_back),
        ("a registry not reached is left to the push with no second try over HTTP", A_registry_not_reached_is_left_to_the_push_with_no_second_try_over_HTTP),
        ("a registry that never answers is left to the push and a stopped publish stops", A_registry_that_never_answers_is_left_to_the_push_and_a_stopped_publish_stops),
        ("docker Hub is asked where its API answers and a registrys scheme and path are left out", Docker_Hub_is_asked_where_its_API_answers_and_a_registrys_scheme_and_path_are_left_out));

    private async Task A_registry_answering_an_anonymous_request_asks_for_no_login()
    {
        Answers answers = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.False(await Asks(Registry, answers));
        Assert.Equal([$"https://{Registry}/v2/"], answers.Asked);
        Assert.False(answers.SentLogin, "An anonymous question carried a login");
    }

    private async Task A_registry_answering_401_asks_for_a_login()
    {
        Assert.True(await Asks(Registry, new Answers(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))));
    }

    /// <summary>Only a 401 sends the SDK looking for a login; whatever else is wrong, the push says better.</summary>
    private async Task Any_other_answer_is_left_to_the_push(HttpStatusCode status)
    {
        Assert.False(await Asks(Registry, new Answers(_ => new HttpResponseMessage(status))));
    }

    private async Task A_registry_speaking_no_TLS_is_asked_again_over_HTTP_as_the_SDK_falls_back()
    {
        Answers plain = new(address => address.Scheme == "https" ? throw NoTls() : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.True(await Asks(Registry, plain));
        Assert.Equal([$"https://{Registry}/v2/", $"http://{Registry}/v2/"], plain.Asked);
        Assert.False(await Asks(Registry, new Answers(address => address.Scheme == "https" ? throw NoTls() : new HttpResponseMessage(HttpStatusCode.OK))));
    }

    private async Task A_registry_not_reached_is_left_to_the_push_with_no_second_try_over_HTTP()
    {
        Answers refused = new(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        Assert.False(await Asks(Registry, refused));
        Assert.Equal([$"https://{Registry}/v2/"], refused.Asked);
        Assert.False(await Asks(Registry, new Answers(address => address.Scheme == "https" ? throw NoTls() : throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"))));
    }

    private async Task A_registry_that_never_answers_is_left_to_the_push_and_a_stopped_publish_stops()
    {
        Assert.False(await Asks(Registry, new Answers(_ => throw new TaskCanceledException("timed out"))));
        using CancellationTokenSource stopped = new();
        await stopped.CancelAsync();
        using HttpClient client = new(new Answers(_ => throw new TaskCanceledException("stopped")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RegistryLogins.AsksForLoginAsync(Registry, client, stopped.Token));
    }

    private async Task Docker_Hub_is_asked_where_its_API_answers_and_a_registrys_scheme_and_path_are_left_out()
    {
        Answers hub = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.True(await Asks("docker.io", hub));
        Assert.Equal(["https://registry-1.docker.io/v2/"], hub.Asked);
        Answers named = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Asks($"https://{Registry}/team", named);
        Assert.Equal([$"https://{Registry}/v2/"], named.Asked);
    }

    private static async Task<bool> Asks(string registry, Answers answers)
    {
        using HttpClient client = new(answers, disposeHandler: false);
        return await RegistryLogins.AsksForLoginAsync(registry, client, TestContext.Current.CancellationToken);
    }

    private static HttpRequestException NoTls() => new(HttpRequestError.SecureConnectionError, "no TLS here");

    /// <summary>A registry as a test's answers make it, keeping what it was asked.</summary>
    private sealed class Answers(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        public bool SentLogin { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.ToString());
            SentLogin |= request.Headers.Authorization is not null;
            return Task.FromResult(answer(request.RequestUri));
        }
    }

    private void REGISTRY_AUTH_FILE_is_the_only_file_read_where_it_is_set()
    {
        Dictionary<string, string> files = new() { [Docker] = """{ "credsStore": "desktop" }""", ["/etc/auth.json"] = "{}" };
        Assert.Contains("/etc/auth.json", WhyNot(Registry, files, (RegistryVariables.AuthFile, "/etc/auth.json")), StringComparison.Ordinal);
    }
}
