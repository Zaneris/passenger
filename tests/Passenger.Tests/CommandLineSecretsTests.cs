using System.Text.Json.Nodes;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A command line shown to a person or an agent through the one mask: a running program's as the system gives it
/// (<see cref="ContainerSecrets.CommandLines"/>), one Passenger starts (<see cref="ContainerSecrets.CommandLine"/>), one a
/// program echoes (<see cref="ContainerSecrets.InSaid"/>), an Aspire resource's arguments and Inspect's command lines: each
/// word on its own, so a secret's value goes and the words after it stay, and the word after a flag naming a secret goes
/// too.</summary>
[Collection(Beside.Name)]
public sealed class CommandLineSecretsTests
{
    private const string Masked = ContainerSecrets.Masked;

    /// <summary>A secret given as the word after its flag (<c>--password hunter2</c>, <c>--api-key sk-live-123</c>, PuTTY's
    /// <c>-pw</c>) is masked where it stands, in double quotes still when it was, its spacing kept; a flag that says it takes
    /// no value leaves the next flag as it is, which is read in turn.</summary>
    [Fact]
    public void A_running_programs_secret_given_as_the_word_after_its_flag_is_masked_and_its_other_words_kept()
    {
        Func<string, string> shown = ContainerSecrets.CommandLines();
        Assert.Equal(
            $"dotnet bin/Shop.dll --password {Masked} --api-key {Masked} --port 5000 --project App.csproj --verbose",
            shown("dotnet bin/Shop.dll --password hunter2 --api-key sk-live-123 --port 5000 --project App.csproj --verbose"));
        Assert.Equal($"\"C:\\Tools\\plink.exe\"  -pw  \"{Masked}\"  \"--token\" {Masked} --secret \"\" ada@db.example",
            shown("\"C:\\Tools\\plink.exe\"  -pw  \"two words\"  \"--token\" sk-live-123 --secret \"\" ada@db.example"));
        Assert.Equal($"docker login --password-stdin --username ada --no-password --token {Masked} registry.example",
            shown("docker login --password-stdin --username ada --no-password --token -sk-live-123 registry.example"));
    }

    /// <summary>The word after a flag naming a secret is masked in a running program's line, a command Passenger starts, what a
    /// program says and an argument list alike, the words after it kept: a password, an API key, a token, a client secret,
    /// PuTTY's <c>-pw</c>, an AppRole's secret id, and a value that begins with a dash.</summary>
    [Fact]
    public void The_word_after_a_flag_naming_a_secret_is_masked_every_way_a_command_line_is_shown()
    {
        string[][] secrets =
        [
            ["--password", "hunter2"], ["--api-key", "sk-live-123"], ["--token", "sk-live-123"], ["--client-secret", "hunter2"],
            ["-pw", "hunter2"], ["--access-token", "hunter2"], ["--db-password", "hunter2"], ["--secret-access-key", "hunter2"],
            ["--ApiKey", "hunter2"], ["--account-key", "hunter2"], ["--keystore-password", "hunter2"], ["--secret-id", "hunter2"],
            ["--token", "-sk-live-123"],
        ];
        foreach (string[] secret in secrets)
        {
            ShownEveryWay([secret[0], secret[1], "--port", "5000"], [secret[0], Masked, "--port", "5000"], $"{secret[0]}'s secret is shown");
        }
    }

    /// <summary>The word after a flag that takes no secret is kept every way a command line is shown, as a person needs it: one
    /// itself a flag (the next flag after a switch, psql's <c>--password</c> before <c>-h</c>), one after a flag whose name says
    /// what its value is (a file, path, folder, name, address, id, type or kind), a key into data, and a switch saying it
    /// takes no secret (<c>use-</c>, <c>ask-</c>, <c>store-</c>, <c>no-</c>, <c>-stdin</c>); so too after an '=' (<c>--key-file=</c>).</summary>
    [Fact]
    public void The_word_after_a_flag_that_takes_no_secret_or_is_itself_a_flag_is_kept_every_way_a_command_line_is_shown()
    {
        string[][] kept =
        [
            ["--use-secrets", "--environment", "Development"], ["--store-password-in-clear-text", "--valid-authentication-types", "basic"],
            ["--password", "-h", "db.example"], ["--api-key", "--", "run"], ["--key-file", "deploy/id_ed25519"],
            ["--credentials-file", "/home/me/.aws/credentials"], ["--ssh-key-path", "~/.ssh/id_rsa"], ["--secrets-dir", "/run/secrets"],
            ["--secret-folder", "secrets"], ["--secret-name", "DbPassword"], ["--token-url", "https://login.example/token"],
            ["--key-vault-uri", "https://shop.vault.example"], ["--access-key-id", "AKIAEXAMPLE"], ["--key-type", "rsa"],
            ["--secret-kind", "opaque"], ["--partition-key", "/tenantId"], ["--sort-key", "Name"], ["--public-key", "ssh-ed25519"],
            ["--ask-become-pass", "site.yml"], ["--no-password", "ada"], ["--password-stdin", "registry.example"],
            ["--key-file=deploy/id_ed25519", "--use-secrets=true", "--secret-name=DbPassword", "--no-password=true"],
        ];
        foreach (string[] words in kept)
        {
            ShownEveryWay(words, words, "a word a person needs is masked");
            string line = $"tool {string.Join(' ', words)}";
            Assert.Same(line, ContainerSecrets.CommandLines()(line));
        }
    }

    /// <summary>A variable whose name says its value is a secret's file, name or address shows it, as a key into data does, and
    /// one holding a secret is masked still: a connection string's whatever its connection is named, and an AppRole's secret id.</summary>
    [Fact]
    public void A_variable_naming_a_secrets_file_name_or_address_shows_it_and_a_secrets_own_is_masked()
    {
        Assert.Equal("/run/secrets/db", ContainerSecrets.Shown("POSTGRES_PASSWORD_FILE", "/run/secrets/db"));
        Assert.Equal("DbPassword", ContainerSecrets.Shown("KeyVault__SecretName", "DbPassword"));
        Assert.Equal("https://login.example/token", ContainerSecrets.Shown("OAUTH_TOKEN_URL", "https://login.example/token"));
        Assert.Equal("/tenantId", ContainerSecrets.Shown("CosmosPartitionKey", "/tenantId"));
        Assert.Equal(Masked, ContainerSecrets.Shown("ConnectionStrings__Name", "Host=db;Password=hunter2"));
        Assert.Equal(Masked, ContainerSecrets.Shown("VAULT_SECRET_ID", "hunter2"));
        Assert.Equal(Masked, ContainerSecrets.Shown("SECRET_KEY_BASE", "hunter2"));
        Assert.Equal(Masked, ContainerSecrets.Shown("STORE_PASSWORD", "hunter2"));
    }

    /// <summary>A program's words after it (<c>tool</c>), shown as <paramref name="shown"/> every way the mask shows a command
    /// line: as the system gives a running program's, as Passenger starts one, as a program says one, and as an argument list.</summary>
    private static void ShownEveryWay(string[] words, string[] shown, string why)
    {
        string line = $"tool {string.Join(' ', words)}";
        string expected = $"tool {string.Join(' ', shown)}";
        string running = ContainerSecrets.CommandLines()(line);
        Assert.True(running == expected, $"A running program's {line} shows as {running}: {why}");
        string started = ContainerSecrets.CommandLine(ChildProcesses.Command("tool", words));
        Assert.True(started == expected, $"A command Passenger starts, {line}, is echoed as {started}: {why}");
        string said = ContainerSecrets.InSaid(line);
        Assert.True(said == expected, $"A program saying {line} is shown saying {said}: {why}");
        IReadOnlyList<string> listed = ContainerSecrets.Arguments(["tool", .. words]);
        Assert.True(listed.SequenceEqual(["tool", .. shown]), $"Arguments {line} show as {string.Join(' ', listed)}: {why}");
    }

    /// <summary>A word after a flag that names no secret stays as the system gave it, and so does one after a flag of one
    /// letter, which names nothing the rule can read: <c>-p</c> is a port to psql and ssh, a Compose project to docker
    /// compose and a password only to some.</summary>
    [Fact]
    public void A_word_after_a_flag_naming_no_secret_or_of_one_letter_comes_back_as_it_was()
    {
        string line = "dotnet run --port 5000 --project App.csproj --keymap vs --monkey business -p 5432 -P -k value";
        Assert.Same(line, ContainerSecrets.CommandLines()(line));
        string said = "docker compose -p shop up --build --port 5000 -- --hotkey f5";
        Assert.Same(said, ContainerSecrets.InSaid(said));
    }

    /// <summary>A command Passenger starts, as its log and consoles echo it (<see cref="ChildProcesses.Shown"/>), has the word
    /// after a flag naming a secret masked whatever it holds, and the rest as it runs; an empty one shows as it is.</summary>
    [Fact]
    public void A_command_passenger_starts_has_the_word_after_a_secrets_flag_masked_in_its_echo()
    {
        Assert.Equal($"docker login --username ada --password {Masked} registry.example",
            ContainerSecrets.CommandLine(ChildProcesses.Command("docker", ["login", "--username", "ada", "--password", "hunter2", "registry.example"])));
        Assert.Equal($"dotnet nuget add source https://feed.example/v3/index.json --name feed --api-key {Masked} --configfile nuget.config",
            ContainerSecrets.CommandLine(ChildProcesses.Command("dotnet",
                ["nuget", "add", "source", "https://feed.example/v3/index.json", "--name", "feed", "--api-key", "two words", "--configfile", "nuget.config"])));
        Assert.Equal("docker compose -p shop up --password \"\" --port 5000",
            ContainerSecrets.CommandLine(ChildProcesses.Command("docker", ["compose", "-p", "shop", "up", "--password", "", "--port", "5000"])));
    }

    /// <summary>A command a program echoes (BuildKit's word that a <c>RUN</c> failed, an Aspire resource's arguments) has the
    /// word after a flag naming a secret masked, the quote after it kept, and the rest of the line as it was.</summary>
    [Fact]
    public void A_command_a_program_echoes_has_the_word_after_a_secrets_flag_masked()
    {
        Assert.Equal(
            $"failed to solve: process \"/bin/sh -c dotnet nuget add source https://feed.example/v3/index.json --username ada --password {Masked}\" did not complete successfully: exit code: 1",
            ContainerSecrets.InSaid("failed to solve: process \"/bin/sh -c dotnet nuget add source https://feed.example/v3/index.json --username ada --password hunter2\" did not complete successfully: exit code: 1"));
        Assert.Equal($"tool --password-stdin --token '{Masked}' --verbose\nnext --api-key {Masked}",
            ContainerSecrets.InSaid("tool --password-stdin --token 'two words' --verbose\nnext --api-key sk-live-123"));
        string[] arguments = ["run", "--api-key", Masked, "--port", "5000", "--password", ""];
        Assert.Equal(arguments, ContainerSecrets.Arguments(["run", "--api-key", "sk-live-123", "--port", "5000", "--password", ""]));
    }

    /// <summary>An argument its sender marks sensitive (an Aspire resource's) is masked whatever is before it, the word after
    /// a flag naming a secret still masked and the rest shown; an empty one shows as it is, and marks that are not one an
    /// argument mask every argument.</summary>
    [Fact]
    public void An_argument_marked_sensitive_is_masked_whatever_is_before_it()
    {
        Assert.Equal(["run", Masked, "--port", "5000", "--token", Masked, ""],
            ContainerSecrets.Arguments(["run", "hunter2", "--port", "5000", "--token", "sk-live-123", ""], [false, true, false, false, false, false, true]));
        Assert.Equal([Masked, Masked, ""], ContainerSecrets.Arguments(["run", "hunter2", ""], [true]));
    }

    /// <summary>Inspect's command lines are masked as a program's arguments are, an array read on from the one it continues
    /// (a container's Args from its Path, a Config's Cmd from its Entrypoint) and a health check's Test on its own, and a
    /// copy written so comes back as it was.</summary>
    [Fact]
    public void Inspects_command_lines_are_masked_as_a_programs_arguments_are()
    {
        JsonNode said = JsonNode.Parse("""
            [{ "Path": "/app/shop", "Args": ["--password", "hunter2", "--port", "5000"],
               "Config": { "Entrypoint": ["/app/shop", "--api-key"], "Cmd": ["sk-live-123", "--Password=hunter2", "--verbose"],
                           "Healthcheck": { "Test": ["CMD", "redis-cli", "--pass", "hunter2", "ping"] } } }]
            """)!;
        ContainerSecrets.Mask(said);
        JsonNode container = said[0]!;
        Assert.Equal("/app/shop", (string?)container["Path"]);
        Assert.Equal(["--password", Masked, "--port", "5000"], Words(container["Args"]));
        Assert.Equal(["/app/shop", "--api-key"], Words(container["Config"]!["Entrypoint"]));
        Assert.Equal([Masked, $"--Password={Masked}", "--verbose"], Words(container["Config"]!["Cmd"]));
        Assert.Equal(["CMD", "redis-cli", "--pass", Masked, "ping"], Words(container["Config"]!["Healthcheck"]!["Test"]));
        string masked = said.ToJsonString();
        ContainerSecrets.Mask(said);
        Assert.Equal(masked, said.ToJsonString());

        static string[] Words(JsonNode? array) => [.. array!.AsArray().Select(word => (string)word!)];
    }

    /// <summary>A password, an API key, a connection string's password and a URL's are masked where they stand in the line,
    /// and every other word, the program's path and an option after a secret among them, shows as the system gave it.</summary>
    [Fact]
    public void A_command_lines_secrets_are_masked_word_by_word_and_its_other_words_kept()
    {
        Func<string, string> shown = ContainerSecrets.CommandLines();
        Assert.Equal(
            $"/usr/local/share/dotnet/dotnet bin/Shop.dll --Password={Masked} --ApiKey={Masked} --verbose",
            shown("/usr/local/share/dotnet/dotnet bin/Shop.dll --Password=x --ApiKey=y --verbose"));
        Assert.Equal(
            $"dotnet Shop.dll --ConnectionStrings:Db={Masked};Username=app;Password={Masked} --urls https://ada:{Masked}@db.example/shop",
            shown("dotnet Shop.dll --ConnectionStrings:Db=Host=db;Username=app;Password=hunter2 --urls https://ada:s3cret@db.example/shop"));
    }

    /// <summary>A word in double quotes, as the system quotes one holding a space, is masked inside its quotes, and one
    /// quoted within (Windows' <c>--password="two words"</c>) as a whole value; neither spills into the word after it.</summary>
    [Fact]
    public void A_quoted_words_secret_is_masked_whole_and_the_word_after_it_kept()
    {
        Func<string, string> shown = ContainerSecrets.CommandLines();
        Assert.Equal($"\"C:\\Program Files\\Shop\\Shop.exe\" --Password={Masked} --name \"two words\"",
            shown("\"C:\\Program Files\\Shop\\Shop.exe\" \"--Password=two words\" --name \"two words\""));
        Assert.Equal($"Shop.exe --password={Masked} --verbose", shown("Shop.exe --password=\"two words\" --verbose"));
    }

    /// <summary>A line with nothing to mask comes back as it was, the same string, its spacing and quotes as the system gave
    /// them.</summary>
    [Fact]
    public void A_command_line_with_no_secret_comes_back_as_it_was()
    {
        string line = "\"C:\\Program Files\\dotnet\\dotnet.exe\"  exec   Shop.dll --urls http://localhost:5000 --port=5000 \"\\\"quoted\\\" word\"";
        Assert.Same(line, ContainerSecrets.CommandLines()(line));
    }
}
